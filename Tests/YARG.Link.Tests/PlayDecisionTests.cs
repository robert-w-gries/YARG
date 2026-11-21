using System.Collections.Generic;
using NUnit.Framework;
using YARG.Integration.Link.Play;
using static YARG.Integration.Link.Play.ClockCorrector;
using static YARG.Integration.Link.Play.DepartureTracker;

namespace YARG.Link.Tests
{
    public class ClockCorrectorTests
    {
        private const double FRAME = 1.0 / 60.0;

        [Test]
        public void SmallOffsetsAreLeftAlone()
        {
            var c = new ClockCorrector();
            Assert.That(c.Ease(START_THRESHOLD * 0.9, FRAME, 1.0, out double step), Is.EqualTo(EaseResult.Idle));
            Assert.That(step, Is.Zero);
            Assert.That(c.Correcting, Is.False);
        }

        [Test]
        public void EasesAtTheSlewRateTowardTheHost()
        {
            var c = new ClockCorrector();

            // 50 ms ahead: move back, by at most 2% of the frame
            Assert.That(c.Ease(0.05, FRAME, 1.0, out double step), Is.EqualTo(EaseResult.Started));
            Assert.That(step, Is.EqualTo(-SLEW_RATE * FRAME).Within(1e-12));

            Assert.That(c.Ease(0.049, FRAME, 1.5, out step), Is.EqualTo(EaseResult.Easing));
            Assert.That(step, Is.EqualTo(-SLEW_RATE * FRAME * 1.5).Within(1e-12), "song speed scales the step");

            // Never overshoots
            Assert.That(c.Ease(-0.0001 - STOP_THRESHOLD, 10.0, 1.0, out step), Is.EqualTo(EaseResult.Easing));
            Assert.That(step, Is.EqualTo(0.0001 + STOP_THRESHOLD).Within(1e-12));
        }

        [Test]
        public void KeepsCorrectingUntilWithinTheStopThreshold()
        {
            var c = new ClockCorrector();
            c.Ease(0.02, FRAME, 1.0, out _);

            // Below the start threshold, but not yet in step
            Assert.That(c.Ease(0.005, FRAME, 1.0, out _), Is.EqualTo(EaseResult.Easing));
            Assert.That(c.Ease(0.002, FRAME, 1.0, out double step), Is.EqualTo(EaseResult.Stopped));
            Assert.That(step, Is.Zero);
            Assert.That(c.Correcting, Is.False);
        }

        [Test]
        public void CountsTheTotalCorrection()
        {
            var c = new ClockCorrector();
            c.Ease(0.05, FRAME, 1.0, out double a);
            c.Ease(0.05, FRAME, 1.0, out double b);
            c.Jumped(-0.7, 0);
            Assert.That(c.Corrected, Is.EqualTo(a + b - 0.7).Within(1e-12));
        }

        [Test]
        public void JumpsOnlyFarApartAndNotTooOften()
        {
            var c = new ClockCorrector();
            Assert.That(c.ShouldJump(JUMP_THRESHOLD * 0.9, 100), Is.False);
            Assert.That(c.ShouldJump(-JUMP_THRESHOLD, 100), Is.True);

            c.Jumped(JUMP_THRESHOLD, 100);
            Assert.That(c.ShouldJump(JUMP_THRESHOLD, 100 + JUMP_COOLDOWN * 0.9), Is.False);
            Assert.That(c.ShouldJump(JUMP_THRESHOLD, 100 + JUMP_COOLDOWN), Is.True);
        }

        [Test]
        public void APauseStopsTheCorrection()
        {
            var c = new ClockCorrector();
            c.Ease(0.05, FRAME, 1.0, out _);
            c.Pause();
            Assert.That(c.Correcting, Is.False);
            Assert.That(c.Ease(0.005, FRAME, 1.0, out _), Is.EqualTo(EaseResult.Idle));
        }
    }

    public class HostLossTrackerTests
    {
        [Test]
        public void AnnouncesAfterTheGraceAndExpiresAtTheDeadline()
        {
            var t = new HostLossTracker();
            Assert.That(t.Watch("left", HostLossTracker.HOST_LEFT, now: 10), Is.False);
            Assert.That(t.Watching, Is.True);

            Assert.That(t.TryAnnounce(10 + HostLossTracker.GRACE * 0.5), Is.False);
            Assert.That(t.TryAnnounce(10 + HostLossTracker.GRACE), Is.True);
            Assert.That(t.TryAnnounce(11 + HostLossTracker.GRACE), Is.False, "once");

            Assert.That(t.TryExpire(10 + HostLossTracker.HOST_LEFT - 0.1), Is.False);
            Assert.That(t.TryExpire(10 + HostLossTracker.HOST_LEFT), Is.True);
            Assert.That(t.Watching, Is.False);
        }

