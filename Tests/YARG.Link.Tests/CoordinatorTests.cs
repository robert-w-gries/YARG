using System.Collections.Generic;
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
