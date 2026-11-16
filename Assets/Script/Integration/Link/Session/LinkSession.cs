using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Session
{
    public enum LinkRole
    {
        /// <summary>Accepts joiners, coordinates synced starts, and relays game data between members.</summary>
        Host,

        /// <summary>Connected to a host; follows its lead.</summary>
        Join,
    }

    /// <summary>Another game in the session.</summary>
    public sealed class LinkMember
    {
        public string Id     { get; }
        public string Name   { get; internal set; }
        public bool   IsHost { get; internal set; }

        /// <summary>See <see cref="SceneMessage.Scene"/>.</summary>
        public string Scene { get; internal set; } = LinkSession.SCENE_MENU;

        /// <summary>What the member is doing, as they report it. See <see cref="StateMessage.Status"/>.</summary>
        public string Status { get; internal set; }

        /// <summary>The member's players, as they report them.</summary>
        public IReadOnlyList<PlayerInfo> Players { get; internal set; } = Array.Empty<PlayerInfo>();

        /// <summary>Once the member has left: why, if known.</summary>
        public string LeftReason { get; internal set; }

        /// <summary>
        /// Once the member has left: whether the connection was lost rather than closed on purpose. Only the
        /// host knows; for joiners it's always <c>false</c>.
        /// </summary>
        public bool ConnectionLost { get; internal set; }

        /// <summary>The direct connection to this member, if we have one (the host has one to everyone).</summary>
        internal PeerId? Peer { get; set; }

        internal LinkMember(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString() => $"{Name} ({Id})";
    }

    /// <summary>Why a joiner lost its host.</summary>
    public readonly struct LinkDisconnect
    {
        /// <summary>For people.</summary>
        public readonly string Reason;

        /// <summary>One of <see cref="ByeCodes"/>, or <c>null</c> if the connection just dropped.</summary>
        public readonly string Code;

        public LinkDisconnect(string reason, string code)
        {
            Reason = reason;
            Code = code;
        }

        /// <summary>Whether joining again could work without anyone changing anything.</summary>
        public bool CanRetry => Code is null or ByeCodes.LEFT;

        public override string ToString() => Code == null ? Reason : $"{Reason} ({Code})";
    }

    public sealed class LinkSessionOptions
    {
        /// <summary>The game build. Members must match exactly.</summary>
        public string GameVersion = "dev";

        /// <summary>Shown to other members.</summary>
        public string Name = "Player";

        /// <summary>Identifies this game to the other members for the lifetime of the session.</summary>
        public string MemberId = Guid.NewGuid().ToString("N");

        /// <summary>Lead time between "everyone is ready" and playback starting (host only).</summary>
        public double StartLeadSeconds = 2.0;

        /// <summary>Stop waiting for members who haven't reported ready after this long (host only).</summary>
        public double StartTimeoutSeconds = 90.0;

        /// <summary>Drop connections that haven't said hello within this long.</summary>
        public double HelloTimeoutSeconds = 10.0;

        /// <summary>Host only: the most joiners at once. More are refused with <see cref="ByeCodes.FULL"/>.</summary>
        public int MaxMembers = 8;

        /// <summary>Host only: connections that haven't said hello yet. More are dropped straight away.</summary>
        public int MaxUnadmittedPeers = 4;

        /// <summary>Messages larger than this are neither sent nor accepted; a peer that sends one is dropped.</summary>
        public int MaxMessageBytes = LinkLimits.MAX_MESSAGE_BYTES;

        /// <summary>
        /// What one joiner may send the host, on average, in messages and bytes per second. Inputs go out at
        /// up to 120 Hz, so these leave about twice the room a real game needs. A host may send each joiner
        /// <see cref="MaxMembers"/> + 1 times as much, since it passes everyone's data on.
        /// </summary>
        public double MessagesPerSecond = 250;
        public double BytesPerSecond    = 512 * 1024;

        /// <summary>How far over the average rates a peer may go for a moment (a song setup, say).</summary>
        public double BurstSeconds = 4.0;

        /// <summary>Transport events handled per <see cref="LinkSession.Update"/>, so a flood can't stall a frame.</summary>
        public int MaxEventsPerUpdate = 4096;
    }

    /// <summary>
    /// The members of an online session and what they say to each other: version handshake, roster, synced
    /// starts and opaque game data. Knows nothing about Unity or which transport it runs on.
    /// </summary>
    /// <remarks>
    /// Drive it by calling <see cref="Update"/> every frame from one thread. Events are raised from inside
    /// <see cref="Update"/> (and from the <c>Report*</c> calls on the host, whose coordinator runs locally).
    /// See <see cref="LinkProtocol"/> for the wire format.
    /// </remarks>
    public sealed class LinkSession : IDisposable
    {
        public const string SCENE_MENU = "menu";

        private const double TICK_INTERVAL = 1.0;

        private sealed class PeerState
        {
            public readonly PeerId Peer;
            public readonly double ConnectedAt;

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

        /// <summary>The start we reported ready for won't happen.</summary>
        public event Action<string> StartCancelled;

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
            _coordinator = new SyncStartCoordinator(_options.StartLeadSeconds, _options.StartTimeoutSeconds);
            _nextTick = _clock() + TICK_INTERVAL;
        }

        #region Public API

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
            Info($"joining {ticket}");
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

        /// <summary>The status last passed to <see cref="SetLocalState"/>.</summary>
        public string LocalStatus => _localStatus;

        /// <summary>Host only: remove a joiner from the session.</summary>
        public void Kick(LinkMember member)
        {
            if (Role != LinkRole.Host)
            {
                throw new InvalidOperationException("Only the host can remove members");
            }

            if (member?.Peer is not { } peer || !_peers.TryGetValue(peer, out var state) || state.Member != member)
            {
                return;
            }

            Info($"removing {member}");
            Refuse(state, "the host removed you from the session", ByeCodes.KICKED);
        }

        /// <summary>Our gameplay for <paramref name="song"/> is loaded and holding for the start.</summary>
        public void ReportReady(SongInfo song)
        {
            if (Role == LinkRole.Host)
            {
                Apply(_coordinator.OnReady(LocalMemberId, song, Present(), _clock()));
            }
            else
            {
                SendToHost(new ReadyMessage { Song = song });
            }
        }

        /// <summary>We can't or won't play the song we were asked to load.</summary>
        public void ReportDecline(string hash, string reason)
        {
            if (Role == LinkRole.Host)
            {
                Apply(_coordinator.OnDecline(LocalMemberId, hash, Present()));
            }
            else
            {
                SendToHost(new DeclineMessage { Hash = hash, Reason = reason });
            }
        }

        /// <summary>
        /// Send game data to every other member. Unreliable data that doesn't fit in a datagram is sent
        /// reliably instead.
        /// </summary>
        public void Broadcast(string kind, JObject data, Delivery delivery = Delivery.Reliable)
        {
            var message = new GameDataMessage { From = LocalMemberId, Kind = kind, Data = data };
            if (Role == LinkRole.Host)
            {
                foreach (var member in _members.Values)
                {
                    if (member.Peer is { } peer) Send(peer, message, delivery);
                }
            }
            else if (_hostAccepted && _hostPeer is { } host)
            {
                Send(host, message, delivery);
            }
        }

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

        #endregion

        #region Transport events

        private void OnPeerConnected(PeerId peer)
        {
            if (Role == LinkRole.Host && _peers.Values.Count(p => p.Member == null) >= _options.MaxUnadmittedPeers)
            {
                Warn($"dropping {peer}: too many connections waiting to say hello");
                _transport.Disconnect(peer);
                return;
            }

            // The host passes on everyone's data, so a joiner allows it proportionally more
            double scale = Role == LinkRole.Join ? Math.Max(1, _options.MaxMembers) + 1 : 1;
            var state = new PeerState(peer, _clock(), _options.MessagesPerSecond * scale,
                _options.BytesPerSecond * scale, _options.BurstSeconds);
            _peers[peer] = state;

            if (Role == LinkRole.Join && peer != _hostPeer)
            {
                Refuse(state, "this game isn't hosting", ByeCodes.REFUSED);
                return;
            }

            Send(peer, new HelloMessage
            {
                Protocol = LinkProtocol.VERSION,
                Game = _options.GameVersion,
                Member = LocalMemberId,
                Name = LocalName,
                Host = Role == LinkRole.Host,
            }, Delivery.Reliable);
        }

        private void OnPeerDisconnected(PeerId peer, string reason)
        {
            _peers.TryGetValue(peer, out var state);
            _peers.Remove(peer);
            bool lost = state?.ByeReason == null;
            reason = state?.ByeReason ?? reason;

            if (Role == LinkRole.Join)
            {
                if (peer != _hostPeer) return;

                var disconnect = new LinkDisconnect(reason, state?.ByeCode);
                Info($"left the host: {disconnect}");
                ForgetHost();
                Raise(() => Disconnected?.Invoke(disconnect));
                return;
            }

            var member = state?.Member;
            if (member == null) return;

            _members.Remove(member.Id);
            member.LeftReason = reason;
            member.ConnectionLost = lost;
            Info($"{member} left: {reason}");
            Raise(() => MemberLeft?.Invoke(member));
            SendRoster();
            Apply(_coordinator.OnTick(Present(), _clock()));
        }

        private void OnTransportMessage(PeerId peer, Delivery delivery, byte[] data)
        {
            if (!_peers.TryGetValue(peer, out var state) || state.Dropped) return;

            // Check the size and rate before decoding anything
            int limit = state.Member == null
                ? Math.Min(_options.MaxMessageBytes, LinkLimits.MAX_UNADMITTED_MESSAGE_BYTES)
                : _options.MaxMessageBytes;
            if (data.Length > limit)
            {
                Refuse(state, $"sent a message of {data.Length} bytes, over the {limit} byte limit", ByeCodes.REFUSED);
                return;
            }

            double now = _clock();
            if (!state.Messages.TryTake(1, now) || !state.Bytes.TryTake(data.Length, now))
            {
                Refuse(state, "sent more data than a game would", ByeCodes.REFUSED);
                return;
            }

            LinkMessage message;
            try
            {
                message = LinkProtocol.Decode(data);
            }
            catch (FormatException e)
            {
                Warn($"bad message from {peer}: {e.Message}");
                return;
            }

            switch (message)
            {
                case null:
                    // A message type from a newer version
                    return;
                case HelloMessage hello:
                    OnHello(state, hello);
                    return;
                case ByeMessage bye:
                    state.ByeReason = LinkLimits.Clamp(bye.Reason, LinkLimits.MAX_REASON_LENGTH) ?? "left";
                    state.ByeCode = LinkLimits.Clamp(bye.Code, LinkLimits.MAX_SHORT_LENGTH);
                    return;
            }

            if (state.Member == null)
            {
                Warn($"{peer} sent {LinkProtocol.TagOf(message)} before its hello");
                return;
            }

            if (Role == LinkRole.Host)
            {
                OnMessageFromJoiner(state.Member, message, delivery);
            }
            else
            {
                OnMessageFromHost(message);
            }
        }

        private void OnHello(PeerState state, HelloMessage hello)
        {
            if (state.Member != null)
            {
                Warn($"{state.Member} said hello twice");
                return;
            }

            if (hello.Protocol != LinkProtocol.VERSION)
            {
                // The reason reaches both sides, so it doesn't say which is whose
                Refuse(state, $"incompatible online protocol versions ({hello.Protocol} and {LinkProtocol.VERSION})",
                    ByeCodes.INCOMPATIBLE);
                return;
            }

            if (hello.Game != _options.GameVersion)
            {
                string theirs = LinkLimits.Clamp(hello.Game, LinkLimits.MAX_SHORT_LENGTH);
                Refuse(state, $"different game versions ({theirs} and {_options.GameVersion})",
                    ByeCodes.INCOMPATIBLE);
                return;
            }

            if (string.IsNullOrEmpty(hello.Member) || hello.Member.Length > LinkLimits.MAX_ID_LENGTH)
            {
                Refuse(state, "bad member ID", ByeCodes.REFUSED);
                return;
            }

            if (hello.Member == LocalMemberId || _members.ContainsKey(hello.Member))
            {
                Refuse(state, "already in this session", ByeCodes.REFUSED);
                return;
            }

            if (Role == LinkRole.Host && _members.Count >= _options.MaxMembers)
            {
                Refuse(state, "the session is full", ByeCodes.FULL);
                return;
            }

            string name = LinkLimits.Clamp(hello.Name, LinkLimits.MAX_NAME_LENGTH);
            var member = new LinkMember(hello.Member, string.IsNullOrEmpty(name) ? "Friend" : name)
            {
                IsHost = hello.Host,
                Peer = state.Peer,
            };

            if (Role == LinkRole.Host)
            {
                if (hello.Host)
                {
                    Refuse(state, "both games are hosting", ByeCodes.REFUSED);
                    return;
                }

                state.Member = member;
                _members[member.Id] = member;
                Info($"{member} joined");
                Raise(() => MemberJoined?.Invoke(member));
                SendRoster();
                Apply(_coordinator.OnTick(Present(), _clock()));
            }
            else
            {
                if (!hello.Host)
                {
                    Refuse(state, "that game isn't hosting", ByeCodes.REFUSED);
                    return;
                }

                // The host joins the member list with its roster, which follows right away
                state.Member = member;
                _hostAccepted = true;
                Info($"joined {member}'s session");
                SendToHost(new SceneMessage { Scene = _localScene });
                SendToHost(new StateMessage { Status = _localStatus, Players = _localPlayers });
            }
        }

        private void OnMessageFromJoiner(LinkMember member, LinkMessage message, Delivery delivery)
        {
            switch (message)
            {
                case SceneMessage scene:
                    member.Scene = LinkLimits.Clamp(scene.Scene, LinkLimits.MAX_SHORT_LENGTH) ?? SCENE_MENU;
                    SendRoster();
                    Apply(_coordinator.OnTick(Present(), _clock()));
                    break;

                case StateMessage memberState:
                    member.Status = LinkLimits.Clamp(memberState.Status, LinkLimits.MAX_SHORT_LENGTH);
                    member.Players = LinkLimits.Clamp(memberState.Players);
                    SendRoster();
                    break;

                case ReadyMessage ready when LinkLimits.Clamp(ready.Song) is { } song:
                    Info($"{member} is ready for {song}");
                    Apply(_coordinator.OnReady(member.Id, song, Present(), _clock()));
                    break;

                case DeclineMessage decline when decline.Hash == null || decline.Hash.Length <= LinkLimits.MAX_ID_LENGTH:
                    Info($"{member} declined: {LinkLimits.Clamp(decline.Reason, LinkLimits.MAX_SHORT_LENGTH)}");
                    Apply(_coordinator.OnDecline(member.Id, decline.Hash, Present()));
                    break;

                case GameDataMessage game when game.Kind != null && game.Kind.Length <= LinkLimits.MAX_SHORT_LENGTH:
                    // Pass it on to the other joiners, who aren't connected to each other
                    var relay = new GameDataMessage { From = member.Id, Kind = game.Kind, Data = game.Data };
                    foreach (var other in _members.Values)
                    {
                        if (other != member && other.Peer is { } peer) Send(peer, relay, delivery);
                    }
                    Raise(() => GameDataReceived?.Invoke(member, game.Kind, game.Data));
                    break;

                default:
                    Warn($"ignoring {LinkProtocol.TagOf(message)} from joiner {member}");
                    break;
            }
        }

        private void OnMessageFromHost(LinkMessage message)
        {
            switch (message)
            {
                case RosterMessage roster:
                    ApplyRoster(roster);
                    break;

                case LoadSongMessage load when LinkLimits.Clamp(load.Song) is { } song:
                    Raise(() => LoadSongRequested?.Invoke(song));
                    break;

                case StartMessage start:
                {
                    // The host's lead time counts from when it sent this
                    double oneWay = 0;
                    if (_hostPeer is { } host && _transport.GetStats(host).Rtt is { } rtt)
                    {
                        oneWay = rtt.TotalSeconds / 2;
                    }
                    double delay = Math.Max(0, start.DelayMs / 1000.0 - oneWay);
                    Info($"starting in {delay * 1000:0} ms (host lead {start.DelayMs} ms, one-way {oneWay * 1000:0} ms)");
                    Raise(() => StartScheduled?.Invoke(delay));
                    break;
                }

                case CancelMessage cancel:
                    string reason = LinkLimits.Clamp(cancel.Reason, LinkLimits.MAX_REASON_LENGTH) ?? "cancelled";
                    Raise(() => StartCancelled?.Invoke(reason));
                    break;

                case GameDataMessage game when game.Kind != null && game.Kind.Length <= LinkLimits.MAX_SHORT_LENGTH:
                    if (game.From != null && _members.TryGetValue(game.From, out var from))
                    {
                        Raise(() => GameDataReceived?.Invoke(from, game.Kind, game.Data));
                    }
                    break;

                default:
                    Warn($"ignoring {LinkProtocol.TagOf(message)} from the host");
                    break;
            }
        }

        private void ApplyRoster(RosterMessage roster)
        {
            var seen = new HashSet<string>();
            var joined = new List<LinkMember>();

            // The host and its joiners, and us among them
            int maxEntries = Math.Max(1, _options.MaxMembers) + 1;
            foreach (var info in (roster.Members ?? new List<MemberInfo>()).Take(maxEntries))
            {
                if (info == null || string.IsNullOrEmpty(info.Id) || info.Id.Length > LinkLimits.MAX_ID_LENGTH ||
                    info.Id == LocalMemberId || !seen.Add(info.Id))
                {
                    continue;
                }

                string name = LinkLimits.Clamp(info.Name, LinkLimits.MAX_NAME_LENGTH);
                if (!_members.TryGetValue(info.Id, out var member))
                {
                    member = new LinkMember(info.Id, name ?? "Friend");
                    if (info.Host && _hostPeer is { } host)
                    {
                        member.Peer = host;
                    }
                    _members[info.Id] = member;
                    joined.Add(member);
                }

                member.Name = name ?? member.Name;
                member.Scene = LinkLimits.Clamp(info.Scene, LinkLimits.MAX_SHORT_LENGTH) ?? SCENE_MENU;
                member.IsHost = info.Host;
                member.Status = LinkLimits.Clamp(info.Status, LinkLimits.MAX_SHORT_LENGTH);
                member.Players = LinkLimits.Clamp(info.Players);
            }

            var left = _members.Values.Where(m => !seen.Contains(m.Id)).ToList();
            foreach (var member in left)
            {
                _members.Remove(member.Id);
            }

            foreach (var member in left)
            {
                Info($"{member} left");
                Raise(() => MemberLeft?.Invoke(member));
            }
            foreach (var member in joined)
            {
                Info($"{member} is in the session");
                Raise(() => MemberJoined?.Invoke(member));
            }
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

        #endregion

        #region Helpers

        private List<SyncStartCoordinator.Present> Present()
        {
            var present = new List<SyncStartCoordinator.Present>
            {
                new(LocalMemberId, _localScene == SCENE_MENU),
            };
            foreach (var member in _members.Values)
            {
                present.Add(new SyncStartCoordinator.Present(member.Id, member.Scene == SCENE_MENU));
            }
            return present;
        }

        private void Apply(List<SyncStartCoordinator.Action> actions)
        {
            foreach (var action in actions)
            {
                var command = action.Command;
                if (action.Member == LocalMemberId)
                {
                    switch (command.Type)
                    {
                        case SyncStartCoordinator.CommandType.LoadSong:
                            Raise(() => LoadSongRequested?.Invoke(command.Song));
                            break;
                        case SyncStartCoordinator.CommandType.Start:
                            Info($"starting in {command.DelayMs} ms");
                            Raise(() => StartScheduled?.Invoke(command.DelayMs / 1000.0));
                            break;
                        case SyncStartCoordinator.CommandType.Cancel:
                            Raise(() => StartCancelled?.Invoke(command.Reason));
                            break;
                    }
                    continue;
                }

                if (!_members.TryGetValue(action.Member, out var member) || member.Peer is not { } peer)
                {
                    continue;
                }

                LinkMessage message = command.Type switch
                {
                    SyncStartCoordinator.CommandType.LoadSong => new LoadSongMessage { Song = command.Song },
                    SyncStartCoordinator.CommandType.Start    => new StartMessage { DelayMs = command.DelayMs },
                    _                                         => new CancelMessage { Reason = command.Reason },
                };
                Send(peer, message, Delivery.Reliable);
            }
        }

        private void SendRoster()
        {
            if (Role != LinkRole.Host) return;

            var roster = new RosterMessage();
            roster.Members.Add(new MemberInfo
            {
                Id = LocalMemberId, Name = LocalName, Scene = _localScene, Host = true,
                Status = _localStatus, Players = _localPlayers,
            });
            foreach (var member in _members.Values)
            {
                roster.Members.Add(new MemberInfo
                {
                    Id = member.Id, Name = member.Name, Scene = member.Scene,
                    Status = member.Status, Players = member.Players.ToList(),
                });
            }

            foreach (var member in _members.Values)
            {
                if (member.Peer is { } peer) Send(peer, roster, Delivery.Reliable);
            }
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

        private void Refuse(PeerState state, string reason, string code)
        {
            if (state.Dropped) return;
            state.Dropped = true;

            Warn($"refusing {state.Peer}: {reason}");
            state.ByeReason ??= reason;
            state.ByeCode ??= code;
            Send(state.Peer, new ByeMessage { Reason = reason, Code = code }, Delivery.Reliable);
            _transport.Disconnect(state.Peer);

            if (Role == LinkRole.Host && code != ByeCodes.KICKED)
            {
                Raise(() => PeerRefused?.Invoke(reason, code));
            }
        }

        private void ForgetHost()
        {
            _hostPeer = null;
            _hostAccepted = false;

            var left = _members.Values.ToList();
            _members.Clear();
            foreach (var member in left)
            {
                Raise(() => MemberLeft?.Invoke(member));
            }
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

        #endregion
    }
}
