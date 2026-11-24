using System.Collections.Generic;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Synced song starts. The host runs the <see cref="SyncStartCoordinator"/> and carries out what it decides;
    /// a joiner only reports to the host.
    /// </summary>
    public sealed partial class LinkSession
    {
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

        /// <summary>
        /// Play <paramref name="song"/> again from the start, with the members we started it with
        /// (<paramref name="play"/> is our play of it, which the session passes on without looking inside). The
        /// host's restart happens straight away; a joiner's opens or joins a vote, reported through
        /// <see cref="RestartVoteChanged"/>. When the song restarts, everyone in it hears
        /// <see cref="RestartRequested"/>, us included. A refusal comes as <see cref="StartCancelled"/>.
        /// </summary>
        public void RequestRestart(SongInfo song, string play)
        {
            if (Role == LinkRole.Host)
            {
                Apply(_coordinator.OnRestart(LocalMemberId, song, play, fromHost: true, Present(), _clock()));
            }
            else
            {
                SendToHost(new RestartRequestMessage { Song = song, Play = play });
            }
        }

        /// <summary>Our song (<paramref name="hash"/>) carried on, so its restart vote is over.</summary>
        public void WithdrawRestart(string hash)
        {
            if (Role == LinkRole.Host)
            {
                Apply(_coordinator.OnRestartWithdraw(LocalMemberId, hash, Present()));
            }
            else
            {
                SendToHost(new RestartWithdrawMessage { Hash = hash });
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

        private List<SyncStartCoordinator.Present> Present()
        {
            var present = new List<SyncStartCoordinator.Present>
            {
                new(LocalMemberId, CanLoadSong(_localScene), _localScene == SCENE_GAMEPLAY),
            };
            foreach (var member in _members.Values)
            {
                present.Add(new SyncStartCoordinator.Present(member.Id, CanLoadSong(member.Scene),
                    member.Scene == SCENE_GAMEPLAY));
            }
            return present;
        }

        private static bool CanLoadSong(string scene) => scene is SCENE_MENU or SCENE_SCORE;

        private void Apply(List<SyncStartCoordinator.Action> actions)
        {
            // Send to the others first, then act ourselves. What we do can send messages of its own (leaving the
            // play we restart, say), and reliable messages arrive in order: so every joiner hears the command
            // before anything that follows from it.
            var local = new List<SyncStartCoordinator.Command>();
            foreach (var action in actions)
            {
                var command = action.Command;
                if (action.Member == LocalMemberId)
                {
                    local.Add(command);
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
                    SyncStartCoordinator.CommandType.Restart  => new RestartMessage
                    {
                        Song = command.Song, Member = command.Requester, Play = command.Play,
                    },
                    SyncStartCoordinator.CommandType.RestartVote => new RestartVoteMessage
                    {
                        Song = command.Vote.Song, Yes = command.Vote.Yes, Waiting = command.Vote.Waiting,
                        Ended = command.Vote.Ended, Reason = command.Vote.Reason,
                    },
                    _                                         => new CancelMessage { Reason = command.Reason },
                };
                Send(peer, message, Delivery.Reliable);
            }

            foreach (var command in local)
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
                    case SyncStartCoordinator.CommandType.Restart:
                        Raise(() => RestartRequested?.Invoke(command.Song, command.Requester, command.Play));
                        break;
                    case SyncStartCoordinator.CommandType.RestartVote:
                        Raise(() => RestartVoteChanged?.Invoke(command.Vote));
                        break;
                }
            }
        }
    }
}
