//! The wire format on a connection.
//!
//! Each connection has one bidirectional QUIC stream for reliable messages, and QUIC datagrams for
//! unreliable messages.
//!
//! # Frames on the stream
//!
//! The stream is a sequence of frames. Each frame has this layout (integers are little-endian):
//!
//! ```text
//! | length: u32 | kind: u8 | payload: (length - 1) bytes |
//! ```
//!
//! The length counts the kind byte and the payload, but not the length field itself. Thus a frame
//! with an empty payload has the length 1, and a length of 0 is never correct. `Frame` and
//! `FrameKind` are the Rust form of a frame.
//!
//! # Datagrams
//!
//! A datagram is one unreliable message. It has no frame header: QUIC keeps the datagram's
//! boundaries and length.
//!
//! # Handshake
//!
//! Before any message, the two sides exchange hello frames on the stream. The roles are fixed:
//! the side that connects (the joiner) writes its hello first, and the side that accepts (the host)
//! reads that hello first. Refer to `hello_as_joiner` and `hello_as_host`. A peer counts as
//! connected only when the hellos are done.

use bytes::Bytes;
use iroh::endpoint::{ReadExactError, RecvStream, SendStream};

/// The ALPN of this protocol.
///
/// ALPN (Application-Layer Protocol Negotiation, RFC 7301) is a protocol name that the two ends
/// agree on in the TLS handshake of QUIC. The endpoint accepts only the names in its list
/// (`Builder::alpns`), and `yl_connect` asks for this name. Thus a game with a different wire
/// format cannot connect: its handshake fails before any frame is read. Change the number at the
/// end on each change to the wire format. The test `wire_format_is_unchanged` fails when the bytes
/// of a frame change, to remind you.
pub const ALPN: &[u8] = b"yarg-link/3";

/// The version in the hello frame. Both sides must have the same version.
pub const TRANSPORT_VERSION: u16 = 1;

/// The size of the length field at the start of each frame, in bytes (a `u32`).
const LENGTH_FIELD_LEN: usize = 4;
/// The size of the kind field after the length field, in bytes (a `u8`).
const KIND_FIELD_LEN: usize = 1;
/// The size of a frame without its payload, in bytes.
const FRAME_HEADER_LEN: usize = LENGTH_FIELD_LEN + KIND_FIELD_LEN;

/// The largest reliable frame, in bytes, with the kind byte. Thus the largest reliable payload is
/// 1 byte less (`YL_MAX_RELIABLE`). The game's biggest message is a song setup (replay data for a
/// few players, with their presets), far below this.
pub const MAX_RELIABLE_FRAME: usize = 1024 * 1024;

/// The largest unreliable payload (one QUIC datagram), in bytes (`YL_MAX_UNRELIABLE`). QUIC needs
/// paths that carry UDP packets of at least 1,200 bytes. Thus a 1,024-byte datagram fits on every
/// path, with space for the QUIC headers. A larger one can be too large for some paths.
pub const MAX_UNRELIABLE_FRAME: usize = 1024;

/// The largest hello frame, in bytes, with the kind byte. A hello is 3 bytes (the kind and a
/// `u16`). A larger frame is not a hello, so `read_hello` stops before it reads much of it.
pub const MAX_HELLO_FRAME: usize = 64;

/// The most bytes that `read_frame` reserves for a payload before the bytes arrive: 64 KiB.
///
/// The length field comes from the peer, and the peer can lie. If `read_frame` reserved the full
/// length at once, a peer could send only a header of 1 MiB and make us allocate 1 MiB for each
/// such header. With this limit, the buffer grows only when real bytes arrive. Most messages are
/// smaller than 64 KiB, so they need only one allocation.
const MAX_PAYLOAD_RESERVE: usize = 64 * 1024;

/// The kind of a frame: the byte after the length field.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameKind {
    /// The first frame from each side. Its payload is the `TRANSPORT_VERSION` (`u16`).
    Hello,
    /// A reliable message of the game. Its payload is the message.
    Data,
}

