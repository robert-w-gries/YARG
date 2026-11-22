using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Song;
using YARG.Gameplay;
using YARG.Gameplay.HUD;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;
using YARG.Menu;
using YARG.Menu.Persistent;
using YARG.Player;
using YARG.Settings;

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
    /// During a synced song each game broadcasts a <see cref="SetupMessage"/> (its players' profiles and engine
    /// parameters, as replay data) when it's ready, then streams <see cref="InputsMessage"/>s: each player's
    /// recorded inputs plus their score. The other games add a highway per friend's player that plays those
    /// inputs back like a replay, so you see their notes being hit and missed. The score box shows the band's
    /// score from every game.
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
    /// This class opens and closes the session, sends each session event and each message from a friend to the
    /// part that handles it, and is the one entry point for the rest of the game. The parts:
    /// <list type="bullet">
    /// <item><see cref="LinkTransportFactory"/>: the YargLink library and its settings.</item>
    /// <item><see cref="LinkStatus"/> and <see cref="ConnectionLog"/>: our status for friends' lobbies, and how
    /// friends are connected.</item>
    /// <item><see cref="SongRequests"/>: a friend picked a song.</item>
    /// <item><see cref="SyncedStart"/>: everyone starts the song at the same moment.</item>
    /// <item><see cref="SyncedSong"/>: one synced song, with our players (<see cref="InputSender"/>), our
    /// friends' (<see cref="RemoteSetup"/>), and the features that run during it: the band meter, pauses, clock
    /// sync and drop-outs.</item>
    /// <item><see cref="HostLoss"/>: leaving a song, and a song that lost its host.</item>
    /// <item><see cref="LinkDebugOverlays"/>: the debug panels in Settings › Debug.</item>
    /// </list>
    /// The messages that games send each other about songs are in <see cref="PlayProtocol"/>. The decisions
    /// that don't need Unity are in the <c>Play</c> folder (<see cref="ClockCorrector"/>,
    /// <see cref="DepartureTracker"/>, <see cref="HostLossTracker"/>, <see cref="ResumeTiming"/>), with tests in
    /// <c>Tests/YARG.Link.Tests</c>.
    /// </para>
    /// <para>
    /// The transport is always <see cref="NativeLinkTransport"/> (iroh: hole punching, relays, encryption).
    /// Without the YargLink library for this platform, online play isn't available.
    /// </para>
    /// </remarks>
    public sealed class LinkController : MonoBehaviour
    {
        /// <summary>A joiner tries to reach the host again this often, in seconds.</summary>
        private const double REJOIN_INTERVAL = 5.0;

        /// <summary>How often our status and players are checked for changes, for friends' lobbies, in seconds.</summary>
        private const double STATE_INTERVAL = 0.25;

        public static LinkController Instance { get; private set; }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>The clock of all online timing, in seconds.</summary>
        internal static double Now => Clock.Elapsed.TotalSeconds;

        private LinkSession _session;

        /// <summary>Joiner only: the host's invite code.</summary>
        private string _joinTicket;
        private double _nextJoinTime = double.PositiveInfinity;
        private double _nextStateTime;

        /// <summary>Launch options (or environment variables) can open a session once, at startup.</summary>
        private bool _launchOptionsUsed;

        private readonly SongRequests  _requests = new();
        private readonly SyncedStart   _start = new();
        private readonly HostLoss      _hostLoss = new();
        private readonly ConnectionLog _connections = new();
        private LinkDebugOverlays _debug;
        private LinkBanner        _banner;

        // Friends' latest player setups not yet added to a song, by member ID: kept for us to join
        private readonly Dictionary<string, RemoteSetup> _pendingSetups = new();

        // The synced song we're in, if any
        private SyncedSong _song;

        /// <summary>Whether Settings › Experimental › "Enable online link" is on.</summary>
        public static bool IsEnabled => SettingsManager.Settings?.LinkEnable.Value ?? false;

        /// <summary>
        /// Whether gameplay should hold at the loading screen for a synced start.
        /// </summary>
        public bool ShouldHoldForStart => _session is { IsConnected: true } && _session.Members.Count > 0;

        /// <summary>The current session, if we're hosting or joining one.</summary>
        public LinkSession CurrentSession => _session;

        /// <summary>The synced song we're in, if any.</summary>
        internal SyncedSong Song => _song;

        internal HostLoss HostLoss => _hostLoss;

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
        internal string LastProblem { get; private set; }

        /// <summary>Joiner only: why the last attempt to reach the host failed, if it did.</summary>
        public string ConnectProblem => IsConnecting ? LastProblem : null;

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

        private void Awake()
        {
            _debug = LinkDebugOverlays.Create(this);
            _banner = LinkBanner.Create(transform);
        }

        private void Update()
        {
            if (GlobalVariables.Instance == null) return;

            // The debug panels use IMGUI, which costs every frame, so the component only runs while one is on
            bool debug = LinkDebugOverlays.AnyEnabled;
            if (_debug.enabled != debug) _debug.enabled = debug;

            if (!IsEnabled)
            {
                if (_session != null) Close();
                _hostLoss.Clear();
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
                _hostLoss.Update(null, null, Now);
                return;
            }

            if (_joinTicket != null && !_session.IsConnected && !_session.IsJoining && Now >= _nextJoinTime)
            {
                _nextJoinTime = Now + REJOIN_INTERVAL;
                ConnectToHost();
                if (_session == null) return;
            }

            _session.SetLocalScene(LinkStatus.SceneName(GlobalVariables.Instance.CurrentScene));
            UpdateLocalState();
            _session.Update();
            if (_session == null) return;

            _connections.Update(_session, Now);
            _requests.Update(_session);

            // Drop per-song state once we've left gameplay. OnSongExited usually has already.
            if (_song != null && GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                EndSong();
            }

            _song?.Update(Now);
            _hostLoss.Update(_session, _song, Now);
        }

        private void LateUpdate()
        {
            // After GameManager.Update, so this frame's inputs are recorded
            if (_session != null && _song != null && !_start.IsWaiting)
            {
                _song.Local.Update(_session, Now);
            }

            UpdateBanner();
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
            if (!Open(LinkRole.Host)) return false;

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
            if (!Open(LinkRole.Join)) return false;

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

        /// <summary>How we're connected to a member, for people. Refer to <see cref="LinkStatus.DescribeConnection"/>.</summary>
        public string DescribeConnection(LinkMember member) => LinkStatus.DescribeConnection(_session, member);

        /// <summary>Whether a member's connection is too slow to play on comfortably.</summary>
        public bool IsConnectionPoor(LinkMember member) => LinkStatus.IsConnectionPoor(_session, member);

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

        private bool Open(LinkRole role)
        {
            ILinkTransport transport = LinkTransportFactory.Create(role, out string problem);
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
            _session.LoadSongRequested += OnLoadSongRequested;
            _session.StartScheduled += OnStartScheduled;
            _session.StartCancelled += OnStartCancelled;
            _session.GameDataReceived += OnGameData;
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

        private void Close()
        {
            _session?.Dispose();
            _session = null;
            _joinTicket = null;
            _nextJoinTime = double.PositiveInfinity;
            LastProblem = null;
            _requests.Clear();
            _pendingSetups.Clear();
            _connections.Clear();
            EndSong();
        }

        /// <summary>What we're doing and who's playing, for friends' lobbies. The session only sends changes.</summary>
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
                    if (_start.IsReady) return LinkStatus.READY;
                    return _song is { Pause: { IsActive: true } } ? LinkStatus.PAUSED : LinkStatus.PLAYING;
                case SceneIndex.Score:
                    return LinkStatus.RESULTS;
                case SceneIndex.Menu:
                    bool choosing = _requests.IsChoosing ||
                        MenuManager.Instance != null && MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect;
                    return choosing ? LinkStatus.CHOOSING : LinkStatus.BROWSING;
                default:
                    return LinkStatus.SceneName(GlobalVariables.Instance.CurrentScene);
            }
        }

        private void OnMemberJoined(LinkMember member)
        {
            LastProblem = null;
            _song?.Departures.ResendDropOuts();
            ToastManager.ToastInformation(member.IsHost && _session.Role == LinkRole.Join
                ? $"Joined {member.Name}'s online session"
                : $"{member.Name} joined the online session");
        }

        private void OnMemberLeft(LinkMember member)
        {
            // A friend who is on a highway right now stays there (in the song's friends), shown as stalled
            _pendingSetups.Remove(member.Id);

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
                _hostLoss.OnSessionEnded(_session, _song, disconnect.Code switch
                {
                    ByeCodes.KICKED => "You were removed from the online session",
                    ByeCodes.LEFT   => "The host ended the online session",
                    _               => "Lost the online session",
                }, Now);

                Close();
                ShowProblem(title, message);
                return;
            }

            // A joiner keeps trying, but only says so when something changes
            _nextJoinTime = Now + REJOIN_INTERVAL;
            if (disconnect.Reason != LastProblem)
            {
                LastProblem = disconnect.Reason;
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

        private LinkMember FindMember(string id)
        {
            if (_session == null) return null;
            foreach (var member in _session.Members)
            {
                if (member.Id == id) return member;
            }
            return null;
        }

        #endregion

        #region Song requests and the synced start

        public enum StartResult
        {
            /// <summary>A synced start is scheduled; call <see cref="WaitForStartTime"/>.</summary>
            Synced,
            /// <summary>The session is gone or took too long; start right away.</summary>
            Alone,
            /// <summary>The start was cancelled; return to the menu.</summary>
            Cancelled,
        }

        private void OnLoadSongRequested(SongInfo song) => _requests.OnLoadSong(_session, song);

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
            EndSong();
            _requests.Clear();

            // Forget setups for any other song
            string hash = song.Hash.ToString();
            var stale = new List<string>();
            foreach (var (member, setup) in _pendingSetups)
            {
                if (setup.SongHash != hash) stale.Add(member);
            }
            foreach (var member in stale)
            {
                _pendingSetups.Remove(member);
            }

            // Hold for the start before reporting ready: a host that's the last one ready gets it inside ReportReady
            _start.Begin();

            // Tell friends about our players first, so they have it by the time the start is scheduled
            var local = new InputSender(gameManager, hash);
            _session.Send(local.CreateSetupMessage());
            _session.ReportReady(ToSongInfo(song));

            context.SetLoadingText("Waiting for other players...", song.Name.Original);
            YargLogger.LogInfo("Link: waiting for synced start");

            var result = await _start.WaitForSignal(() => _session is { IsConnected: true });
            if (result != StartResult.Synced) return result;

            // The session closed in the frame that the start arrived
            if (_session == null)
            {
                _start.Abandon();
                return StartResult.Alone;
            }

            // A host taking part sent its setup before it reported ready, so before the start reached us.
            // Without one (it's in another song, or another play of this one), we're on our own.
            bool hostInPlay = _session.Role == LinkRole.Join && FindHost() is { } host &&
                _pendingSetups.TryGetValue(host.Id, out var hostSetup) && hostSetup.SongHash == hash;
            _song = new SyncedSong(_session, local, hostInPlay);
            return StartResult.Synced;
        }

        /// <summary>
        /// Wait for the moment scheduled by <see cref="WaitForStartSignal"/>.
        /// </summary>
        public UniTask WaitForStartTime() => _start.WaitForStartTime(() => _session != null);

        /// <summary>
        /// Friends' players to add to the current song: their replay frames, plus the feeds their inputs arrive on.
        /// </summary>
        public List<(ReplayFrame Frame, ReplayData Data, RemoteInputFeed Feed)> TakeRemotePlayers(SongEntry song)
        {
            var players = new List<(ReplayFrame, ReplayData, RemoteInputFeed)>();
            if (_song == null) return players;

            string hash = song.Hash.ToString();
            var taken = new List<string>();
            foreach (var (member, setup) in _pendingSetups)
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
                _song.Friends[member] = setup;

                foreach (var feed in setup.Feeds)
                {
                    // The host's fail meter is the band's, so friends' players count toward it there
                    feed.InHostBand = _song.IsHost;

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
                _pendingSetups.Remove(member);
            }

            YargLogger.LogInfo($"Link: {_song.Friends.Count} friend setup(s) for this song, {players.Count} highway(s) to add");
            return players;
        }

        private void OnStartScheduled(double delaySeconds) => _start.OnScheduled(delaySeconds);

        private void OnStartCancelled(string reason)
        {
            if (_start.OnCancelled(reason)) return;

            if (_requests.IsChoosing)
            {
                _requests.Clear();
                ToastManager.ToastWarning($"Online start cancelled: {reason}");
            }
        }

        private static SongInfo ToSongInfo(SongEntry song) =>
            new(song.Hash.ToString(), song.Name.Original, song.Artist.Original);

        private LinkMember FindHost()
        {
            if (_session == null) return null;
            foreach (var member in _session.Members)
            {
                if (member.IsHost) return member;
            }
            return null;
        }

        #endregion

        #region Game data

        /// <summary>A message from a friend about a song: check it, and send it to the part that handles it.</summary>
        private void OnGameData(LinkMember member, string kind, JObject data)
        {
            if (data == null) return;

            try
            {
                var message = PlayProtocol.Decode(kind, data);
                if (message == null) return;

                switch (message)
                {
                    case SetupMessage setup:
                        OnSetup(member, setup);
                        break;
                    case InputsMessage inputs:
                        OnInputs(member, inputs);
                        break;
                    case LeftMessage left:
                        OnFriendLeftSong(member, left);
                        break;
                    case FailedMessage failed:
                        OnFriendFailed(member, failed);
                        break;
                    case DeclinedMessage declined:
                        _requests.OnDeclined(member, declined);
                        break;
                    case PlayerFailedMessage or RevivedMessage or MeterMessage:
                        _song?.BandMeter.OnMessage(member, (SongPlayMessage) message, Now);
                        break;
                    case PauseMessage pause:
                        _song?.Pause.OnPause(member, pause, Now);
                        break;
                    case ResumeMessage resume:
                        _song?.Pause.OnResume(member, resume, Now);
                        break;
                    case EndSongMessage endSong:
                        if (_song != null) _hostLoss.OnEndSong(_song, member, endSong);
                        break;
                    case DroppedMessage dropped:
                        if (_song != null && _song.Departures.OnDropped(member, dropped))
                        {
                            _hostLoss.OnDroppedUs(_song, member);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Link: bad {kind} data from {member.Name}");
            }
        }

        private void OnSetup(LinkMember member, SetupMessage message)
        {
            var setup = RemoteSetup.From(member.Id, message);
            _pendingSetups[member.Id] = setup;
            YargLogger.LogInfo($"Link: got {setup.Feeds.Count} player(s) from {member.Name} for {setup.SongHash}");

            // Starting again (a restart) leaves the play of theirs that's on our highways
            if (_song != null && _song.Friends.TryGetValue(member.Id, out var current) && !current.Left)
            {
                current.Left = true;
                YargLogger.LogInfo($"Link: {member.Name} started another song or play without us");
            }
        }

        private void OnInputs(LinkMember member, InputsMessage message)
        {
            if (_song != null && _song.IsFromPlay(member, message))
            {
                _song.Friends[member.Id].Receive(message, Now);
                _song.Clock.OnSample(member, message, Now);
                _song.Departures.NoteInputs(member, Now);
            }
            else if (_pendingSetups.TryGetValue(member.Id, out var pending) && pending.IsPlay(message))
            {
                pending.Receive(message, Now);
            }
        }

        /// <summary>A friend left their play of a song: the one on our highways, or one we haven't joined.</summary>
        private void OnFriendLeftSong(LinkMember member, LeftMessage message)
        {
            if (_song != null && _song.IsFromPlay(member, message))
            {
                _song.Friends[member.Id].Left = true;
            }

            // Nobody can join a play that's over
            if (_pendingSetups.TryGetValue(member.Id, out var pending) && pending.IsPlay(message))
            {
                _pendingSetups.Remove(member.Id);
            }
        }

        private void OnFriendFailed(LinkMember member, FailedMessage message)
        {
            var gm = _song?.GameManager;
            if (gm == null || !_song.IsFromPlay(member, message)) return;
            if (!gm.IsSongStarted || gm.PlayerHasFailed) return;

            YargLogger.LogInfo($"Link: {member.Name} failed the song at {message.Time:0.00}s, failing with them");
            gm.FailFromLink();
        }

        /// <summary>
        /// Forget the synced song we were in. Setups that were used are done with; ones still waiting for us to
        /// join are kept.
        /// </summary>
        private void EndSong()
        {
            _song?.End();
            _song = null;
        }

        #endregion

        #region Calls from the game

        /// <summary>
        /// Our band meter ran out. Tell friends so they fail with us: once we pause, our inputs stop
        /// and their mirror of our track would just freeze.
        /// </summary>
        public void ReportSongFailed(GameManager gameManager)
        {
            if (_song == null || _song.GameManager != gameManager) return;

            YargLogger.LogInfo("Link: we failed the song, telling friends");
            _song.Send(new FailedMessage { Time = gameManager.SongTime });
        }

        /// <summary>
        /// Called by <see cref="GameManager"/> as it's destroyed, which is how every song ends: finished, quit,
        /// restarted, or switched to practice. Restarting reloads gameplay without leaving it, so the scene alone
        /// can't tell friends we've left this play of the song.
        /// </summary>
        public void OnSongExited(GameManager gm)
        {
            if (_song == null || _song.GameManager != gm) return;

            YargLogger.LogInfo("Link: we left the synced song");
            _song.Send(new LeftMessage());
            EndSong();
        }

        /// <summary>Called by <see cref="GameManager"/> when it opens the pause menu, whatever opened it.</summary>
        public void OnLocalPause(GameManager gm) => _song?.Pause.OnLocalPause(gm);

        /// <summary>
        /// Called by <see cref="GameManager.Resume"/>. In a synced pause, resuming marks us ready instead of
        /// resuming, and the host resumes everyone.
        /// </summary>
        /// <returns>Whether we took over, so the game shouldn't resume.</returns>
        public bool HoldResume(GameManager gm) => _song?.Pause.HoldResume(gm, Now) ?? false;

        /// <summary>
        /// Called by the pause menu's "Back To Library". When we host a synced song friends are still playing,
        /// ask whether to take them along.
        /// </summary>
        /// <returns>Whether we took over (the dialog is open), so the pause menu shouldn't quit.</returns>
        public bool OfferLeaveWithParty(GameManager gm) => HostLoss.OfferLeaveWithParty(_song, gm);

        /// <summary>
        /// The host's band meter, for <see cref="GameManager.BandHappiness"/>, or <c>null</c> when this game's own
        /// meter is the band's.
        /// </summary>
        public float? HostBandHappiness(GameManager gm) => _song?.BandMeter.HostHappinessFor(gm);

        /// <summary>Whether the host's meter decides when this song fails, so this game's own meter mustn't.</summary>
        public bool IsBandMeterRemote(GameManager gm) => _song?.BandMeter.IsRemoteFor(gm) ?? false;

        /// <summary>The fail meter's icons on a joiner, or <c>null</c> when this game's own meter is the band's.</summary>
        public List<FailMeter.Marker> BandMeterMarkers(GameManager gm) => _song?.BandMeter.Markers(gm);

        /// <summary>
        /// The feeds of all friends' players in the song of <paramref name="gm"/>, with or without a highway. The
        /// band score, stars and star power multiplier count all of them.
        /// </summary>
        public List<RemoteInputFeed> FriendFeeds(GameManager gm)
        {
            var feeds = new List<RemoteInputFeed>();
            if (_song == null || _song.GameManager != gm) return feeds;

            foreach (var setup in _song.Friends.Values)
            {
                feeds.AddRange(setup.Feeds);
            }
            return feeds;
        }

        #endregion

        #region Overlays

        /// <summary>
        /// The banner over a song: a song's countdown when it lost its host (even after the session ended), or
        /// what a synced pause is waiting on.
        /// </summary>
        private void UpdateBanner()
        {
            if (GlobalVariables.Instance == null || !IsEnabled ||
                GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay)
            {
                _banner.Hide();
                return;
            }

            if (HostGoneText() is { } hostGone)
            {
                _banner.Show(hostGone, LinkBanner.Style.Warning);
                return;
            }

            var gm = _song?.GameManager;
            bool inSong = _session != null && gm != null && gm.IsSongStarted;
            _banner.Show(inSong ? PauseStatusText() : null, LinkBanner.Style.Info);
        }

        /// <summary>What a synced pause is waiting on, for the banner over the pause menu.</summary>
        /// <returns><c>null</c> when no synced pause is holding the song.</returns>
        private string PauseStatusText()
        {
            var gm = _song?.GameManager;
            if (_song == null || !_song.IsFor(gm) || !_song.Pause.IsActive) return null;

            var pause = _song.Pause;
            if (pause.ResumeAt.HasValue)
            {
                return "<b>Resuming together...</b>";
            }

            var text = new System.Text.StringBuilder();
            string by = pause.PausedBy == _session.LocalMemberId ? "You"
                : FindMember(pause.PausedBy)?.Name ?? "A friend";
            text.Append($"<b>{Escape(by)} paused</b>");

            var names = new List<string>();
            bool waitingOnHost = false;
            foreach (string id in pause.WaitingOn())
            {
                if (id == _session.LocalMemberId) continue;
                var member = FindMember(id);
                if (member == null) continue;
                if (member.IsHost) waitingOnHost = true;
                else names.Add(Escape(member.Name));
            }

            text.Append('\n');
            if (!pause.LocalReady)
            {
                text.Append("Resume when you're ready. The song carries on once everyone is.");
            }
            else if (names.Count > 0)
            {
                text.Append("You're ready. Pause again if you're not.");
            }
            else if (waitingOnHost)
            {
                text.Append("Everyone's ready. Waiting for the host to resume.");
            }
            else
            {
                text.Append("Everyone's ready.");
            }

            if (names.Count > 0)
            {
                text.Append($"\n<color=#c0c0c0>Waiting for {string.Join(", ", names)}</color>");
            }
            return text.ToString();
        }

        /// <summary>The countdown over a song that lost its host, once the host has been gone a while.</summary>
        /// <returns><c>null</c> when it isn't showing.</returns>
        private string HostGoneText()
        {
            if (_hostLoss.Countdown is not { Announced: true } countdown) return null;

            int seconds = Mathf.Max(0, Mathf.CeilToInt((float) (countdown.Deadline - Now)));
            return $"<b>{Escape(countdown.Reason)}</b>\n" +
                $"Returning to the library in {seconds}s. You can leave now from the pause menu.";
        }

        /// <summary>
        /// Text from a friend, made safe for a rich text label: a zero-width space after each "&lt;" stops it from
        /// starting a tag.
        /// </summary>
        private static string Escape(string text) => text?.Replace("<", "<\u200B") ?? "";

        #endregion
    }
}
