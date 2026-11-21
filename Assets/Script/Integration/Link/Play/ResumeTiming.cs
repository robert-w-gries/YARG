using System;

namespace YARG.Integration.Link.Play
{
    /// <summary>
    /// The timing of a synced resume. The host picks an input time to resume from (<c>PauseCoordinator</c>) and
    /// sends it with a lead (<see cref="LEAD"/>). Each game, joiners allowing for half the round trip, rewinds
    /// to that time and plays on at the same moment.
    /// </summary>
    public static class ResumeTiming
    {
        /// <summary>
        /// How far ahead the host schedules a resume, in seconds, which has to cover the trip to every joiner and
        /// the rewind animation.
        /// </summary>
        public const double LEAD = 1.5;

        /// <summary>The longest lead we accept from a host, in seconds, so a bad value can't hold us paused for long.</summary>
        public const double MAX_LEAD = 10.0;

        /// <summary>
        /// How long the resume's rewind animation runs before the song plays, in seconds
        /// (<c>SongRunner.RewindAndResume</c>), so it can start this early and finish on time.
        /// </summary>
        public const double REWIND_ANIMATION = 0.5;

        /// <summary>
        /// Ignore friends' clocks for this long after the song plays again, in seconds: their first inputs
        /// messages can still carry the paused time.
        /// </summary>
        public const double CLOCK_HOLD = 1.0;

        public static long LeadMs => (long) Math.Round(LEAD * 1000);

        /// <summary>Joiner: when the song plays again, on our clock, for a resume that the host sent.</summary>
        /// <param name="oneWay">Half the round trip to the host, if known.</param>
        public static double ScheduleAt(double now, long delayMs, double? oneWay) =>
            now + Math.Clamp(delayMs / 1000.0, 0, MAX_LEAD) - (oneWay ?? 0);

        /// <summary>Whether to start the rewind animation now, for a song that plays again at <paramref name="at"/>.</summary>
        public static bool ShouldStart(double at, double now) => now >= at - REWIND_ANIMATION;

        public readonly struct Plan
        {
            /// <summary>How late we start the rewind, in seconds (the resume arrived late, or a long frame).</summary>
            public readonly double Late;

            /// <summary>How far to rewind from our pause, in seconds of input time.</summary>
            public readonly double Rewind;

            /// <summary>
            /// How far behind the others we'll start, in seconds of input time: a resume can't go past our pause.
            /// Clock correction takes care of it.
            /// </summary>
            public readonly double Behind;

            public Plan(double late, double rewind, double behind)
            {
                Late = late;
                Rewind = rewind;
                Behind = behind;
            }
        }

        /// <summary>
        /// The rewind for a resume from <paramref name="resumeInputTime"/> that plays at <paramref name="at"/>.
        /// If we start too late for the song to play at that moment, the others are already that far into the
        /// song, so resume that much further on, as far as our pause allows.
        /// </summary>
        public static Plan PlanRewind(double pausedInputTime, double resumeInputTime, double at, double now,
            double songSpeed)
        {
            double late = Math.Max(0, now + REWIND_ANIMATION - at);
            double resumeFrom = resumeInputTime + late * songSpeed;
            double rewind = pausedInputTime - resumeFrom;
            return rewind < 0 ? new Plan(late, 0, -rewind) : new Plan(late, rewind, 0);
        }
    }
}
