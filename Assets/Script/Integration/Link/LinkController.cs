using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cysharp.Threading.Tasks;
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
using YARG.Helpers;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;
using YARG.Menu;
using YARG.Menu.Persistent;
using YARG.Settings;
using YARG.Song;

namespace YARG.Integration.Link
{
    /// <summary>
    /// Online play with friends: the gameplay side of a <see cref="LinkSession"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enabled by Settings › Experimental › "Enable online link". When enabled and at least one other member
    /// is in the session, gameplay holds at the loading screen until the session schedules a synced start.
    /// </para>
    /// <para>
    /// During a synced song each game broadcasts a <c>setup</c> (its players' profiles and engine parameters,
    /// as replay data) when it's ready, then streams <c>inputs</c>: each player's recorded inputs plus their
    /// score. The other games add a highway per friend's player that plays those inputs back like a replay,
    /// so you see their notes being hit and missed, and show everyone's score in a scoreboard.
    /// </para>
    /// <para>
    /// Until there's a lobby, the session is set up from the command line (or environment variables, handy
    /// in the Editor). By default the game hosts, and the ticket to join it is logged, shown in a toast and
    /// copied to the clipboard. To join, pass <c>-link-join &lt;ticket&gt;</c> (<c>YARG_LINK_JOIN</c>).
    /// <c>-link-name</c> (<c>YARG_LINK_NAME</c>) sets the name friends see.
    /// </para>
    /// <para>
    /// The transport is <see cref="NativeLinkTransport"/> (iroh: hole punching, relays, encryption) unless
    /// <c>-link-transport loopback</c> (<c>YARG_LINK_TRANSPORT</c>) or a <c>loopback:</c> ticket picks
    /// <see cref="LoopbackLinkTransport"/>, which only reaches this machine or the LAN. Hosting falls back
    /// to loopback when this platform has no native library. For loopback, <c>-link-port</c>
    /// (<c>YARG_LINK_PORT</c>) picks the port (<see cref="DEFAULT_PORT"/> by default) and <c>-link-lan</c>
    /// (<c>YARG_LINK_LAN=1</c>) accepts connections from the local network.
    /// </para>
    /// </remarks>
    public class LinkController : MonoBehaviour
    {
        public const int DEFAULT_PORT = 36120;

        /// <summary>Give up on a synced start after this long and start alone.</summary>
        private const double MAX_WAIT_SECONDS = 120.0;

        /// <summary>A joiner tries to reach the host again this often.</summary>
        private const double REJOIN_INTERVAL = 5.0;

        /// <summary>Send new inputs at most this often.</summary>
        private const double INPUTS_INTERVAL_MIN = 1.0 / 120.0;

        /// <summary>Send at least this often, so friends' tracks keep moving when nothing is pressed.</summary>
        private const double INPUTS_INTERVAL_MAX = 1.0 / 30.0;

        /// <summary>Bytes per input in the <c>inputs</c> message: time (double), action (int), value (int).</summary>
        private const int INPUT_SIZE = 16;

        private const string KIND_SETUP  = "setup";
        private const string KIND_INPUTS = "inputs";

        public static LinkController Instance { get; private set; }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double Now => Clock.Elapsed.TotalSeconds;

        private LinkSession _session;
        private string      _joinTicket;
        private double      _nextOpenTime;
        private double      _nextJoinTime = double.PositiveInfinity;
        private string      _lastProblem;

        // Synced-start state, touched only on the main thread
        private bool    _waiting;
        private double? _startAt;
        private string  _cancelReason;

        // How we last saw each directly connected member's connection, to log changes
        private readonly Dictionary<string, LinkPath> _paths = new();
        private double _nextPathCheck;

        // Song a friend asked us to load, while we're still in difficulty select
        private string _remoteRequestHash;

        // Friends' player setups, by member ID. Kept until we leave the song they were for.
        private readonly Dictionary<string, RemoteSetup> _remoteSetups = new();

        // The synced song we're sharing our inputs for, if any
        private SharedSong _shared;

        private GUIStyle _scoreboardStyle;

        /// <summary>
        /// Whether gameplay should hold at the loading screen for a synced start.
        /// </summary>
        public bool ShouldHoldForStart => _session is { IsConnected: true } && _session.Members.Count > 0;

