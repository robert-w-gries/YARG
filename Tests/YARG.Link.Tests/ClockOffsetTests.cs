using System;
using NUnit.Framework;
using YARG.Integration.Link.Session;

namespace YARG.Link.Tests
{
    public class ClockOffsetTests
    {
        private const double ONE_WAY = 0.04;
        private const double STEP = 1.0 / 30;

        /// <summary>
        /// Feed messages from a friend whose clock is <paramref name="offset"/> behind ours, each arriving
        /// late by the one-way latency plus <paramref name="extraDelay"/>(i).
        /// </summary>
        private static double Feed(ClockOffsetEstimator c, double offset, double start, double seconds,
            Func<int, double> extraDelay = null, double songSpeed = 1.0)
        {
            int count = (int) (seconds / STEP);
            double now = start;
            for (int i = 0; i < count; i++)
            {
                double sent = start + i * STEP;
                now = sent + ONE_WAY + (extraDelay?.Invoke(i) ?? 0);
                double remote = sent * songSpeed;
                double local = now * songSpeed + offset;
                c.AddSample(now, local, remote, ONE_WAY, songSpeed);
            }
            return now;
        }

        [Test]
        public void NoEstimateUntilEnoughData()
        {
            var c = new ClockOffsetEstimator();
            Feed(c, 0.05, 0, 0.5);
            Assert.That(c.HasEstimate, Is.False);

            Feed(c, 0.05, 0.5, 1.0);
            Assert.That(c.HasEstimate, Is.True);
        }

        [TestCase(0.0)]
        [TestCase(0.08)]
        [TestCase(-0.12)]
        public void MeasuresOffset(double offset)
        {
            var c = new ClockOffsetEstimator();
            Feed(c, offset, 0, 2);
            Assert.That(c.Offset, Is.EqualTo(offset).Within(1e-9));
        }

        [Test]
        public void IgnoresLateMessages()
        {
            // Most messages delayed by jitter or retransmits; the on-time ones give the answer
            var c = new ClockOffsetEstimator();
            Feed(c, 0.03, 0, 2, i => i % 7 == 0 ? 0 : 0.01 + (i % 5) * 0.02);
            Assert.That(c.Offset, Is.EqualTo(0.03).Within(1e-9));
        }

        [Test]
        public void AccountsForSongSpeed()
        {
            var c = new ClockOffsetEstimator();
            Feed(c, 0.05, 0, 2, songSpeed: 1.5);
            Assert.That(c.Offset, Is.EqualTo(0.05).Within(1e-9));
        }

        [Test]
        public void ShiftMovesEstimate()
        {
            var c = new ClockOffsetEstimator();
            double now = Feed(c, 0.05, 0, 2);
            c.Shift(-0.02);
            Assert.That(c.Offset, Is.EqualTo(0.03).Within(1e-9));

            // New samples reflect the shifted clock, and the window isn't reset by the shift
            Feed(c, 0.03, now, 1);
            Assert.That(c.Offset, Is.EqualTo(0.03).Within(1e-9));
            Assert.That(c.Resets, Is.Zero);
        }

        [Test]
        public void OldSamplesExpire()
        {
            var c = new ClockOffsetEstimator();
            double now = Feed(c, -0.01, 0, 2);
            // A gradual drift (not a jump) to a new offset
            Feed(c, 0.02, now, ClockOffsetEstimator.WINDOW_SECONDS + 1);
            Assert.That(c.Offset, Is.EqualTo(0.02).Within(1e-9));
        }

        [Test]
        public void FriendPausingResets()
        {
            var c = new ClockOffsetEstimator();
            double now = Feed(c, 0, 0, 2);
            Assert.That(c.HasEstimate, Is.True);

            // Their clock stops while messages keep coming
            double frozen = now - ONE_WAY;
            for (int i = 1; i <= 30; i++)
            {
                c.AddSample(now + i * STEP, now + i * STEP, frozen, ONE_WAY, 1);
            }
            Assert.That(c.Resets, Is.GreaterThan(0));
            Assert.That(c.HasEstimate, Is.False);
        }

        [Test]
        public void JumpResets()
        {
            var c = new ClockOffsetEstimator();
            double now = Feed(c, 0, 0, 2);
            Feed(c, 1.5, now, 2);
            Assert.That(c.Resets, Is.EqualTo(1));
            Assert.That(c.Offset, Is.EqualTo(1.5).Within(1e-9));
        }

        [Test]
        public void ResetForgets()
        {
            var c = new ClockOffsetEstimator();
            Feed(c, 0.05, 0, 2);
            c.Reset();
            Assert.That(c.HasEstimate, Is.False);
        }
    }
}
