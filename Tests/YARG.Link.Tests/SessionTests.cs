using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    /// <summary>Several sessions on real transports (loopback, and native when it's built), in one process.</summary>
    [TestFixture("loopback")]
    [TestFixture("native")]
    public class SessionTests
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly List<Member> _all = new();
        private readonly string _transport;

        public SessionTests(string transport)
        {
            _transport = transport;
        }

        private ILinkTransport NewTransport() =>
            _transport == "native" ? NativeLibrary.Transport() : new LoopbackLinkTransport();

        private sealed class Member : IDisposable
        {
            public readonly LinkSession Session;
            public readonly List<string> Log = new();
            public readonly List<SongInfo> LoadRequests = new();
            public readonly List<double> Starts = new();
            public readonly List<string> Cancels = new();
            public readonly List<LinkDisconnect> Disconnects = new();
            public readonly List<(string From, string Kind, JObject Data)> Data = new();

            public Member(ILinkTransport transport, LinkRole role, string name, string game = "test")
            {
                var options = new LinkSessionOptions { Name = name, GameVersion = game, StartLeadSeconds = 0.5 };
                Session = new LinkSession(transport, role, options, () => Clock.Elapsed.TotalSeconds);
                Session.LogInfo = Log.Add;
                Session.LogWarning = m => Log.Add("WARN " + m);
                Session.LoadSongRequested += LoadRequests.Add;
                Session.StartScheduled += Starts.Add;
                Session.StartCancelled += Cancels.Add;
                Session.Disconnected += Disconnects.Add;
                Session.GameDataReceived += (from, kind, data) => Data.Add((from.Name, kind, data));
            }

            public bool HasMembers(params string[] names) =>
                Session.Members.Select(m => m.Name).OrderBy(n => n).SequenceEqual(names.OrderBy(n => n));

            public void Dispose() => Session.Dispose();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var m in _all) m.Dispose();
            foreach (var m in _all)
            {
                TestContext.Out.WriteLine($"--- {m.Session.LocalName}");
                foreach (var line in m.Log) TestContext.Out.WriteLine(line);
            }
            _all.Clear();
        }

        private Member Add(LinkRole role, string name, string game = "test")
        {
            var m = new Member(NewTransport(), role, name, game);
            _all.Add(m);
            return m;
        }

        private void PumpUntil(Func<bool> condition, double timeoutSeconds = 5)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed.TotalSeconds > timeoutSeconds) Assert.Fail("timed out");
                foreach (var m in _all) m.Session.Update();
                Thread.Sleep(1);
            }
        }

        private (Member Host, Member A, Member B) ThreeMembers()
        {
            var host = Add(LinkRole.Host, "Host");
            var a = Add(LinkRole.Join, "A");
            var b = Add(LinkRole.Join, "B");
            a.Session.Join(host.Session.LocalTicket);
            b.Session.Join(host.Session.LocalTicket);
            PumpUntil(() => host.HasMembers("A", "B") && a.HasMembers("Host", "B") && b.HasMembers("Host", "A"));
            return (host, a, b);
        }

        [Test]
        public void JoinersSeeEachOtherThroughTheHost()
        {
            var (host, a, b) = ThreeMembers();
            Assert.That(a.Session.IsConnected && b.Session.IsConnected, Is.True);
            Assert.That(a.Session.Members.Single(m => m.Name == "Host").IsHost, Is.True);

            // Scene changes reach everyone through the roster
            b.Session.SetLocalScene("gameplay");
            PumpUntil(() => a.Session.Members.Single(m => m.Name == "B").Scene == "gameplay");
            Assert.That(host.Session.Members.Single(m => m.Name == "B").Scene, Is.EqualTo("gameplay"));
        }

        [Test]
        public void GameDataIsRelayedToEveryoneElse()
        {
            var (host, a, b) = ThreeMembers();

            a.Session.Broadcast("setup", new JObject { ["n"] = 1 });
            host.Session.Broadcast("inputs", new JObject { ["n"] = 2 }, Delivery.Unreliable);

            PumpUntil(() => host.Data.Count == 1 && b.Data.Count == 2 && a.Data.Count == 1);
            Assert.That(host.Data.Single(), Is.EqualTo(("A", "setup", host.Data[0].Data)));
            Assert.That((int) host.Data[0].Data["n"], Is.EqualTo(1));
            Assert.That(b.Data.Select(d => (d.From, d.Kind)), Is.EquivalentTo(new[] { ("A", "setup"), ("Host", "inputs") }));
            Assert.That(a.Data.Single().From, Is.EqualTo("Host"));

            // Nobody gets their own data back
            Thread.Sleep(50);
            PumpUntil(() => true);
            Assert.That(a.Data.Any(d => d.From == "A"), Is.False);
        }

        [Test]
        public void SyncedStartWithEveryone()
        {
            var (host, a, b) = ThreeMembers();
            var song = new SongInfo("abc", "Song", "Artist");

            // A picks a song and loads it; the others are asked to load it too
            a.Session.SetLocalScene("gameplay");
            a.Session.ReportReady(song);
            PumpUntil(() => host.LoadRequests.Count == 1 && b.LoadRequests.Count == 1);
            Assert.That(host.LoadRequests[0], Is.EqualTo(song));
            Assert.That(a.LoadRequests, Is.Empty);
            Assert.That(a.Starts, Is.Empty);

            host.Session.SetLocalScene("gameplay");
            host.Session.ReportReady(song);
            b.Session.SetLocalScene("gameplay");
            b.Session.ReportReady(song);

            PumpUntil(() => host.Starts.Count == 1 && a.Starts.Count == 1 && b.Starts.Count == 1);
            Assert.That(host.Starts[0], Is.EqualTo(0.5));
            Assert.That(a.Starts[0], Is.InRange(0.4, 0.5));
            Assert.That(host.Session.PendingStart, Is.Null);
        }

        [Test]
        public void DeclineAndLeavingDoNotBlockTheStart()
        {
            var (host, a, b) = ThreeMembers();
            var song = new SongInfo("abc", "Song", "Artist");

            host.Session.SetLocalScene("gameplay");
            host.Session.ReportReady(song);
            PumpUntil(() => a.LoadRequests.Count == 1 && b.LoadRequests.Count == 1);

            a.Session.ReportDecline("abc", "song not in library");
            PumpUntil(() => host.Log.Any(l => l.Contains("declined")));
            Assert.That(host.Starts, Is.Empty);

            b.Dispose();
            _all.Remove(b);
            PumpUntil(() => host.Starts.Count == 1);
            Assert.That(a.Starts, Is.Empty);
            PumpUntil(() => a.HasMembers("Host"));
        }

        [Test]
        public void ConflictingSongIsCancelled()
        {
            var (host, a, _) = ThreeMembers();

            host.Session.SetLocalScene("gameplay");
            host.Session.ReportReady(new SongInfo("x", "X", "A"));
            a.Session.SetLocalScene("gameplay");
            a.Session.ReportReady(new SongInfo("y", "Y", "A"));

            PumpUntil(() => a.Cancels.Count == 1);
            Assert.That(a.Cancels[0], Does.Contain("already starting"));
        }

        [Test]
        public void JoiningUntilTheHostAccepts()
        {
            var host = Add(LinkRole.Host, "Host");
            var a = Add(LinkRole.Join, "A");
            Assert.That(a.Session.IsJoining, Is.False);

            a.Session.Join(host.Session.LocalTicket);
            Assert.That(a.Session.IsJoining, Is.True);
            PumpUntil(() => a.Session.IsConnected);
            Assert.That(a.Session.IsJoining, Is.False);

            host.Dispose();
            _all.Remove(host);
            PumpUntil(() => a.Disconnects.Count == 1);
            Assert.That(a.Session.IsJoining, Is.False);
        }

        [Test]
        public void DifferentGameVersionIsRefused()
        {
            var host = Add(LinkRole.Host, "Host", game: "v1");
            var a = Add(LinkRole.Join, "A", game: "v2");
            var refused = new List<string>();
            host.Session.PeerRefused += (_, code) => refused.Add(code);
            a.Session.Join(host.Session.LocalTicket);

            PumpUntil(() => a.Disconnects.Count == 1 && refused.Count == 1);
            Assert.That(refused[0], Is.EqualTo(ByeCodes.INCOMPATIBLE));
            Assert.That(a.Disconnects[0].Reason, Does.Contain("different game versions"));
            Assert.That(a.Disconnects[0].Code, Is.EqualTo(ByeCodes.INCOMPATIBLE));
            Assert.That(a.Disconnects[0].CanRetry, Is.False);
            Assert.That(a.Session.IsConnected, Is.False);
            Assert.That(host.Session.Members, Is.Empty);
        }

        [Test]
        public void JoiningAJoinerIsRefused()
        {
            var host = Add(LinkRole.Host, "Host");
            var a = Add(LinkRole.Join, "A");
            var b = Add(LinkRole.Join, "B");
            a.Session.Join(host.Session.LocalTicket);
            PumpUntil(() => a.Session.IsConnected);

            b.Session.Join(a.Session.LocalTicket);
            PumpUntil(() => b.Disconnects.Count == 1);
            Assert.That(b.Disconnects[0].Reason, Does.Contain("isn't hosting"));
            Assert.That(b.Disconnects[0].Code, Is.EqualTo(ByeCodes.REFUSED));
            Assert.That(a.Session.IsConnected, Is.True);
        }

        [Test]
        public void HostLeavingDisconnectsJoiners()
        {
            var (host, a, b) = ThreeMembers();
            host.Dispose();
            _all.Remove(host);

            PumpUntil(() => a.Disconnects.Count == 1 && b.Disconnects.Count == 1);
            Assert.That(a.Disconnects[0].Reason, Is.EqualTo("left the session"));
            Assert.That(a.Disconnects[0].Code, Is.EqualTo(ByeCodes.LEFT));
            Assert.That(a.Session.Members, Is.Empty);
            Assert.That(a.Session.IsConnected, Is.False);
        }

        [Test]
        public void StateReachesEveryoneThroughTheRoster()
        {
            var (host, a, b) = ThreeMembers();
            var guitar = new PlayerInfo { Name = "Ann", Instrument = "FiveFretGuitar", Difficulty = "Expert" };
            var drums = new PlayerInfo { Name = "Bot", Instrument = "FourLaneDrums", Difficulty = "Hard", Bot = true };

            a.Session.SetLocalState("browsing", new[] { guitar, drums });
            host.Session.SetLocalState("ready", new[] { guitar });

            PumpUntil(() => b.Session.Members.Single(m => m.Name == "A").Players.Count == 2 &&
                b.Session.Members.Single(m => m.Name == "Host").Status == "ready");

            var seen = b.Session.Members.Single(m => m.Name == "A");
            Assert.That(seen.Status, Is.EqualTo("browsing"));
            Assert.That(seen.Players, Is.EqualTo(new[] { guitar, drums }));
            Assert.That(host.Session.Members.Single(m => m.Name == "A").Players, Is.EqualTo(new[] { guitar, drums }));
            Assert.That(a.Session.Members.Single(m => m.Name == "Host").Players, Is.EqualTo(new[] { guitar }));

            // A state set before joining is sent once the host accepts us
            var c = Add(LinkRole.Join, "C");
            c.Session.SetLocalState("browsing", new[] { drums });
            c.Session.Join(host.Session.LocalTicket);
            PumpUntil(() => a.Session.Members.Any(m => m.Name == "C" && m.Players.Count == 1));
        }

        [Test]
        public void HostCanRemoveAMember()
        {
            var (host, a, b) = ThreeMembers();
            host.Session.Kick(host.Session.Members.Single(m => m.Name == "A"));

            PumpUntil(() => a.Disconnects.Count == 1 && host.HasMembers("B") && b.HasMembers("Host"));
            Assert.That(a.Disconnects[0].Code, Is.EqualTo(ByeCodes.KICKED));
            Assert.That(a.Disconnects[0].CanRetry, Is.False);
            Assert.That(a.Session.IsConnected, Is.False);
            Assert.Throws<InvalidOperationException>(() => b.Session.Kick(b.Session.Members.First()));
        }

        [Test]
        public void HostTellsLeavingFromLosingTheConnection()
        {
            var (host, a, b) = ThreeMembers();
            var left = new List<LinkMember>();
            host.Session.MemberLeft += left.Add;

            a.Dispose();
            _all.Remove(a);
            PumpUntil(() => left.Count == 1);
            Assert.That(left[0].ConnectionLost, Is.False);
            Assert.That(left[0].LeftReason, Is.EqualTo("left the session"));
        }
    }
}