        /// <summary>The current session, if the link is enabled.</summary>
        public LinkSession CurrentSession => _session;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(LinkController));
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LinkController>();
            Application.quitting += () => Instance?.Close();
        }

        private void Update()
        {
            if (GlobalVariables.Instance == null) return;

            bool linkEnabled = SettingsManager.Settings?.LinkEnable.Value ?? false;
            if (!linkEnabled)
            {
                if (_session != null) Close();
                _nextOpenTime = 0;
                return;
            }

            if (_session == null)
            {
                if (Now < _nextOpenTime) return;
                Open();
                if (_session == null) return;
            }

            if (_joinTicket != null && !_session.IsConnected && !_session.IsJoining && Now >= _nextJoinTime)
            {
                _nextJoinTime = Now + REJOIN_INTERVAL;
                Join();
            }

            _session.SetLocalScene(SceneName(GlobalVariables.Instance.CurrentScene));
            _session.Update();
            LogPathChanges();

            CheckRemoteRequestAbandoned();
            CheckSongEnded();
        }

        private void LateUpdate()
        {
            // After GameManager.Update, so this frame's inputs are recorded
            if (_session != null && _shared != null)
            {
                SendInputs();
            }
        }

        private void OnDestroy()
        {
            Close();
        }

        #region Session

        private void Open()
        {
            // Without a working setup, don't retry every frame
            _nextOpenTime = Now + REJOIN_INTERVAL;

            string join = CommandLineArgs.LinkJoin ?? Environment.GetEnvironmentVariable("YARG_LINK_JOIN");
            int? port = CommandLineArgs.LinkPort;
            if (port == null && int.TryParse(Environment.GetEnvironmentVariable("YARG_LINK_PORT"), out int envPort))
            {
                port = envPort;
            }
            bool lan = CommandLineArgs.LinkLan || Environment.GetEnvironmentVariable("YARG_LINK_LAN") == "1";
            string name = CommandLineArgs.LinkName ?? Environment.GetEnvironmentVariable("YARG_LINK_NAME") ??
                Environment.UserName;

            var role = string.IsNullOrWhiteSpace(join) ? LinkRole.Host : LinkRole.Join;

            string kind = CommandLineArgs.LinkTransport ?? Environment.GetEnvironmentVariable("YARG_LINK_TRANSPORT");
            if (string.IsNullOrWhiteSpace(kind))
            {
                kind = join != null && join.Trim().StartsWith(LoopbackLinkTransport.TICKET_PREFIX,
                    StringComparison.OrdinalIgnoreCase) ? "loopback" : "native";
            }

            ILinkTransport transport = null;
            if (!kind.Equals("loopback", StringComparison.OrdinalIgnoreCase))
            {
                transport = CreateNativeTransport(out string problem);
                if (transport == null && role == LinkRole.Join)
                {
                    YargLogger.LogFormatError("Link: {0}", problem);
                    ToastManager.ToastError("Online link isn't available on this platform yet");
                    // Retrying won't help; toggling the setting tries again
                    _nextOpenTime = double.PositiveInfinity;
                    return;
                }
                if (transport == null)
                {
                    YargLogger.LogWarning($"Link: {problem}; hosting on the loopback transport instead");
                }
            }

            try
            {
                transport ??= CreateTransport(port ?? (role == LinkRole.Host ? DEFAULT_PORT : 0), lan);
            }
            catch (SocketException e) when (port == null && role == LinkRole.Host)
            {
                // Probably another instance on this machine; host on any free port instead
                YargLogger.LogWarning($"Link: port {DEFAULT_PORT} is in use ({e.Message}), using another one");
                try
                {
                    transport = CreateTransport(0, lan);
                }
                catch (SocketException e2)
                {
                    YargLogger.LogFormatError("Link: couldn't open a socket: {0}", e2.Message);
                    return;
                }
            }
            catch (SocketException e)
            {
                YargLogger.LogFormatError("Link: couldn't open a socket: {0}", e.Message);
                ToastManager.ToastError($"Online link: couldn't open port {port}");
                return;
            }

            var options = new LinkSessionOptions
            {
                GameVersion = GlobalVariables.Instance.CurrentVersion,
                Name = name,
            };
            _session = new LinkSession(transport, role, options, () => Now)
            {
                LogInfo = message => YargLogger.LogInfo(message),
                LogWarning = message => YargLogger.LogWarning(message),
            };

            _session.MemberJoined += OnMemberJoined;
            _session.MemberLeft += OnMemberLeft;
            _session.LoadSongRequested += HandleLoadSong;
            _session.StartScheduled += OnStartScheduled;
            _session.StartCancelled += OnStartCancelled;
            _session.GameDataReceived += HandleGameData;
            _session.Disconnected += OnDisconnected;

            if (role == LinkRole.Host)
            {
                YargLogger.LogInfo($"Link: hosting as {name}. Friends join with: -link-join {_session.LocalTicket}");
                GUIUtility.systemCopyBuffer = _session.LocalTicket;
                ToastManager.ToastInformation("Hosting an online session. The invite code is on your clipboard.");
            }
            else
            {
                _joinTicket = join.Trim();
                _nextJoinTime = Now;
            }
        }

        /// <summary>The iroh transport, or <c>null</c> (with why) if it can't be used here.</summary>
        private static NativeLinkTransport CreateNativeTransport(out string problem)
        {
            string path;
            try
            {
                path = NativeLinkLibrary.GetPath();
            }
            catch (IOException e)
            {
                problem = $"couldn't prepare the YargLink library: {e.Message}";
                return null;
            }

            if (path == null)
            {
                problem = "there's no YargLink library for this platform";
                return null;
            }

            try
            {
                var transport = new NativeLinkTransport(path, new NativeLinkConfig
                {
                    // The same identity every time, so an invite code keeps working
                    KeyFile = Path.Combine(PathHelper.PersistentDataPath, "link", "secret.key"),
                });
                transport.Log = LogNative;
                problem = null;
                return transport;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                or InvalidOperationException)
            {
                problem = e.Message;
                return null;
            }
        }

        private static void LogNative(NativeLogLevel level, string message)
        {
            switch (level)
            {
                case NativeLogLevel.Error:
                    YargLogger.LogError($"Link (native): {message}");
                    break;
                case NativeLogLevel.Warning:
                    YargLogger.LogWarning($"Link (native): {message}");
                    break;
                case NativeLogLevel.Info:
                    YargLogger.LogInfo($"Link (native): {message}");
                    break;
                default:
                    YargLogger.LogDebug($"Link (native): {message}");
                    break;
            }
        }

        private static LoopbackLinkTransport CreateTransport(int port, bool lan)
        {
            return lan
                ? new LoopbackLinkTransport(port, IPAddress.Any, FindLanAddress())
                : new LoopbackLinkTransport(port);
        }

        private static string FindLanAddress()
        {
            try
            {
                foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    {
                        return address.ToString();
                    }
                }
            }
            catch (SocketException)
            {
                // Fall back to loopback
            }

            return null;
        }

        private void Join()
        {
            try
            {
                _session.Join(_joinTicket);
            }
            catch (FormatException e)
            {
                YargLogger.LogFormatError("Link: {0}", e.Message);
                ToastManager.ToastError("Online link: that isn't a valid invite");
                _joinTicket = null;
            }
        }

        /// <summary>
        /// Log when a connection switches between direct and relayed, which says a lot about how a
        /// session will feel.
        /// </summary>
        private void LogPathChanges()
        {
            if (Now < _nextPathCheck) return;
            _nextPathCheck = Now + 2.0;

            foreach (var member in _session.Members)
            {
                if (member.Peer == null) continue;

                var stats = _session.GetStats(member);
                if (!stats.Connected) continue;
                if (_paths.TryGetValue(member.Id, out var last) && last == stats.Path) continue;

                _paths[member.Id] = stats.Path;
                string rtt = stats.Rtt is { } r ? $"{r.TotalMilliseconds:0} ms" : "unknown";
                YargLogger.LogInfo($"Link: connection to {member.Name} is {stats.Path}, round trip {rtt}");
            }
        }

        private void Close()
        {
            _session?.Dispose();
            _session = null;
            _joinTicket = null;
            _nextOpenTime = 0;
            _nextJoinTime = double.PositiveInfinity;
            _lastProblem = null;
            _remoteRequestHash = null;
            _remoteSetups.Clear();
            _paths.Clear();
            _shared = null;
        }

        private void OnMemberJoined(LinkMember member)
        {
            _lastProblem = null;
            ToastManager.ToastInformation($"{member.Name} is in the online session");
        }

        private void OnMemberLeft(LinkMember member)
        {
            // A friend who is on a highway right now stays there, shown as stalled
            if (_remoteSetups.TryGetValue(member.Id, out var setup) && !setup.Taken)
            {
                _remoteSetups.Remove(member.Id);
            }

            if (_session is { IsConnected: true })
            {
                ToastManager.ToastInformation($"{member.Name} left the online session");
            }
        }

        private void OnDisconnected(string reason)
        {
            // A joiner keeps trying, but only says so when something changes
            _nextJoinTime = Now + REJOIN_INTERVAL;
            if (reason != _lastProblem)
            {
                _lastProblem = reason;
                ToastManager.ToastWarning($"Online session: {reason}");
            }
        }

        private static string SceneName(SceneIndex scene) => scene switch
        {
            SceneIndex.Menu        => LinkSession.SCENE_MENU,
            SceneIndex.Gameplay    => "gameplay",
            SceneIndex.Score       => "score",
            SceneIndex.Calibration => "calibration",
            _                      => scene.ToString().ToLowerInvariant(),
        };

        private static SongInfo ToSongInfo(SongEntry song) =>
            new(song.Hash.ToString(), song.Name.Original, song.Artist.Original);

        #endregion

        #region Synced start

        public enum StartResult
        {
            /// <summary>A synced start is scheduled; call <see cref="WaitForStartTime"/>.</summary>
            Synced,
            /// <summary>The session is gone or took too long; start right away.</summary>
            Alone,
            /// <summary>The start was cancelled; return to the menu.</summary>
            Cancelled,
        }

        /// <summary>
        /// Report that gameplay is loaded, then wait until the session schedules the start.
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
            _shared = null;

            // Forget setups for any other song
            string hash = song.Hash.ToString();
            var stale = new List<string>();
            foreach (var (member, setup) in _remoteSetups)
            {
                if (setup.SongHash != hash) stale.Add(member);
            }
            foreach (var member in stale)
            {
                _remoteSetups.Remove(member);
            }

            // Tell friends about our players first, so they have it by the time the start is scheduled
            var shared = new SharedSong(gameManager, hash);
            _session.Broadcast(KIND_SETUP, shared.CreateSetupMessage());
            _session.ReportReady(ToSongInfo(song));

            context.SetLoadingText("Waiting for other players...", song.Name.Original);
            YargLogger.LogInfo("Link: waiting for synced start");

            double deadline = Now + MAX_WAIT_SECONDS;
            while (true)
            {
                if (_cancelReason != null)
                {
                    ToastManager.ToastWarning($"Online start cancelled: {_cancelReason}");
                    _waiting = false;
                    return StartResult.Cancelled;
                }

                if (_startAt.HasValue)
                {
                    _shared = shared;
                    return StartResult.Synced;
                }

                if (_session is not { IsConnected: true } || Now >= deadline)
                {
                    ToastManager.ToastWarning("Lost the online session, starting without sync");
                    _waiting = false;
                    return StartResult.Alone;
                }

                await UniTask.Yield();
            }
        }

        /// <summary>
        /// Wait for the moment scheduled by <see cref="WaitForStartSignal"/>.
        /// </summary>
        public async UniTask WaitForStartTime()
        {
            try
            {
                while (_startAt.HasValue && Now < _startAt.Value && _session != null)
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

        private void OnStartScheduled(double delaySeconds)
        {
            if (_waiting)
            {
                _startAt = Now + delaySeconds;
            }
        }

        private void OnStartCancelled(string reason)
        {
            if (_waiting)
            {
                _cancelReason = reason;
            }
            else if (_remoteRequestHash != null)
            {
                _remoteRequestHash = null;
                ToastManager.ToastWarning($"Online start cancelled: {reason}");
            }
        }

        #endregion

        #region Incoming

        private void HandleLoadSong(SongInfo song)
        {
            string hash = song.Hash;
            string name = song.Name ?? "?";

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu || MenuManager.Instance == null)
            {
                _session.ReportDecline(hash, "busy");
                return;
            }

            if (hash.Length != HashWrapper.HASH_SIZE_IN_BYTES * 2 ||
                !SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(hash), out var entries) ||
                entries.Count == 0)
            {
                ToastManager.ToastWarning($"Your friend picked \"{name}\", which isn't in your library");
                _session.ReportDecline(hash, "song not in library");
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
        /// If the player backs out of a friend's song, decline it so nobody waits on us.
        /// </summary>
        private void CheckRemoteRequestAbandoned()
        {
            if (_remoteRequestHash == null) return;

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                // Moved on to gameplay; reporting ready takes it from here
                _remoteRequestHash = null;
                return;
            }

            if (MenuManager.Instance != null && MenuManager.Instance.CurrentMenu != MenuManager.Menu.DifficultySelect)
            {
                _session.ReportDecline(_remoteRequestHash, "backed out");
                _remoteRequestHash = null;
            }
        }

        private void HandleGameData(LinkMember member, string kind, JObject data)
        {
            if (data == null) return;

            try
            {
                switch (kind)
                {
                    case KIND_SETUP:
                        var setup = RemoteSetup.Parse(member.Id, data);
                        _remoteSetups[member.Id] = setup;
                        YargLogger.LogInfo($"Link: got {setup.Feeds.Count} player(s) from {member.Name} for {setup.SongHash}");
                        break;

                    case KIND_INPUTS:
                        if (_remoteSetups.TryGetValue(member.Id, out var existing))
                        {
                            existing.Receive(data, Now);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Link: bad {kind} data from {member.Name}");
            }
        }

        /// <summary>
        /// Drop per-song state once we've left gameplay.
        /// </summary>
        private void CheckSongEnded()
        {
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay) return;
            if (_shared == null) return;

            _shared = null;

            // Setups that were used are done with; ones still waiting for us to join are kept
            var used = new List<string>();
            foreach (var (member, setup) in _remoteSetups)
            {
                if (setup.Taken) used.Add(member);
            }
            foreach (var member in used)
            {
                _remoteSetups.Remove(member);
            }
        }

        #endregion

        #region Inputs

        private void SendInputs()
        {
            var gm = _shared.GameManager;
            if (gm == null || !gm.IsSongStarted || _waiting)
            {
                return;
            }

            double sinceLast = Now - _shared.LastSendTime;
            if (sinceLast < INPUTS_INTERVAL_MIN)
            {
                return;
            }

            bool anyNew = false;
            foreach (var player in _shared.Players)
            {
                if (player != null && player.ReplayInputs.Count > _shared.SentCounts[player])
                {
                    anyNew = true;
                    break;
                }
            }

            if (!anyNew && sinceLast < INPUTS_INTERVAL_MAX)
            {
                return;
            }

            _shared.LastSendTime = Now;

            // Reliable: each message only carries the inputs since the last one
            _session.Broadcast(KIND_INPUTS, _shared.CreateInputsMessage(gm));
        }

        /// <summary>
        /// Our side of a synced song: which players we share and how many of their inputs we've sent.
        /// </summary>
        private class SharedSong
        {
            public readonly GameManager      GameManager;
            public readonly string           SongHash;
            public readonly List<BasePlayer> Players = new();
            public readonly Dictionary<BasePlayer, int> SentCounts = new();
            public double LastSendTime = double.NegativeInfinity;

            private int _seq;

            public SharedSong(GameManager gameManager, string songHash)
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

            public static RemoteSetup Parse(string member, JObject msg)
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
                    setup.Feeds.Add(new RemoteInputFeed(member, i,
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
            if (_shared == null || _session == null) return;
            if (GlobalVariables.Instance == null || GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay) return;

            var gm = _shared.GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            var rows = new List<(string Name, string Detail, int Score, bool Local, bool Stalled)>();
            foreach (var player in _shared.Players)
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

        private static string Escape(string text) => text.Replace("<", "<​");

        #endregion
    }
}
