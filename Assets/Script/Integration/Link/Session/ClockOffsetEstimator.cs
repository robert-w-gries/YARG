using System;
using System.Collections.Generic;

namespace YARG.Integration.Link.Session
{
    /// <summary>
    /// Estimates how far our song clock is from a friend's, as a pure state machine (time passed in) so it
    /// can be tested.
    /// </summary>
    /// <remarks>
    /// Each message from the friend carries their input time when they sent it. Adding the one-way latency
    /// gives their input time now; our input time minus that is one sample of the offset. Queueing, frame
    /// timing and retransmits only ever make a message later, which pushes samples up, so the smallest sample
    /// in a short window is the best estimate. A pause, seek or rewind on either side breaks the relationship
    /// between the clocks, so the window starts over when that happens.
    /// </remarks>
    public sealed class ClockOffsetEstimator
    {
        /// <summary>How much history the estimate is taken from, in seconds.</summary>
        public const double WINDOW_SECONDS = 3.0;

        /// <summary>Samples needed before there's an estimate.</summary>
        public const int MIN_SAMPLES = 10;

        /// <summary>The samples must also span at least this long, in seconds.</summary>
        public const double MIN_SPAN_SECONDS = 1.0;

        /// <summary>
        /// If either clock moves this much more or less than real time over the window, one of them paused or
        /// jumped, and the earlier samples no longer apply. Compared against the oldest sample rather than the
        /// previous one, so a paused clock is caught however often the messages come.
        /// </summary>
        public const double DISCONTINUITY_SECONDS = 0.25;

        private readonly struct Sample
        {
            public readonly double Now;
            public readonly double Local;
            public readonly double Remote;
            public readonly double Offset;

            public Sample(double now, double local, double remote, double offset)
            {
                Now = now;
                Local = local;
                Remote = remote;
                Offset = offset;
            }

            public Sample Shifted(double delta) => new(Now, Local + delta, Remote, Offset + delta);
        }

        private readonly List<Sample> _samples = new();

        /// <summary>Times the window started over because a clock paused or jumped.</summary>
        public int Resets { get; private set; }

        /// <summary>Whether there's enough data for <see cref="Offset"/>.</summary>
        public bool HasEstimate => _samples.Count >= MIN_SAMPLES &&
            _samples[_samples.Count - 1].Now - _samples[0].Now >= MIN_SPAN_SECONDS;

        /// <summary>
        /// Our input time minus the friend's, in input-time seconds: positive means we're ahead. Only meaningful
        /// when <see cref="HasEstimate"/>.
        /// </summary>
        public double Offset
        {
            get
            {
                double min = double.PositiveInfinity;
                foreach (var sample in _samples)
                {
                    min = Math.Min(min, sample.Offset);
                }
                return min;
            }
        }

        /// <summary>
        /// Add a sample.
        /// </summary>
        /// <param name="now">Real time it arrived, in seconds.</param>
        /// <param name="localTime">Our input time when it arrived.</param>
        /// <param name="remoteTime">The friend's input time when they sent it.</param>
        /// <param name="oneWay">One-way latency to the friend, in real seconds.</param>
        /// <param name="songSpeed">Input time per real second.</param>
        public void AddSample(double now, double localTime, double remoteTime, double oneWay, double songSpeed)
        {
            if (double.IsNaN(localTime) || double.IsNaN(remoteTime) || double.IsNaN(oneWay))
            {
                return;
            }

            if (_samples.Count > 0)
            {
                var first = _samples[0];
                double expected = (now - first.Now) * songSpeed;
                bool localJumped = Math.Abs(localTime - first.Local - expected) > DISCONTINUITY_SECONDS;
                bool remoteJumped = Math.Abs(remoteTime - first.Remote - expected) > DISCONTINUITY_SECONDS;
                if (localJumped || remoteJumped || now < _samples[_samples.Count - 1].Now)
                {
                    _samples.Clear();
                    Resets++;
                }
            }

            int expired = 0;
            while (expired < _samples.Count && now - _samples[expired].Now > WINDOW_SECONDS)
            {
                expired++;
            }
            _samples.RemoveRange(0, expired);

            _samples.Add(new Sample(now, localTime, remoteTime, localTime - (remoteTime + oneWay * songSpeed)));
        }

        /// <summary>
        /// We moved our own clock by <paramref name="delta"/> input-time seconds; adjust what's been measured.
        /// </summary>
        public void Shift(double delta)
        {
            for (int i = 0; i < _samples.Count; i++)
            {
                _samples[i] = _samples[i].Shifted(delta);
            }
        }

        /// <summary>Forget everything, e.g. after we pause.</summary>
        public void Reset()
        {
            _samples.Clear();
        }
    }
}
