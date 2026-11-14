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
    /// <summary>Several sessions on real loopback transports, in one process.</summary>
    public class SessionTests
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly List<Member> _all = new();

        private sealed class Member : IDisposable
        {
            public readonly LinkSession Session;
            public readonly List<string> Log = new();
            public readonly List<SongInfo> LoadRequests = new();
            public readonly List<double> Starts = new();
            public readonly List<string> Cancels = new();
            public readonly List<string> Disconnects = new();
            public readonly List<(string From, string Kind, JObject Data)> Data = new();

            public Member(LinkRole role, string name, string game = "test")
            {
                var options = new LinkSessionOptions { Name = name, GameVersion = game, StartLeadSeconds = 0.5 };
                Session = new LinkSession(new LoopbackLinkTransport(), role, options, () => Clock.Elapsed.TotalSeconds);
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
            var m = new Member(role, name, game);
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
        public void DifferentGameVersionIsRefused()
        {
            var host = Add(LinkRole.Host, "Host", game: "v1");
            var a = Add(LinkRole.Join, "A", game: "v2");
            a.Session.Join(host.Session.LocalTicket);

            PumpUntil(() => a.Disconnects.Count == 1);
            Assert.That(a.Disconnects[0], Does.Contain("different game versions"));
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
            Assert.That(b.Disconnects[0], Does.Contain("isn't hosting"));
            Assert.That(a.Session.IsConnected, Is.True);
        }

        [Test]
        public void HostLeavingDisconnectsJoiners()
        {
            var (host, a, b) = ThreeMembers();
            host.Dispose();
            _all.Remove(host);

            PumpUntil(() => a.Disconnects.Count == 1 && b.Disconnects.Count == 1);
            Assert.That(a.Disconnects[0], Is.EqualTo("left the session"));
            Assert.That(a.Session.Members, Is.Empty);
            Assert.That(a.Session.IsConnected, Is.False);
        }
    }
}
