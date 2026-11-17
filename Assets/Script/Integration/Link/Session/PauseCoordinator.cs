using System;
using System.Collections.Generic;
using System.Linq;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Synced pause and resume during a song, as a pure state machine (no I/O, no Unity) so it can be tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every game in a synced song runs one. When anyone pauses, they tell everyone (with their input time
    /// at the pause), and everyone else pauses too. Pressing resume in the pause menu doesn't resume: it marks
    /// that member ready, and pressing it again takes that back.
    /// </para>
    /// <para>
    /// The host decides when to resume: once the host and every other member in the song are ready, it picks
    /// one input time for everyone to resume at (<see cref="ResumePlan.InputTime"/>), a bit before the latest
    /// pause so every game rewinds into it, and tells everyone to resume there at the same moment.
    /// </para>
    /// <para>
    /// Pauses are numbered (<see cref="Epoch"/>, which goes up with every synced resume), so a pause report
    /// that crosses a resume on the wire can't pause a game again or count toward the next pause.
    /// </para>
    /// </remarks>
    public sealed class PauseCoordinator
    {
        /// <summary>One member's part in the current pause.</summary>
        public readonly struct Entry
        {
            /// <summary>Their input time when they paused.</summary>
            public readonly double Time;

            /// <summary>They pressed resume and are waiting for the others.</summary>
            public readonly bool Ready;

            public Entry(double time, bool ready)
            {
                Time = time;
                Ready = ready;
            }
        }

        /// <summary>What the host tells everyone when it resumes.</summary>
        public readonly struct ResumePlan
        {
            /// <summary>The pause this resume ends.</summary>
            public readonly int Epoch;

            /// <summary>The input time every game resumes playing from.</summary>
            public readonly double InputTime;

            public ResumePlan(int epoch, double inputTime)
            {
                Epoch = epoch;
                InputTime = inputTime;
            }
        }

        public enum RemotePauseResult
        {
            /// <summary>For an earlier pause, or nonsense: nothing changed.</summary>
            Ignored,

            /// <summary>Noted for the current pause.</summary>
            Recorded,

            /// <summary>A friend paused while we're playing: pause our game too.</summary>
            PauseUs,
        }

        /// <summary>
        /// Rewind at least this far behind the latest pause on resume, like the game's own resume does, so
        /// players see the notes coming before they're due.
        /// </summary>
        public double MinRewind;

        /// <summary>
        /// Rewind this much further than the earliest pause, so no game has to skip ahead. Usually games pause
        /// within a round trip of each other, so this is only a margin.
        /// </summary>
        public double RewindMargin;

        private readonly string _localId;
        private readonly Dictionary<string, Entry> _entries = new();

        /// <summary>The current (or next) pause's number. Goes up with every synced resume.</summary>
        public int Epoch { get; private set; }

        /// <summary>We're in a synced pause, and haven't been told to resume yet.</summary>
        public bool Active { get; private set; }

        /// <summary>The member who started this pause, if we know.</summary>
        public string PausedBy { get; private set; }

        /// <summary>Whether we pressed resume and are waiting for the others.</summary>
        public bool LocalReady => _entries.TryGetValue(_localId, out var entry) && entry.Ready;

        /// <summary>Our own input time when we paused, if we're in a pause.</summary>
        public double? LocalTime => _entries.TryGetValue(_localId, out var entry) ? entry.Time : null;

        public PauseCoordinator(string localId, double minRewind = 1.0, double rewindMargin = 0.25)
        {
            _localId = localId ?? throw new ArgumentNullException(nameof(localId));
            MinRewind = minRewind;
            RewindMargin = rewindMargin;
        }

        /// <summary>
        /// Our game paused, on its own or because a friend did (<paramref name="pausedBy"/>).
        /// </summary>
        /// <returns>Whether this started a new pause rather than joining the current one.</returns>
        public bool OnLocalPause(double inputTime, string pausedBy = null)
        {
            bool started = !Active;
            if (started)
            {
                Active = true;
                PausedBy = pausedBy ?? _localId;
            }

            // Pausing again (say, during the countdown of a resume) takes back a ready
            _entries[_localId] = new Entry(inputTime, false);
            return started;
        }

        /// <summary>
        /// A friend's pause report. <paramref name="playing"/> says whether our game is playing right now (not
        /// paused, failed, or still loading).
        /// </summary>
        public RemotePauseResult OnRemotePause(string member, int epoch, double inputTime, bool ready, bool playing)
        {
            if (member == null || member == _localId || epoch < Epoch) return RemotePauseResult.Ignored;
            if (double.IsNaN(inputTime) || double.IsInfinity(inputTime)) return RemotePauseResult.Ignored;

            // A later pause than ours means we missed a resume; catch up, since the host's word goes
            if (epoch > Epoch)
            {
                Epoch = epoch;
                Active = false;
                _entries.Clear();
            }

            _entries[member] = new Entry(inputTime, ready);

            if (!Active)
            {
                Active = true;
                PausedBy = member;
                return playing ? RemotePauseResult.PauseUs : RemotePauseResult.Recorded;
            }

            return RemotePauseResult.Recorded;
        }

        /// <summary>We pressed resume (<c>true</c>), or reopened the pause menu (<c>false</c>).</summary>
        public void SetLocalReady(bool ready)
        {
            if (!Active || !_entries.TryGetValue(_localId, out var entry)) return;
            _entries[_localId] = new Entry(entry.Time, ready);
        }

        /// <summary>A member left the session or the song: stop waiting for them.</summary>
        public void RemoveMember(string member)
        {
            if (member != _localId) _entries.Remove(member);
        }

        /// <summary>Members in <paramref name="participants"/> who haven't pressed resume (or haven't paused).</summary>
        public List<string> WaitingOn(IEnumerable<string> participants)
        {
            if (!Active) return new List<string>();
            return participants
                .Where(m => !_entries.TryGetValue(m, out var entry) || !entry.Ready)
                .ToList();
        }

        /// <summary>Whether <paramref name="member"/> has reported this pause.</summary>
        public bool HasPaused(string member) => Active && _entries.ContainsKey(member);

        /// <summary>
        /// Host only: resume if everyone is ready. <paramref name="participants"/> is everyone playing this
        /// song, the host included.
        /// </summary>
        /// <returns>The plan to send everyone, or <c>null</c> if we're still waiting.</returns>
        public ResumePlan? TryResume(IReadOnlyCollection<string> participants)
        {
            if (!Active || !LocalReady) return null;
            if (WaitingOn(participants).Count > 0) return null;

            double latest = double.NegativeInfinity;
            double earliest = double.PositiveInfinity;
            foreach (string member in participants.Append(_localId).Distinct())
            {
                if (!_entries.TryGetValue(member, out var entry)) continue;
                latest = Math.Max(latest, entry.Time);
                earliest = Math.Min(earliest, entry.Time);
            }

            double time = Math.Min(latest - MinRewind, earliest - RewindMargin);
            var plan = new ResumePlan(Epoch, time);
            EndPause();
            return plan;
        }

        /// <summary>Joiner: the host resumed. Ignored unless it ends the current (or a later) pause.</summary>
        /// <returns>Whether to resume.</returns>
        public bool OnRemoteResume(int epoch)
        {
            if (epoch < Epoch) return false;

            Epoch = epoch;
            EndPause();
            return true;
        }

        /// <summary>The song ended, or we left it.</summary>
        public void Reset()
        {
            Epoch = 0;
            Active = false;
            PausedBy = null;
            _entries.Clear();
        }

        private void EndPause()
        {
            Epoch++;
            Active = false;
            PausedBy = null;
            _entries.Clear();
        }
    }
}
