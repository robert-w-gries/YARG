using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YARG.Integration.Link.Transport
{
    public enum NativeLogLevel
    {
        Error = 1,
        Warning = 2,
        Info = 3,
        Debug = 4,
        Trace = 5,
    }

    /// <summary>Settings for <see cref="NativeLinkTransport"/>; see <c>yl_create</c> in yarg_link.h.</summary>
    public sealed class NativeLinkConfig
    {
        /// <summary>Fall back to n0's public relays when a direct path can't be made.</summary>
        public bool Relay = true;

        /// <summary>Find peers by endpoint ID alone, through n0's DNS.</summary>
        public bool AddressLookup = true;

        /// <summary>For example <c>127.0.0.1:0</c>; <c>null</c> for every interface on a random port.</summary>
        public string BindAddress;

        /// <summary>Put direct addresses in the ticket (needed without <see cref="AddressLookup"/>).</summary>
        public bool TicketAddresses;

        public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Which log lines to report, as a tracing filter; <c>null</c> for the library default.</summary>
        public string LogFilter;

        /// <summary>Accept incoming connections. Only a host turns this on; it is off for a game that only joins others.</summary>
        public bool Accept;

        /// <summary>
        /// The most peers at once, counting incoming connections that are still handshaking. This is a limit of the
        /// library, not the party size: <c>LinkController</c> gives its own value.
        /// </summary>
        public int MaxPeers = 16;

        internal string ToJson()
        {
            var json = new StringBuilder("{");
            void Add(string name, string value)
            {
                if (json.Length > 1) json.Append(',');
                json.Append('"').Append(name).Append("\":").Append(value);
            }

            Add("relay", Relay ? "true" : "false");
            Add("address_lookup", AddressLookup ? "true" : "false");
            if (BindAddress != null) Add("bind_addr", Quote(BindAddress));
            Add("ticket_addrs", TicketAddresses ? "true" : "false");
            Add("connect_timeout_ms",
                ((long) Math.Max(1, ConnectTimeout.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture));
            if (LogFilter != null) Add("log_filter", Quote(LogFilter));
            Add("accept", Accept ? "true" : "false");
            Add("max_peers", Math.Max(1, MaxPeers).ToString(CultureInfo.InvariantCulture));
            return json.Append('}').ToString();
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':  quoted.Append("\\\""); break;
                    case '\\': quoted.Append("\\\\"); break;
                    default:
                        if (c < 0x20)
                            quoted.Append("\\u").Append(((int) c).ToString("x4"));
                        else
                            quoted.Append(c);
                        break;
                }
            }
            return quoted.Append('"').ToString();
        }
    }

    /// <summary>
    /// The release transport: iroh (QUIC with hole punching, end-to-end encryption and relay fallback)
    /// in the YargLink native library (<c>Native/YargLink</c>).
    /// </summary>
    /// <remarks>
    /// Tickets are endpoint IDs, optionally with <c>@</c> and direct addresses. Every instance gets a new,
    /// random endpoint ID, so its ticket stops working once it's disposed. The library runs its own threads
    /// and never calls back; <see cref="TryPoll"/> drains its event queue.
    /// </remarks>
    public sealed class NativeLinkTransport : ILinkTransport
    {
        /// <summary>The largest unreliable message (<c>YL_MAX_UNRELIABLE</c>).</summary>
        public const int MAX_UNRELIABLE_SIZE = 1024;

        /// <summary>The largest reliable message (<c>YL_MAX_RELIABLE</c>).</summary>
        public const int MAX_RELIABLE_SIZE = 1024 * 1024 - 1;

        // Copies of values in yarg_link.h. C# cannot read the header, so it has its own copies.
        // HeaderConstantsTests checks that they agree with the header. They are internal for that test.
        internal const int ABI_VERSION = 4;

        internal const int YL_OK                   = 0;
        internal const int YL_ERR_BAD_TICKET       = -4;
        internal const int YL_ERR_BUFFER_TOO_SMALL = -5;
        internal const int YL_ERR_TOO_LARGE        = -6;

        internal const uint EVENT_PEER_CONNECTED    = 1;
        internal const uint EVENT_PEER_DISCONNECTED = 2;
        internal const uint EVENT_MESSAGE           = 3;
        internal const uint EVENT_ERROR             = 4;
        internal const uint EVENT_LOG               = 5;

        internal const uint PATH_DIRECT = 1;
        internal const uint PATH_RELAY  = 2;

        private IntPtr _handle;
        private byte[] _buffer = new byte[64 * 1024];

        public string LocalTicket { get; }
        public int MaxUnreliableSize => MAX_UNRELIABLE_SIZE;

        /// <summary>The library's log lines. Called from <see cref="TryPoll"/>, so on the polling thread.</summary>
        public Action<NativeLogLevel, string> Log { get; set; }

        /// <param name="libraryPath">The YargLink library to load. Only the first path is ever loaded;
        /// native libraries can't be unloaded.</param>
        /// <exception cref="DllNotFoundException">The library can't be loaded.</exception>
        /// <exception cref="InvalidOperationException">The endpoint can't be opened.</exception>
        public NativeLinkTransport(string libraryPath, NativeLinkConfig config = null)
        {
            Native.Load(libraryPath);
            config ??= new NativeLinkConfig();

            int rc = Native.Create(NullTerminated(config.ToJson()), out _handle);
            if (rc != YL_OK)
            {
                throw new InvalidOperationException($"Couldn't open the link endpoint ({rc}): {Native.LastError()}");
            }

            try
            {
                LocalTicket = ReadTicket();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        #region ILinkTransport

        public PeerId Connect(string ticket)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(ticket))
            {
                throw new FormatException("Empty ticket");
            }

            int rc = Native.Connect(_handle, NullTerminated(ticket.Trim()), out uint peer);
            if (rc == YL_ERR_BAD_TICKET)
            {
                // The library's message never includes the ticket, which may be anything from the clipboard
                throw new FormatException($"Not a valid ticket: {Native.LastError()}");
            }
            Check(rc, "connect");
            return new PeerId(peer);
        }

        public void Disconnect(PeerId peer)
        {
            if (_handle == IntPtr.Zero) return;
            Check(Native.Disconnect(_handle, peer.Value), "disconnect");
        }

        public unsafe void Send(PeerId peer, ReadOnlySpan<byte> data, Delivery delivery)
        {
            if (delivery == Delivery.Unreliable && data.Length > MAX_UNRELIABLE_SIZE)
            {
                throw new ArgumentException(
                    $"Unreliable message of {data.Length} bytes is over the {MAX_UNRELIABLE_SIZE} byte limit",
                    nameof(data));
            }
            if (_handle == IntPtr.Zero) return;

            int rc;
            fixed (byte* ptr = data)
            {
                rc = Native.Send(_handle, peer.Value, (byte) delivery, ptr, (UIntPtr) data.Length);
            }
            if (rc == YL_ERR_TOO_LARGE)
            {
                throw new ArgumentException(Native.LastError(), nameof(data));
            }
            Check(rc, "send");
        }

        public unsafe bool TryPoll(out LinkEvent ev)
        {
            while (_handle != IntPtr.Zero)
            {
                Native.Event native;
                int rc;
                fixed (byte* buf = _buffer)
                {
                    rc = Native.Poll(_handle, out native, buf, (UIntPtr) _buffer.Length);
                }

                if (rc == 0) break;
                if (rc == YL_ERR_BUFFER_TOO_SMALL)
                {
                    _buffer = new byte[Math.Max((int) native.Len, _buffer.Length * 2)];
                    continue;
                }
                Check(rc, "poll");

                int len = (int) native.Len;
                switch (native.Type)
                {
                    case EVENT_PEER_CONNECTED:
                        ev = LinkEvent.Connected(new PeerId(native.Peer));
                        return true;
                    case EVENT_PEER_DISCONNECTED:
                        ev = LinkEvent.Disconnected(new PeerId(native.Peer), Text(len));
                        return true;
                    case EVENT_MESSAGE:
                    {
                        var data = new byte[len];
                        Buffer.BlockCopy(_buffer, 0, data, 0, len);
                        ev = LinkEvent.Message(new PeerId(native.Peer), (Delivery) native.Channel, data);
                        return true;
                    }
                    case EVENT_ERROR:
                        ev = LinkEvent.Error(Text(len));
                        return true;
                    case EVENT_LOG:
                        Log?.Invoke((NativeLogLevel) native.LogLevel, Text(len));
                        break;
                    default:
                        // From a newer library; skip it
                        break;
                }
            }

            ev = default;
            return false;
        }

        public PeerStats GetStats(PeerId peer)
        {
            if (_handle == IntPtr.Zero || Native.PeerStats(_handle, peer.Value, out var stats) != YL_OK)
            {
                return default;
            }

            var path = stats.Path switch
            {
                PATH_DIRECT => LinkPath.Direct,
                PATH_RELAY  => LinkPath.Relay,
                _ => LinkPath.Unknown,
            };
            return new PeerStats(
                stats.Connected != 0,
                stats.RttMicros >= 0 ? TimeSpan.FromTicks(stats.RttMicros * 10) : (TimeSpan?) null,
                path,
                (long) stats.BytesSent,
                (long) stats.BytesReceived);
        }

        public unsafe string GetPeerIdentity(PeerId peer)
        {
            if (_handle == IntPtr.Zero) return null;

            var buf = new byte[128];
            UIntPtr len;
            fixed (byte* ptr = buf)
            {
                if (Native.PeerId(_handle, peer.Value, ptr, (UIntPtr) buf.Length, out len) != YL_OK) return null;
            }
            if ((int) len > buf.Length) return null; // endpoint IDs are 64 characters
            return Encoding.UTF8.GetString(buf, 0, (int) len);
        }

        /// <summary>
        /// Close the transport. Returns at once: the library still sends queued reliable messages
        /// (goodbyes) first, which can take a few seconds with a slow peer, so that happens on a
        /// thread of its own. Call <see cref="WaitForClosedHandles"/> before the process exits.
        /// </summary>
        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                DestroyInBackground(handle);
            }
            GC.SuppressFinalize(this);
        }

        private static readonly List<Thread> _closingHandles = new();

        private static void DestroyInBackground(IntPtr handle)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Native.Destroy(handle);
                }
                finally
                {
                    lock (_closingHandles) _closingHandles.Remove(Thread.CurrentThread);
                }
            })
            {
                IsBackground = true,
                Name = "YargLink close",
            };
            lock (_closingHandles)
            {
                _closingHandles.Add(thread);
                thread.Start();
            }
        }

        /// <summary>
        /// Wait for transports closed by <see cref="Dispose"/> to finish, so their goodbyes go out
        /// before the process exits.
        /// </summary>
        /// <returns>Whether all of them finished within <paramref name="timeout"/>.</returns>
        public static bool WaitForClosedHandles(TimeSpan timeout)
        {
            Thread[] threads;
            lock (_closingHandles) threads = _closingHandles.ToArray();

            var deadline = DateTime.UtcNow + timeout;
            foreach (var thread in threads)
            {
                var left = deadline - DateTime.UtcNow;
                if (left < TimeSpan.Zero || !thread.Join(left)) return false;
            }
            return true;
        }

        ~NativeLinkTransport()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                Native.Destroy(handle);
            }
        }

        #endregion

        private unsafe string ReadTicket()
        {
            var buf = new byte[512];
            UIntPtr len;
            fixed (byte* ptr = buf)
            {
                Check(Native.LocalTicket(_handle, ptr, (UIntPtr) buf.Length, out len), "local ticket");
            }
            if ((int) len > buf.Length)
            {
                buf = new byte[(int) len];
                fixed (byte* ptr = buf)
                {
                    Check(Native.LocalTicket(_handle, ptr, (UIntPtr) buf.Length, out len), "local ticket");
                }
            }
            return Encoding.UTF8.GetString(buf, 0, (int) len);
        }

        private string Text(int len) => Encoding.UTF8.GetString(_buffer, 0, len);

        private static byte[] NullTerminated(string text)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
            return bytes;
        }

        private static void Check(int rc, string what)
        {
            if (rc < 0)
            {
                throw new InvalidOperationException($"YargLink {what} failed ({rc}): {Native.LastError()}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeLinkTransport));
        }

        /// <summary>
        /// The library's functions, bound once per process from whichever path is loaded first.
        /// </summary>
        private static unsafe class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct Event
            {
                public uint  Type;
                public uint  Peer;
                public uint  Channel;
                public uint  LogLevel;
                public ulong Len;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Stats
            {
                public uint  Connected;
                public uint  Path;
                public long  RttMicros;
                public ulong BytesSent;
                public ulong BytesReceived;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint AbiVersionFn();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int LastErrorFn(byte* buf, UIntPtr cap, out UIntPtr len);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int CreateFn(byte[] config, out IntPtr handle);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void DestroyFn(IntPtr handle);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int LocalTicketFn(IntPtr handle, byte* buf, UIntPtr cap, out UIntPtr len);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int ConnectFn(IntPtr handle, byte[] ticket, out uint peer);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int DisconnectFn(IntPtr handle, uint peer);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int SendFn(IntPtr handle, uint peer, byte channel, byte* data, UIntPtr len);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int PollFn(IntPtr handle, out Event ev, byte* buf, UIntPtr cap);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int PeerStatsFn(IntPtr handle, uint peer, out Stats stats);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int PeerIdFn(IntPtr handle, uint peer, byte* buf, UIntPtr cap, out UIntPtr len);

            private static readonly object _lock = new();
            private static IntPtr _library;

            private static LastErrorFn   _lastError;
            private static CreateFn      _create;
            private static DestroyFn     _destroy;
            private static LocalTicketFn _localTicket;
            private static ConnectFn     _connect;
            private static DisconnectFn  _disconnect;
            private static SendFn        _send;
            private static PollFn        _poll;
            private static PeerStatsFn   _peerStats;
            private static PeerIdFn      _peerId;

            public static void Load(string path)
            {
                lock (_lock)
                {
                    if (_library != IntPtr.Zero) return;

                    var library = LoadLibrary(path, out string error);
                    if (library == IntPtr.Zero)
                    {
                        throw new DllNotFoundException($"Couldn't load YargLink from {path}: {error}");
                    }

                    var abi = Bind<AbiVersionFn>(library, "yl_abi_version")();
                    if (abi != ABI_VERSION)
                    {
                        throw new DllNotFoundException(
                            $"{path} has YargLink ABI version {abi}, but this build needs {ABI_VERSION}");
                    }

                    _lastError = Bind<LastErrorFn>(library, "yl_last_error");
                    _create = Bind<CreateFn>(library, "yl_create");
                    _destroy = Bind<DestroyFn>(library, "yl_destroy");
                    _localTicket = Bind<LocalTicketFn>(library, "yl_local_ticket");
                    _connect = Bind<ConnectFn>(library, "yl_connect");
                    _disconnect = Bind<DisconnectFn>(library, "yl_disconnect");
                    _send = Bind<SendFn>(library, "yl_send");
                    _poll = Bind<PollFn>(library, "yl_poll");
                    _peerStats = Bind<PeerStatsFn>(library, "yl_peer_stats");
                    _peerId = Bind<PeerIdFn>(library, "yl_peer_id");
                    _library = library;
                }
            }

            public static string LastError()
            {
                var buf = new byte[1024];
                UIntPtr len;
                fixed (byte* ptr = buf)
                {
                    _lastError(ptr, (UIntPtr) buf.Length, out len);
                }
                return Encoding.UTF8.GetString(buf, 0, Math.Min((int) len, buf.Length));
            }

            public static int Create(byte[] config, out IntPtr handle) => _create(config, out handle);
            public static void Destroy(IntPtr handle) => _destroy(handle);

            public static int LocalTicket(IntPtr handle, byte* buf, UIntPtr cap, out UIntPtr len) =>
                _localTicket(handle, buf, cap, out len);

            public static int Connect(IntPtr handle, byte[] ticket, out uint peer) => _connect(handle, ticket, out peer);
            public static int Disconnect(IntPtr handle, uint peer) => _disconnect(handle, peer);

            public static int Send(IntPtr handle, uint peer, byte channel, byte* data, UIntPtr len) =>
                _send(handle, peer, channel, data, len);

            public static int Poll(IntPtr handle, out Event ev, byte* buf, UIntPtr cap) =>
                _poll(handle, out ev, buf, cap);

            public static int PeerStats(IntPtr handle, uint peer, out Stats stats) =>
                _peerStats(handle, peer, out stats);

            public static int PeerId(IntPtr handle, uint peer, byte* buf, UIntPtr cap, out UIntPtr len) =>
                _peerId(handle, peer, buf, cap, out len);

            private static T Bind<T>(IntPtr library, string name) where T : Delegate
            {
                var address = GetSymbol(library, name);
                if (address == IntPtr.Zero)
                {
                    throw new EntryPointNotFoundException($"YargLink has no export {name}");
                }
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            #region Platform loading

            private const int RTLD_NOW = 2;

            private static IntPtr LoadLibrary(string path, out string error)
            {
                error = null;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var library = Windows.LoadLibraryW(path);
                    if (library == IntPtr.Zero) error = $"error {Marshal.GetLastWin32Error()}";
                    return library;
                }

                var handle = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Mac.dlopen(path, RTLD_NOW)
                    : Linux.dlopen(path, RTLD_NOW);
                if (handle == IntPtr.Zero)
                {
                    var message = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Mac.dlerror() : Linux.dlerror();
                    error = message == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(message);
                }
                return handle;
            }

            private static IntPtr GetSymbol(IntPtr library, string name)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Windows.GetProcAddress(library, name);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return Mac.dlsym(library, name);
                return Linux.dlsym(library, name);
            }

            private static class Windows
            {
                [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
                public static extern IntPtr LoadLibraryW(string path);

                [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
                public static extern IntPtr GetProcAddress(IntPtr module, string name);
            }

            private static class Linux
            {
                [DllImport("libdl.so.2")]
                public static extern IntPtr dlopen(string path, int flags);

                [DllImport("libdl.so.2")]
                public static extern IntPtr dlsym(IntPtr handle, string name);

                [DllImport("libdl.so.2")]
                public static extern IntPtr dlerror();
            }

            private static class Mac
            {
                [DllImport("libSystem.dylib")]
                public static extern IntPtr dlopen(string path, int flags);

                [DllImport("libSystem.dylib")]
                public static extern IntPtr dlsym(IntPtr handle, string name);

                [DllImport("libSystem.dylib")]
                public static extern IntPtr dlerror();
            }

            #endregion
        }
    }
}
