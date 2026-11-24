using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Integration.Link.Session;
using static YARG.Integration.Link.Session.SyncStartCoordinator;

namespace YARG.Link.Tests
{
    /// <summary>Ported from yarg-link's coordinator.rs.</summary>
    public class CoordinatorTests
    {
        private const string LOCAL = "local";

        private static SongInfo Song(string hash) => new(hash, $"Song {hash}", "A");
        private static Present Menu(string who) => new(who, true);
        private static SyncStartCoordinator Coord() => new(2.0, 60.0);
        private static readonly Command START = Command.Start(2000);

        private static List<(string, Command)> Flat(List<Action> actions) =>
            actions.ConvertAll(a => (a.Member, a.Command));

        [Test]
        public void TwoPlayersHappyPath()
        {
            var c = Coord();
            var present = new[] { Menu(LOCAL), Menu("b") };

            var a = c.OnReady(LOCAL, Song("x"), present, 0);
            Assert.That(Flat(a), Is.EqualTo(new[] { ("b", Command.LoadSong(Song("x"))) }));
            Assert.That(c.WaitingOn(present), Is.EqualTo(new[] { "b" }));

            a = c.OnReady("b", Song("x"), present, 0);
            Assert.That(Flat(a), Is.EquivalentTo(new[] { ("b", START), (LOCAL, START) }));
            Assert.That(c.PendingSong, Is.Null);
        }

        [Test]
        public void AloneStartsImmediately()
        {
            var c = Coord();
            var a = c.OnReady(LOCAL, Song("x"), new[] { Menu(LOCAL) }, 0);
            Assert.That(Flat(a), Is.EqualTo(new[] { (LOCAL, START) }));
        }

        [Test]
        public void DeclineAndBusyAreSkipped()
        {
            var c = Coord();
            var present = new[] { Menu(LOCAL), Menu("b"), new Present("d", false) };

            var a = c.OnReady(LOCAL, Song("x"), present, 0);
            Assert.That(Flat(a), Is.EqualTo(new[] { ("b", Command.LoadSong(Song("x"))) })); // d is busy

            a = c.OnDecline("b", "x", present);
            Assert.That(Flat(a), Is.EqualTo(new[] { (LOCAL, START) }));
        }

        [Test]
        public void ConflictingSongIsCancelled()
        {
            var c = Coord();
            var present = new[] { Menu(LOCAL), Menu("b") };
            c.OnReady(LOCAL, Song("x"), present, 0);

            var a = c.OnReady("b", Song("y"), present, 0);
            Assert.That(a, Has.Count.EqualTo(1));
            Assert.That(a[0].Member, Is.EqualTo("b"));
            Assert.That(a[0].Command.Type, Is.EqualTo(CommandType.Cancel));
            Assert.That(c.PendingSong, Is.EqualTo(Song("x")));
        }

        [Test]
        public void StragglerTimesOut()
        {
            var c = Coord();
            var present = new[] { Menu(LOCAL), Menu("b") };
            c.OnReady(LOCAL, Song("x"), present, 0);

            Assert.That(c.OnTick(present, 10), Is.Empty);
            Assert.That(Flat(c.OnTick(present, 61)), Is.EqualTo(new[] { (LOCAL, START) }));
        }

        [Test]
        public void LeavingMemberUnblocksStart()
        {
            var c = Coord();
            c.OnReady(LOCAL, Song("x"), new[] { Menu(LOCAL), Menu("b") }, 0);

            var a = c.OnTick(new[] { Menu(LOCAL) }, 0);
            Assert.That(Flat(a), Is.EqualTo(new[] { (LOCAL, START) }));
        }

        private static Present Playing(string who) => new(who, false, true);

        private static List<(string, CommandType)> Types(List<Action> actions) =>
            actions.ConvertAll(a => (a.Member, a.Command.Type));

