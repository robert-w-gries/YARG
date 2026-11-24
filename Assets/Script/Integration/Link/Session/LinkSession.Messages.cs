using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// What admitted members say: the host handles each joiner's messages, keeps the roster and relays game
    /// data; a joiner handles what the host sends.
    /// </summary>
    public sealed partial class LinkSession
    {
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

                case RestartRequestMessage restart when LinkLimits.Clamp(restart.Song) is { } song &&
                    LinkLimits.IsId(restart.Play):
                    Info($"{member} asked to restart {song}");
                    Apply(_coordinator.OnRestart(member.Id, song, restart.Play, fromHost: false, Present(), _clock()));
                    break;

                case RestartWithdrawMessage withdraw when LinkLimits.IsId(withdraw.Hash):
                    Apply(_coordinator.OnRestartWithdraw(member.Id, withdraw.Hash, Present()));
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

                case RestartMessage restart when LinkLimits.Clamp(restart.Song) is { } song &&
                    LinkLimits.IsId(restart.Member) && LinkLimits.IsId(restart.Play):
                    Info($"restarting {song}");
                    Raise(() => RestartRequested?.Invoke(song, restart.Member, restart.Play));
                    break;

                case RestartVoteMessage vote when LinkLimits.Clamp(vote.Song) is { } song:
                {
                    var status = new SyncStartCoordinator.RestartVote
                    {
                        Song = song,
                        Yes = LinkLimits.ClampIds(vote.Yes, _options.MaxMembers + 1),
                        Waiting = LinkLimits.ClampIds(vote.Waiting, _options.MaxMembers + 1),
                        Ended = vote.Ended,
                        Reason = LinkLimits.Clamp(vote.Reason, LinkLimits.MAX_REASON_LENGTH),
                    };
                    Raise(() => RestartVoteChanged?.Invoke(status));
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
    }
}
