using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using YARG.Player;
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
    /// Sessions are hosted and joined from the lobby (<see cref="LinkLobby"/>, "Online" in the main menu). The
    /// host's invite code is copied to the clipboard; friends paste it to join.
    /// </para>
    /// <para>
    /// For development, launch options can host or join at startup. Environment variables do the same, and
    /// are easier to use in the Editor. Only Editor builds and test builds read these options:
    /// <list type="bullet">
    /// <item><c>-link-host</c> (<c>YARG_LINK_HOST=1</c>) hosts a session.</item>
    /// <item><c>-link-join &lt;ticket&gt;</c> (<c>YARG_LINK_JOIN</c>) joins a session.</item>
    /// <item><c>-link-name</c> (<c>YARG_LINK_NAME</c>) sets the name that friends see. The default is the
    /// profile name of the first player.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The host's fail meter is the band's: see <see cref="UpdateBandMeter"/>.
    /// </para>
    /// <para>
    /// The transport is always <see cref="NativeLinkTransport"/> (iroh: hole punching, relays, encryption).
    /// Without the YargLink library for this platform, online play isn't available.
    /// </para>
    /// </remarks>
    public partial class LinkController : MonoBehaviour
    {
        /// <summary>Give up on a synced start after this long and start alone.</summary>
        private const double MAX_WAIT_SECONDS = 120.0;

        /// <summary>A joiner tries to reach the host again this often.</summary>
        private const double REJOIN_INTERVAL = 5.0;

        /// <summary>How often our status and players are checked for changes, for friends' lobbies.</summary>
        private const double STATE_INTERVAL = 0.25;

        /// <summary>Send new inputs at most this often.</summary>
        private const double INPUTS_INTERVAL_MIN = 1.0 / 120.0;

        /// <summary>Send at least this often, so friends' tracks keep moving when nothing is pressed.</summary>
        private const double INPUTS_INTERVAL_MAX = 1.0 / 30.0;

        /// <summary>Bytes per input in the <c>inputs</c> message: time (double), action (int), value (int).</summary>
        private const int INPUT_SIZE = 16;

        /// <summary>
        /// Inputs per player in one <c>inputs</c> message. A backlog (after a hitch, say) goes out over several
        /// messages, so no message gets near the transport's size limit; friends drop messages with more.
        /// </summary>
        private const int MAX_INPUTS_PER_MESSAGE = 4096;

        /// <summary>Input and song times further from zero than this (a day) are nonsense, whoever sent them.</summary>
        private const double MAX_INPUT_TIME = 24 * 60 * 60;

        private const string KIND_SETUP    = "setup";
        private const string KIND_INPUTS   = "inputs";
        private const string KIND_FAILED   = "failed";
        private const string KIND_DECLINED = "declined";
        private const string KIND_LEFT     = "left";

        // Member statuses, as shown in the lobby (see LinkSession.SetLocalState)
        public const string STATUS_BROWSING = "browsing";
        public const string STATUS_CHOOSING = "choosing";
        public const string STATUS_READY    = "ready";
        public const string STATUS_PLAYING  = "playing";
        public const string STATUS_RESULTS  = "results";

        /// <summary>The scene a member in a song reports (see <see cref="SceneName"/>).</summary>
        private const string SCENE_GAMEPLAY = "gameplay";

        public static LinkController Instance { get; private set; }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double Now => Clock.Elapsed.TotalSeconds;

        private LinkSession _session;

        /// <summary>Joiner only: the host's invite code.</summary>
        private string _joinTicket;
        private double _nextJoinTime = double.PositiveInfinity;
        private string _lastProblem;

        /// <summary>Launch options (or environment variables) can open a session once, at startup.</summary>
        private bool _launchOptionsUsed;

        private double _nextStateTime;

        // Synced-start state, touched only on the main thread
        private bool    _waiting;
        private double? _startAt;
        private string  _cancelReason;

        // How we last saw each directly connected member's connection, to log changes
        private readonly Dictionary<string, LinkPath> _paths = new();
        private double _nextPathCheck;

        // Song a friend asked us to load, while we're still in difficulty select
        private string _remoteRequestHash;
        private string _remoteRequestName;

        // Friends' latest player setups not yet added to a song, by member ID: kept for us to join
        private readonly Dictionary<string, RemoteSetup> _remoteSetups = new();

        // Friends' setups added to the synced song we're in (_shared), by member ID. Until that song ends
        private readonly Dictionary<string, RemoteSetup> _songSetups = new();

        // The synced song we're sharing our inputs for, if any
        private SharedSong _shared;

        private GUIStyle _scoreboardStyle;

        /// <summary>Whether Settings › Experimental › "Enable online link" is on.</summary>
        public static bool IsEnabled => SettingsManager.Settings?.LinkEnable.Value ?? false;

        /// <summary>
        /// Whether gameplay should hold at the loading screen for a synced start.
        /// </summary>
        public bool ShouldHoldForStart => _session is { IsConnected: true } && _session.Members.Count > 0;

        /// <summary>The current session, if we're hosting or joining one.</summary>
        public LinkSession CurrentSession => _session;

        /// <summary>
        /// The code friends use to join: ours when hosting, the host's when joining. <c>null</c> without a session.
        /// </summary>
        public string InviteCode => _session == null ? null
            : _session.Role == LinkRole.Host ? _session.LocalTicket : _joinTicket;

        /// <summary>
        /// The invite code of the last session we hosted, so the lobby doesn't offer to join it from the clipboard.
        /// </summary>
        public string LastHostedInviteCode { get; private set; }

        /// <summary>Joiner only: not (or no longer) connected to the host, and trying.</summary>
        public bool IsConnecting => _session is { Role: LinkRole.Join, IsConnected: false };

        /// <summary>Joiner only: why the last attempt to reach the host failed, if it did.</summary>
        public string ConnectProblem => IsConnecting ? _lastProblem : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(LinkController));
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LinkController>();
            Application.quitting += () =>
            {
                Instance?.Close();
                // Closing happens off the main thread; let the goodbyes go out before the process ends
                NativeLinkTransport.WaitForClosedHandles(TimeSpan.FromSeconds(3));
            };
        }

        private void Update()
        {
            if (GlobalVariables.Instance == null) return;

            if (!IsEnabled)
            {
                if (_session != null) Close();
                _hostGone = null;
                LinkLobby.CloseIfOpen();
                return;
            }

            LinkLobby.Refresh();

            if (_session == null && !_launchOptionsUsed)
            {
                _launchOptionsUsed = true;
                OpenFromLaunchOptions();
            }

            if (_session == null)
            {
                // A song whose session ended still counts down to leaving
                UpdateHostLoss();
                return;
            }

            if (_joinTicket != null && !_session.IsConnected && !_session.IsJoining && Now >= _nextJoinTime)
            {
                _nextJoinTime = Now + REJOIN_INTERVAL;
                ConnectToHost();
                if (_session == null) return;
            }

            _session.SetLocalScene(SceneName(GlobalVariables.Instance.CurrentScene));
            UpdateLocalState();
            _session.Update();
            if (_session == null) return;

            LogPathChanges();
            CheckRemoteRequestAbandoned();
            CheckSongEnded();
            UpdateBandMeter();
            UpdatePauseSync();
            UpdateClockSync();
            UpdateDepartedFriends();
            UpdateHostLoss();
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

        /// <summary>
        /// Host a session. The invite code is copied to the clipboard.
        /// </summary>
        /// <returns>Whether a session is open.</returns>
        public bool Host()
        {
            Close();
            if (!Open(LinkRole.Host, null)) return false;

            LastHostedInviteCode = _session.LocalTicket;
            CopyInviteCode();
            ToastManager.ToastInformation("Hosting an online session. The invite code is on your clipboard.");
            return true;
        }

        /// <summary>
        /// Join the session with this invite code. Connecting carries on in the background, and is retried if
        /// the connection drops.
        /// </summary>
        /// <returns>Whether the code was accepted (not whether we're connected yet).</returns>
        public bool Join(string ticket)
        {
            ticket = ticket?.Trim();
            if (string.IsNullOrEmpty(ticket))
            {
                ShowProblem("Couldn't join", "Paste the invite code your friend copied when they started hosting.");
                return false;
            }

            Close();
            if (!Open(LinkRole.Join, ticket)) return false;

            _joinTicket = ticket;
            ConnectToHost();
            _nextJoinTime = Now + REJOIN_INTERVAL;
            return _session != null;
        }

        /// <summary>Leave the session (and end it, for the host).</summary>
        public void Leave()
        {
            if (_session == null) return;

            bool host = _session.Role == LinkRole.Host;
            Close();
            ToastManager.ToastInformation(host ? "You ended the online session" : "You left the online session");
        }

        /// <summary>
        /// Host only: remove a friend from the session. The session refuses them if they come back as the same
        /// game, but a modified game can still get back in with the invite code; see <see cref="KickAndChangeCode"/>.
        /// </summary>
        public void Kick(LinkMember member)
        {
            if (_session is not { Role: LinkRole.Host }) return;
            _session.Kick(member);
        }

        /// <summary>
        /// Host only: remove a friend, then start a new session with a new invite code (copied to the clipboard),
        /// so the old code stops working for anyone. Everyone else leaves too, and joins again with the new code.
        /// </summary>
        public void KickAndChangeCode(LinkMember member)
        {
            if (_session is not { Role: LinkRole.Host }) return;
            _session.Kick(member);
            if (Host())
            {
                ToastManager.ToastInformation("Started a new session. Send your other friends the new invite code.");
            }
        }

        public void CopyInviteCode()
        {
            if (InviteCode == null) return;
            GUIUtility.systemCopyBuffer = InviteCode;
        }

        /// <summary>
        /// Launch options for development (two instances without touching the lobby): <c>-link-join</c> joins,
        /// <c>-link-host</c> hosts.
        /// </summary>
        private void OpenFromLaunchOptions()
        {
#if UNITY_EDITOR || YARG_TEST_BUILD
            string join = CommandLineArgs.LinkJoin ?? Environment.GetEnvironmentVariable("YARG_LINK_JOIN");
            bool host = CommandLineArgs.LinkHost || Environment.GetEnvironmentVariable("YARG_LINK_HOST") == "1";

            if (!string.IsNullOrWhiteSpace(join))
            {
                Join(join);
            }
            else if (host)
            {
                Host();
            }
#endif
        }

        private bool Open(LinkRole role, string join)
        {
            ILinkTransport transport = CreateNativeTransport(role, out string problem);
            if (transport == null)
            {
                YargLogger.LogFormatError("Link: {0}", problem);
                ShowProblem("Online play isn't available", "Online play isn't available on this platform yet.");
                return false;
            }

#if UNITY_EDITOR || YARG_TEST_BUILD
            string name = CommandLineArgs.LinkName ?? Environment.GetEnvironmentVariable("YARG_LINK_NAME") ??
                DefaultName();
#else
            string name = DefaultName();
#endif
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
            _session.PeerRefused += OnPeerRefused;

            // Our state goes out with the hello's follow-ups, so friends see it straight away
            _nextStateTime = 0;
            UpdateLocalState();

            if (role == LinkRole.Host)
            {
                // Not the invite code itself: logs get posted publicly when people ask for help
                YargLogger.LogInfo($"Link: hosting as {name}, invite code {LinkSession.ShortTicket(_session.LocalTicket)}");
            }
            return true;
        }

        /// <summary>The name friends see: the first player's profile, or the computer's user.</summary>
        private static string DefaultName()
        {
            foreach (var player in PlayerContainer.Players)
            {
                if (!player.Profile.IsBot && !string.IsNullOrWhiteSpace(player.Profile.Name))
                {
                    return player.Profile.Name;
                }
            }
            return Environment.UserName;
        }

        /// <summary>The iroh transport, or <c>null</c> (with why) if it can't be used here.</summary>
        /// <remarks>
        /// Every transport gets a new identity, so a host's invite code stops working when its session ends, and
        /// a joiner can't be recognised from one session to the next.
        /// </remarks>
        private static NativeLinkTransport CreateNativeTransport(LinkRole role, out string problem)
        {
            DeleteOldKeyFile();

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
                    // A joiner only talks to its host, so nobody else gets to connect to it
                    Accept = role == LinkRole.Host,
                    MaxPeers = TransportMaxPeers,
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

        private static bool _oldKeyFileChecked;

        /// <summary>
        /// Earlier builds kept one identity in <c>link/secret.key</c>, so their invite code worked forever. The key
        /// is a credential (whoever has it can pose as that host), and nothing uses it any more, so remove it.
        /// </summary>
        private static void DeleteOldKeyFile()
        {
            if (_oldKeyFileChecked) return;
            _oldKeyFileChecked = true;

            string dir = Path.Combine(PathHelper.PersistentDataPath, "link");
            foreach (string name in new[] { "secret.key", "secret.key.lock", "secret.tmp" })
            {
                string path = Path.Combine(dir, name);
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    YargLogger.LogInfo($"Link: deleted {name}, which older versions used for a permanent invite code");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    YargLogger.LogWarning($"Link: couldn't delete the old {name}: {e.Message}");
                }
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

        /// <summary>
        /// Connections a transport takes at once: every joiner the session admits, plus room for a few still
        /// saying hello (which the session caps itself).
        /// </summary>
        private static int TransportMaxPeers
        {
            get
            {
                var defaults = new LinkSessionOptions();
                return defaults.MaxMembers + defaults.MaxUnadmittedPeers;
            }
        }

        private void ConnectToHost()
        {
            try
            {
                _session.Join(_joinTicket);
            }
            catch (FormatException e)
            {
                YargLogger.LogFormatError("Link: {0}", e.Message);
                Close();
                ShowProblem("Couldn't join", "That isn't a valid invite code. Ask your friend to copy it again.");
            }
        }

        /// <summary>
        /// What we're doing and who's playing, for friends' lobbies. The session only sends changes.
        /// </summary>
        private void UpdateLocalState()
        {
            if (Now < _nextStateTime) return;
            _nextStateTime = Now + STATE_INTERVAL;

            var players = new List<PlayerInfo>();
            foreach (var player in PlayerContainer.Players)
            {
                if (player.SittingOut) continue;

                var profile = player.Profile;
                players.Add(new PlayerInfo
                {
                    Name = profile.Name,
                    Instrument = profile.CurrentInstrument.ToString(),
                    Difficulty = profile.CurrentDifficulty.ToString(),
                    Bot = profile.IsBot,
                });
            }

            _session.SetLocalState(LocalStatus(), players);
        }

        private string LocalStatus()
        {
            switch (GlobalVariables.Instance.CurrentScene)
            {
                case SceneIndex.Gameplay:
                    if (_waiting && !_startAt.HasValue) return STATUS_READY;
                    return IsSyncedPauseActive ? STATUS_PAUSED : STATUS_PLAYING;
                case SceneIndex.Score:
                    return STATUS_RESULTS;
                case SceneIndex.Menu:
                    bool choosing = _remoteRequestHash != null ||
                        MenuManager.Instance != null && MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect;
                    return choosing ? STATUS_CHOOSING : STATUS_BROWSING;
                default:
                    return SceneName(GlobalVariables.Instance.CurrentScene);
            }
        }

        /// <summary>A member's status for people, for example "Ready" or "Picking a part".</summary>
        public static string DescribeStatus(string status) => status switch
        {
            STATUS_BROWSING => "Browsing",
            STATUS_CHOOSING => "Picking a part",
            STATUS_READY    => "Ready",
            STATUS_PLAYING  => "Playing",
            STATUS_PAUSED   => "Paused",
            STATUS_RESULTS  => "Results",
            null or ""      => "In the session",
            _               => char.ToUpperInvariant(status[0]) + status.Substring(1),
        };

        /// <summary>
        /// How we're connected to a member, for people: "Direct · 34 ms", "Relay · 120 ms", or for a joiner's
        /// view of another joiner, "Through the host".
        /// </summary>
        public string DescribeConnection(LinkMember member)
        {
            if (_session == null || member == null) return "";

            // Joiners only talk to the host, which passes everything on
            if (_session.Role == LinkRole.Join && !member.IsHost)
            {
                return "Through the host";
            }

            var stats = _session.GetStats(member);
            if (!stats.Connected) return "Connecting";

            string path = stats.Path switch
            {
                LinkPath.Direct => "Direct",
                LinkPath.Relay  => "Relay",
                _               => "Connected",
            };
            return stats.Rtt is { } rtt ? $"{path} · {rtt.TotalMilliseconds:0} ms" : path;
        }

        /// <summary>
        /// Whether a member's connection is good enough to play on: <c>false</c> when it's relayed with a high
        /// round trip, so the lobby can highlight it.
        /// </summary>
        public bool IsConnectionPoor(LinkMember member)
        {
            if (_session == null || member == null) return false;
            if (_session.Role == LinkRole.Join && !member.IsHost) return false;

            var stats = _session.GetStats(member);
            return stats.Connected && stats.Rtt is { } rtt && rtt.TotalMilliseconds > 150;
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
            _nextJoinTime = double.PositiveInfinity;
            _lastProblem = null;
            _remoteRequestHash = null;
            _remoteRequestName = null;
            _remoteSetups.Clear();
            _paths.Clear();
            _resendDropOuts = false;
            EndSharedSong();
        }

        private void OnMemberJoined(LinkMember member)
        {
            _lastProblem = null;
            ResendDropOuts();
            ToastManager.ToastInformation(member.IsHost && _session.Role == LinkRole.Join
                ? $"Joined {member.Name}'s online session"
                : $"{member.Name} joined the online session");
        }

        private void OnMemberLeft(LinkMember member)
        {
            // A friend who is on a highway right now stays there (in _songSetups), shown as stalled
            _remoteSetups.Remove(member.Id);

            // A joiner who lost the host hears about that instead
            if (_session is not { IsConnected: true }) return;

            if (member.ConnectionLost)
            {
                ToastManager.ToastWarning($"Lost the connection to {member.Name}");
            }
            else
            {
                ToastManager.ToastInformation($"{member.Name} left the online session");
            }
        }

        private void OnDisconnected(LinkDisconnect disconnect)
        {
            if (!disconnect.CanRetry)
            {
                // Trying again won't help: tell the player and close the session
                string title = disconnect.Code switch
                {
                    ByeCodes.INCOMPATIBLE => "Can't join: different versions",
                    ByeCodes.KICKED       => "Removed from the session",
                    ByeCodes.LEFT         => "Session ended",
                    _                     => "Couldn't join",
                };
                string message = disconnect.Code switch
                {
                    ByeCodes.INCOMPATIBLE =>
                        $"Everyone in an online session has to play the same version of YARG.\n\n{Sentence(disconnect.Reason)}",
                    // Every session has its own code, so the old one won't work again
                    ByeCodes.LEFT => "The host ended the online session. If they host again, ask them for the new invite code.",
                    _             => Sentence(disconnect.Reason),
                };

                // A joiner mid-song can't carry on without the host for long
                OnSessionEndedInSong(disconnect.Code switch
                {
                    ByeCodes.KICKED => "You were removed from the online session",
                    ByeCodes.LEFT   => "The host ended the online session",
                    _               => "Lost the online session",
                });

                Close();
                ShowProblem(title, message);
                return;
            }

            // A joiner keeps trying, but only says so when something changes
            _nextJoinTime = Now + REJOIN_INTERVAL;
            if (disconnect.Reason != _lastProblem)
            {
                _lastProblem = disconnect.Reason;
                ToastManager.ToastWarning($"Lost the online session ({disconnect.Reason}). Trying to reconnect...");
            }
        }

        private void OnPeerRefused(string reason, string code)
        {
            ToastManager.ToastWarning($"Someone couldn't join the online session: {reason}");
        }

        /// <summary>
        /// Tell the player about a problem that needs their attention: a dialog in the menu, or a toast elsewhere
        /// (or when another dialog is open).
        /// </summary>
        private static void ShowProblem(string title, string message)
        {
            var dialogs = DialogManager.Instance;
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Menu && dialogs != null)
            {
                LinkLobby.CloseIfOpen();
                if (!dialogs.IsDialogShowing)
                {
                    dialogs.ShowMessage(title, message);
                    return;
                }
            }

            ToastManager.ToastError($"{title}. {message}");
        }

        private static string Sentence(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            return text.EndsWith(".") ? text : text + ".";
        }

        private static string SceneName(SceneIndex scene) => scene switch
        {
            SceneIndex.Menu        => LinkSession.SCENE_MENU,
            SceneIndex.Gameplay    => SCENE_GAMEPLAY,
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

            // A song that's still ours (gameplay reloaded without its GameManager telling us) is over now
            EndSharedSong();

            _waiting = true;
            _startAt = null;
            _cancelReason = null;
            _remoteRequestHash = null;

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
                    // A host taking part sent its setup before it reported ready, so before the start reached us.
                    // Without one (it's in another song, or another play of this one), we're on our own.
                    shared.HostInPlay = _session.Role == LinkRole.Join && HostMember() is { } host &&
                        _remoteSetups.TryGetValue(host.Id, out var hostSetup) && hostSetup.SongHash == hash;
                    _shared = shared;
                    StartBandMeter(gameManager);
                    StartClockSync();
                    StartPauseSync();
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
            var taken = new List<string>();
            foreach (var (member, setup) in _remoteSetups)
            {
                // WaitForStartSignal removed the setups for other songs. If a setup for a different song arrived
                // after that, the friend gets no highway in this song.
                if (setup.SongHash != hash)
                {
                    YargLogger.LogFormatInfo("Link: {0} sent a setup for a different song, so they have no " +
                        "highway in this one", setup.Feeds.Count > 0 ? setup.Feeds[0].Name : member);
                    continue;
                }

                taken.Add(member);
                _songSetups[member] = setup;

                foreach (var feed in setup.Feeds)
                {
                    // The host's fail meter is the band's, so friends' players count toward it there
                    feed.InHostBand = _session?.Role == LinkRole.Host;

                    if (feed.Mirrored)
                    {
                        var frame = setup.Data.Frames[setup.FrameIndices[feed.Index]];
                        frame.Profile.IsBot = feed.IsBot;
                        players.Add((frame, setup.Data, feed));
                    }
                }
            }

            foreach (var member in taken)
            {
                _remoteSetups.Remove(member);
            }

            YargLogger.LogInfo($"Link: {_songSetups.Count} friend setup(s) for this song, {players.Count} highway(s) to add");
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
                Decline(hash, name, DECLINE_BUSY);
                return;
            }

            if (hash.Length != HashWrapper.HASH_SIZE_IN_BYTES * 2 ||
                !SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(hash), out var entries) ||
                entries.Count == 0)
            {
                ToastManager.ToastWarning($"Your friend picked \"{name}\", which isn't in your library. " +
                    "They'll play it without you.");
                Decline(hash, name, DECLINE_MISSING);
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
            _remoteRequestName = entry.Name.Original;
        }

        private const string DECLINE_MISSING = "missing";
        private const string DECLINE_BUSY    = "busy";
        private const string DECLINE_BACKED  = "backed out";

        /// <summary>
        /// Turn down a friend's song: the host's coordinator stops waiting for us, and everyone hears why.
        /// </summary>
        private void Decline(string hash, string name, string reason)
        {
            _session.ReportDecline(hash, reason);
            _session.Broadcast(KIND_DECLINED, new JObject
            {
                ["song"] = hash,
                ["name"] = name,
                ["reason"] = reason,
            });
        }

        private void OnFriendDeclined(LinkMember member, JObject data)
        {
            string name = (string) data["name"] ?? "the song";
            string message = (string) data["reason"] switch
            {
                DECLINE_MISSING => $"{member.Name} doesn't have \"{name}\", so they'll sit this one out",
                DECLINE_BACKED  => $"{member.Name} backed out of \"{name}\"",
                _               => $"{member.Name} can't play \"{name}\" right now",
            };
            ToastManager.ToastWarning(message);
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
                Decline(_remoteRequestHash, _remoteRequestName, DECLINE_BACKED);
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

                        // Starting again (a restart) leaves the play of theirs that's on our highways
                        if (_songSetups.TryGetValue(member.Id, out var current) && !current.Left)
                        {
                            current.Left = true;
                            YargLogger.LogInfo($"Link: {member.Name} started another song or play without us");
                        }
                        break;

                    case KIND_INPUTS:
                        if (IsFromSongPlay(member, data))
                        {
                            _songSetups[member.Id].Receive(data, Now);
                            OnClockSample(member, data);
                            NoteInputsFrom(member);
                        }
                        else if (_remoteSetups.TryGetValue(member.Id, out var pending) && pending.IsPlay(data))
                        {
                            pending.Receive(data, Now);
                        }
                        break;

                    case KIND_LEFT:
                        OnFriendLeftSong(member, data);
                        break;

                    case KIND_FAILED:
                        OnFriendFailed(member, data);
                        break;

                    case KIND_DECLINED:
                        OnFriendDeclined(member, data);
                        break;

                    case KIND_PLAYER_FAILED:
                    case KIND_REVIVED:
                    case KIND_METER:
                        OnBandMeterData(member, kind, data);
                        break;

                    case KIND_PAUSE:
                    case KIND_RESUME:
                        OnPauseData(member, kind, data);
                        break;

                    case KIND_END_SONG:
                        OnEndSongData(member, data);
                        break;

                    case KIND_DROPPED:
                        OnDroppedData(member, data);
                        break;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Link: bad {kind} data from {member.Name}");
            }
        }

        /// <summary>
        /// Our band meter ran out. Tell friends so they fail with us: once we pause, our inputs stop
        /// and their mirror of our track would just freeze.
        /// </summary>
        public void ReportSongFailed(GameManager gameManager)
        {
            if (_session == null || _shared == null || _shared.GameManager != gameManager) return;

            YargLogger.LogInfo("Link: we failed the song, telling friends");
            _session.Broadcast(KIND_FAILED, new JObject
            {
                ["song"] = _shared.SongHash,
                ["play"] = _shared.PlayId,
                ["time"] = gameManager.SongTime,
            });
        }

        private void OnFriendFailed(LinkMember member, JObject data)
        {
            var gm = _shared?.GameManager;
            if (gm == null || !IsFromSongPlay(member, data)) return;
            if (!gm.IsSongStarted || gm.PlayerHasFailed) return;

            YargLogger.LogInfo($"Link: {member.Name} failed the song at {(double?) data["time"] ?? 0:0.00}s, failing with them");
            gm.FailFromLink();
        }

        /// <summary>
        /// Called by <see cref="GameManager"/> as it's destroyed, which is how every song ends: finished, quit,
        /// restarted, or switched to practice. Restarting reloads gameplay without leaving it, so the scene alone
        /// can't tell friends we've left this play of the song.
        /// </summary>
        public void OnSongExited(GameManager gm)
        {
            if (_session == null || _shared == null || _shared.GameManager != gm) return;

            YargLogger.LogInfo("Link: we left the synced song");
            _session.Broadcast(KIND_LEFT, new JObject
            {
                ["song"] = _shared.SongHash,
                ["play"] = _shared.PlayId,
            });
            EndSharedSong();
        }

        /// <summary>A friend left their play of a song: the one on our highways, or one we haven't joined.</summary>
        private void OnFriendLeftSong(LinkMember member, JObject data)
        {
            if (IsFromSongPlay(member, data))
            {
                _songSetups[member.Id].Left = true;
            }

            // Nobody can join a play that's over
            if (_remoteSetups.TryGetValue(member.Id, out var pending) && pending.IsPlay(data))
            {
                _remoteSetups.Remove(member.Id);
            }
        }

        /// <summary>
        /// Whether <paramref name="data"/> is from <paramref name="member"/>'s play of the synced song we're in:
        /// the one whose setup we added. Anything else (a play they started again, or one for a song we started
        /// alone) isn't about our song, even with the same song hash.
        /// </summary>
        private bool IsFromSongPlay(LinkMember member, JObject data) =>
            _shared != null && member != null && (string) data["song"] == _shared.SongHash &&
            _songSetups.TryGetValue(member.Id, out var setup) && !setup.Left && setup.IsPlay(data);

        /// <summary>
        /// Drop per-song state once we've left gameplay. <see cref="OnSongExited"/> usually has already.
        /// </summary>
        private void CheckSongEnded()
        {
            if (GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay) return;
            if (_shared == null) return;

            EndSharedSong();
        }

        /// <summary>
        /// Forget the synced song we were in. Setups that were used are done with; ones still waiting for us to
        /// join are kept.
        /// </summary>
        private void EndSharedSong()
        {
            _shared = null;
            _songSetups.Clear();
            _friendGoneSince.Clear();
            _droppedFriends.Clear();
            _lastInputsAt.Clear();
            EndBandMeter();
            EndClockSync();
            EndPauseSync();
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

            /// <summary>
            /// This play of the song, in every message about it. Restarting is another play of the same song, so
            /// friends can tell the two apart.
            /// </summary>
            public readonly string PlayId = Guid.NewGuid().ToString("N");

            /// <summary>
            /// Joiner: the host is in this play of the song, so it runs the band's meter and resumes pauses. Not
            /// when we started the song alone (a restart, say) while the host was busy with another.
            /// </summary>
            public bool HostInPlay;
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
                        // Bots don't record inputs (and the replay format doesn't say who's a bot), so
                        // friends simulate a bot of their own
                        ["bot"]        = profile.IsBot,
                        ["instrument"] = profile.CurrentInstrument.ToString(),
                        ["difficulty"] = profile.CurrentDifficulty.ToString(),
                        ["frame"]      = frameIndex,
                    });
                }

                bool noFail = GameManager.NoFailMode != NoFailMode.Off;
                var data = new ReplayData(colorProfiles, cameraPresets, rockMeterPresets, noFail,
                    frames.ToArray(), Array.Empty<double>());
                var bytes = data.Serialize().ToArray();

                // Friends check this before parsing it; if ours fails, the check is behind YARG.Core's format
                if (!ReplayBlobCheck.IsSafe(bytes, ReplayIO.REPLAY_VERSIONS.CURRENT, out string problem))
                {
                    YargLogger.LogFormatError("Link: friends will turn down our players' replay data ({0}). " +
                        "ReplayBlobCheck needs updating for YARG.Core's replay format.", problem);
                }

                return new JObject
                {
                    ["song"]    = SongHash,
                    ["play"]    = PlayId,
                    ["players"] = players,
                    ["replay"]  = Convert.ToBase64String(bytes),
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
                    int count = Math.Min(inputs.Count - from, MAX_INPUTS_PER_MESSAGE);

                    var bytes = new byte[count * INPUT_SIZE];
                    using (var writer = new BinaryWriter(new MemoryStream(bytes)))
                    {
                        for (int j = from; j < from + count; j++)
                        {
                            writer.Write(inputs[j].Time);
                            writer.Write(inputs[j].Action);
                            writer.Write(inputs[j].Integer);
                        }
                    }
                    // Anything left goes in the next message
                    SentCounts[player] = from + count;

                    players.Add(new JObject
                    {
                        ["i"]          = i,
                        ["from"]       = from,
                        ["inputs"]     = Convert.ToBase64String(bytes),
                        ["score"]      = player.Score,
                        ["bonus"]      = player.BandBonusScore,
                        ["combo"]      = player.Combo,
                        ["stars"]      = player.Stars,
                        ["notesHit"]   = player.NotesHit,
                        ["totalNotes"] = player.BaseStats.TotalNotes,
                    });
                }

                return new JObject
                {
                    ["song"]    = SongHash,
                    ["play"]    = PlayId,
                    ["seq"]     = _seq++,
                    // Every input up to this time has been recorded (before each player's calibration)
                    ["time"]    = gm.InputTime,
                    // Our input time right now, for friends to measure how far apart our clocks are
                    ["clock"]   = gm.InputTimeAtCall,
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
            /// <summary>Which play of the song (see <see cref="SharedSong.PlayId"/>).</summary>
            public string Play;
            public ReplayData Data;
            public readonly List<RemoteInputFeed> Feeds = new();
            /// <summary>Player index → frame index in <see cref="Data"/>, or -1.</summary>
            public readonly List<int> FrameIndices = new();
            /// <summary>The friend has left this play of the song: it ended, they quit, or they restarted.</summary>
            public bool Left;

            /// <summary>Whether a message is about this play of the song.</summary>
            public bool IsPlay(JObject msg) =>
                Play != null && (string) msg["song"] == SongHash && (string) msg["play"] == Play;

            /// <exception cref="Exception">The setup is malformed; the caller logs it.</exception>
            public static RemoteSetup Parse(string member, JObject msg)
            {
                var setup = new RemoteSetup { SongHash = (string) msg["song"] };
                if (setup.SongHash == null || setup.SongHash.Length > LinkLimits.MAX_ID_LENGTH)
                {
                    throw new FormatException("bad song hash");
                }

                setup.Play = (string) msg["play"];
                if (string.IsNullOrEmpty(setup.Play) || setup.Play.Length > LinkLimits.MAX_ID_LENGTH)
                {
                    throw new FormatException("bad play ID");
                }

                var bytes = Convert.FromBase64String((string) msg["replay"]);

                // YARG.Core's reader trusts lengths in the data, so check it first
                if (!ReplayBlobCheck.IsSafe(bytes, ReplayIO.REPLAY_VERSIONS.CURRENT, out string problem))
                {
                    throw new FormatException($"unusable replay data: {problem}");
                }
                using (var array = FixedArray.Read(new MemoryStream(bytes), bytes.Length))
                {
                    setup.Data = new ReplayData(array.ToValueStream(), ReplayIO.REPLAY_VERSIONS.CURRENT,
                        new ReplayReadOptions());
                }

                var players = (JArray) msg["players"];
                if (players.Count > LinkLimits.MAX_PLAYERS_PER_MEMBER)
                {
                    throw new FormatException($"{players.Count} players is more than {LinkLimits.MAX_PLAYERS_PER_MEMBER}");
                }
                for (int i = 0; i < players.Count; i++)
                {
                    var p = (JObject) players[i];
                    int frame = (int?) p["frame"] ?? -1;
                    bool mirrored = frame >= 0 && frame < setup.Data.Frames.Length;

                    setup.FrameIndices.Add(mirrored ? frame : -1);
                    setup.Feeds.Add(new RemoteInputFeed(member, i,
                        LinkLimits.Clamp((string) p["name"], LinkLimits.MAX_NAME_LENGTH) ?? "Friend",
                        LinkLimits.Clamp((string) p["instrument"], LinkLimits.MAX_SHORT_LENGTH) ?? "",
                        LinkLimits.Clamp((string) p["difficulty"], LinkLimits.MAX_SHORT_LENGTH) ?? "",
                        mirrored,
                        (bool?) p["bot"] ?? false));
                }

                return setup;
            }

            public static bool IsSaneTime(double time) =>
                !double.IsNaN(time) && !double.IsInfinity(time) && Math.Abs(time) <= MAX_INPUT_TIME;

            public void Receive(JObject msg, double now)
            {
                if (!IsPlay(msg)) return;

                double time = (double) msg["time"];
                if (!IsSaneTime(time))
                {
                    throw new FormatException($"bad input time {time}");
                }

                var inputs = new List<GameInput>();
                var players = (JArray) msg["players"];
                if (players.Count > Feeds.Count)
                {
                    throw new FormatException($"inputs for {players.Count} players, but the setup has {Feeds.Count}");
                }
                foreach (var token in players)
                {
                    var p = (JObject) token;
                    int i = (int) p["i"];
                    if (i < 0 || i >= Feeds.Count) continue;

                    var bytes = Convert.FromBase64String((string) p["inputs"] ?? "");
                    int from = (int?) p["from"] ?? 0;
                    if (bytes.Length / INPUT_SIZE > MAX_INPUTS_PER_MESSAGE || from < 0)
                    {
                        throw new FormatException($"bad inputs for player {i}");
                    }

                    var feed = Feeds[i];
                    feed.SetStats(
                        (int?) p["score"] ?? 0,
                        (int?) p["bonus"] ?? 0,
                        (int?) p["combo"] ?? 0,
                        (float?) p["stars"] ?? 0f,
                        (int?) p["notesHit"] ?? 0,
                        (int?) p["totalNotes"] ?? 0);

                    inputs.Clear();
                    using (var reader = new BinaryReader(new MemoryStream(bytes)))
                    {
                        for (int n = 0; n < bytes.Length / INPUT_SIZE; n++)
                        {
                            double t = reader.ReadDouble();
                            int action = reader.ReadInt32();
                            int value = reader.ReadInt32();
                            if (!IsSaneTime(t))
                            {
                                throw new FormatException($"bad input time {t}");
                            }
                            inputs.Add(new GameInput(t, action, value));
                        }
                    }

                    feed.Receive(from, inputs, time, now);
                }
            }
        }

        #endregion

        #region Scoreboard

        /// <summary>
        /// The synced pause status, and the debugging scoreboard (Settings › Debug, off by default): the band's
        /// score and stars are in the score box, and this adds per-player stats and each friend's connection
        /// and clock sync.
        /// </summary>
        private void OnGUI()
        {
            if (GlobalVariables.Instance == null || GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay) return;

            // Even after the session ended, a song without its host shows its countdown
            bool hostGone = DrawHostGone();

            if (_shared == null || _session == null) return;

            var gm = _shared.GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            if (!hostGone) DrawPauseStatus();

            if (!(SettingsManager.Settings?.LinkDebugScoreboard.Value ?? false)) return;

            var rows = new List<(string Name, string Detail, int Score, bool Local, bool Stalled, string Connection)>();

            // The band's total and stars are in the score box (GameManager.ScoreBoxScore)
            foreach (var player in _shared.Players)
            {
                if (player == null) continue;
                rows.Add((player.Player.Profile.Name,
                    Detail(player.Stars, player.Combo, player.NotesHit, player.BaseStats.TotalNotes),
                    player.Score, true, false, null));
            }

            bool anyRemote = false;
            foreach (var setup in _songSetups.Values)
            {
                foreach (var feed in setup.Feeds)
                {
                    anyRemote = true;
                    bool stalled = !gm.Paused && Now - feed.LastReceived > RemoteInputFeed.STALL_SECONDS;
                    var member = FindMember(feed.Member);
                    string connection = member == null ? null : DescribeConnection(member);
                    if (SyncDetail(feed) is { } sync)
                    {
                        connection = connection == null ? sync : $"{connection} · {sync}";
                    }
                    rows.Add((feed.Name, Detail(feed.Stars, feed.Combo, feed.NotesHit, feed.TotalNotes),
                        feed.Score, false, stalled, connection));
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
                string connection = string.IsNullOrEmpty(row.Connection) ? "" : $"  <color=#909090>{row.Connection}</color>";
                text.AppendLine($"<size={Mathf.RoundToInt(16 * scale)}><color=#c0c0c0>{row.Detail}</color>{connection}</size>");
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

        private LinkMember FindMember(string id)
        {
            if (_session == null) return null;
            foreach (var member in _session.Members)
            {
                if (member.Id == id) return member;
            }
            return null;
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
