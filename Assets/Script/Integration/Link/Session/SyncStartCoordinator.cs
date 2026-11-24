using System;
using System.Collections.Generic;
using System.Linq;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Synced song start, as a pure state machine (no I/O, time passed in) so it can be tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the host. Any member can start a song; when its gameplay is loaded it reports ready. The
    /// coordinator then asks every other member who is in the menu to load the same song, waits until all of
    /// them are ready (or decline, leave, or time out), and tells everyone who is ready to start after a
    /// common lead time.
    /// </para>
    /// <para>
    /// <b>Groups.</b> The members that a start sends off together are a group: they play the song together. Only
    /// the coordinator decides that, so it is the one record of who plays with whom that no member can change. A
    /// member is in the group of the last start it took part in. A member of a group that is not in gameplay any
    /// more (it quit to the menu, say) doesn't count for it.
    /// </para>
    /// <para>
    /// <b>Restarts.</b> A restart (<see cref="OnRestart"/>) is a start of the song that a group is playing. The
    /// host's restart happens straight away. A joiner's restart is a vote: it happens once every member of the
    /// group in gameplay asks for it too. The vote ends without a restart when the song carries on
    /// (<see cref="OnRestartWithdraw"/>), when it runs longer than <see cref="RestartVoteSeconds"/>, or when
    /// everyone who asked leaves. A joiner can't open a vote within <see cref="RestartCooldownSeconds"/> of
    /// opening its last one; joining an open vote is always allowed. The members of the group are told about
    /// each change of the vote (<see cref="CommandType.RestartVote"/>).
    /// </para>
    /// <para>
    /// When a restart happens, the members of the group in gameplay are told to load the song again, and the
    /// others are left out of it. Each one checks that it is in the play of the member named in the command,
    /// loads the song again and reports ready, as for any start; one that isn't in that play declines. A member
    /// who leaves gameplay before it is ready is left out.
    /// </para>
    /// <para>
    /// <see cref="PauseCoordinator"/> also waits until members are ready, but it is a different class on
    /// purpose. This coordinator runs only on the host, in <see cref="LinkSession"/>, before a song. It decides
    /// who takes part, and it can time out or let a member decline. The pause coordinator runs in every game,
    /// in <c>LinkController</c>, during a song. It records the input time of each member, counts pauses
    /// (<see cref="PauseCoordinator.Epoch"/>), and calculates the time to resume from. All members must resume
    /// together, thus no member can decline or time out. The only part that the two classes have in common is
    /// the set of ready members. A shared class for that set does not make the code smaller, and it connects
    /// the session layer to the gameplay layer.
    /// </para>
    /// </remarks>
    public sealed class SyncStartCoordinator
    {
        /// <summary>A member currently in the session.</summary>
        public readonly struct Present
        {
            public readonly string Member;

            /// <summary>In the menu or on the score screen, i.e. able to be asked to load a song.</summary>
            public readonly bool InMenu;

            /// <summary>In gameplay, i.e. able to be asked to restart the song it plays.</summary>
            public readonly bool InSong;

            public Present(string member, bool inMenu, bool inSong = false)
            {
                Member = member;
                InMenu = inMenu;
                InSong = inSong;
            }
        }

        public enum CommandType
        {
            LoadSong,
            Start,
            Cancel,
            /// <summary>Load the song again, if you're in the play <see cref="Command.Play"/> of <see cref="Command.Requester"/>.</summary>
            Restart,
            /// <summary>The restart vote of your group changed or ended: <see cref="Command.Vote"/>.</summary>
            RestartVote,
        }

        /// <summary>A restart vote, as the members of its group see it.</summary>
        public sealed class RestartVote
        {
            public SongInfo Song;

            /// <summary>The members who asked for the restart, the first one first.</summary>
            public List<string> Yes = new();

            /// <summary>The members of the group in gameplay who haven't asked yet.</summary>
            public List<string> Waiting = new();

            /// <summary>The vote is over: the song restarts, or it doesn't (<see cref="Reason"/>).</summary>
            public bool Ended;

            /// <summary>For people: why the vote ended without a restart. <c>null</c> when the song restarts.</summary>
            public string Reason;
        }

        public readonly struct Command : IEquatable<Command>
        {
            public readonly CommandType Type;
            public readonly SongInfo    Song;
            public readonly long        DelayMs;
            public readonly string      Reason;

            /// <summary>Restart only: the member whose play restarts.</summary>
            public readonly string Requester;

            /// <summary>
            /// Restart only: the play of the song that <see cref="Requester"/> is in. The session doesn't look
            /// inside it.
            /// </summary>
            public readonly string Play;

            /// <summary>RestartVote only.</summary>
            public readonly RestartVote Vote;

            private Command(CommandType type, SongInfo song, long delayMs, string reason, string requester = null,
                string play = null, RestartVote vote = null)
            {
                Type = type;
                Song = song;
                DelayMs = delayMs;
                Reason = reason;
                Requester = requester;
                Play = play;
                Vote = vote;
            }

            public static Command LoadSong(SongInfo song) => new(CommandType.LoadSong, song, 0, null);
            public static Command Start(long delayMs) => new(CommandType.Start, null, delayMs, null);
            public static Command Cancel(string reason) => new(CommandType.Cancel, null, 0, reason);

            public static Command Restart(SongInfo song, string requester, string play) =>
                new(CommandType.Restart, song, 0, null, requester, play);

            public static Command RestartVoteChanged(RestartVote vote) =>
                new(CommandType.RestartVote, vote.Song, 0, vote.Reason, vote: vote);

            public bool Equals(Command other) => Type == other.Type && Equals(Song, other.Song) &&
                DelayMs == other.DelayMs && Reason == other.Reason && Requester == other.Requester &&
                Play == other.Play && ReferenceEquals(Vote, other.Vote);

            public override bool Equals(object obj) => obj is Command other && Equals(other);
            public override int GetHashCode() => (int) Type ^ DelayMs.GetHashCode();

            public override string ToString() => Type switch
            {
                CommandType.LoadSong    => $"LoadSong({Song})",
                CommandType.Start       => $"Start({DelayMs} ms)",
                CommandType.Restart     => $"Restart({Song}, {Requester})",
                CommandType.RestartVote => Vote.Ended
                    ? $"RestartVote(ended: {Reason ?? "restarting"})"
                    : $"RestartVote({Vote.Yes.Count} of {Vote.Yes.Count + Vote.Waiting.Count})",
                _                       => $"Cancel({Reason})",
            };
        }

        public readonly struct Action
        {
            public readonly string  Member;
            public readonly Command Command;

            public Action(string member, Command command)
            {
                Member = member;
                Command = command;
            }

            public override string ToString() => $"{Member}: {Command}";
        }

        private sealed class Pending
        {
            public SongInfo Song;
            public readonly HashSet<string> Ready = new();
            /// <summary>Declined, busy, or otherwise not taking part in this start.</summary>
            public readonly HashSet<string> Out = new();
            public double Since;

            /// <summary>A restart: only the members it was sent to take part.</summary>
            public bool Restart;
        }

        /// <summary>Members that a start sent off together. Refer to the remarks on this class.</summary>
        private sealed class Group
        {
            public SongInfo Song;
            public readonly HashSet<string> Members = new();
            public Vote Vote;
        }

        private sealed class Vote
        {
            public double Since;

            /// <summary>Who asked, in order, with the play each one is in.</summary>
            public readonly List<(string Member, string Play)> Yes = new();

            /// <summary>Who was last told about the vote, so the end reaches them too.</summary>
            public readonly HashSet<string> Told = new();

            /// <summary>What the group was last told, so it is only told about changes.</summary>
            public string LastSent;
        }

        private Pending _pending;

        // The group of each member: the last start it took part in
        private readonly Dictionary<string, Group> _groupOf = new();

        // When each member last opened a restart vote
        private readonly Dictionary<string, double> _lastVoteAt = new();

        /// <summary>Lead time between "everyone is ready" and playback starting, in seconds.</summary>
        public double LeadSeconds;

        /// <summary>Give up waiting for stragglers after this long, in seconds.</summary>
        public double TimeoutSeconds;

        /// <summary>A restart vote that hasn't passed after this long, in seconds, ends without a restart.</summary>
        public double RestartVoteSeconds = 30.0;

        /// <summary>A joiner can't open a restart vote within this long of opening its last one, in seconds.</summary>
        public double RestartCooldownSeconds = 30.0;

        public SyncStartCoordinator(double leadSeconds, double timeoutSeconds)
        {
            LeadSeconds = leadSeconds;
            TimeoutSeconds = timeoutSeconds;
        }

        public SongInfo PendingSong => _pending?.Song;

        /// <summary>Who we're still waiting on.</summary>
        public List<string> WaitingOn(IEnumerable<Present> present)
        {
            if (_pending == null) return new List<string>();
            return present
                .Select(x => x.Member)
                .Where(m => !_pending.Ready.Contains(m) && !_pending.Out.Contains(m))
                .ToList();
        }

        public List<Action> OnReady(string member, SongInfo song, IReadOnlyList<Present> present, double now)
        {
            var actions = new List<Action>();
            if (_pending != null && _pending.Song.Hash == song.Hash)
            {
                _pending.Ready.Add(member);
                _pending.Out.Remove(member);
            }
            else if (_pending != null)
            {
                actions.Add(new Action(member, Command.Cancel($"\"{_pending.Song.Name}\" is already starting")));
                return actions;
            }
            else
            {
                _pending = new Pending { Song = song, Since = now };
                _pending.Ready.Add(member);
                foreach (var x in present)
                {
                    if (x.Member == member) continue;

                    if (x.InMenu)
                    {
                        actions.Add(new Action(x.Member, Command.LoadSong(song)));
                    }
                    else
                    {
                        // Mid-song, or somewhere else that can't take a song (calibration, say)
                        _pending.Out.Add(x.Member);
                    }
                }
            }

            actions.AddRange(Check(present));
            return actions;
        }

        /// <summary>
        /// <paramref name="requester"/> wants to play <paramref name="song"/> again, from the start, with its
        /// group. <paramref name="play"/> is the play of the song it is in. From the host
        /// (<paramref name="fromHost"/>), the song restarts straight away; from a joiner, this opens or joins
        /// the group's vote. Refer to the remarks on this class.
        /// </summary>
        public List<Action> OnRestart(string requester, SongInfo song, string play, bool fromHost,
            IReadOnlyList<Present> present, double now)
        {
            var actions = new List<Action>();
            if (!_groupOf.TryGetValue(requester, out var group) || group.Song.Hash != song.Hash ||
                !IsInSong(requester, present))
            {
                actions.Add(new Action(requester, Command.Cancel("you aren't playing that song with the band")));
                return actions;
            }

            if (_pending != null)
            {
                actions.Add(new Action(requester, Command.Cancel($"\"{_pending.Song.Name}\" is already starting")));
                return actions;
            }

            if (fromHost)
            {
                EndVote(group, null, actions);
                BeginRestart(group, requester, play, present, now, actions);
                return actions;
            }

            var vote = group.Vote;
            if (vote == null)
            {
                if (_lastVoteAt.TryGetValue(requester, out double last) && now - last < RestartCooldownSeconds)
                {
                    int wait = (int) Math.Ceiling(RestartCooldownSeconds - (now - last));
                    actions.Add(new Action(requester, Command.Cancel(
                        $"you asked a moment ago. You can ask again in {wait} s")));
                    return actions;
                }

                _lastVoteAt[requester] = now;
                vote = group.Vote = new Vote { Since = now };
            }

            if (!vote.Yes.Any(v => v.Member == requester))
            {
                vote.Yes.Add((requester, play));
            }
            else
            {
                // Asked again: tell them where the vote is, even if nothing changed
                vote.LastSent = null;
            }

            Evaluate(group, present, now, actions);
            return actions;
        }

        /// <summary>
        /// <paramref name="member"/>'s song carried on while its group voted on a restart of
        /// <paramref name="hash"/>, so the vote ends without a restart.
        /// </summary>
        public List<Action> OnRestartWithdraw(string member, string hash, IReadOnlyList<Present> present)
        {
            var actions = new List<Action>();
            if (_groupOf.TryGetValue(member, out var group) && group.Vote != null && group.Song.Hash == hash)
            {
                EndVote(group, "the song carried on", actions);
            }
            return actions;
        }

        public List<Action> OnDecline(string member, string hash, IReadOnlyList<Present> present)
        {
            if (_pending == null || _pending.Song.Hash != hash)
            {
                return new List<Action>();
            }

            _pending.Ready.Remove(member);
            _pending.Out.Add(member);
            return Check(present);
        }

        /// <summary>Call when members join or leave, and about once a second.</summary>
        public List<Action> OnTick(IReadOnlyList<Present> present, double now)
        {
            var actions = new List<Action>();

            foreach (var group in _groupOf.Values.Distinct().ToList())
            {
                if (group.Vote != null) Evaluate(group, present, now, actions);
            }

            if (_pending == null) return actions;

            if (now - _pending.Since >= TimeoutSeconds)
            {
                foreach (var x in present)
                {
                    if (!_pending.Ready.Contains(x.Member))
                    {
                        _pending.Out.Add(x.Member);
                    }
                }
            }

            actions.AddRange(Check(present));
            return actions;
        }

        /// <summary>Start if every present, participating member is ready.</summary>
        private List<Action> Check(IReadOnlyList<Present> present)
        {
            var actions = new List<Action>();
            if (_pending == null) return actions;

            if (_pending.Restart)
            {
                // Left gameplay before it was ready (quit to the menu, say), or joined the session since
                foreach (var x in present)
                {
                    if (!x.InSong && !_pending.Ready.Contains(x.Member)) _pending.Out.Add(x.Member);
                }
            }

            var presentSet = new HashSet<string>(present.Select(x => x.Member));
            if (!presentSet.All(m => _pending.Ready.Contains(m) || _pending.Out.Contains(m)))
            {
                return actions;
            }

            var pending = _pending;
            _pending = null;

            var group = new Group { Song = pending.Song };
            long delayMs = (long) Math.Round(LeadSeconds * 1000);
            foreach (string member in pending.Ready.Where(presentSet.Contains).OrderBy(m => m, StringComparer.Ordinal))
            {
                actions.Add(new Action(member, Command.Start(delayMs)));
                JoinGroup(member, group, actions);
            }
            return actions;
        }

        private void JoinGroup(string member, Group group, List<Action> actions)
        {
            if (_groupOf.TryGetValue(member, out var old))
            {
                old.Members.Remove(member);
                if (old.Members.Count == 0) EndVote(old, null, actions);
            }
            group.Members.Add(member);
            _groupOf[member] = group;
        }

        private static bool IsInSong(string member, IReadOnlyList<Present> present) =>
            present.Any(x => x.Member == member && x.InSong);

        /// <summary>The members of <paramref name="group"/> in gameplay, in a fixed order.</summary>
        private static List<string> Players(Group group, IReadOnlyList<Present> present) =>
            present.Where(x => x.InSong && group.Members.Contains(x.Member))
                .Select(x => x.Member)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

        /// <summary>Restart the group's song if its vote passed, end it if it can't, else tell the group.</summary>
        private void Evaluate(Group group, IReadOnlyList<Present> present, double now, List<Action> actions)
        {
            var vote = group.Vote;
            var players = Players(group, present);

            // Who asked, and then left the song, doesn't count
            vote.Yes.RemoveAll(v => !players.Contains(v.Member));

            if (vote.Yes.Count == 0)
            {
                EndVote(group, "everyone who asked left the song", actions);
                return;
            }

            if (players.All(m => vote.Yes.Any(v => v.Member == m)))
            {
                if (_pending != null)
                {
                    EndVote(group, $"\"{_pending.Song.Name}\" is already starting", actions);
                    return;
                }

                var (requester, play) = vote.Yes[0];
                EndVote(group, null, actions);
                BeginRestart(group, requester, play, present, now, actions);
                return;
            }

            if (now - vote.Since >= RestartVoteSeconds)
            {
                EndVote(group, "not everyone agreed in time", actions);
                return;
            }

            var status = new RestartVote
            {
                Song = group.Song,
                Yes = vote.Yes.Select(v => v.Member).ToList(),
                Waiting = players.Where(m => vote.Yes.All(v => v.Member != m)).ToList(),
            };
            string key = string.Join(",", status.Yes) + "|" + string.Join(",", status.Waiting);
            if (key == vote.LastSent) return;

            vote.LastSent = key;
            vote.Told.UnionWith(players);
            foreach (string member in players)
            {
                actions.Add(new Action(member, Command.RestartVoteChanged(status)));
            }
        }

        /// <summary>
        /// End the group's vote, if it has one, and tell everyone who heard about it. A <c>null</c>
        /// <paramref name="reason"/> means the song restarts.
        /// </summary>
        private static void EndVote(Group group, string reason, List<Action> actions)
        {
            var vote = group.Vote;
            if (vote == null) return;

            group.Vote = null;
            var status = new RestartVote
            {
                Song = group.Song,
                Yes = vote.Yes.Select(v => v.Member).ToList(),
                Ended = true,
                Reason = reason,
            };
            foreach (string member in vote.Told.OrderBy(m => m, StringComparer.Ordinal))
            {
                actions.Add(new Action(member, Command.RestartVoteChanged(status)));
            }
        }

        /// <summary>Tell the members of the group in gameplay to load the song again, and wait for them.</summary>
        private void BeginRestart(Group group, string requester, string play, IReadOnlyList<Present> present,
            double now, List<Action> actions)
        {
            var players = new HashSet<string>(Players(group, present));
            _pending = new Pending { Song = group.Song, Since = now, Restart = true };
            foreach (var x in present)
            {
                if (players.Contains(x.Member))
                {
                    actions.Add(new Action(x.Member, Command.Restart(group.Song, requester, play)));
                }
                else
                {
                    _pending.Out.Add(x.Member);
                }
            }

            actions.AddRange(Check(present));
        }
    }
}
