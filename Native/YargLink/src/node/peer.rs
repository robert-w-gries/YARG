//! One peer: a connection from the start of its connect or accept to its close.
//!
//! This file has the peer's own data and the operations that need only the peer: its state, its
//! writer, its connection, its statistics. The operations that also change the event queue or the
//! list of peers (`mark_ready`, `push_message`, `close_peer`) are in `shared.rs`, with the state
//! machine.

use std::{
    sync::{Arc, Mutex, OnceLock},
    time::Duration,
};

use bytes::Bytes;
use iroh::endpoint::Connection;
use tokio::sync::{OwnedSemaphorePermit, Semaphore, mpsc, mpsc::error::TrySendError};
use tokio_util::sync::{CancellationToken, DropGuard};

use crate::{YL_PATH_DIRECT, YL_PATH_RELAY, YL_PATH_UNKNOWN, lock_ignoring_poison as lock};

/// Reliable messages to one peer that may wait to go out. A peer that stops reading (QUIC flow
/// control then holds the writer) is disconnected once this much piles up, so it can't make us
/// buffer whatever the game keeps sending. This is the capacity of the writer's channel.
pub(super) const MAX_OUTGOING_MESSAGES_PER_PEER: usize = 64 * 1024;
/// Bytes of reliable frames to one peer that may wait to go out.
pub(super) const MAX_OUTGOING_BYTES_PER_PEER: usize = 32 * 1024 * 1024;

/// The state of a peer. Refer to "Peer states" in `shared.rs`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum State {
    Connecting,
    Ready,
    Closed,
}

/// What the writer task does next.
pub(super) enum Write {
    /// Write this frame. The permit holds the frame's bytes of `Peer::outgoing_bytes` until the
    /// frame is written.
    Frame(Bytes, OwnedSemaphorePermit),
    /// Send what's queued, end the stream, and close once the peer has read it.
    Finish,
}

/// Why a peer closes. `Shared::close_peer` takes one, and gets from it the text of the
/// `Disconnected` event and whether the close is graceful.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(super) enum CloseReason {
    /// The game called `yl_disconnect`. Graceful.
    Local,
    /// The game called `yl_destroy`. Graceful. The game does not see this event, because nobody
    /// polls after `yl_destroy` starts.
    HandleClosing,
    /// Too much waits to go out to the peer: it stopped reading.
    SlowReader,
    /// Too many messages from the peer wait for the game to poll them.
    Flooded,
    /// The connect or the handshake failed, or the connection ended. The text says why.
    Failed(String),
}

impl CloseReason {
    /// The text of the `Disconnected` event (`YL_EVENT_PEER_DISCONNECTED`).
    pub fn message(&self) -> String {
        match self {
            CloseReason::Local => "disconnected locally".into(),
            CloseReason::HandleClosing => "the handle closed".into(),
            CloseReason::SlowReader => "peer stopped reading what we sent".into(),
            CloseReason::Flooded => "peer sent more than the game could take in".into(),
            CloseReason::Failed(reason) => reason.clone(),
        }
    }

    /// A graceful close sends the reliable messages that wait already (the game's goodbye),
    /// before it closes. Only a close that this game asks for is graceful. Any other close stops
    /// at once: the connection is broken, or the peer misbehaves.
    pub fn is_graceful(&self) -> bool {
        matches!(self, CloseReason::Local | CloseReason::HandleClosing)
    }
}

/// What `Peer::queue_frame` did with a frame.
pub(super) enum Queued {
    /// The writer task has the frame.
    Yes,
    /// The writer task ended. Its close is on the way, so the frame is not needed.
    WriterEnded,
    /// Too much waits to go out already. The caller closes the peer (`CloseReason::SlowReader`).
    Full,
}

/// The statistics of a connected peer. Refer to `Node::stats`.
#[derive(Debug, Clone, Copy)]
pub struct Stats {
    pub path: u32,
    /// `None` until QUIC has measured the round-trip time.
    pub rtt: Option<Duration>,
    pub bytes_sent: u64,
    pub bytes_received: u64,
}