        /// <summary>
        /// Start song x with these members, all in the menu, then put them in gameplay. Returns who is present
        /// once they play it.
        /// </summary>
        private static Present[] StartTogether(SyncStartCoordinator c, params string[] members)
        {
            var menu = System.Array.ConvertAll(members, Menu);
            foreach (string m in members) c.OnReady(m, Song("x"), menu, 0);
            Assert.That(c.PendingSong, Is.Null);
            return System.Array.ConvertAll(members, Playing);
        }

        private static RestartVote LastVote(List<Action> actions, string member) =>
            actions.FindLast(a => a.Member == member && a.Command.Type == CommandType.RestartVote).Command.Vote;

        [Test]
        public void HostRestartsStraightAway()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b");

            var a = c.OnRestart(LOCAL, Song("x"), "play", fromHost: true, present, 1);
            var restart = Command.Restart(Song("x"), LOCAL, "play");
            Assert.That(Flat(a), Is.EquivalentTo(new[] { (LOCAL, restart), ("b", restart) }));

            c.OnReady("b", Song("x"), present, 1);
            a = c.OnReady(LOCAL, Song("x"), present, 1);
            Assert.That(Flat(a), Is.EquivalentTo(new[] { (LOCAL, START), ("b", START) }));
        }

        [Test]
        public void JoinerRestartWaitsForEveryoneToAgree()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b", "c");