        [Test]
        public void ALostConnectionCountsFromTheLastInputs()
        {
            var t = new HostLossTracker();
            t.Watch("lost", HostLossTracker.LOST_CONNECTION, now: 20, since: 17);
            Assert.That(t.Since, Is.EqualTo(17));
            Assert.That(t.Deadline, Is.EqualTo(17 + HostLossTracker.LOST_CONNECTION));
        }

        [Test]
        public void ANewReasonNeverMovesTheDeadlineLater()
        {
            var t = new HostLossTracker();
            t.Watch("lost", HostLossTracker.LOST_CONNECTION, now: 0);

            // Same reason: nothing changes
            Assert.That(t.Watch("lost", HostLossTracker.LOST_CONNECTION, now: 3), Is.False);
            Assert.That(t.Deadline, Is.EqualTo(HostLossTracker.LOST_CONNECTION));

            // The connection came back, but the host had left the song: a longer countdown, but not later
            Assert.That(t.Watch("left", HostLossTracker.HOST_LEFT, now: 4), Is.False, "not announced yet");
            Assert.That(t.Reason, Is.EqualTo("left"));
            Assert.That(t.Deadline, Is.EqualTo(HostLossTracker.LOST_CONNECTION));
        }

        [Test]
        public void TellsWhenTheReasonChangesAfterTheAnnouncement()
        {
            var t = new HostLossTracker();
            t.Watch("left", HostLossTracker.HOST_LEFT, now: 0);
            t.TryAnnounce(HostLossTracker.GRACE);
            Assert.That(t.Watch("lost", HostLossTracker.LOST_CONNECTION, now: 3), Is.True);
            Assert.That(t.Deadline, Is.EqualTo(3 + HostLossTracker.LOST_CONNECTION));
        }

        [Test]
        public void ClearSaysWhetherThePlayerSawTheCountdown()
        {
            var t = new HostLossTracker();
            Assert.That(t.Clear(), Is.False);

            t.Watch("left", HostLossTracker.HOST_LEFT, now: 0);
            Assert.That(t.Clear(), Is.False);

            t.Watch("left", HostLossTracker.HOST_LEFT, now: 0);
            t.TryAnnounce(HostLossTracker.GRACE);
            Assert.That(t.Clear(), Is.True);
            Assert.That(t.Watching, Is.False);
        }
    }

    public class DepartureTrackerTests
    {
        private static Friend Here(string id, bool host = false) => new(id, false, false, host);
        private static Friend Left(string id, bool host = false) => new(id, false, true, host);
        private static Friend Gone(string id) => new(id, true, false, false);

        [Test]
        public void HostDropsAFriendWhoLeftAfterTheGrace()
        {
            var t = new DepartureTracker();
            Assert.That(t.Check(Left("a"), weHost: true, now: 10), Is.EqualTo(Departure.None));
            Assert.That(t.TryGetGoneSince("a", out double since) && since == 10, Is.True);
            Assert.That(t.Check(Left("a"), true, 10 + LEFT_GRACE), Is.EqualTo(Departure.Left));
            Assert.That(t.TryGetGoneSince("a", out _), Is.False);
        }

        [Test]
        public void HostDropsALostFriendAfterTheDropTime()
        {
            var t = new DepartureTracker();
            t.Check(Gone("a"), true, 0);
            Assert.That(t.Check(Gone("a"), true, LOST_CONNECTION_DROP * 0.9), Is.EqualTo(Departure.None));
            Assert.That(t.Check(Gone("a"), true, LOST_CONNECTION_DROP), Is.EqualTo(Departure.Lost));
        }

        [Test]
        public void AFriendWhoComesBackIsNotDropped()
        {
            var t = new DepartureTracker();
            t.Check(Gone("a"), true, 0);
            Assert.That(t.Check(Here("a"), true, 1), Is.EqualTo(Departure.None));
            Assert.That(t.TryGetGoneSince("a", out _), Is.False);
            Assert.That(t.Check(Gone("a"), true, LOST_CONNECTION_DROP), Is.EqualTo(Departure.None),
                "counts again from when they were seen gone again");
        }

        [Test]
        public void SilentInputsCountAsLostFromTheLastInputs()
        {
            var t = new DepartureTracker();
            t.NoteInputs("a", 10);
            Assert.That(t.SilentSince("a", 10 + INPUTS_SILENCE * 0.9), Is.Null);
            Assert.That(t.SilentSince("a", 10 + INPUTS_SILENCE), Is.EqualTo(10));
            Assert.That(t.SilentSince("never", 100), Is.Null, "a friend who never sent inputs isn't silent");

            Assert.That(t.Check(Here("a"), true, 10 + INPUTS_SILENCE), Is.EqualTo(Departure.None));
            Assert.That(t.Check(Here("a"), true, 10 + LOST_CONNECTION_DROP), Is.EqualTo(Departure.Lost));
        }

