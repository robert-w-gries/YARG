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
    }
}