            var a = c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 1);
            Assert.That(Types(a), Is.EquivalentTo(new[]
            {
                (LOCAL, CommandType.RestartVote), ("b", CommandType.RestartVote), ("c", CommandType.RestartVote),
            }));
            var vote = LastVote(a, "c");
            Assert.That(vote.Yes, Is.EqualTo(new[] { "b" }));
            Assert.That(vote.Waiting, Is.EqualTo(new[] { "c", LOCAL }));
            Assert.That(vote.Ended, Is.False);

            a = c.OnRestart("c", Song("x"), "play-c", fromHost: false, present, 2);
            Assert.That(LastVote(a, "b").Waiting, Is.EqualTo(new[] { LOCAL }));
            Assert.That(a.Any(x => x.Command.Type == CommandType.Restart), Is.False);

            // The last one agrees: the vote ends and everyone restarts the play of the first one who asked
            a = c.OnRestart(LOCAL, Song("x"), "play-l", fromHost: false, present, 3);
            Assert.That(LastVote(a, "b").Ended, Is.True);
            Assert.That(LastVote(a, "b").Reason, Is.Null);
            var restart = Command.Restart(Song("x"), "b", "play-b");
            Assert.That(Flat(a.FindAll(x => x.Command.Type == CommandType.Restart)),
                Is.EquivalentTo(new[] { (LOCAL, restart), ("b", restart), ("c", restart) }));
        }

        [Test]
        public void HostOverridesAnOpenVote()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b", "c");
            c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 1);

            var a = c.OnRestart(LOCAL, Song("x"), "play-l", fromHost: true, present, 2);
            Assert.That(a.FindAll(x => x.Command.Type == CommandType.Restart), Has.Count.EqualTo(3));
            Assert.That(LastVote(a, "c").Ended, Is.True);
        }

        [Test]
        public void VoteEndsWhenTheSongCarriesOn()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b");
            c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 1);

            var a = c.OnRestartWithdraw(LOCAL, "x", present);
            Assert.That(LastVote(a, "b").Reason, Does.Contain("carried on"));

            // Nothing left to agree to
            a = c.OnRestart(LOCAL, Song("x"), "play-l", fromHost: false, present, 2);
            Assert.That(a.Any(x => x.Command.Type == CommandType.Restart), Is.False);
        }

        [Test]
        public void VoteTimesOut()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b");
            c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 1);

            Assert.That(c.OnTick(present, 20), Is.Empty);
            var a = c.OnTick(present, 32);
            Assert.That(LastVote(a, "b").Reason, Does.Contain("in time"));
        }

        [Test]
        public void JoinerCannotOpenVotesBackToBack()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b");
            c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 1);
            c.OnRestartWithdraw(LOCAL, "x", present);

            var a = c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 10);
            Assert.That(Types(a), Is.EqualTo(new[] { ("b", CommandType.Cancel) }));
            Assert.That(a[0].Command.Reason, Does.Contain("21 s"));

            a = c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 31);
            Assert.That(LastVote(a, "b"), Is.Not.Null);
        }

        [Test]
        public void MembersWhoLeaveTheSongDoNotBlockTheVote()
        {
            var c = Coord();
            StartTogether(c, LOCAL, "b", "c");
            c.OnRestart("b", Song("x"), "play-b", fromHost: false, new[] { Playing(LOCAL), Playing("b"), Playing("c") }, 1);
            c.OnRestart("c", Song("x"), "play-c", fromHost: false, new[] { Playing(LOCAL), Playing("b"), Playing("c") }, 1);

            // The host quit to the menu instead of agreeing
            var a = c.OnTick(new[] { Menu(LOCAL), Playing("b"), Playing("c") }, 2);
            Assert.That(Flat(a.FindAll(x => x.Command.Type == CommandType.Restart)), Is.EquivalentTo(new[]
            {
                ("b", Command.Restart(Song("x"), "b", "play-b")), ("c", Command.Restart(Song("x"), "b", "play-b")),
            }));
        }

        [Test]
        public void OnlyTheGroupIsAskedToRestart()
        {
            var c = Coord();
            // b and c play x; d plays something else on its own
            StartTogether(c, "b", "c");
            c.OnReady("d", Song("y"), new[] { Menu("d") }, 0);
            var present = new[] { Menu(LOCAL), Playing("b"), Playing("c"), Playing("d") };

            c.OnRestart("b", Song("x"), "play-b", fromHost: false, present, 1);
            var a = c.OnRestart("c", Song("x"), "play-c", fromHost: false, present, 1);
            Assert.That(a.FindAll(x => x.Command.Type == CommandType.Restart).ConvertAll(x => x.Member),
                Is.EquivalentTo(new[] { "b", "c" }));
        }

        [Test]
        public void RestartOutsideTheGroupIsRefused()
        {
            var c = Coord();
            StartTogether(c, LOCAL, "b");

            // c never started x with anyone
            var a = c.OnRestart("c", Song("x"), "play", fromHost: false, new[] { Playing(LOCAL), Playing("b"), Playing("c") }, 1);
            Assert.That(Types(a), Is.EqualTo(new[] { ("c", CommandType.Cancel) }));
        }

        [Test]
        public void RestartDoesNotWaitForMembersWhoLeaveGameplay()
        {
            var c = Coord();
            var present = StartTogether(c, LOCAL, "b");
            c.OnRestart(LOCAL, Song("x"), "play", fromHost: true, present, 1);
            c.OnReady(LOCAL, Song("x"), present, 1);

            // b quit to the menu instead of loading the song again
            var a = c.OnTick(new[] { Playing(LOCAL), Menu("b") }, 2);
            Assert.That(Flat(a), Is.EqualTo(new[] { (LOCAL, START) }));
        }

        [Test]
        public void RestartDuringAStartIsRefused()
        {
            var c = Coord();
            StartTogether(c, LOCAL, "b");

            // m picks y and waits for n to choose a part, so y's start is pending
            var present = new[] { Playing(LOCAL), Playing("b"), Menu("m"), Menu("n") };
            c.OnReady("m", Song("y"), present, 1);

            var a = c.OnRestart("b", Song("x"), "play", fromHost: false, present, 1);
            Assert.That(Types(a), Is.EqualTo(new[] { ("b", CommandType.Cancel) }));
            Assert.That(c.PendingSong, Is.EqualTo(Song("y")));
        }

        [Test]
        public void DeclineForOtherSongIsIgnored()
        {
            var c = Coord();
            var present = new[] { Menu(LOCAL), Menu("b") };
            c.OnReady(LOCAL, Song("x"), present, 0);

            Assert.That(c.OnDecline("b", "y", present), Is.Empty);
            Assert.That(c.PendingSong, Is.EqualTo(Song("x")));
        }
    }
}
