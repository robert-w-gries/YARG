using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// The members of an online session and what they say to each other: version handshake, roster, synced
    /// starts and opaque game data. Knows nothing about Unity or which transport it runs on.
    /// </summary>
    /// <remarks>
    /// Drive it by calling <see cref="Update"/> every frame from one thread. Events are raised from inside
    /// <see cref="Update"/> (and from the <c>Report*</c> calls on the host, whose coordinator runs locally).
    /// See <see cref="LinkProtocol"/> for the wire format.
    /// </remarks>
    public sealed partial class LinkSession : IDisposable
    {
        public const string SCENE_MENU = "menu";

        /// <summary>
        /// The score screen. A member there can still be asked to load a song: the game takes them back to the
        /// menu for it, so finishing a song doesn't leave them behind.
        /// </summary>
        public const string SCENE_SCORE = "score";

        /// <summary>Gameplay. A member there can be asked to restart its song.</summary>
        public const string SCENE_GAMEPLAY = "gameplay";

        /// <summary>How often <see cref="OnTick"/> runs, in seconds.</summary>
        private const double TICK_INTERVAL = 1.0;

        private sealed class PeerState
        {
            public readonly PeerId Peer;
            public readonly double ConnectedAt;

            /// <summary>Who the transport says is on the other end; see <see cref="ILinkTransport.GetPeerIdentity"/>.</summary>
            public string Identity;

            /// <summary>Set once the hello is accepted.</summary>
            public LinkMember Member;

            /// <summary>Why the other side said it's closing, if it did.</summary>
            public string ByeReason;
            public string ByeCode;

            /// <summary>What it may still send us right now.</summary>
            public readonly TokenBucket Messages;
            public readonly TokenBucket Bytes;

            /// <summary>Refused or flooding: nothing more from it is handled.</summary>
            public bool Dropped;

            public PeerState(PeerId peer, double connectedAt, double messagesPerSecond, double bytesPerSecond,
                double burstSeconds)
            {
                Peer = peer;
                ConnectedAt = connectedAt;
                Messages = new TokenBucket(messagesPerSecond, Math.Max(1, messagesPerSecond * burstSeconds), connectedAt);
                Bytes = new TokenBucket(bytesPerSecond, Math.Max(LinkLimits.MAX_MESSAGE_BYTES, bytesPerSecond * burstSeconds),
                    connectedAt);
            }
        }

        private readonly ILinkTransport       _transport;
        private readonly LinkSessionOptions   _options;
        private readonly Func<double>         _clock;
        private readonly SyncStartCoordinator _coordinator;

        private readonly Dictionary<PeerId, PeerState>  _peers   = new();
        private readonly Dictionary<string, LinkMember> _members = new();

        /// <summary>
        /// Host only: transport identities of members removed from this session, refused if they come back.
        /// Only stops a game that reconnects as itself; see <see cref="Kick"/>.
        /// </summary>
        private readonly HashSet<string> _banned = new();

        private string _localScene = SCENE_MENU;
        private string _localStatus;
        private List<PlayerInfo> _localPlayers = new();
        private double _nextTick;
        private bool   _disposed;

        /// <summary>Joiner only: the connection to the host, while we have one.</summary>
        private PeerId? _hostPeer;

        /// <summary>Joiner only: the host accepted our hello.</summary>
        private bool _hostAccepted;

        /// <summary>A member's game data: (from member, kind, data).</summary>
        public event Action<LinkMember, string, JObject> GameDataReceived;

        public event Action<LinkMember> MemberJoined;
        public event Action<LinkMember> MemberLeft;

        /// <summary>Another member picked a song: load it, then <see cref="ReportReady"/> or <see cref="ReportDecline"/>.</summary>
        public event Action<SongInfo> LoadSongRequested;

        /// <summary>Everyone is ready: start playback this many seconds from now.</summary>
        public event Action<double> StartScheduled;

        /// <summary>
        /// The start we reported ready for won't happen, or the restart we asked for (<see cref="RequestRestart"/>)
        /// was refused.
        /// </summary>
        public event Action<string> StartCancelled;

        /// <summary>
        /// A member (song, member ID, play) asked to play the song again from the start. If we're in that play of
        /// the song, load it again and <see cref="ReportReady"/>; else <see cref="ReportDecline"/>.
        /// </summary>
        public event Action<SongInfo, string, string> RestartRequested;

        /// <summary>The restart vote of our song changed or ended (see <see cref="RequestRestart"/>).</summary>
        public event Action<SyncStartCoordinator.RestartVote> RestartVoteChanged;

        /// <summary>Joiner only: lost the host, or the host refused us. The session is empty until rejoined.</summary>
        public event Action<LinkDisconnect> Disconnected;

        /// <summary>
        /// Host only: we turned away someone trying to join, with the reason and one of <see cref="ByeCodes"/>.
        /// </summary>
        public event Action<string, string> PeerRefused;

        public Action<string> LogInfo;
        public Action<string> LogWarning;

        public LinkRole Role          { get; }
        public string   LocalMemberId => _options.MemberId;
        public string   LocalName     => _options.Name;

        /// <summary>What another game needs to join this one.</summary>
        public string LocalTicket => _transport.LocalTicket;

        /// <summary>The other members, in no particular order.</summary>
        public IReadOnlyCollection<LinkMember> Members => _members.Values;

        /// <summary>
        /// Whether we're part of a session: always for a host, and for a joiner once the host accepted us.
        /// </summary>
        public bool IsConnected => Role == LinkRole.Host || _hostAccepted;

        /// <summary>
        /// Joiner only: whether a <see cref="Join"/> is still in progress (connecting, or waiting for the
        /// host to accept us). Connecting through NAT can take several seconds.
        /// </summary>
        public bool IsJoining => _hostPeer != null && !_hostAccepted;

        public SongInfo PendingStart => _coordinator.PendingSong;

        public LinkSession(ILinkTransport transport, LinkRole role, LinkSessionOptions options, Func<double> clock)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = options ?? new LinkSessionOptions();
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Role = role;
            _coordinator = new SyncStartCoordinator(_options.StartLeadSeconds, _options.StartTimeoutSeconds)
            {
                RestartVoteSeconds = _options.RestartVoteSeconds,
                RestartCooldownSeconds = _options.RestartCooldownSeconds,
            };
            _nextTick = _clock() + TICK_INTERVAL;
        }

        /// <summary>
        /// Joiner only: connect to a host. Calling it again drops the current host first.
        /// </summary>
        /// <exception cref="FormatException">The transport doesn't understand the ticket.</exception>
        public void Join(string ticket)
        {
            if (Role != LinkRole.Join)
            {
                throw new InvalidOperationException("Only a joiner can join a host");
            }

            if (_hostPeer is { } old)
            {
                _transport.Disconnect(old);
                ForgetHost();
            }

            _hostPeer = _transport.Connect(ticket);
            Info($"joining {ShortTicket(ticket)}");
        }

        public void SetLocalScene(string scene)
        {
            if (scene == _localScene) return;
            _localScene = scene;

            if (Role == LinkRole.Host)
            {
                SendRoster();
                Apply(_coordinator.OnTick(Present(), _clock()));
            }
            else
            {
                SendToHost(new SceneMessage { Scene = scene });
            }
        }

        /// <summary>
        /// What we're doing and who is playing, for the other members' lobby. Only sent when it changes, so it's
        /// fine to call every frame.
        /// </summary>
        public void SetLocalState(string status, IReadOnlyList<PlayerInfo> players)
        {
            players ??= Array.Empty<PlayerInfo>();
            if (status == _localStatus && players.SequenceEqual(_localPlayers)) return;

            _localStatus = status;
            _localPlayers = players.ToList();

            if (Role == LinkRole.Host)
            {
                SendRoster();
            }
            else
            {
                SendToHost(new StateMessage { Status = status, Players = _localPlayers });
            }
        }

        /// <summary>The players last passed to <see cref="SetLocalState"/>.</summary>
        public IReadOnlyList<PlayerInfo> LocalPlayers => _localPlayers;

        /// <summary>The players of this game and of all members that we know of.</summary>
        public int PlayerCount() => _localPlayers.Count + _members.Values.Sum(member => member.Players.Count);

        /// <summary>The status last passed to <see cref="SetLocalState"/>.</summary>
        public string LocalStatus => _localStatus;

        public PeerStats GetStats(LinkMember member)
        {
            return member.Peer is { } peer ? _transport.GetStats(peer) : default;
        }

        /// <summary>Handle everything the transport received. Call every frame.</summary>
        public void Update()
        {
            if (_disposed) return;

            int handled = 0;
            while (!_disposed && handled++ < _options.MaxEventsPerUpdate && _transport.TryPoll(out var ev))
            {
                switch (ev.Type)
                {
                    case LinkEventType.PeerConnected:
                        OnPeerConnected(ev.Peer);
                        break;
                    case LinkEventType.PeerDisconnected:
                        OnPeerDisconnected(ev.Peer, ev.Reason);
                        break;
                    case LinkEventType.Message:
                        OnTransportMessage(ev.Peer, ev.Delivery, ev.Data);
                        break;
                    case LinkEventType.Error:
                        Warn($"transport error: {ev.Reason}");
                        break;
                }
            }

            double now = _clock();
            if (!_disposed && now >= _nextTick)
            {
                _nextTick = now + TICK_INTERVAL;
                OnTick(now);
            }
        }

        /// <summary>Say goodbye to everyone and close the transport.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var state in _peers.Values)
            {
                Send(state.Peer, new ByeMessage { Reason = "left the session", Code = ByeCodes.LEFT }, Delivery.Reliable);
            }
            _transport.Dispose();
            _peers.Clear();
            _members.Clear();
        }

        private void OnTick(double now)
        {
            // Drop connections that never said hello
            foreach (var state in _peers.Values.ToList())
            {
                if (state.Member == null && now - state.ConnectedAt > _options.HelloTimeoutSeconds)
                {
                    Warn($"{state.Peer} never said hello");
                    _transport.Disconnect(state.Peer);
                }
            }

            if (Role == LinkRole.Host)
            {
                Apply(_coordinator.OnTick(Present(), now));
            }
        }

        /// <summary>
        /// Enough of a ticket to tell two apart in a log, but not to use one. Logs get posted publicly, and a
        /// ticket is an invite code.
        /// </summary>
        public static string ShortTicket(string ticket)
        {
            ticket = ticket?.Trim();
            if (string.IsNullOrEmpty(ticket)) return "(none)";
            return ticket.Length <= 8 ? "(short ticket)" : ticket.Substring(0, 8) + "...";
        }

        private void SendToHost(LinkMessage message)
        {
            if (_hostAccepted && _hostPeer is { } host)
            {
                Send(host, message, Delivery.Reliable);
            }
        }

        private void Send(PeerId peer, LinkMessage message, Delivery delivery)
        {
            var bytes = LinkProtocol.Encode(message);
            if (bytes.Length > _options.MaxMessageBytes)
            {
                // The other side would drop us for it
                Warn($"not sending a {LinkProtocol.TagOf(message)} of {bytes.Length} bytes, over the {_options.MaxMessageBytes} byte limit");
                return;
            }
            if (delivery == Delivery.Unreliable && bytes.Length > _transport.MaxUnreliableSize)
            {
                delivery = Delivery.Reliable;
            }
            _transport.Send(peer, bytes, delivery);
        }

        private void Raise(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Warn($"event handler failed: {e}");
            }
        }

        private void Info(string message) => LogInfo?.Invoke($"Link: {message}");
        private void Warn(string message) => LogWarning?.Invoke($"Link: {message}");
    }
}
