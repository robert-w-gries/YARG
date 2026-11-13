using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Menu;
using YARG.Menu.Persistent;
using YARG.Settings;
using YARG.Song;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Experimental bridge to the <c>yarg-link</c> sidecar, which connects friends' games over iroh.
    /// </summary>
    /// <remarks>
    /// Talks JSON over localhost UDP, one message per datagram (see yarg-link's <c>protocol.rs</c>).
    /// <list type="bullet">
    /// <item>Game → sidecar: <c>status</c> (heartbeat, song, scores), <c>ready</c>, <c>decline</c>,
    /// <c>broadcast</c> (opaque data for friends' games).</item>
    /// <item>Sidecar → game: <c>ping</c>, <c>load_song</c>, <c>start</c>, <c>cancel</c>,
    /// <c>peer</c> (a friend's broadcast), <c>peer_left</c>.</item>
    /// </list>
    /// Enabled by Settings › Experimental › "Enable online link". When enabled and at least one friend
    /// is connected, gameplay holds at the loading screen until the sidecar sends a synced start.
    /// <para>
    /// During a synced song each game broadcasts a <c>setup</c> (its players' profiles and engine
    /// parameters, as replay data) when it's ready, then streams <c>inputs</c>: each player's recorded
    /// inputs plus their score. The other games add a highway per friend's player that plays those
    /// inputs back like a replay, so you see their notes being hit and missed, and show everyone's
    /// score in a scoreboard.
    /// </para>
    /// </remarks>
    public class LinkController : MonoBehaviour
    {
        /// <summary>The sidecar's game link port (yarg-link's <c>GAME_PORT</c>).</summary>
        public const int SIDECAR_PORT = 36110;

        /// <summary>The sidecar counts as gone after this long without a ping.</summary>
        private const double SIDECAR_TIMEOUT = 3.0;

        /// <summary>Give up on a synced start after this long and start alone.</summary>
        private const double MAX_WAIT_SECONDS = 120.0;

        private const double STATUS_INTERVAL_GAMEPLAY = 0.1;
        private const double STATUS_INTERVAL_IDLE     = 1.0;

        /// <summary>Send new inputs at most this often.</summary>
        private const double INPUTS_INTERVAL_MIN = 1.0 / 120.0;

        /// <summary>Send at least this often, so friends' tracks keep moving when nothing is pressed.</summary>
        private const double INPUTS_INTERVAL_MAX = 1.0 / 30.0;

        /// <summary>Bytes per input in the <c>inputs</c> message: time (double), action (int), value (int).</summary>
        private const int INPUT_SIZE = 16;

        public static LinkController Instance { get; private set; }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double Now => Clock.Elapsed.TotalSeconds;

        private readonly ConcurrentQueue<string> _incoming = new();

        private UdpClient _client;
        private Thread    _receiveThread;

        private GameManager _gameManager;
        private double      _nextStatusTime;

        private int    _peers;
        private double _lastPingTime = double.NegativeInfinity;

        // Synced-start state, touched only on the main thread
        private bool    _waiting;
        private double? _startAt;
        private string  _cancelReason;

        // Song a friend asked us to load, while we're still in difficulty select
        private string _remoteRequestHash;

        // Friends' player setups, by peer ID. Kept until we leave the song they were for.
        private readonly Dictionary<string, RemoteSetup> _remoteSetups = new();

        // The synced song we're sharing our inputs for, if any
        private Session _session;

        private GUIStyle _scoreboardStyle;

        private bool SidecarAlive => Now - _lastPingTime < SIDECAR_TIMEOUT;

        /// <summary>
        /// Whether gameplay should hold at the loading screen for a synced start.
        /// </summary>
        public bool ShouldHoldForStart => _client != null && SidecarAlive && _peers > 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(LinkController));
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LinkController>();
        }

        private void Update()
        {
            if (GlobalVariables.Instance == null) return;

            bool linkEnabled = SettingsManager.Settings?.LinkEnable.Value ?? false;
            if (!linkEnabled)
            {
                if (_client != null) Close();
                return;
            }

            if (_client == null) Open();

            while (_incoming.TryDequeue(out var text))
            {
                HandleMessage(text);
            }

            CheckRemoteRequestAbandoned();
            CheckSessionEnded();

            if (Now >= _nextStatusTime)
            {
                bool inGameplay = GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay;
                _nextStatusTime = Now + (inGameplay ? STATUS_INTERVAL_GAMEPLAY : STATUS_INTERVAL_IDLE);
                SendStatus();
            }
        }

        private void LateUpdate()
        {
            // After GameManager.Update, so this frame's inputs are recorded
            if (_client != null && _session != null)
            {
                SendInputs();
            }
        }

        private void OnDestroy()
        {
            Close();
        }

        #region Synced start

        public enum StartResult
        {
            /// <summary>A synced start is scheduled; call <see cref="WaitForStartTime"/>.</summary>
            Synced,
            /// <summary>The link is gone or took too long; start right away.</summary>
            Alone,
            /// <summary>The start was cancelled; return to the menu.</summary>
            Cancelled,
        }

        /// <summary>
        /// Report that gameplay is loaded, then wait until the sidecar schedules the start.
        /// Called by <see cref="GameManager"/> once its own players exist.
        /// </summary>
        /// <remarks>
        /// The start is scheduled a couple of seconds ahead, which leaves time to add friends' players
        /// (<see cref="TakeRemotePlayers"/>) before <see cref="WaitForStartTime"/>.
        /// </remarks>
        public async UniTask<StartResult> WaitForStartSignal(GameManager gameManager, LoadingContext context)
        {
            var song = gameManager.Song;

            _waiting = true;
            _startAt = null;
            _cancelReason = null;
            _remoteRequestHash = null;
            _session = null;

            // Forget setups for any other song
            string hash = song.Hash.ToString();
            var stale = new List<string>();
            foreach (var (peer, setup) in _remoteSetups)
            {
                if (setup.SongHash != hash) stale.Add(peer);
            }
            foreach (var peer in stale)
            {
                _remoteSetups.Remove(peer);
            }

            // Tell friends about our players first, so they have it by the time the start is scheduled
            var session = new Session(gameManager, hash);
            Send(new JObject
            {
                ["type"] = "broadcast",
                ["data"] = session.CreateSetupMessage(),
            });

            Send(new JObject
            {
                ["type"] = "ready",
                ["song"] = SongJson(song),
            });

            context.SetLoadingText("Waiting for other players...", song.Name.Original);
            YargLogger.LogInfo("Link: waiting for synced start");

            double deadline = Now + MAX_WAIT_SECONDS;
            while (true)
            {
                await UniTask.Yield();

                if (_cancelReason != null)
                {
                    ToastManager.ToastWarning($"Online start cancelled: {_cancelReason}");
                    _waiting = false;
                    return StartResult.Cancelled;
                }

                if (_startAt.HasValue)
                {
                    _session = session;
                    return StartResult.Synced;
                }

                if (!SidecarAlive || Now >= deadline)
                {
                    ToastManager.ToastWarning("Lost the online link, starting without sync");
                    _waiting = false;
                    return StartResult.Alone;
                }
            }
        }

        /// <summary>
        /// Wait for the moment scheduled by <see cref="WaitForStartSignal"/>.
        /// </summary>
        public async UniTask WaitForStartTime()
        {
            try
            {
                while (_startAt.HasValue && Now < _startAt.Value && SidecarAlive)
                {
                    await UniTask.Yield();
                }
                YargLogger.LogInfo("Link: synced start");
            }
            finally
            {
                _waiting = false;
                _startAt = null;
            }
        }

        /// <summary>
        /// Friends' players to add to the current song: their replay frames, plus the feeds their inputs arrive on.
        /// </summary>
        public List<(ReplayFrame Frame, ReplayData Data, RemoteInputFeed Feed)> TakeRemotePlayers(SongEntry song)
        {
            var players = new List<(ReplayFrame, ReplayData, RemoteInputFeed)>();
            string hash = song.Hash.ToString();
            foreach (var setup in _remoteSetups.Values)
            {
                if (setup.SongHash != hash || setup.Taken) continue;
                setup.Taken = true;

                foreach (var feed in setup.Feeds)
                {
                    if (feed.Mirrored)
                    {
                        players.Add((setup.Data.Frames[setup.FrameIndices[feed.Index]], setup.Data, feed));
                    }
                }
            }

            YargLogger.LogInfo($"Link: {_remoteSetups.Count} friend setup(s) received, {players.Count} highway(s) to add");
            return players;
        }

        #endregion

        #region Incoming

        private void HandleMessage(string text)
        {
            JObject msg;
            try
            {
                msg = JObject.Parse(text);
            }
            catch (JsonException e)
            {
                YargLogger.LogFormatWarning("Link: bad message from sidecar: {0}", e.Message);
                return;
            }

            switch ((string) msg["type"])
            {
                case "ping":
                    _peers = (int?) msg["peers"] ?? 0;
                    _lastPingTime = Now;
                    break;

                case "load_song":
                    HandleLoadSong(msg["song"] as JObject);
                    break;

                case "start":
                    if (_waiting)
                    {
                        double delay = ((double?) msg["delayMs"] ?? 0) / 1000.0;
                        _startAt = Now + delay;
                    }
                    break;

                case "peer":
                    HandlePeerData((string) msg["from"], msg["data"] as JObject);
                    break;

                case "peer_left":
                    YargLogger.LogFormatInfo("Link: friend {0} left", (string) msg["from"]);
                    break;

                case "cancel":
                    if (_waiting)
                    {
                        _cancelReason = (string) msg["reason"] ?? "cancelled";
                    }
                    else if (_remoteRequestHash != null)
                    {
                        _remoteRequestHash = null;
                        ToastManager.ToastWarning($"Online start cancelled: {(string) msg["reason"]}");
                    }
                    break;
            }
        }

        private void HandleLoadSong(JObject song)
        {
            string hash = (string) song?["hash"];
            string name = (string) song?["name"] ?? "?";
            if (string.IsNullOrEmpty(hash)) return;

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu || MenuManager.Instance == null)
            {
                Decline(hash, "busy");
                return;
            }

            if (hash.Length != HashWrapper.HASH_SIZE_IN_BYTES * 2 ||
                !SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(hash), out var entries) ||
                entries.Count == 0)
            {
                ToastManager.ToastWarning($"Your friend picked \"{name}\", which isn't in your library");
                Decline(hash, "song not in library");
                return;
            }

            var entry = entries[0];
            ToastManager.ToastInformation($"Your friend picked \"{entry.Name.Original}\"! Choose your part.");

            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.State.CurrentSong = entry;

            // Reopen difficulty select so it picks up the new song
            if (MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect)
            {
                MenuManager.Instance.PopMenu();
            }
            MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);

            _remoteRequestHash = hash;
        }

        /// <summary>
        /// If the player backs out of a friend's song, tell the sidecar so nobody waits on us.
        /// </summary>
        private void CheckRemoteRequestAbandoned()
        {
            if (_remoteRequestHash == null) return;

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                // Moved on to gameplay; the ready message takes it from here
                _remoteRequestHash = null;
                return;
            }

            if (MenuManager.Instance != null && MenuManager.Instance.CurrentMenu != MenuManager.Menu.DifficultySelect)
            {
                Decline(_remoteRequestHash, "backed out");
                _remoteRequestHash = null;
            }
        }

        private void HandlePeerData(string peer, JObject data)
        {
            if (string.IsNullOrEmpty(peer) || data == null) return;

            try
            {
                switch ((string) data["kind"])
                {
                    case "setup":
                        var setup = RemoteSetup.Parse(peer, data);
                        _remoteSetups[peer] = setup;
                        YargLogger.LogInfo($"Link: got {setup.Feeds.Count} player(s) from a friend for {setup.SongHash}");
                        break;

                    case "inputs":
                        if (_remoteSetups.TryGetValue(peer, out var existing))
                        {
                            existing.Receive(data, Now);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Link: bad data from a friend");
            }
        }

        /// <summary>
        /// Drop per-song state once we've left gameplay.
        /// </summary>
        private void CheckSessionEnded()
        {
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay) return;

            _gameManager = null;

            if (_session != null)
            {
                _session = null;

                // Setups that were used are done with; ones still waiting for us to join are kept
                var used = new List<string>();
                foreach (var (peer, setup) in _remoteSetups)
                {
                    if (setup.Taken) used.Add(peer);
                }
                foreach (var peer in used)
                {
                    _remoteSetups.Remove(peer);
                }
            }
        }

        private void Decline(string hash, string reason)
        {
            Send(new JObject
            {
                ["type"] = "decline",
                ["hash"] = hash,
                ["reason"] = reason,
            });
        }

        #endregion

        #region Status

        private void SendStatus()
        {
            var scene = GlobalVariables.Instance.CurrentScene;
            var status = new JObject
            {
                ["type"] = "status",
                ["scene"] = scene switch
                {
                    SceneIndex.Menu        => "menu",
                    SceneIndex.Gameplay    => "gameplay",
                    SceneIndex.Score       => "score",
                    SceneIndex.Calibration => "calibration",
                    _                      => scene.ToString().ToLowerInvariant(),
                },
                ["waiting"] = _waiting,
            };

            if (scene == SceneIndex.Gameplay)
            {
                if (_gameManager == null)
                {
                    _gameManager = FindAnyObjectByType<GameManager>();
                }

                if (_gameManager != null)
                {
                    AddGameplayStatus(status, _gameManager);
                }
            }
            else if (scene == SceneIndex.Score && GlobalVariables.State.CurrentSong != null)
            {
                status["song"] = SongJson(GlobalVariables.State.CurrentSong);
            }

            Send(status);
        }

        private static void AddGameplayStatus(JObject status, GameManager gm)
        {
            if (gm.Song != null)
            {
                status["song"] = SongJson(gm.Song);
            }

            status["paused"] = gm.Paused;

            if (!gm.IsSongStarted || gm.Players == null)
            {
                return;
            }

            status["songTime"] = gm.SongTime;

            var players = new JArray();
            foreach (var player in gm.Players)
            {
                if (player == null || player.Player == null || player.IsRemote) continue;

                var profile = player.Player.Profile;
                players.Add(new JObject
                {
                    ["name"]       = profile.Name,
                    ["instrument"] = profile.CurrentInstrument.ToString(),
                    ["difficulty"] = profile.CurrentDifficulty.ToString(),
                    ["score"]      = player.Score,
                    ["combo"]      = player.Combo,
                    ["stars"]      = player.Stars,
                    ["notesHit"]   = player.NotesHit,
                    ["totalNotes"] = player.BaseStats.TotalNotes,
                });
            }
            status["players"] = players;
        }

        private static JObject SongJson(SongEntry song)
        {
            return new JObject
            {
                ["hash"]   = song.Hash.ToString(),
                ["name"]   = song.Name.Original,
                ["artist"] = song.Artist.Original,
            };
        }

        #endregion

        #region Inputs

        private void SendInputs()
        {
            var gm = _session.GameManager;
            if (gm == null || !gm.IsSongStarted || _waiting)
            {
                return;
            }

            double sinceLast = Now - _session.LastSendTime;
            if (sinceLast < INPUTS_INTERVAL_MIN)
            {
                return;
            }

            bool anyNew = false;
            foreach (var player in _session.Players)
            {
                if (player != null && player.ReplayInputs.Count > _session.SentCounts[player])
                {
                    anyNew = true;
                    break;
                }
            }

            if (!anyNew && sinceLast < INPUTS_INTERVAL_MAX)
            {
                return;
            }

            _session.LastSendTime = Now;
            Send(new JObject
            {
                ["type"] = "broadcast",
                ["data"] = _session.CreateInputsMessage(gm),
            });
        }

        /// <summary>
        /// Our side of a synced song: which players we share and how many of their inputs we've sent.
        /// </summary>
        private class Session
        {
            public readonly GameManager      GameManager;
            public readonly string           SongHash;
            public readonly List<BasePlayer> Players = new();
            public readonly Dictionary<BasePlayer, int> SentCounts = new();
            public double LastSendTime = double.NegativeInfinity;

            private int _seq;

            public Session(GameManager gameManager, string songHash)
            {
                GameManager = gameManager;
                SongHash = songHash;

                foreach (var player in gameManager.Players)
                {
                    if (player.IsRemote) continue;

                    Players.Add(player);
                    SentCounts[player] = 0;
                }
            }

            public JObject CreateSetupMessage()
            {
                var frames = new List<ReplayFrame>();
                var colorProfiles = new Dictionary<Guid, ColorProfile>();
                var cameraPresets = new Dictionary<Guid, CameraPreset>();
                var rockMeterPresets = new Dictionary<Guid, RockMeterPreset>();

                var players = new JArray();
                foreach (var player in Players)
                {
                    var yargPlayer = player.Player;
                    var profile = yargPlayer.Profile;

                    int frameIndex = -1;
                    if (CanMirror(profile.GameMode))
                    {
                        try
                        {
                            var (frame, _) = player.ConstructReplayData();
                            frameIndex = frames.Count;
                            frames.Add(frame);

                            if (!yargPlayer.ColorProfile.DefaultPreset)
                                colorProfiles.TryAdd(yargPlayer.ColorProfile.Id, yargPlayer.ColorProfile);
                            if (!yargPlayer.CameraPreset.DefaultPreset)
                                cameraPresets.TryAdd(yargPlayer.CameraPreset.Id, yargPlayer.CameraPreset);
                            if (!yargPlayer.RockMeterPreset.DefaultPreset)
                                rockMeterPresets.TryAdd(yargPlayer.RockMeterPreset.Id, yargPlayer.RockMeterPreset);
                        }
                        catch (Exception e)
                        {
                            YargLogger.LogException(e, $"Link: couldn't share {profile.Name}'s setup");
                            frameIndex = -1;
                        }
                    }

                    players.Add(new JObject
                    {
                        ["name"]       = profile.Name,
                        ["instrument"] = profile.CurrentInstrument.ToString(),
                        ["difficulty"] = profile.CurrentDifficulty.ToString(),
                        ["frame"]      = frameIndex,
                    });
                }

                bool noFail = SettingsManager.Settings.NoFail.Value != NoFailMode.Off;
                var data = new ReplayData(colorProfiles, cameraPresets, rockMeterPresets, noFail,
                    frames.ToArray(), Array.Empty<double>());

                return new JObject
                {
                    ["kind"]    = "setup",
                    ["song"]    = SongHash,
                    ["players"] = players,
                    ["replay"]  = Convert.ToBase64String(data.Serialize().ToArray()),
                };
            }

            public JObject CreateInputsMessage(GameManager gm)
            {
                var players = new JArray();
                for (int i = 0; i < Players.Count; i++)
                {
                    var player = Players[i];
                    if (player == null) continue;

                    var inputs = player.ReplayInputs;
                    int from = SentCounts[player];
                    int count = inputs.Count - from;

                    var bytes = new byte[count * INPUT_SIZE];
                    using (var writer = new BinaryWriter(new MemoryStream(bytes)))
                    {
                        for (int j = from; j < inputs.Count; j++)
                        {
                            writer.Write(inputs[j].Time);
                            writer.Write(inputs[j].Action);
                            writer.Write(inputs[j].Integer);
                        }
                    }
                    SentCounts[player] = inputs.Count;

                    players.Add(new JObject
                    {
                        ["i"]          = i,
                        ["from"]       = from,
                        ["inputs"]     = Convert.ToBase64String(bytes),
                        ["score"]      = player.Score,
                        ["combo"]      = player.Combo,
                        ["stars"]      = player.Stars,
                        ["notesHit"]   = player.NotesHit,
                        ["totalNotes"] = player.BaseStats.TotalNotes,
                    });
                }

                return new JObject
                {
                    ["kind"]    = "inputs",
                    ["song"]    = SongHash,
                    ["seq"]     = _seq++,
                    // Every input up to this time has been recorded (before each player's calibration)
                    ["time"]    = gm.InputTime,
                    ["players"] = players,
                };
            }
        }

        /// <summary>
        /// Game modes whose players can be shown as a highway on friends' screens.
        /// </summary>
        private static bool CanMirror(GameMode mode)
        {
            // Vocals share one track and pro guitar has no replay support
            return mode is not (GameMode.Vocals or GameMode.ProGuitar);
        }

        /// <summary>
        /// A friend's players for one song.
        /// </summary>
        private class RemoteSetup
        {
            public string SongHash;
            public ReplayData Data;
            public readonly List<RemoteInputFeed> Feeds = new();
            /// <summary>Player index → frame index in <see cref="Data"/>, or -1.</summary>
            public readonly List<int> FrameIndices = new();
            /// <summary>Added to a song already.</summary>
            public bool Taken;

            public static RemoteSetup Parse(string peer, JObject msg)
            {
                var setup = new RemoteSetup { SongHash = (string) msg["song"] };

                var bytes = Convert.FromBase64String((string) msg["replay"]);
                using (var array = FixedArray.Read(new MemoryStream(bytes), bytes.Length))
                {
                    setup.Data = new ReplayData(array.ToValueStream(), ReplayIO.REPLAY_VERSIONS.CURRENT,
                        new ReplayReadOptions());
                }

                var players = (JArray) msg["players"];
                for (int i = 0; i < players.Count; i++)
                {
                    var p = (JObject) players[i];
                    int frame = (int?) p["frame"] ?? -1;
                    bool mirrored = frame >= 0 && frame < setup.Data.Frames.Length;

                    setup.FrameIndices.Add(mirrored ? frame : -1);
                    setup.Feeds.Add(new RemoteInputFeed(peer, i,
                        (string) p["name"] ?? "Friend",
                        (string) p["instrument"] ?? "",
                        (string) p["difficulty"] ?? "",
                        mirrored));
                }

                return setup;
            }

            public void Receive(JObject msg, double now)
            {
                if ((string) msg["song"] != SongHash) return;

                double time = (double) msg["time"];
                var inputs = new List<GameInput>();
                foreach (var token in (JArray) msg["players"])
                {
                    var p = (JObject) token;
                    int i = (int) p["i"];
                    if (i < 0 || i >= Feeds.Count) continue;

                    var feed = Feeds[i];
                    feed.SetStats(
                        (int?) p["score"] ?? 0,
                        (int?) p["combo"] ?? 0,
                        (float?) p["stars"] ?? 0f,
                        (int?) p["notesHit"] ?? 0,
                        (int?) p["totalNotes"] ?? 0);

                    inputs.Clear();
                    var bytes = Convert.FromBase64String((string) p["inputs"] ?? "");
                    using (var reader = new BinaryReader(new MemoryStream(bytes)))
                    {
                        for (int n = 0; n < bytes.Length / INPUT_SIZE; n++)
                        {
                            double t = reader.ReadDouble();
                            int action = reader.ReadInt32();
                            int value = reader.ReadInt32();
                            inputs.Add(new GameInput(t, action, value));
                        }
                    }

                    feed.Receive((int?) p["from"] ?? 0, inputs, time, now);
                }
            }
        }

        #endregion

        #region Scoreboard

        private void OnGUI()
        {
            if (_session == null || _client == null) return;
            if (GlobalVariables.Instance == null || GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay) return;

            var gm = _session.GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            var rows = new List<(string Name, string Detail, int Score, bool Local, bool Stalled)>();
            foreach (var player in _session.Players)
            {
                if (player == null) continue;
                rows.Add((player.Player.Profile.Name,
                    Detail(player.Stars, player.Combo, player.NotesHit, player.BaseStats.TotalNotes),
                    player.Score, true, false));
            }

            bool anyRemote = false;
            foreach (var setup in _remoteSetups.Values)
            {
                if (!setup.Taken) continue;
                foreach (var feed in setup.Feeds)
                {
                    anyRemote = true;
                    bool stalled = !gm.Paused && Now - feed.LastReceived > RemoteInputFeed.STALL_SECONDS;
                    rows.Add((feed.Name, Detail(feed.Stars, feed.Combo, feed.NotesHit, feed.TotalNotes),
                        feed.Score, false, stalled));
                }
            }

            if (!anyRemote) return;

            rows.Sort((a, b) => b.Score.CompareTo(a.Score));

            float scale = Screen.height / 1080f;
            if (_scoreboardStyle == null)
            {
                _scoreboardStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false };
            }
            _scoreboardStyle.fontSize = Mathf.RoundToInt(22 * scale);

            var text = new StringBuilder();
            foreach (var row in rows)
            {
                string name = row.Local ? $"<b>{Escape(row.Name)}</b>" : Escape(row.Name);
                string status = row.Stalled ? " <color=#ff8080>(connection lost)</color>" : "";
                text.AppendLine($"{name}  <b>{row.Score:N0}</b>{status}");
                text.AppendLine($"<size={Mathf.RoundToInt(16 * scale)}><color=#c0c0c0>{row.Detail}</color></size>");
            }

            var content = new GUIContent(text.ToString().TrimEnd());
            var size = _scoreboardStyle.CalcSize(content);
            float pad = 12 * scale;
            var box = new Rect(16 * scale, Screen.height * 0.3f, size.x + pad * 2, size.y + pad * 2);

            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = oldColor;
            GUI.Label(new Rect(box.x + pad, box.y + pad, size.x, size.y), content, _scoreboardStyle);
        }

        private static string Detail(float stars, int combo, int notesHit, int totalNotes)
        {
            string percent = totalNotes > 0 ? $"{100.0 * notesHit / totalNotes:0}%" : "-";
            return $"{stars:0.0}★  x{combo}  {notesHit}/{totalNotes} ({percent})";
        }

        private static string Escape(string text) => text.Replace("<", "<\u200B");

        #endregion

        #region Socket

        private void Open()
        {
            try
            {
                _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                // Only accept datagrams from the sidecar
                _client.Connect(IPAddress.Loopback, SIDECAR_PORT);
            }
            catch (SocketException e)
            {
                YargLogger.LogFormatError("Link: failed to open socket: {0}", e.Message);
                _client?.Dispose();
                _client = null;
                return;
            }

            var client = _client;
            _receiveThread = new Thread(() => ReceiveLoop(client))
            {
                IsBackground = true,
                Name = "YARG Link Receiver",
            };
            _receiveThread.Start();
            YargLogger.LogInfo("Link: enabled");
        }

        private void Close()
        {
            _client?.Dispose();
            _client = null;
            _receiveThread = null;
            _peers = 0;
            _lastPingTime = double.NegativeInfinity;
            _remoteRequestHash = null;
            _remoteSetups.Clear();
            _session = null;
        }

        private void ReceiveLoop(UdpClient client)
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try
                {
                    var bytes = client.Receive(ref remote);
                    _incoming.Enqueue(Encoding.UTF8.GetString(bytes));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.Interrupted || e.SocketErrorCode == SocketError.NotSocket)
                    {
                        return;
                    }

                    // "Connection refused/reset" just means the sidecar isn't running
                    Thread.Sleep(100);
                }
            }
        }

        private void Send(JObject msg)
        {
            if (_client == null) return;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(msg.ToString(Formatting.None));
                _client.Send(bytes, bytes.Length);
            }
            catch (SocketException)
            {
                // Sidecar not running; expected
            }
            catch (ObjectDisposedException)
            {
                // Closed concurrently
            }
        }

        #endregion
    }
}
