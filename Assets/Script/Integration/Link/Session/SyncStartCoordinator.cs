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

            // Tests compare expected commands with the actual commands. Thus, compare all fields. A vote is a
            // class, so compare it by reference.
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

        /// <summary>One member's request to restart: who asked, and the play of the song it is in.</summary>
        private readonly struct Ask
        {
            public readonly string Member;
            public readonly string Play;

            /// <summary>The host asked. The host's ask passes the vote alone.</summary>
            public readonly bool FromHost;

            public Ask(string member, string play, bool fromHost)
            {
                Member = member;
                Play = play;
                FromHost = fromHost;
            }
        }

        /// <summary>The result of a restart vote at one moment. <see cref="DecideVote"/> sets it.</summary>
        private enum VoteResult
        {
            /// <summary>The vote continues. Tell the group the current count.</summary>
            Open,
            /// <summary>Restart the song. Then the vote ends.</summary>
            Passed,
            /// <summary>Do not restart the song. The vote ends.</summary>
            Failed,
        }

        private sealed class Vote
        {
            public double Since;

            /// <summary>Who asked, in order.</summary>
            public readonly List<Ask> Yes = new();

            /// <summary>Who was last told about the vote, so the end reaches them too.</summary>
            public readonly HashSet<string> Told = new();

            /// <summary>What the group was last told, so it is only told about changes.</summary>
            public string LastSent;

            public bool HasAsked(string member) => Yes.Any(a => a.Member == member);
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
            string refusal = RestartRefusal(requester, song, fromHost, present, now, out var group);
            if (refusal != null)
            {
                actions.Add(new Action(requester, Command.Cancel(refusal)));
                return actions;
            }

            if (group.Vote == null)
            {
                group.Vote = new Vote { Since = now };
                if (!fromHost) _lastVoteAt[requester] = now;
            }

            var vote = group.Vote;
            if (vote.HasAsked(requester))
            {
                // Asked again: tell them where the vote is, even if nothing changed
                vote.LastSent = null;
            }
            else
            {
                vote.Yes.Add(new Ask(requester, play, fromHost));
            }

            EvaluateRestart(group, present, now, actions);
            return actions;
        }

        /// <summary>
        /// Find why the coordinator must refuse the restart request of <paramref name="requester"/>.
        /// </summary>
        /// <returns>The reason for the refusal, or <c>null</c> if the request is valid.</returns>
        private string RestartRefusal(string requester, SongInfo song, bool fromHost, IReadOnlyList<Present> present,
            double now, out Group group)
        {
            if (!IsInPlay(requester, song, present, out group))
            {
                return "you aren't playing that song with the band";
            }

            if (_pending != null)
            {
                return $"\"{_pending.Song.Name}\" is already starting";
            }

            // Only a joiner that opens a new vote has to wait. Anyone can join an open vote.
            if (!fromHost && group.Vote == null &&
                _lastVoteAt.TryGetValue(requester, out double last) && now - last < RestartCooldownSeconds)
            {
                int wait = (int) Math.Ceiling(RestartCooldownSeconds - (now - last));
                return $"you asked a moment ago. You can ask again in {wait} s";
            }

            return null;
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

            // A vote can pass or fail without a new request: a member leaves the song, or the time limit
            // ends. Thus, examine each open vote again. Many members share one group, so examine each group
            // one time only.
            foreach (var group in _groupOf.Values.Distinct().ToList())
            {
                if (group.Vote != null) EvaluateRestart(group, present, now, actions);
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

                // Record who plays this song together. A restart request later uses this group.
                JoinGroup(member, group, actions);
            }
            return actions;
        }

        /// <summary>
        /// Move <paramref name="member"/> into <paramref name="group"/>. A member is in one group only: the
        /// group of the last start it took part in. If the old group has no members after this, its open vote
        /// ends.
        /// </summary>
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

        /// <summary>
        /// Whether <paramref name="member"/> plays <paramref name="song"/> now, with the group that started it.
        /// </summary>
        private bool IsInPlay(string member, SongInfo song, IReadOnlyList<Present> present, out Group group) =>
            _groupOf.TryGetValue(member, out group) && group.Song.Hash == song.Hash &&
            present.Any(x => x.Member == member && x.InSong);

        /// <summary>The members of <paramref name="group"/> in gameplay, in a fixed order.</summary>
        private static List<string> SongPlayers(Group group, IReadOnlyList<Present> present) =>
            present.Where(x => x.InSong && group.Members.Contains(x.Member))
                .Select(x => x.Member)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Examine the open restart vote of <paramref name="group"/> and do what its result requires:
        /// <list type="number">
        /// <item>Remove the requests of members that are not in the song now.</item>
        /// <item>Get the result from <see cref="DecideVote"/>.</item>
        /// <item>If the vote passed, end it and restart the song.</item>
        /// <item>If the vote failed, end it and tell the members why.</item>
        /// <item>If the vote is open, tell the members the count, if it changed.</item>
        /// </list>
        /// </summary>
        private void EvaluateRestart(Group group, IReadOnlyList<Present> present, double now, List<Action> actions)
        {
            var vote = group.Vote;
            var players = SongPlayers(group, present);
            vote.Yes.RemoveAll(a => !players.Contains(a.Member));

            switch (DecideVote(vote, players, now, out string reason))
            {
                case VoteResult.Passed:
                    // The host's request decides the restart. Else, the first request does.
                    var ask = vote.Yes.FirstOrDefault(a => a.FromHost);
                    if (ask.Member == null) ask = vote.Yes[0];
                    EndVote(group, null, actions);
                    BeginRestart(group, ask.Member, ask.Play, present, now, actions);
                    break;

                case VoteResult.Failed:
                    EndVote(group, reason, actions);
                    break;

                default:
                    TellVote(group, players, actions);
                    break;
            }
        }

        /// <summary>
        /// Get the result of <paramref name="vote"/>. This is the only place that decides the result. The
        /// rules, in this order:
        /// <list type="number">
        /// <item>If no member that asked is in the song now, the vote fails.</item>
        /// <item>
        /// If the host asked, or all <paramref name="songPlayers"/> asked, the vote passes. But if a different
        /// start is pending, the vote fails, because only one start can be pending.
        /// </item>
        /// <item>If the vote is older than <see cref="RestartVoteSeconds"/>, it fails.</item>
        /// <item>Else, the vote stays open.</item>
        /// </list>
        /// </summary>
        /// <param name="reason">If the vote failed: why, for people. Else <c>null</c>.</param>
        private VoteResult DecideVote(Vote vote, List<string> songPlayers, double now, out string reason)
        {
            reason = null;

            if (vote.Yes.Count == 0)
            {
                reason = "everyone who asked left the song";
                return VoteResult.Failed;
            }

            bool agreed = vote.Yes.Any(a => a.FromHost) || songPlayers.All(vote.HasAsked);
            if (agreed && _pending != null)
            {
                reason = $"\"{_pending.Song.Name}\" is already starting";
                return VoteResult.Failed;
            }
            if (agreed)
            {
                return VoteResult.Passed;
            }

            if (now - vote.Since >= RestartVoteSeconds)
            {
                reason = "not everyone agreed in time";
                return VoteResult.Failed;
            }

            return VoteResult.Open;
        }

        /// <summary>Tell the members of the group in the song the count of the open vote, if it changed.</summary>
        private static void TellVote(Group group, List<string> songPlayers, List<Action> actions)
        {
            var vote = group.Vote;
            var status = new RestartVote
            {
                Song = group.Song,
                Yes = vote.Yes.Select(a => a.Member).ToList(),
                Waiting = songPlayers.Where(m => !vote.HasAsked(m)).ToList(),
            };
            string key = string.Join(",", status.Yes) + "|" + string.Join(",", status.Waiting);
            if (key == vote.LastSent) return;

            vote.LastSent = key;
            vote.Told.UnionWith(songPlayers);
            foreach (string member in songPlayers)
            {
                actions.Add(new Action(member, Command.RestartVoteChanged(status)));
            }
        }

        /// <summary>
        /// End the group's vote, if it has one, and tell everyone who heard about it. A <c>null</c>
        /// <paramref name="reason"/> means the song restarts. Like all of this class, it runs on the host only.
        /// </summary>
        private static void EndVote(Group group, string reason, List<Action> actions)
        {
            var vote = group.Vote;
            if (vote == null) return;

            group.Vote = null;
            var status = new RestartVote
            {
                Song = group.Song,
                Yes = vote.Yes.Select(a => a.Member).ToList(),
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
            var players = new HashSet<string>(SongPlayers(group, present));
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
