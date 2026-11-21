using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;
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
    /// During a synced song each game broadcasts a <c>setup</c> (its players' profiles and engine parameters,
    /// as replay data) when it's ready, then streams <c>inputs</c>: each player's recorded inputs plus their
    /// score. The other games add a highway per friend's player that plays those inputs back like a replay,
    /// so you see their notes being hit and missed. The score box shows the band's score from every game.
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
    /// This file opens and closes the session, and handles the session's events. Each other part of online play
    /// is in its own file:
    /// <list type="bullet">
    /// <item><c>Transport</c>: the YargLink library and its settings.</item>
    /// <item><c>Status</c>: our status and players for friends' lobbies, and how friends are connected.</item>
    /// <item><c>SongRequests</c>: a friend picked a song.</item>
    /// <item><c>SyncedStart</c>: everyone starts the song at the same moment.</item>
    /// <item><c>GameData</c>: what the games send each other during a song. <c>SharedSong</c> is our side of
    /// the song, and <c>RemoteSetup</c> a friend's side.</item>
    /// <item><c>BandMeter</c>: the host's fail meter is the band's (<see cref="UpdateBandMeter"/>).</item>
    /// <item><c>PauseSync</c>: a pause pauses everyone.</item>
    /// <item><c>ClockSync</c>: the joiners' song clocks follow the host's.</item>
    /// <item><c>HostLoss</c>: leaving a song, and a song that lost its host.</item>
    /// <item><c>DebugOverlays</c>: the debug panels in Settings › Debug.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The transport is always <see cref="NativeLinkTransport"/> (iroh: hole punching, relays, encryption).
    /// Without the YargLink library for this platform, online play isn't available.
    /// </para>
    /// </remarks>
    public partial class LinkController : MonoBehaviour
    {
        /// <summary>A joiner tries to reach the host again this often, in seconds.</summary>
        private const double REJOIN_INTERVAL = 5.0;

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

        // Friends' latest player setups not yet added to a song, by member ID: kept for us to join
        private readonly Dictionary<string, RemoteSetup> _remoteSetups = new();

        // Friends' setups added to the synced song we're in (_shared), by member ID. Until that song ends
        private readonly Dictionary<string, RemoteSetup> _songSetups = new();

        // The synced song we're sharing our inputs for, if any
        private SharedSong _shared;

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

        #endregion

        /// <summary>
        /// The synced pause status, a song's countdown when it lost its host, and the debug overlays
        /// (Settings › Debug, each off by default; refer to LinkController.DebugOverlays.cs).
        /// </summary>
        private void OnGUI()
        {
            if (GlobalVariables.Instance == null) return;

            DrawConnectionsDebug();

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Gameplay) return;

            // Even after the session ended, a song without its host shows its countdown
            bool hostGone = DrawHostGone();

            if (_shared == null || _session == null) return;

            var gm = _shared.GameManager;
            if (gm == null || !gm.IsSongStarted) return;

            if (!hostGone) DrawPauseStatus();

            DrawScoreBoxDebug(gm);
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
    }
}
