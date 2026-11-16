using System;

namespace YARG.Integration.Link.Transport
{
    // Everything in Transport/ and Session/ is plain .NET (no UnityEngine, no YARG.Core) so it can be
    // unit-tested outside Unity; see Tests/YARG.Link.Tests.

    /// <summary>
    /// A connection to another game, as numbered by the local transport. Only meaningful to the transport
    /// that issued it; the session layer identifies members across machines by their member ID instead.
    /// </summary>
    public readonly struct PeerId : IEquatable<PeerId>
    {
        public readonly uint Value;

        public PeerId(uint value)
        {
            Value = value;
        }

        public bool Equals(PeerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PeerId other && Equals(other);
        public override int GetHashCode() => (int) Value;
        public override string ToString() => $"peer#{Value}";

        public static bool operator ==(PeerId a, PeerId b) => a.Value == b.Value;
        public static bool operator !=(PeerId a, PeerId b) => a.Value != b.Value;
    }

    public enum Delivery : byte
    {
        /// <summary>Arrives exactly once, in order with other reliable messages to the same peer.</summary>
        Reliable = 0,

        /// <summary>
        /// May be lost, duplicated or reordered, and is never ordered against reliable messages. At most
        /// <see cref="ILinkTransport.MaxUnreliableSize"/> bytes.
        /// </summary>
        Unreliable = 1,
    }

    public enum LinkEventType
    {
        /// <summary>A connection is up, whether we opened it or the other side did.</summary>
        PeerConnected,

        /// <summary>
        /// A connection is gone, or a <see cref="ILinkTransport.Connect"/> failed. Raised exactly once per
        /// peer, including after a local <see cref="ILinkTransport.Disconnect"/>.
        /// </summary>
        PeerDisconnected,

        Message,

        /// <summary>Something went wrong that isn't tied to one peer (for example, the socket closed).</summary>
        Error,
    }

    public readonly struct LinkEvent
    {
        public readonly LinkEventType Type;
        public readonly PeerId        Peer;
        public readonly Delivery      Delivery;

        /// <summary>The payload of a <see cref="LinkEventType.Message"/>; owned by the receiver.</summary>
        public readonly byte[] Data;

        /// <summary>Why a peer disconnected, or what the error was.</summary>
        public readonly string Reason;

        private LinkEvent(LinkEventType type, PeerId peer, Delivery delivery, byte[] data, string reason)
        {
            Type = type;
            Peer = peer;
            Delivery = delivery;
            Data = data;
            Reason = reason;
        }

        public static LinkEvent Connected(PeerId peer) =>
            new(LinkEventType.PeerConnected, peer, default, null, null);

        public static LinkEvent Disconnected(PeerId peer, string reason) =>
            new(LinkEventType.PeerDisconnected, peer, default, null, reason);

        public static LinkEvent Message(PeerId peer, Delivery delivery, byte[] data) =>
            new(LinkEventType.Message, peer, delivery, data, null);

        public static LinkEvent Error(string reason) =>
            new(LinkEventType.Error, default, default, null, reason);

        public override string ToString() => Type switch
        {
            LinkEventType.Message => $"{Type}({Peer}, {Delivery}, {Data?.Length ?? 0} bytes)",
            _                     => $"{Type}({Peer}, {Reason})",
        };
    }

    public enum LinkPath
    {
        Unknown,

        /// <summary>Straight to the peer (hole-punched, LAN, or loopback).</summary>
        Direct,

        /// <summary>Through a relay server.</summary>
        Relay,
    }

    public readonly struct PeerStats
    {
        public readonly bool Connected;

        /// <summary>Round-trip time, or <c>null</c> if it hasn't been measured yet.</summary>
        public readonly TimeSpan? Rtt;

        public readonly LinkPath Path;
        public readonly long     BytesSent;
        public readonly long     BytesReceived;

        public PeerStats(bool connected, TimeSpan? rtt, LinkPath path, long bytesSent, long bytesReceived)
        {
            Connected = connected;
            Rtt = rtt;
            Path = path;
            BytesSent = bytesSent;
            BytesReceived = bytesReceived;
        }
    }

    /// <summary>
    /// A game-agnostic pipe to other games: connect, send bytes, poll events. Message meaning lives in the
    /// session layer above it (<see cref="Session.LinkSession"/>).
    /// </summary>
    /// <remarks>
    /// All members are called from one thread (Unity's main thread); implementations do their I/O elsewhere
    /// and hand results over through <see cref="TryPoll"/>. Nothing is ever called back.
    /// </remarks>
    public interface ILinkTransport : IDisposable
    {
        /// <summary>What another game passes to <see cref="Connect"/> to reach this one.</summary>
        string LocalTicket { get; }

        /// <summary>The largest payload <see cref="Delivery.Unreliable"/> can carry.</summary>
        int MaxUnreliableSize { get; }

        /// <summary>
        /// Start connecting. The result arrives later as <see cref="LinkEventType.PeerConnected"/> or
        /// <see cref="LinkEventType.PeerDisconnected"/> for the returned peer.
        /// </summary>
        /// <exception cref="FormatException">The ticket isn't one this transport understands.</exception>
        PeerId Connect(string ticket);

        /// <summary>
        /// Close a connection. Reliable messages already sent to the peer are still delivered (best effort),
        /// so a goodbye followed by a disconnect arrives. The same goes for <see cref="IDisposable.Dispose"/>.
        /// </summary>
        void Disconnect(PeerId peer);

        /// <summary>
        /// Queue a message. Sending to a peer that isn't connected (any more) is silently dropped; the
        /// disconnect is reported through <see cref="TryPoll"/>.
        /// </summary>
        /// <exception cref="ArgumentException">An unreliable message is over <see cref="MaxUnreliableSize"/>, or a
        /// reliable one over the transport's own limit (1 MiB for the transports here).</exception>
        void Send(PeerId peer, ReadOnlySpan<byte> data, Delivery delivery);

        /// <summary>Take the next event, if any. Call until it returns <c>false</c>, every frame.</summary>
        bool TryPoll(out LinkEvent ev);

        PeerStats GetStats(PeerId peer);

        /// <summary>
        /// Who is on the other end of a connection, as the transport knows them: for the native transport, the
        /// peer's endpoint ID, which its handshake authenticates. Available from
        /// <see cref="LinkEventType.PeerConnected"/> until the disconnect; <c>null</c> otherwise.
        /// </summary>
        /// <remarks>
        /// Endpoint IDs are free to make, so this only recognises a peer that keeps the same one. Games make a new
        /// one for every session, so it's no use for recognising anyone across sessions either.
        /// </remarks>
        string GetPeerIdentity(PeerId peer);
    }
}
