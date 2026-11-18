using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    /// <summary>
    /// Where <see cref="MemoryLinkTransport"/>s find each other. Each test makes its own, so tests can't reach
    /// each other's transports.
    /// </summary>
    /// <remarks>
    /// Everything happens on the calling thread: a send lands in the other transport's event queue at once, so
    /// tests run as fast as they can poll, with no sockets, threads or timing.
    /// </remarks>
    public sealed class MemoryLinkNetwork
    {
        internal readonly object Lock = new();

        private readonly Dictionary<string, MemoryLinkTransport> _transports = new();

        public MemoryLinkTransport Transport(bool accept = true, int maxPeers = 16)
        {
            lock (Lock)
            {
                var transport = new MemoryLinkTransport(this, accept, maxPeers);
                _transports[transport.LocalTicket] = transport;
                return transport;
            }
        }

        internal MemoryLinkTransport Find(string ticket)
        {
            _transports.TryGetValue(ticket, out var transport);
            return transport;
        }

        internal void Remove(string ticket) => _transports.Remove(ticket);
    }

    /// <summary>
    /// An <see cref="ILinkTransport"/> for tests: transports on the same <see cref="MemoryLinkNetwork"/> pass
    /// messages through memory. It keeps the contract the game relies on (reliable messages in order, a goodbye
    /// before a disconnect arrives, one disconnect per peer, the same size limits as the native transport) and
    /// can do what a real network does to a game on purpose: <see cref="Sever"/> drops a connection without a
    /// goodbye, and <see cref="DropUnreliable"/> loses datagrams.
    /// </summary>
    public sealed class MemoryLinkTransport : ILinkTransport
    {
        /// <summary>The same as the native transport's, so tests catch messages that wouldn't fit there.</summary>
        public const int MAX_UNRELIABLE_SIZE = NativeLinkTransport.MAX_UNRELIABLE_SIZE;

        public const int MAX_RELIABLE_SIZE = NativeLinkTransport.MAX_RELIABLE_SIZE;

        private readonly MemoryLinkNetwork _network;
        private readonly bool _accept;
        private readonly int  _maxPeers;
        private readonly string _identity = NewIdentity();

        private readonly Queue<LinkEvent> _events = new();
        private readonly Dictionary<uint, End> _ends = new();
        private uint _nextPeer;
        private bool _disposed;

        /// <summary>Lose every unreliable message this transport sends, as a congested path would.</summary>
        public bool DropUnreliable { get; set; }

        /// <summary>
        /// This transport's identity, like a native ticket's endpoint ID: 32 random hex digits, so tests that
        /// check logs for tickets have something to look for.
        /// </summary>
        public string LocalTicket => _identity;

        public int MaxUnreliableSize => MAX_UNRELIABLE_SIZE;

        internal MemoryLinkTransport(MemoryLinkNetwork network, bool accept, int maxPeers)
        {
            _network = network;
            _accept = accept;
            _maxPeers = Math.Max(1, maxPeers);
        }

        /// <summary>One side of a connection.</summary>
        private sealed class End
        {
            public MemoryLinkTransport Owner;
            public PeerId Id;
            public End    Other;
            public long   BytesSent;
            public long   BytesReceived;
        }

        #region ILinkTransport

        public PeerId Connect(string ticket)
        {
            string target = ParseTicket(ticket);
            lock (_network.Lock)
            {
                ThrowIfDisposed();
                var local = NewEnd();

                var remote = _network.Find(target);
                if (remote == null || remote._disposed)
                {
                    Close(local, "couldn't connect: no transport has that ticket");
                }
                else if (!remote._accept || remote._ends.Count >= remote._maxPeers)
                {
                    Close(local, "couldn't connect: refused");
                }
                else
                {
                    var other = remote.NewEnd();
                    local.Other = other;
                    other.Other = local;
                    _events.Enqueue(LinkEvent.Connected(local.Id));
                    remote._events.Enqueue(LinkEvent.Connected(other.Id));
                }

                return local.Id;
            }
        }

        public void Disconnect(PeerId peer)
        {
            lock (_network.Lock)
            {
                if (_ends.TryGetValue(peer.Value, out var end))
                {
                    Close(end, "disconnected locally", "closed by peer");
                }
            }
        }

        public void Send(PeerId peer, ReadOnlySpan<byte> data, Delivery delivery)
        {
            int max = delivery == Delivery.Unreliable ? MAX_UNRELIABLE_SIZE : MAX_RELIABLE_SIZE;
            if (data.Length > max)
            {
                throw new ArgumentException($"Message of {data.Length} bytes is over the {max} byte limit",
                    nameof(data));
            }

            var bytes = data.ToArray();
            lock (_network.Lock)
            {
                if (_disposed || !_ends.TryGetValue(peer.Value, out var end) || end.Other == null)
                {
                    return;
                }

                end.BytesSent += bytes.Length;
                if (delivery == Delivery.Unreliable && DropUnreliable)
                {
                    return;
                }

                var other = end.Other;
                other.BytesReceived += bytes.Length;
                other.Owner._events.Enqueue(LinkEvent.Message(other.Id, delivery, bytes));
            }
        }

        public bool TryPoll(out LinkEvent ev)
        {
            lock (_network.Lock)
            {
                if (_disposed)
                {
                    ev = default;
                    return false;
                }

                return _events.TryDequeue(out ev);
            }
        }

        public PeerStats GetStats(PeerId peer)
        {
            lock (_network.Lock)
            {
                if (!_ends.TryGetValue(peer.Value, out var end) || end.Other == null)
                {
                    return default;
                }

                return new PeerStats(true, TimeSpan.Zero, LinkPath.Direct, end.BytesSent, end.BytesReceived);
            }
        }

        public string GetPeerIdentity(PeerId peer)
        {
            lock (_network.Lock)
            {
                return _ends.TryGetValue(peer.Value, out var end) && end.Other != null
                    ? end.Other.Owner._identity
                    : null;
            }
        }

        public void Dispose()
        {
            lock (_network.Lock)
            {
                if (_disposed) return;
                _disposed = true;

                foreach (var end in new List<End>(_ends.Values))
                {
                    Close(end, "transport closed", "closed by peer");
                }

                _events.Clear();
                _network.Remove(LocalTicket);
            }
        }

        #endregion

        /// <summary>
        /// Lose the connection to a peer without a goodbye, as when a cable is pulled. Both sides hear of it,
        /// and messages already sent still arrive (they were in flight before the loss).
        /// </summary>
        public void Sever(PeerId peer)
        {
            lock (_network.Lock)
            {
                if (_ends.TryGetValue(peer.Value, out var end))
                {
                    Close(end, "connection lost", "connection lost");
                }
            }
        }

        /// <summary><see cref="Sever"/> every connection this transport has.</summary>
        public void SeverAll()
        {
            lock (_network.Lock)
            {
                foreach (var end in new List<End>(_ends.Values))
                {
                    Close(end, "connection lost", "connection lost");
                }
            }
        }

        private End NewEnd()
        {
            var end = new End { Owner = this, Id = new PeerId(++_nextPeer) };
            _ends[end.Id.Value] = end;
            return end;
        }

        /// <summary>Close one end and its other side, each reporting its disconnect exactly once.</summary>
        private static void Close(End end, string reason, string otherReason = null)
        {
            var other = end.Other;
            end.Other = null;
            end.Owner.Forget(end, reason);

            if (other != null)
            {
                other.Other = null;
                other.Owner.Forget(other, otherReason ?? reason);
            }
        }

        private void Forget(End end, string reason)
        {
            if (!_ends.Remove(end.Id.Value)) return;
            if (!_disposed)
            {
                _events.Enqueue(LinkEvent.Disconnected(end.Id, reason));
            }
        }

        private static string ParseTicket(string ticket)
        {
            string id = ticket?.Trim().ToLowerInvariant() ?? "";
            if (id.Length == 32 && id.TrimStart("0123456789abcdef".ToCharArray()).Length == 0)
            {
                return id;
            }

            // Not repeating the ticket, like the native transport: it may be anything from the clipboard
            throw new FormatException("Not a memory transport ticket (expected 32 hex digits)");
        }

        private static string NewIdentity()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryLinkTransport));
        }
    }
}
