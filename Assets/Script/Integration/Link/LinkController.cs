using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Gameplay;
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
    /// <item>Game → sidecar: <c>status</c> (heartbeat, song, scores), <c>ready</c>, <c>decline</c>.</item>
    /// <item>Sidecar → game: <c>ping</c>, <c>load_song</c>, <c>start</c>, <c>cancel</c>.</item>
    /// </list>
    /// Enabled by Settings › Experimental › "Enable online link". When enabled and at least one friend
    /// is connected, gameplay holds at the loading screen until the sidecar sends a synced start.
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

            if (Now >= _nextStatusTime)
            {
                bool inGameplay = GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay;
                _nextStatusTime = Now + (inGameplay ? STATUS_INTERVAL_GAMEPLAY : STATUS_INTERVAL_IDLE);
                SendStatus();
            }
        }

        private void OnDestroy()
        {
            Close();
        }

        #region Synced start

        /// <summary>
        /// Report that gameplay is loaded, then wait until the sidecar says to start.
        /// Called by <see cref="GameManager"/> just before it enables gameplay.
        /// </summary>
        /// <returns><c>false</c> if the start was cancelled and the caller should return to the menu.</returns>
        public async UniTask<bool> WaitForSyncedStart(SongEntry song, LoadingContext context)
        {
            _waiting = true;
            _startAt = null;
            _cancelReason = null;
            _remoteRequestHash = null;

            Send(new JObject
            {
                ["type"] = "ready",
                ["song"] = SongJson(song),
            });

            context.SetLoadingText("Waiting for other players...", song.Name.Original);
            YargLogger.LogInfo("Link: waiting for synced start");

            double deadline = Now + MAX_WAIT_SECONDS;
            try
            {
                while (true)
                {
                    await UniTask.Yield();

                    if (_cancelReason != null)
                    {
                        ToastManager.ToastWarning($"Online start cancelled: {_cancelReason}");
                        return false;
                    }

                    if (_startAt.HasValue && Now >= _startAt.Value)
                    {
                        YargLogger.LogInfo("Link: synced start");
                        return true;
                    }

                    if (!SidecarAlive || Now >= deadline)
                    {
                        ToastManager.ToastWarning("Lost the online link, starting without sync");
                        return true;
                    }
                }
            }
            finally
            {
                _waiting = false;
                _startAt = null;
            }
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
                if (player == null || player.Player == null) continue;

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