impl FrameKind {
    fn to_byte(self) -> u8 {
        match self {
            FrameKind::Hello => 0,
            FrameKind::Data => 1,
        }
    }

    /// The kind of a byte. Another byte is not a kind of this wire format: a peer with the same
    /// ALPN never sends it, so the caller treats it as an error.
    fn from_byte(byte: u8) -> Option<FrameKind> {
        match byte {
            0 => Some(FrameKind::Hello),
            1 => Some(FrameKind::Data),
            _ => None,
        }
    }
}

/// One frame that `read_frame` read.
#[derive(Debug, PartialEq, Eq)]
pub struct Frame {
    pub kind: FrameKind,
    pub payload: Vec<u8>,
}

/// Make the bytes of a frame with `kind` and `payload`. The payload must be at most
/// `MAX_RELIABLE_FRAME - 1` bytes (the caller checks this), so the length fits in a `u32`.
pub fn encode_frame(kind: FrameKind, payload: &[u8]) -> Bytes {
    // The length field counts the kind byte and the payload (refer to "Frames on the stream")
    let length = u32::try_from(KIND_FIELD_LEN + payload.len()).expect("the caller limits the payload to MAX_RELIABLE_FRAME");
    let mut frame = Vec::with_capacity(FRAME_HEADER_LEN + payload.len());
    frame.extend_from_slice(&length.to_le_bytes());
    frame.push(kind.to_byte());
    frame.extend_from_slice(payload);
    frame.into()
}

/// Read the next frame from `recv`. The frame (kind and payload) must be at most `max` bytes.
///
/// The results are:
/// - `Ok(Some(frame))`: a full frame.
/// - `Ok(None)`: the peer ended the stream cleanly, between two frames.
/// - `Err(reason)`: the stream broke, it ended in the middle of a frame, or the frame is not
///   correct (length 0, more than `max` bytes, or an unknown kind).
///
/// The function holds no lock. It waits only for the peer's bytes, and the caller sets a timeout
/// (in the handshake) or can cancel it (after the handshake).
pub async fn read_frame(recv: &mut RecvStream, max: usize) -> Result<Option<Frame>, String> {
    // 1. Read the length field. If the stream ends before its first byte, the peer ended the
    //    stream between frames, which is a clean end.
    let mut length_field = [0u8; LENGTH_FIELD_LEN];
    match recv.read_exact(&mut length_field).await {
        Ok(()) => {}
        Err(ReadExactError::FinishedEarly(0)) => return Ok(None),
        Err(ReadExactError::FinishedEarly(_)) => return Err("stream ended in the middle of a frame".into()),
        Err(e) => return Err(e.to_string()),
    }

    // 2. Check the length before we read more. The length counts the kind byte, so it is at
    //    least 1. The limit stops a peer that announces a frame that is too large.
    let frame_len = u32::from_le_bytes(length_field) as usize;
    if frame_len < KIND_FIELD_LEN {
        return Err("empty frame".into());
    }
    if frame_len > max {
        return Err(format!("frame of {frame_len} bytes is over the {max} byte limit"));
    }

    // 3. Read the kind byte, and check that it is a known kind.
    let mut kind_field = [0u8; KIND_FIELD_LEN];
    match recv.read_exact(&mut kind_field).await {
        Ok(()) => {}
        Err(ReadExactError::FinishedEarly(_)) => return Err("stream ended in the middle of a frame".into()),
        Err(e) => return Err(e.to_string()),
    }
    let Some(kind) = FrameKind::from_byte(kind_field[0]) else {
        return Err(format!("unknown frame kind {}", kind_field[0]));
    };

    // 4. Read the payload. The buffer grows as the bytes arrive (refer to MAX_PAYLOAD_RESERVE).
    let payload_len = frame_len - KIND_FIELD_LEN;
    let mut payload = Vec::with_capacity(payload_len.min(MAX_PAYLOAD_RESERVE));
    while payload.len() < payload_len {
        match recv.read_chunk(payload_len - payload.len()).await.map_err(|e| e.to_string())? {
            Some(chunk) => payload.extend_from_slice(&chunk),
            None => return Err("stream ended in the middle of a frame".into()),
        }
    }
    Ok(Some(Frame { kind, payload }))
}

