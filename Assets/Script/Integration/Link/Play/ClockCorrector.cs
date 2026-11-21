using System;

namespace YARG.Integration.Link.Play
{
    /// <summary>
    /// A joiner's decisions to move its song clock toward the host's, from the estimated offset between the two.
    /// Pure: the caller measures the offset (<c>ClockOffsetEstimator</c>) and moves the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Small differences are eased away: the song runs slightly fast or slow (<see cref="SLEW_RATE"/>), which the
    /// audio sync follows without a seek. Correction starts at <see cref="START_THRESHOLD"/> and stops below
    /// <see cref="STOP_THRESHOLD"/>, so it doesn't chase noise.
    /// </para>
    /// <para>
    /// Easing a large difference would take most of a song, so from <see cref="JUMP_THRESHOLD"/> the joiner
    /// seeks into step once instead. The player hears a jump, and a jump forward misses the notes it skips.
    /// </para>
    /// </remarks>
    public sealed class ClockCorrector
    {
        /// <summary>Start correcting once our clock is this far from the host's, in seconds.</summary>
        public const double START_THRESHOLD = 0.010;

        /// <summary>Stop correcting once we're within this of the host's clock, in seconds.</summary>
        public const double STOP_THRESHOLD = 0.003;

        /// <summary>
        /// How fast to correct, in seconds per second: 0.02 runs our song at 98% or 102% speed while correcting,
        /// so 100 ms takes 5 seconds.
        /// </summary>
        public const double SLEW_RATE = 0.02;

        /// <summary>From this far apart, in seconds, seek into step instead of easing (25 s at the slew rate).</summary>
        public const double JUMP_THRESHOLD = 0.5;

        /// <summary>
        /// The least time between two jumps, in seconds. A jump starts the estimate over, so the next one is
        /// based on new samples. If those still say we're far apart so soon, the estimate is likely wrong, so
        /// ease meanwhile instead of jumping again.
        /// </summary>
        public const double JUMP_COOLDOWN = 10.0;

        public enum EaseResult
        {
            /// <summary>Close enough; nothing to do.</summary>
            Idle,
            /// <summary>Started correcting with this step.</summary>
            Started,
            /// <summary>Correcting with this step.</summary>
            Easing,
            /// <summary>Back in step; stopped correcting.</summary>
            Stopped,
        }

        private double _lastJump = double.NegativeInfinity;

        /// <summary>Easing toward the host right now.</summary>
        public bool Correcting { get; private set; }

        /// <summary>How far our clock has been moved in total this song, in seconds.</summary>
        public double Corrected { get; private set; }

        /// <summary>
        /// Whether to seek into step now: our clock is <paramref name="offset"/> seconds ahead of the host's
        /// (negative: behind). If the seek isn't possible, call <see cref="Ease"/> instead.
        /// </summary>
        public bool ShouldJump(double offset, double now) =>
            Math.Abs(offset) >= JUMP_THRESHOLD && now - _lastJump >= JUMP_COOLDOWN;

        /// <summary>The caller moved the clock by <paramref name="seconds"/> with a seek.</summary>
        public void Jumped(double seconds, double now)
        {
            _lastJump = now;
            Correcting = false;
            Corrected += seconds;
        }

        /// <summary>
        /// The step to move our clock by this frame, at most the slew rate of <paramref name="deltaTime"/> (in
        /// song time, so <paramref name="songSpeed"/> scales it).
        /// </summary>
        public EaseResult Ease(double offset, double deltaTime, double songSpeed, out double step)
        {
            step = 0;
            double distance = Math.Abs(offset);

            var result = EaseResult.Easing;
            if (!Correcting)
            {
                if (distance < START_THRESHOLD) return EaseResult.Idle;

                Correcting = true;
                result = EaseResult.Started;
            }
            else if (distance < STOP_THRESHOLD)
            {
                Correcting = false;
                return EaseResult.Stopped;
            }

            double maxStep = SLEW_RATE * deltaTime * songSpeed;
            step = Math.Clamp(-offset, -maxStep, maxStep);
            Corrected += step;
            return result;
        }

        /// <summary>Our clock stopped (a pause), so start measuring over: stop correcting.</summary>
        public void Pause() => Correcting = false;
    }
}
