using NUnit.Framework;
using YARG.Integration.Link.Session;
using static YARG.Integration.Link.Session.PauseCoordinator;

namespace YARG.Link.Tests
{
    public class PauseCoordinatorTests
    {
        private const string HOST = "host";
        private const string B = "b";
        private const string C = "c";

        private static PauseCoordinator Coord(string local = HOST) => new(local, minRewind: 1.0, rewindMargin: 0.25);

        [Test]
        public void LocalPauseStartsAPauseOnce()
        {
            var c = Coord();
            Assert.That(c.OnLocalPause(10.0), Is.True);
            Assert.That(c.Active, Is.True);
            Assert.That(c.PausedBy, Is.EqualTo(HOST));
            Assert.That(c.OnLocalPause(10.1), Is.False);
            Assert.That(c.LocalTime, Is.EqualTo(10.1));
        }

        [Test]
        public void FriendsPausePausesUsWhilePlaying()
        {
            var c = Coord();
            Assert.That(c.OnRemotePause(B, 0, 10.0, false, playing: true), Is.EqualTo(RemotePauseResult.PauseUs));
            Assert.That(c.PausedBy, Is.EqualTo(B));

            // Our game pausing in response joins the same pause, still credited to them
            Assert.That(c.OnLocalPause(10.02, B), Is.False);
            Assert.That(c.PausedBy, Is.EqualTo(B));

            Assert.That(c.OnRemotePause(C, 0, 10.05, false, playing: true), Is.EqualTo(RemotePauseResult.Recorded));
        }

        [Test]
        public void FriendsPauseWhileWeAreNotPlayingIsOnlyRecorded()
        {
            var c = Coord();
            Assert.That(c.OnRemotePause(B, 0, 10.0, false, playing: false), Is.EqualTo(RemotePauseResult.Recorded));
            Assert.That(c.Active, Is.True);
        }

        [Test]
        public void HostWaitsForEveryoneThenResumesBeforeTheLatestPause()
        {
            var c = Coord();
            var everyone = new[] { HOST, B, C };

            c.OnLocalPause(10.00);
            c.OnRemotePause(B, 0, 10.03, false, true);
            c.OnRemotePause(C, 0, 10.05, false, true);

            c.SetLocalReady(true);
            Assert.That(c.TryResume(everyone), Is.Null);
            Assert.That(c.WaitingOn(everyone), Is.EquivalentTo(new[] { B, C }));

            c.OnRemotePause(B, 0, 10.03, true, true);
            Assert.That(c.TryResume(everyone), Is.Null);
            Assert.That(c.WaitingOn(everyone), Is.EqualTo(new[] { C }));

            c.OnRemotePause(C, 0, 10.05, true, true);
            var plan = c.TryResume(everyone);
            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.Value.Epoch, Is.EqualTo(0));
            Assert.That(plan.Value.InputTime, Is.EqualTo(9.05).Within(1e-9));

            Assert.That(c.Active, Is.False);
            Assert.That(c.Epoch, Is.EqualTo(1));
        }

        [Test]
        public void HostMustBeReadyToo()
        {
            var c = Coord();
            c.OnLocalPause(10.0);
            c.OnRemotePause(B, 0, 10.0, true, true);
            Assert.That(c.TryResume(new[] { HOST, B }), Is.Null);
            Assert.That(c.WaitingOn(new[] { HOST, B }), Is.EqualTo(new[] { HOST }));
        }

        [Test]
        public void ReadyCanBeTakenBack()
        {
            var c = Coord();
            c.OnLocalPause(10.0);
            c.OnRemotePause(B, 0, 10.0, true, true);
            c.OnRemotePause(B, 0, 10.0, false, true);
            c.SetLocalReady(true);
            Assert.That(c.TryResume(new[] { HOST, B }), Is.Null);
        }

        [Test]
        public void FriendWhoNeverPausedIsWaitedFor()
        {
            var c = Coord();
            c.OnLocalPause(10.0);
            c.SetLocalReady(true);
            Assert.That(c.TryResume(new[] { HOST, B }), Is.Null);
            Assert.That(c.HasPaused(B), Is.False);
        }

        [Test]
        public void LeavingStopsTheWait()
        {
            var c = Coord();
            c.OnLocalPause(10.0);
            c.OnRemotePause(B, 0, 10.0, false, true);
            c.SetLocalReady(true);
            c.RemoveMember(B);
            Assert.That(c.TryResume(new[] { HOST }), Is.Not.Null);
        }

        [Test]
        public void FarApartPausesRewindEveryoneBehindTheEarliest()
        {
            var c = Coord();
            c.OnLocalPause(10.0);
            c.OnRemotePause(B, 0, 13.0, true, true);
            c.SetLocalReady(true);
            var plan = c.TryResume(new[] { HOST, B });
            Assert.That(plan.Value.InputTime, Is.EqualTo(9.75).Within(1e-9));
        }

        [Test]
        public void StalePauseAfterAResumeIsIgnored()
        {
            var c = Coord();
            c.OnLocalPause(10.0);
            c.OnRemotePause(B, 0, 10.0, true, true);
            c.SetLocalReady(true);
            Assert.That(c.TryResume(new[] { HOST, B }), Is.Not.Null);

            // B's earlier report, crossing the resume on the wire
            Assert.That(c.OnRemotePause(B, 0, 10.0, true, true), Is.EqualTo(RemotePauseResult.Ignored));
            Assert.That(c.Active, Is.False);

            // A new pause
            Assert.That(c.OnRemotePause(B, 1, 20.0, false, true), Is.EqualTo(RemotePauseResult.PauseUs));
        }

        [Test]
        public void JoinerFollowsTheHostsResume()
        {
            var c = Coord(B);
            c.OnRemotePause(HOST, 0, 10.0, false, true);
            c.OnLocalPause(10.02, HOST);
            c.SetLocalReady(true);

            Assert.That(c.OnRemoteResume(0), Is.True);
            Assert.That(c.Active, Is.False);
            Assert.That(c.Epoch, Is.EqualTo(1));

            // A repeat of the same resume does nothing
            Assert.That(c.OnRemoteResume(0), Is.False);
        }

        [Test]
        public void JoinerCatchesUpWithALaterPause()
        {
            var c = Coord(B);
            Assert.That(c.OnRemotePause(HOST, 2, 30.0, false, true), Is.EqualTo(RemotePauseResult.PauseUs));
            Assert.That(c.Epoch, Is.EqualTo(2));
        }

        [Test]
        public void BadTimesAreIgnored()
        {
            var c = Coord();
            Assert.That(c.OnRemotePause(B, 0, double.NaN, false, true), Is.EqualTo(RemotePauseResult.Ignored));
            Assert.That(c.OnRemotePause(B, 0, double.PositiveInfinity, false, true), Is.EqualTo(RemotePauseResult.Ignored));
            Assert.That(c.Active, Is.False);
        }
    }
}
