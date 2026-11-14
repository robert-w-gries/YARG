using System;
using System.Collections.Generic;
using System.Linq;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Synced song start, as a pure state machine (no I/O, time passed in) so it can be tested.
    /// </summary>
    /// <remarks>
    /// Runs on the host. Any member can start a song; when its gameplay is loaded it reports ready. The
    /// coordinator then asks every other member who is in the menu to load the same song, waits until all of
    /// them are ready (or decline, leave, or time out), and tells everyone who is ready to start after a
    /// common lead time.
    /// </remarks>
    public sealed class SyncStartCoordinator
    {
        /// <summary>A member currently in the session.</summary>
        public readonly struct Present
        {
            public readonly string Member;

            /// <summary>In the menu, i.e. able to be asked to load a song.</summary>
            public readonly bool InMenu;

            public Present(string member, bool inMenu)
            {
                Member = member;
                InMenu = inMenu;
            }
        }

        public enum CommandType
        {
            LoadSong,
            Start,
            Cancel,
        }

        public readonly struct Command : IEquatable<Command>
        {
            public readonly CommandType Type;
            public readonly SongInfo    Song;
            public readonly long        DelayMs;
            public readonly string      Reason;

            private Command(CommandType type, SongInfo song, long delayMs, string reason)
            {
                Type = type;
                Song = song;
                DelayMs = delayMs;
                Reason = reason;
            }

            public static Command LoadSong(SongInfo song) => new(CommandType.LoadSong, song, 0, null);
            public static Command Start(long delayMs) => new(CommandType.Start, null, delayMs, null);
            public static Command Cancel(string reason) => new(CommandType.Cancel, null, 0, reason);

            public bool Equals(Command other) => Type == other.Type && Equals(Song, other.Song) &&
                DelayMs == other.DelayMs && Reason == other.Reason;

            public override bool Equals(object obj) => obj is Command other && Equals(other);
            public override int GetHashCode() => (int) Type ^ DelayMs.GetHashCode();

            public override string ToString() => Type switch
            {
                CommandType.LoadSong => $"LoadSong({Song})",
                CommandType.Start    => $"Start({DelayMs} ms)",
                _                    => $"Cancel({Reason})",
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
        }

        private Pending _pending;

        /// <summary>Lead time between "everyone is ready" and playback starting, in seconds.</summary>
        public double LeadSeconds;

        /// <summary>Give up waiting for stragglers after this long, in seconds.</summary>
        public double TimeoutSeconds;

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
                        // Mid-song or on the score screen
                        _pending.Out.Add(x.Member);
                    }
                }
            }

            actions.AddRange(Check(present));
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
            if (_pending == null) return new List<Action>();

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

            return Check(present);
        }

        /// <summary>Start if every present, participating member is ready.</summary>
        private List<Action> Check(IReadOnlyList<Present> present)
        {
            var actions = new List<Action>();
            if (_pending == null) return actions;

            var presentSet = new HashSet<string>(present.Select(x => x.Member));
            if (!presentSet.All(m => _pending.Ready.Contains(m) || _pending.Out.Contains(m)))
            {
                return actions;
            }

            var pending = _pending;
            _pending = null;

            long delayMs = (long) Math.Round(LeadSeconds * 1000);
            foreach (string member in pending.Ready.Where(presentSet.Contains).OrderBy(m => m, StringComparer.Ordinal))
            {
                actions.Add(new Action(member, Command.Start(delayMs)));
            }
            return actions;
        }
    }
}