        [Test]
        public void AJoinerOnlyDropsAHostThatLeft()
        {
            var t = new DepartureTracker();
            t.Check(Gone("a"), weHost: false, 0);
            Assert.That(t.Check(Gone("a"), false, 100), Is.EqualTo(Departure.None), "the host decides");
            t.Check(Left("b"), false, 0);
            Assert.That(t.Check(Left("b"), false, 100), Is.EqualTo(Departure.None), "the host decides");

            t.Check(Left("host", host: true), false, 0);
            Assert.That(t.Check(Left("host", host: true), false, LEFT_GRACE), Is.EqualTo(Departure.Left));
        }

        [Test]
        public void RemembersWhoIsDropped()
        {
            var t = new DepartureTracker();
            t.Check(Left("a"), true, 0);
            t.MarkDropped("a");
            Assert.That(t.IsDropped("a"), Is.True);
            Assert.That(t.Dropped, Is.EquivalentTo(new[] { "a" }));
            Assert.That(t.TryGetGoneSince("a", out _), Is.False);
        }
    }

    public class ResumeTimingTests
    {
        [Test]
        public void JoinersAllowForHalfTheRoundTripAndAClampedLead()
        {
            Assert.That(ResumeTiming.ScheduleAt(10, 1500, 0.05), Is.EqualTo(11.45).Within(1e-12));
            Assert.That(ResumeTiming.ScheduleAt(10, 1500, null), Is.EqualTo(11.5).Within(1e-12));
            Assert.That(ResumeTiming.ScheduleAt(10, 999_999, null), Is.EqualTo(10 + ResumeTiming.MAX_LEAD));
            Assert.That(ResumeTiming.ScheduleAt(10, -5, null), Is.EqualTo(10));
        }

        [Test]
        public void StartsTheRewindAnimationEarly()
        {
            Assert.That(ResumeTiming.ShouldStart(at: 10, now: 10 - ResumeTiming.REWIND_ANIMATION - 0.01), Is.False);
            Assert.That(ResumeTiming.ShouldStart(at: 10, now: 10 - ResumeTiming.REWIND_ANIMATION), Is.True);
        }

        [Test]
        public void OnTimeRewindsToTheResumeTime()
        {
            var plan = ResumeTiming.PlanRewind(pausedInputTime: 30, resumeInputTime: 28.5, at: 10,
                now: 10 - ResumeTiming.REWIND_ANIMATION, songSpeed: 1);
            Assert.That(plan.Late, Is.Zero);
            Assert.That(plan.Rewind, Is.EqualTo(1.5).Within(1e-12));
            Assert.That(plan.Behind, Is.Zero);
        }

        [Test]
        public void LateResumesThatMuchFurtherOn()
        {
            var plan = ResumeTiming.PlanRewind(30, 28.5, at: 10, now: 10 - ResumeTiming.REWIND_ANIMATION + 0.2,
                songSpeed: 1.5);
            Assert.That(plan.Late, Is.EqualTo(0.2).Within(1e-12));
            Assert.That(plan.Rewind, Is.EqualTo(1.5 - 0.3).Within(1e-12), "song speed scales the lateness");
        }

        [Test]
        public void CannotResumePastOurPause()
        {
            var plan = ResumeTiming.PlanRewind(30, 29.9, at: 10, now: 10 + 1, songSpeed: 1);
            Assert.That(plan.Rewind, Is.Zero);
            Assert.That(plan.Behind, Is.EqualTo(29.9 + 1.5 - 30).Within(1e-9));
        }
    }

    public class HostMeterReadingsTests
    {
        private static MeterMessage Meter(float? happiness, Dictionary<string, List<float?>> players) =>
            new() { Song = "s", Play = "p", Happiness = happiness, Players = players };

        [Test]
        public void KeepsOnlyMembersOfTheSession()
        {
            var r = new HostMeterReadings();
            r.Apply(Meter(0.5f, new Dictionary<string, List<float?>>
            {
                ["us"] = new() { 0.25f, null },
                ["stranger"] = new() { 1f },
            }), id => id == "us", now: 3);

            Assert.That(r.Happiness, Is.EqualTo(0.5f));
            Assert.That(r.LastAt, Is.EqualTo(3));
            Assert.That(r.TryGetPlayer("us", 0, out float value) && value == 0.25f, Is.True);
            Assert.That(r.TryGetPlayer("us", 1, out _), Is.False, "not counted");
            Assert.That(r.TryGetPlayer("stranger", 0, out _), Is.False);
        }

        [Test]
        public void AMeterWithoutPlayersKeepsTheLastOnes()
        {
            var r = new HostMeterReadings();
            r.Apply(Meter(0.5f, new Dictionary<string, List<float?>> { ["us"] = new() { 0.25f } }), _ => true, 1);
            r.Apply(Meter(0.4f, null), _ => true, 2);
            Assert.That(r.Happiness, Is.EqualTo(0.4f));
            Assert.That(r.TryGetPlayer("us", 0, out _), Is.True);

            r.Apply(Meter(0.4f, new Dictionary<string, List<float?>>()), _ => true, 3);
            Assert.That(r.TryGetPlayer("us", 0, out _), Is.False);
        }
    }
}
