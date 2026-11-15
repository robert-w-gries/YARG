using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    /// <summary>The <see cref="ILinkTransport"/> contract, on each transport.</summary>
    [TestFixture("loopback")]
    [TestFixture("native")]
    public class TransportTests
    {
        private readonly List<IDisposable> _disposables = new();
        private readonly string _kind;

        public TransportTests(string kind)
        {
            _kind = kind;
        }

        private ILinkTransport NewTransport() =>
            _kind == "native" ? NativeLibrary.Transport() : new LoopbackLinkTransport();

        [TearDown]
        public void TearDown()
        {
            foreach (var d in _disposables) d.Dispose();
            _disposables.Clear();
        }

        private ILinkTransport Transport()
        {
            var t = NewTransport();
            _disposables.Add(t);
            return t;
        }

        /// <summary>Poll until an event matches, collecting everything seen on the way.</summary>
        private static LinkEvent WaitFor(ILinkTransport t, Func<LinkEvent, bool> match, List<LinkEvent> seen = null,
            double timeoutSeconds = 5)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                while (t.TryPoll(out var ev))
                {
                    seen?.Add(ev);
                    if (match(ev)) return ev;
                }
                Thread.Sleep(1);
            }
            Assert.Fail("timed out waiting for an event");
            return default;
        }

        private static (PeerId AtA, PeerId AtB) Pair(ILinkTransport a, ILinkTransport b)
        {
            var atA = a.Connect(b.LocalTicket);
            WaitFor(a, e => e.Type == LinkEventType.PeerConnected && e.Peer == atA);
            var atB = WaitFor(b, e => e.Type == LinkEventType.PeerConnected).Peer;
            return (atA, atB);
        }

        [Test]
        public void ParseTicket()
        {
            if (_kind != "loopback") Assert.Ignore("loopback tickets only");
            Assert.That(LoopbackLinkTransport.ParseTicket("loopback:127.0.0.1:36120"), Is.EqualTo(("127.0.0.1", 36120)));
            Assert.That(LoopbackLinkTransport.ParseTicket(" localhost:5 "), Is.EqualTo(("localhost", 5)));
            Assert.Throws<FormatException>(() => LoopbackLinkTransport.ParseTicket("127.0.0.1"));
            Assert.Throws<FormatException>(() => LoopbackLinkTransport.ParseTicket("loopback:host:99999"));
            Assert.Throws<FormatException>(() => LoopbackLinkTransport.ParseTicket(""));
        }

        [Test]
        public void ReliableMessagesArriveInOrder()
        {
            var (a, b) = (Transport(), Transport());
            var (atA, atB) = Pair(a, b);

            for (int i = 0; i < 500; i++)
            {
                a.Send(atA, BitConverter.GetBytes(i), Delivery.Reliable);
            }

            var received = new List<int>();
            while (received.Count < 500)
            {
                var ev = WaitFor(b, e => e.Type == LinkEventType.Message);
                Assert.That(ev.Peer, Is.EqualTo(atB));
                Assert.That(ev.Delivery, Is.EqualTo(Delivery.Reliable));
                received.Add(BitConverter.ToInt32(ev.Data, 0));
            }
            Assert.That(received, Is.EqualTo(Enumerable.Range(0, 500)));
        }

        [Test]
        public void LargeReliableMessage()
        {
            var (a, b) = (Transport(), Transport());
            var (_, atB) = Pair(a, b);

            var big = new byte[2 * 1024 * 1024];
            new Random(1).NextBytes(big);
            b.Send(atB, big, Delivery.Reliable);

            var ev = WaitFor(a, e => e.Type == LinkEventType.Message);
            Assert.That(ev.Data, Is.EqualTo(big));
        }

        [Test]
        public void UnreliableMessagesArrive()
        {
            var (a, b) = (Transport(), Transport());
            var (atA, atB) = Pair(a, b);

            // Loopback UDP doesn't drop at this rate, but allow for it
            var ev = default(LinkEvent);
            for (int attempt = 0; attempt < 5 && ev.Data == null; attempt++)
            {
                a.Send(atA, new byte[] { 1, 2, 3 }, Delivery.Unreliable);
                try
                {
                    ev = WaitFor(b, e => e.Type == LinkEventType.Message, timeoutSeconds: 1);
                }
                catch (AssertionException) when (attempt < 4)
                {
                }
            }

            Assert.That(ev.Peer, Is.EqualTo(atB));
            Assert.That(ev.Delivery, Is.EqualTo(Delivery.Unreliable));
            Assert.That(ev.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void OversizedUnreliableThrows()
        {
            var (a, b) = (Transport(), Transport());
            var (atA, _) = Pair(a, b);
            Assert.Throws<ArgumentException>(() =>
                a.Send(atA, new byte[a.MaxUnreliableSize + 1], Delivery.Unreliable));
        }

        [Test]
        public void DisconnectIsReportedOnceOnBothSides()
        {
            var (a, b) = (Transport(), Transport());
            var (atA, atB) = Pair(a, b);

            a.Disconnect(atA);
            a.Disconnect(atA); // twice is fine

            var seenA = new List<LinkEvent>();
            var seenB = new List<LinkEvent>();
            WaitFor(a, e => e.Type == LinkEventType.PeerDisconnected, seenA);
            WaitFor(b, e => e.Type == LinkEventType.PeerDisconnected && e.Peer == atB, seenB);

            Thread.Sleep(100);
            while (a.TryPoll(out var ev)) seenA.Add(ev);
            Assert.That(seenA.Count(e => e.Type == LinkEventType.PeerDisconnected), Is.EqualTo(1));

            // Sending to a gone peer is dropped quietly
            Assert.DoesNotThrow(() => a.Send(atA, new byte[] { 1 }, Delivery.Reliable));
            Assert.That(a.GetStats(atA).Connected, Is.False);
        }

        [Test]
        public void ConnectFailureIsReportedAsDisconnect()
        {
            string ticket;
            using (var gone = NewTransport())
            {
                ticket = gone.LocalTicket;
            }

            var a = Transport();
            var peer = a.Connect(ticket);
            var ev = WaitFor(a, e => e.Type == LinkEventType.PeerDisconnected, timeoutSeconds: 10);
            Assert.That(ev.Peer, Is.EqualTo(peer));
            Assert.That(ev.Reason, Is.Not.Empty);
        }

        [Test]
        public void DisposeClosesPeers()
        {
            var a = NewTransport();
            var b = Transport();
            Pair(a, b);

            a.Dispose();
            a.Dispose();
            Assert.That(a.TryPoll(out _), Is.False);
            WaitFor(b, e => e.Type == LinkEventType.PeerDisconnected);
        }

        [Test]
        public void MeasuresRtt()
        {
            var (a, b) = (Transport(), Transport());
            var (atA, _) = Pair(a, b);

            var sw = Stopwatch.StartNew();
            while (a.GetStats(atA).Rtt == null && sw.Elapsed.TotalSeconds < 3)
            {
                Thread.Sleep(10);
            }

            var stats = a.GetStats(atA);
            Assert.That(stats.Connected, Is.True);
            Assert.That(stats.Rtt, Is.Not.Null);
            Assert.That(stats.Rtt.Value.TotalMilliseconds, Is.LessThan(100));
            Assert.That(stats.Path, Is.EqualTo(LinkPath.Direct));
            Assert.That(stats.BytesSent, Is.GreaterThan(0));
        }
            [Test]
        public void GoodbyeBeforeDisconnectArrives()
        {
            var (a, b) = (Transport(), Transport());
            var (atA, _) = Pair(a, b);

            a.Send(atA, new byte[] { 42 }, Delivery.Reliable);
            a.Disconnect(atA);

            var seen = new List<LinkEvent>();
            WaitFor(b, e => e.Type == LinkEventType.PeerDisconnected, seen);
            Assert.That(seen.Where(e => e.Type == LinkEventType.Message).Select(e => e.Data[0]), Is.EqualTo(new[] { 42 }));
        }

        [Test]
        public void GoodbyeBeforeDisposeArrives()
        {
            var a = NewTransport();
            var b = Transport();
            var (atA, _) = Pair(a, b);

            a.Send(atA, new byte[] { 42 }, Delivery.Reliable);
            a.Dispose();

            var seen = new List<LinkEvent>();
            WaitFor(b, e => e.Type == LinkEventType.PeerDisconnected, seen);
            Assert.That(seen.Where(e => e.Type == LinkEventType.Message).Select(e => e.Data[0]), Is.EqualTo(new[] { 42 }));
        }

        [Test]
        public void BadTicketThrowsFormatException()
        {
            var a = Transport();
            Assert.Throws<FormatException>(() => a.Connect("definitely not a ticket"));
            Assert.Throws<FormatException>(() => a.Connect(""));
        }
    }
}
