using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace YARG.Integration.Link.Transport
{
    /// <summary>
    /// A managed transport for development and tests: TCP for reliable messages, UDP for unreliable ones,
    /// on localhost or a LAN. No NAT traversal and no encryption, so not for release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tickets look like <c>loopback:127.0.0.1:36120</c>. The TCP listener and the UDP socket share a port
    /// number when possible; either way the UDP port is announced in a hello frame when a connection opens,
    /// and datagrams are matched to peers by their source address.
    /// </para>
    /// <para>
    /// TCP frames are <c>[u32 length][u8 kind][payload]</c>, little-endian, where the length counts the kind
    /// byte and payload. Kinds: hello (payload: u16 UDP port), data, ping and pong (payload: the sender's
    /// clock as i64 ticks, echoed back to measure the round-trip time).
    /// </para>
    /// </remarks>
    public sealed class LoopbackLinkTransport : ILinkTransport
    {
        public const string TICKET_PREFIX = "loopback:";

        /// <summary>The same budget as an iroh/QUIC datagram on a typical path.</summary>
        public const int MAX_UNRELIABLE_SIZE = 1200;

        /// <summary>The largest reliable message; the same as the native transport's.</summary>
        public const int MAX_RELIABLE_SIZE = 1024 * 1024 - 1;

        private const int MAX_FRAME_SIZE = MAX_RELIABLE_SIZE + 1;
        private const int MAX_HELLO_FRAME_SIZE = 64;

        private const byte FRAME_HELLO = 0;
        private const byte FRAME_DATA  = 1;
        private const byte FRAME_PING  = 2;
        private const byte FRAME_PONG  = 3;

        private static readonly TimeSpan PING_INTERVAL   = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(5);

        private readonly TcpListener _listener;
        private readonly UdpClient   _udp;
        private readonly Timer       _pingTimer;
        private readonly Stopwatch   _clock = Stopwatch.StartNew();

        private readonly ConcurrentQueue<LinkEvent>          _events      = new();
        private readonly ConcurrentDictionary<uint, Connection> _connections = new();

        private readonly bool _accept;
        private readonly int  _maxPeers;

        private int           _nextId;
        private volatile bool _disposed;

        public string LocalTicket { get; }
        public int MaxUnreliableSize => MAX_UNRELIABLE_SIZE;

        /// <summary>The TCP port this transport accepts connections on.</summary>
        public int Port { get; }

        /// <param name="port">Port to listen on, or 0 for any free one.</param>
        /// <param name="address">Address to listen on; <see cref="IPAddress.Loopback"/> by default.
        /// Use <see cref="IPAddress.Any"/> to accept connections from the LAN.</param>
        /// <param name="advertisedHost">Host to put in <see cref="LocalTicket"/>, for example a LAN address.</param>
        /// <param name="accept">Accept incoming connections. Off for a game that only joins others.</param>
        /// <param name="maxPeers">The most connections at once; further incoming ones are closed.</param>
        /// <exception cref="SocketException">The port is in use.</exception>
        public LoopbackLinkTransport(int port = 0, IPAddress address = null, string advertisedHost = null,
            bool accept = true, int maxPeers = 16)
        {
            address ??= IPAddress.Loopback;
            _accept = accept;
            _maxPeers = Math.Max(1, maxPeers);

            _listener = new TcpListener(address, port);
            _listener.Start();
            Port = ((IPEndPoint) _listener.LocalEndpoint).Port;

            try
            {
                _udp = new UdpClient(new IPEndPoint(address, Port));
            }
            catch (SocketException)
            {
                _udp = new UdpClient(new IPEndPoint(address, 0));
            }

            if (advertisedHost == null)
            {
                advertisedHost = address.Equals(IPAddress.Any) ? IPAddress.Loopback.ToString() : address.ToString();
            }
            LocalTicket = $"{TICKET_PREFIX}{advertisedHost}:{Port}";

            StartThread(AcceptLoop, "YARG Link accept");
            StartThread(UdpReceiveLoop, "YARG Link UDP");
            _pingTimer = new Timer(_ => SendPings(), null, PING_INTERVAL, PING_INTERVAL);
        }

        private int UdpPort => ((IPEndPoint) _udp.Client.LocalEndPoint).Port;

        private long NowTicks => _clock.Elapsed.Ticks;

        #region ILinkTransport

        public PeerId Connect(string ticket)
        {
            ThrowIfDisposed();
            var (host, port) = ParseTicket(ticket);

            var connection = AddConnection();
            StartThread(() =>
            {
                TcpClient client = null;
                try
                {
                    var address = ResolveIPv4(host);
                    client = new TcpClient(AddressFamily.InterNetwork);
                    var connect = client.ConnectAsync(address, port);
                    if (!connect.Wait(CONNECT_TIMEOUT))
                    {
                        client.Dispose();
                        Close(connection, "connection timed out");
                        return;
                    }
                }
                catch (Exception e)
                {
                    client?.Dispose();
                    Close(connection, $"couldn't connect: {Unwrap(e).Message}");
                    return;
                }

                Run(connection, client);
            }, "YARG Link connect");

            return connection.Id;
        }

        public void Disconnect(PeerId peer)
        {
            if (_connections.TryGetValue(peer.Value, out var connection))
            {
                Close(connection, "disconnected locally");
            }
        }

        public void Send(PeerId peer, ReadOnlySpan<byte> data, Delivery delivery)
        {
            if (delivery == Delivery.Unreliable && data.Length > MAX_UNRELIABLE_SIZE)
            {
                throw new ArgumentException(
                    $"Unreliable message of {data.Length} bytes is over the {MAX_UNRELIABLE_SIZE} byte limit",
                    nameof(data));
            }

            if (delivery == Delivery.Reliable && data.Length > MAX_RELIABLE_SIZE)
            {
                throw new ArgumentException(
                    $"Message of {data.Length} bytes is over the {MAX_RELIABLE_SIZE} byte limit", nameof(data));
            }

            if (_disposed || !_connections.TryGetValue(peer.Value, out var connection) || !connection.Ready)
            {
                return;
            }

            if (delivery == Delivery.Reliable)
            {
                WriteFrame(connection, FRAME_DATA, data);
                return;
            }

            var endpoint = connection.UdpEndpoint;
            if (endpoint == null) return;

            try
            {
                var bytes = data.ToArray();
                _udp.Send(bytes, bytes.Length, endpoint);
                Interlocked.Add(ref connection.BytesSent, bytes.Length);
            }
            catch (SocketException)
            {
                // Unreliable anyway
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently
            }
        }

        public bool TryPoll(out LinkEvent ev)
        {
            if (_disposed)
            {
                ev = default;
                return false;
            }

            return _events.TryDequeue(out ev);
        }

        public PeerStats GetStats(PeerId peer)
        {
            if (!_connections.TryGetValue(peer.Value, out var connection))
            {
                return default;
            }

            long rtt = Interlocked.Read(ref connection.RttTicks);
            return new PeerStats(
                connection.Ready,
                rtt >= 0 ? TimeSpan.FromTicks(rtt) : (TimeSpan?) null,
                LinkPath.Direct,
                Interlocked.Read(ref connection.BytesSent),
                Interlocked.Read(ref connection.BytesReceived));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pingTimer.Dispose();
            _listener.Stop();
            _udp.Dispose();

            foreach (var connection in _connections.Values)
            {
                Close(connection, "transport closed");
            }

            while (_events.TryDequeue(out _))
            {
            }
        }

        #endregion

        #region Connections

        private sealed class Connection
        {
            public readonly PeerId Id;
            public readonly object WriteLock = new();

            public TcpClient     Client;
            public NetworkStream Stream;

            /// <summary>Set once the peer's hello arrived; only then is the connection reported.</summary>
            public volatile bool Ready;

            /// <summary>Where the peer receives datagrams; set with <see cref="Ready"/>.</summary>
            public volatile IPEndPoint UdpEndpoint;

            public long BytesSent;
            public long BytesReceived;
            public long RttTicks = -1;

            public int Closed;

            public Connection(PeerId id)
            {
                Id = id;
            }
        }

        private Connection AddConnection()
        {
            var id = new PeerId((uint) Interlocked.Increment(ref _nextId));
            var connection = new Connection(id);
            _connections[id.Value] = connection;
            return connection;
        }

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try
                {
                    // Async, not AcceptTcpClient: on macOS, disposing a socket that a thread is
                    // blocked on never wakes that thread, and Dispose then waits forever.
                    client = _listener.AcceptTcpClientAsync().GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    if (!_disposed)
                    {
                        _events.Enqueue(LinkEvent.Error($"stopped accepting connections: {e.Message}"));
                    }
                    return;
                }

                if (!_accept || _connections.Count >= _maxPeers)
                {
                    client.Dispose();
                    continue;
                }

                var connection = AddConnection();
                StartThread(() => Run(connection, client), "YARG Link peer");
            }
        }

        /// <summary>
        /// Read frames from a connection until it closes. Runs on the connection's own thread.
        /// </summary>
        private void Run(Connection connection, TcpClient client)
        {
            lock (connection.WriteLock)
            {
                connection.Client = client;
                if (Volatile.Read(ref connection.Closed) != 0 || _disposed)
                {
                    // Disconnected or disposed while we were connecting
                    client.Dispose();
                    return;
                }
            }

            string reason;
            try
            {
                client.NoDelay = true;
                connection.Stream = client.GetStream();

                Span<byte> hello = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(hello, (ushort) UdpPort);
                WriteFrame(connection, FRAME_HELLO, hello);

                reason = ReadLoop(connection, client);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                reason = $"connection lost: {Unwrap(e).Message}";
            }

            Close(connection, reason);
        }

        private string ReadLoop(Connection connection, TcpClient client)
        {
            var stream = connection.Stream;
            var header = new byte[4];

            while (true)
            {
                if (!ReadExactly(stream, header, 4))
                {
                    return "closed by peer";
                }

                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                int max = connection.Ready ? MAX_FRAME_SIZE : MAX_HELLO_FRAME_SIZE;
                if (length < 1 || length > max)
                {
                    return $"bad frame length {length}";
                }

                var frame = new byte[length];
                if (!ReadExactly(stream, frame, length))
                {
                    return "closed by peer";
                }
                Interlocked.Add(ref connection.BytesReceived, 4 + length);

                byte kind = frame[0];
                var payload = new ReadOnlySpan<byte>(frame, 1, length - 1);

                if (!connection.Ready && kind != FRAME_HELLO)
                {
                    return "peer sent data before its hello";
                }

                switch (kind)
                {
                    case FRAME_HELLO:
                    {
                        if (connection.Ready || payload.Length < 2)
                        {
                            return "bad hello";
                        }

                        int udpPort = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                        var remote = (IPEndPoint) client.Client.RemoteEndPoint;
                        connection.UdpEndpoint = new IPEndPoint(remote.Address, udpPort);
                        connection.Ready = true;
                        _events.Enqueue(LinkEvent.Connected(connection.Id));
                        break;
                    }
                    case FRAME_DATA:
                        _events.Enqueue(LinkEvent.Message(connection.Id, Delivery.Reliable, payload.ToArray()));
                        break;
                    case FRAME_PING:
                        WriteFrame(connection, FRAME_PONG, payload);
                        break;
                    case FRAME_PONG:
                        if (payload.Length >= 8)
                        {
                            long sent = BinaryPrimitives.ReadInt64LittleEndian(payload);
                            Interlocked.Exchange(ref connection.RttTicks, Math.Max(0, NowTicks - sent));
                        }
                        break;
                    default:
                        // From a newer version; skip it
                        break;
                }
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        private void WriteFrame(Connection connection, byte kind, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[5 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, 1 + payload.Length);
            frame[4] = kind;
            payload.CopyTo(new Span<byte>(frame, 5, payload.Length));

            try
            {
                lock (connection.WriteLock)
                {
                    var stream = connection.Stream;
                    if (stream == null) return;
                    stream.Write(frame, 0, frame.Length);
                }
                Interlocked.Add(ref connection.BytesSent, frame.Length);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                Close(connection, $"connection lost: {Unwrap(e).Message}");
            }
        }

        private void Close(Connection connection, string reason)
        {
            if (Interlocked.Exchange(ref connection.Closed, 1) != 0) return;

            lock (connection.WriteLock)
            {
                connection.Client?.Dispose();
                connection.Stream = null;
            }
            connection.Ready = false;

            _connections.TryRemove(connection.Id.Value, out _);
            if (!_disposed)
            {
                _events.Enqueue(LinkEvent.Disconnected(connection.Id, reason));
            }
        }

        private void SendPings()
        {
            if (_disposed) return;

            Span<byte> payload = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(payload, NowTicks);
            foreach (var connection in _connections.Values)
            {
                if (connection.Ready)
                {
                    WriteFrame(connection, FRAME_PING, payload);
                }
            }
        }

        #endregion

        #region UDP

        private void UdpReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!_disposed)
            {
                byte[] bytes;
                try
                {
                    // Async for the same reason as in AcceptLoop
                    var received = _udp.ReceiveAsync().GetAwaiter().GetResult();
                    bytes = received.Buffer;
                    remote = received.RemoteEndPoint;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException e)
                {
                    if (_disposed) return;

                    // Windows reports an earlier send's "port unreachable" here; just keep going
                    if (e.SocketErrorCode == SocketError.ConnectionReset) continue;

                    _events.Enqueue(LinkEvent.Error($"UDP receive failed: {e.Message}"));
                    return;
                }

                foreach (var connection in _connections.Values)
                {
                    if (connection.Ready && remote.Equals(connection.UdpEndpoint))
                    {
                        Interlocked.Add(ref connection.BytesReceived, bytes.Length);
                        _events.Enqueue(LinkEvent.Message(connection.Id, Delivery.Unreliable, bytes));
                        break;
                    }
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Split a ticket into host and port. The <c>loopback:</c> prefix is optional.
        /// </summary>
        /// <exception cref="FormatException">The ticket isn't <c>[loopback:]host:port</c>.</exception>
        public static (string Host, int Port) ParseTicket(string ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket))
            {
                throw new FormatException("Empty ticket");
            }

            string rest = ticket.Trim();
            if (rest.StartsWith(TICKET_PREFIX, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest.Substring(TICKET_PREFIX.Length);
            }

            int colon = rest.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rest.Substring(colon + 1), out int port) || port is <= 0 or > 65535)
            {
                throw new FormatException($"Not a loopback ticket (expected {TICKET_PREFIX}host:port): {ticket}");
            }

            return (rest.Substring(0, colon), port);
        }

        private static IPAddress ResolveIPv4(string host)
        {
            if (IPAddress.TryParse(host, out var address))
            {
                return address;
            }

            foreach (var candidate in Dns.GetHostAddresses(host))
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                {
                    return candidate;
                }
            }

            throw new SocketException((int) SocketError.HostNotFound);
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is AggregateException { InnerException: { } inner })
            {
                e = inner;
            }
            return e is IOException { InnerException: SocketException socket } ? socket : e;
        }

        private static void StartThread(Action action, string name)
        {
            new Thread(() => action()) { IsBackground = true, Name = name }.Start();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LoopbackLinkTransport));
        }

        #endregion
    }
}