/// One connection, from the start of its connect or accept to its close.
pub(super) struct Peer {
    pub id: u32,
    /// The QUIC connection, set once when the handshake is done. Before that, the peer has no
    /// connection. `OnceLock` lets the peer's task set it once, and lets the exported functions
    /// (on other threads) read it without a lock. It is never changed or removed after it is set.
    pub conn: OnceLock<Connection>,
    /// Reliable frames for the writer task, in order. The channel holds at most
    /// `MAX_OUTGOING_MESSAGES_PER_PEER` frames, and `outgoing_bytes` limits their bytes.
    pub writer: mpsc::Sender<Write>,
    /// Permits for the bytes of the frames in `writer`: `MAX_OUTGOING_BYTES_PER_PEER` in all.
    outgoing_bytes: Arc<Semaphore>,
    /// The peer's state. Refer to the table at the top of `shared.rs`. The lock is held only while
    /// the state is read or changed and the matching event is queued, never for the connection's
    /// life. Thus the order of the peer's events is always correct.
    pub(super) state: Mutex<State>,
    /// Tells the peer's task to stop at once: for example after a failure or a local disconnect.
    /// `close_peer` can run on any thread, while the task waits in `.await` on the runtime. A
    /// function call cannot stop that task. The task waits on this token in its `select!`, and the
    /// token wakes it.
    pub cancel: CancellationToken,
    /// The peer's task cancels this token when it ends (refer to `done_on_drop`). `Drop` of `Node`
    /// waits on it, so that goodbyes go out before the runtime stops.
    pub done: CancellationToken,
}

impl Peer {
    /// A new peer in the Connecting state, and the receiving end of its writer.
    pub(super) fn new(id: u32) -> (Peer, mpsc::Receiver<Write>) {
        let (writer, rx) = mpsc::channel(MAX_OUTGOING_MESSAGES_PER_PEER);
        let peer = Peer {
            id,
            conn: OnceLock::new(),
            writer,
            outgoing_bytes: Arc::new(Semaphore::new(MAX_OUTGOING_BYTES_PER_PEER)),
            state: Mutex::new(State::Connecting),
            cancel: CancellationToken::new(),
            done: CancellationToken::new(),
        };
        (peer, rx)
    }

    pub fn is_ready(&self) -> bool {
        *lock(&self.state) == State::Ready
    }

    /// A guard that cancels `done` when it is dropped. The peer's task keeps it in a local
    /// variable for its full life. Thus `done` is cancelled when the task ends in any way: it
    /// returns, it is cancelled, or the runtime drops it.
    pub fn done_on_drop(&self) -> DropGuard {
        self.done.clone().drop_guard()
    }

    /// Give a frame to the writer task. The frame waits in the writer's channel until the task
    /// writes it. Refer to `Queued` for the results.
    pub fn queue_frame(&self, frame: Bytes) -> Queued {
        // At most MAX_RELIABLE_FRAME + 4 bytes (the caller checks the size), which fits in a u32
        let bytes = frame.len() as u32;
        let Ok(permit) = self.outgoing_bytes.clone().try_acquire_many_owned(bytes) else {
            return Queued::Full;
        };
        match self.writer.try_send(Write::Frame(frame, permit)) {
            Ok(()) => Queued::Yes,
            Err(TrySendError::Closed(_)) => Queued::WriterEnded,
            Err(TrySendError::Full(_)) => Queued::Full,
        }
    }

    /// The peer's endpoint ID, when its connection is up. The handshake authenticates it, so a
    /// peer can't claim another's ID (though it can make a new one whenever it likes).
    pub fn endpoint_id(&self) -> Option<String> {
        self.conn.get().map(|conn| conn.remote_id().to_string())
    }

    /// The statistics of the connection. `None` if the peer is not Ready, or has no connection.
    /// The caller must be in the runtime's context (`Runtime::enter`).
    pub fn stats(&self) -> Option<Stats> {
        if !self.is_ready() {
            return None;
        }
        let conn = self.conn.get()?;
        let quic = conn.stats();
        let (path, rtt) = selected_path(conn);
        Some(Stats { path, rtt, bytes_sent: quic.udp_tx.bytes, bytes_received: quic.udp_rx.bytes })
    }
}

/// The path that QUIC uses now for `conn`, and its round-trip time.
///
/// A connection can have more than one path at once (for example a relay path and a direct path,
/// while hole punching runs). QUIC selects one of them to send on. The kind is:
/// - `YL_PATH_RELAY`: the selected path goes through a relay server;
/// - `YL_PATH_DIRECT`: the selected path is not a relay path, thus it goes direct to the peer's IP
///   address (on the LAN, after hole punching, or on loopback);
/// - `YL_PATH_UNKNOWN`: no path is selected yet. Then there is no round-trip time.
pub fn selected_path(conn: &Connection) -> (u32, Option<Duration>) {
    match conn.paths().iter().find(|path| path.is_selected()) {
        Some(path) if path.is_relay() => (YL_PATH_RELAY, Some(path.rtt())),
        Some(path) => (YL_PATH_DIRECT, Some(path.rtt())),
        None => (YL_PATH_UNKNOWN, None),
    }
}

/// The name of a `YL_PATH_*` kind, for log lines.
pub fn path_name(path: u32) -> &'static str {
    match path {
        YL_PATH_RELAY => "relay",
        YL_PATH_DIRECT => "direct",
        _ => "unknown",
    }
}
