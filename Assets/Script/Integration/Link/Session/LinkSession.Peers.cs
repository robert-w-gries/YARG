using System;
using System.Linq;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Connections and admission: who may connect, the hello that checks a game before it becomes a member,
    /// the size and rate limits on what a peer sends, and refusing, removing or losing a peer.
    /// </summary>
    public sealed partial class LinkSession
    {
        /// <summary>
        /// Host only: remove a joiner from the session, and refuse their connection if they come back with the
        /// same identity.
        /// </summary>
        /// <remarks>
        /// A modified game can come back with a new identity whenever it likes, and still has the invite code.
        /// The only removal that holds is a new session, with a new code that the removed member isn't sent.
        /// </remarks>
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
            if (state.Identity != null) _banned.Add(state.Identity);
            Refuse(state, "the host removed you from the session", ByeCodes.KICKED);
        }

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
                _options.BytesPerSecond * scale, _options.BurstSeconds)
            {
                Identity = _transport.GetPeerIdentity(peer),
            };
            _peers[peer] = state;

            if (Role == LinkRole.Join)
            {
                if (peer != _hostPeer)
                {
                    Refuse(state, "this game isn't hosting", ByeCodes.REFUSED);
                    return;
                }

                // We know whom we dialled, so we speak first
                SendHello(peer);
                return;
            }

            if (state.Identity != null && _banned.Contains(state.Identity))
            {
                Refuse(state, "the host removed you from the session", ByeCodes.KICKED);
                return;
            }

            // The host says nothing about itself (name, version) until the joiner's hello checks out
        }

        private void SendHello(PeerId peer)
        {
            Send(peer, new HelloMessage
            {
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

            // The reason reaches both sides, so it doesn't say which is whose
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

            // The new joiner has at least one player, and its player list comes after this hello
            if (Role == LinkRole.Host && PlayerCount() >= LinkLimits.MAX_SESSION_PLAYERS)
            {
                Refuse(state, $"the session has {LinkLimits.MAX_SESSION_PLAYERS} players", ByeCodes.FULL);
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
                SendHello(state.Peer);
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
    }
}