/// The handshake of the side that connects (the joiner): write our hello, then read the host's.
///
/// The order is necessary. The joiner opens the stream, and QUIC tells the host about a new stream
/// only when the first bytes of it arrive. Thus the host cannot read or write until our hello
/// arrives. To read first would wait for a hello that the host cannot send. To do both at once
/// gives no gain: `send_hello` returns when QUIC has the bytes in its buffer, which is immediate,
/// and the host's hello comes only after ours.
pub async fn hello_as_joiner(send: &mut SendStream, recv: &mut RecvStream) -> Result<(), String> {
    send_hello(send).await?;
    read_hello(recv).await
}

/// The handshake of the side that accepts (the host): read the joiner's hello, then write ours.
///
/// The host reads first because the joiner always writes first (refer to `hello_as_joiner`). Thus
/// the two sides never both wait to read. The host also checks the joiner's version before it
/// sends anything. Each side runs its handshake in its own task, with a timeout, and holds no lock
/// during it. Thus a peer that never sends its hello blocks only its own task, until the timeout.
pub async fn hello_as_host(send: &mut SendStream, recv: &mut RecvStream) -> Result<(), String> {
    read_hello(recv).await?;
    send_hello(send).await
}

/// Write our hello frame: the kind `Hello` and our `TRANSPORT_VERSION`.
async fn send_hello(send: &mut SendStream) -> Result<(), String> {
    send.write_all(&encode_frame(FrameKind::Hello, &TRANSPORT_VERSION.to_le_bytes()))
        .await
        .map_err(|e| format!("handshake failed: {e}"))
}

/// Read the peer's hello frame, and check that its version is the same as ours.
///
/// Each result that is not a correct hello is an error: the stream broke, the peer ended the
/// stream, the first frame is not a hello, or the version is different.
async fn read_hello(recv: &mut RecvStream) -> Result<(), String> {
    let frame = read_frame(recv, MAX_HELLO_FRAME).await.map_err(|e| format!("handshake failed: {e}"))?;
    let Some(frame) = frame else {
        return Err("closed by peer during handshake".into());
    };
    if frame.kind != FrameKind::Hello {
        return Err("peer sent data before its hello".into());
    }
    let Some(version) = frame.payload.first_chunk::<2>() else {
        return Err("handshake failed: the hello has no version".into());
    };
    let version = u16::from_le_bytes(*version);
    if version != TRANSPORT_VERSION {
        return Err(format!("peer uses transport version {version}, we use {TRANSPORT_VERSION}"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The bytes of each kind of frame, as they go on the wire.
    ///
    /// If this test fails, you changed the wire format. Then change the number in `ALPN` too, so
    /// that a game with the old format cannot connect to a game with the new one. After that,
    /// update the bytes and the ALPN in this test.
    #[test]
    fn wire_format_is_unchanged() {
        assert_eq!(ALPN, b"yarg-link/3");
        assert_eq!(TRANSPORT_VERSION, 1);
        assert_eq!(&encode_frame(FrameKind::Hello, &TRANSPORT_VERSION.to_le_bytes())[..], [3, 0, 0, 0, 0, 1, 0]);
        assert_eq!(&encode_frame(FrameKind::Data, b"hi")[..], [3, 0, 0, 0, 1, b'h', b'i']);
        assert_eq!(&encode_frame(FrameKind::Data, b"")[..], [1, 0, 0, 0, 1]);
        assert_eq!((MAX_RELIABLE_FRAME, MAX_UNRELIABLE_FRAME), (1024 * 1024, 1024));
    }

    #[test]
    fn kinds_round_trip() {
        for kind in [FrameKind::Hello, FrameKind::Data] {
            assert_eq!(FrameKind::from_byte(kind.to_byte()), Some(kind));
        }
        assert_eq!(FrameKind::from_byte(2), None);
    }
}
