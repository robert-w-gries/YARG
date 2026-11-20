//! The wire format on a connection.
//!
//! Each connection carries one bidirectional QUIC stream for reliable messages and QUIC datagrams
//! for unreliable ones. The stream is framed `[u32 length][u8 kind][payload]`, little-endian, where
//! the length counts the kind byte and the payload. Both sides first send a hello frame (payload:
//! u16 transport version); a peer counts as connected once the other side's hello has arrived.
//! A datagram is one unreliable message, with no framing.

use bytes::Bytes;
use iroh::endpoint::{ReadExactError, RecvStream, SendStream};

/// The ALPN of this protocol.
///
/// ALPN (Application-Layer Protocol Negotiation, RFC 7301) is a protocol name that the two ends
/// agree on in the TLS handshake of QUIC. The endpoint accepts only the names in its list
/// (`Builder::alpns`), and `yl_connect` asks for this name. Thus a game with a different wire
/// format cannot connect: its handshake fails before any frame is read. Change the number at the
/// end on each change to the wire format.
pub const ALPN: &[u8] = b"yarg-link/3";

/// The version in the hello frame. Both sides must have the same version.
pub const TRANSPORT_VERSION: u16 = 1;

/// Frame kinds.
pub const KIND_HELLO: u8 = 0;
pub const KIND_DATA: u8 = 1;

/// The largest reliable frame, in bytes, with the kind byte. Thus the largest reliable payload is
/// 1 byte less (`YL_MAX_RELIABLE`). The game's biggest message is a song setup (replay data for a
/// few players, with their presets), far below this.
pub const MAX_RELIABLE_FRAME: usize = 1024 * 1024;

/// The largest unreliable payload (one QUIC datagram), in bytes (`YL_MAX_UNRELIABLE`). QUIC needs
/// paths that carry UDP packets of at least 1,200 bytes. Thus a 1,024-byte datagram fits on every
/// path, with space for the QUIC headers. A larger one can be too large for some paths.
pub const MAX_UNRELIABLE_FRAME: usize = 1024;

/// The largest hello frame, in bytes. A hello is 3 bytes; anything much bigger isn't one.
pub const MAX_HELLO_FRAME: usize = 64;

pub fn encode_frame(kind: u8, payload: &[u8]) -> Bytes {
    let mut frame = Vec::with_capacity(5 + payload.len());
    frame.extend_from_slice(&((1 + payload.len()) as u32).to_le_bytes());
    frame.push(kind);
    frame.extend_from_slice(payload);
    frame.into()
}

/// Read one frame of at most `max` bytes. `Ok(None)` means the stream ended cleanly between frames.
pub async fn read_frame(recv: &mut RecvStream, max: usize) -> Result<Option<(u8, Vec<u8>)>, String> {
    let mut header = [0u8; 4];
    match recv.read_exact(&mut header).await {
        Ok(()) => {}
        Err(ReadExactError::FinishedEarly(0)) => return Ok(None),
        Err(e) => return Err(e.to_string()),
    }
    let len = u32::from_le_bytes(header) as usize;
    if len == 0 {
        return Err("empty frame".into());
    }
    if len > max {
        return Err(format!("frame of {len} bytes is over the {max} byte limit"));
    }

    // The kind byte on its own, so the payload doesn't have to be shifted down after it's read
    let mut kind = [0u8; 1];
    match recv.read_exact(&mut kind).await {
        Ok(()) => {}
        Err(ReadExactError::FinishedEarly(_)) => return Err("stream ended in the middle of a frame".into()),
        Err(e) => return Err(e.to_string()),
    }

    // Grow the buffer as data arrives, so a header alone can't make us allocate the whole frame
    let len = len - 1;
    let mut payload = Vec::with_capacity(len.min(64 * 1024));
    while payload.len() < len {
        match recv.read_chunk(len - payload.len()).await.map_err(|e| e.to_string())? {
            Some(chunk) => payload.extend_from_slice(&chunk),
            None => return Err("stream ended in the middle of a frame".into()),
        }
    }
    Ok(Some((kind[0], payload)))
}

pub async fn send_hello(send: &mut SendStream) -> Result<(), String> {
    send.write_all(&encode_frame(KIND_HELLO, &TRANSPORT_VERSION.to_le_bytes()))
        .await
        .map_err(|e| format!("handshake failed: {e}"))
}

pub async fn read_hello(recv: &mut RecvStream) -> Result<(), String> {
    match read_frame(recv, MAX_HELLO_FRAME).await {
        Ok(Some((KIND_HELLO, payload))) if payload.len() >= 2 => {
            let version = u16::from_le_bytes([payload[0], payload[1]]);
            if version != TRANSPORT_VERSION {
                return Err(format!("peer uses transport version {version}, we use {TRANSPORT_VERSION}"));
            }
            Ok(())
        }
        Ok(Some(_)) => Err("peer sent data before its hello".into()),
        Ok(None) => Err("closed by peer during handshake".into()),
        Err(e) => Err(format!("handshake failed: {e}")),
    }
}
