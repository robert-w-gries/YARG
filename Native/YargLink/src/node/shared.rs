//! The state that the exported functions and the runtime's tasks share: the event queue and the
//! peers.
//!
//! # Peer states
//!
//! Each peer goes through these states. It never goes back.
//!
//! ```text
//!   Connecting ──(handshake done: mark_ready)──▶ Ready ──(close_peer)──▶ Closed
//!       │                                                                  ▲
//!       └────────(close_peer: the connect failed, or a local disconnect)───┘
//! ```
//!
//! The functions that change the state also queue the peer's events. They hold the peer's `state`
//! lock while they queue the event:
//!
//! | Function       | Change                | Event queued                     |
//! |----------------|-----------------------|----------------------------------|
//! | `mark_ready`   | Connecting → Ready    | `Connected`                      |
//! | `push_message` | none (only in Ready)  | `Message`                        |
//! | `close_peer`   | Connecting/Ready → Closed | `Disconnected`, exactly once |
//!
//! Thus the game sees `Connected`, then the peer's messages, then `Disconnected`, and nothing after
//! `Disconnected`. A peer that never became Ready gets only `Disconnected`.
//!
//! The handle itself has no state machine. It is open until `Drop` of `Node` sets `closing`.
//! `closing` is not a peer state: it is one flag for the whole handle, and the peers still go
//! through their own states while the handle closes.
//!
//! # Locks
//!
//! Each lock is held only for a short, fixed piece of work. No lock is held across an `.await`.
//! When the code holds two locks, it takes them in this order: a peer's `state`, then `peers`,
//! then `events`.

use std::{
    collections::{HashMap, VecDeque},
    sync::{
        Arc, Mutex, MutexGuard, OnceLock,
        atomic::{AtomicBool, Ordering},
    },
};

use bytes::Bytes;
use iroh::endpoint::{Connection, VarInt};
use tokio::sync::{OwnedSemaphorePermit, Semaphore, mpsc};
use tokio_util::sync::CancellationToken;
use tracing::debug;

use crate::lock_ignoring_poison as lock;

/// Messages from one peer that may wait for the game to poll them. A peer that sends more than
/// this is disconnected (reliable) or has its datagrams dropped (unreliable), so a flood can't
/// grow the queue without bound while the game is busy, say loading a song.
const MAX_QUEUED_MESSAGES_PER_PEER: usize = 16 * 1024;
/// Bytes of messages from one peer that may wait for the game to poll them.
const MAX_QUEUED_BYTES_PER_PEER: usize = 32 * 1024 * 1024;

/// Reliable messages to one peer that may wait to go out. A peer that stops reading (QUIC flow
/// control then holds the writer) is disconnected once this much piles up, so it can't make us
/// buffer whatever the game keeps sending. This is the capacity of the writer's channel.
pub(super) const MAX_OUTGOING_MESSAGES_PER_PEER: usize = 64 * 1024;
/// Bytes of reliable frames to one peer that may wait to go out.
pub(super) const MAX_OUTGOING_BYTES_PER_PEER: usize = 32 * 1024 * 1024;

/// Log lines that may wait for the game to poll them. More are dropped, so a chatty filter with
/// nobody polling can't grow the queue without bound.
const MAX_QUEUED_LOGS: usize = 4096;

/// The QUIC close code for every close that this library makes. The reason text tells the peer why.
pub(super) const CLOSE_CODE: VarInt = VarInt::from_u32(0);
/// The QUIC close reason when this game leaves a connection.
pub(super) const CLOSE_REASON_LEFT: &[u8] = b"left";

/// Something for the game to see, through `yl_poll`.
#[derive(Debug)]
pub enum Event {
    /// The peer's handshake is done (`YL_EVENT_PEER_CONNECTED`).
    Connected(u32),
    /// The peer is gone, and why (`YL_EVENT_PEER_DISCONNECTED`).
    Disconnected(u32, String),
    /// A message from a peer, on a channel (`YL_EVENT_MESSAGE`).
    Message(u32, u8, Bytes),
    /// A failure that is not of one peer (`YL_EVENT_ERROR`).
    Error(String),
    /// A log line, with its `YL_LOG_*` level (`YL_EVENT_LOG`).
    Log(u32, String),
}

/// Events waiting for the game, and counts that limit them.
#[derive(Default)]
struct Queue {
    events: VecDeque<Event>,
    /// The messages of each peer in `events`.
    backlog: HashMap<u32, Backlog>,
    /// The log lines in `events`.
    logs: usize,
}

#[derive(Default)]
struct Backlog {
    messages: usize,
    bytes: usize,
}

/// The peers of the handle, and the number for the next one.
struct Peers {
    map: HashMap<u32, Arc<Peer>>,
    next_id: u32,
}

/// State shared between the exported functions and the runtime's tasks.
pub struct Shared {
    events: Mutex<Queue>,
    peers: Mutex<Peers>,
    /// Set once, when the handle starts to close. After that, no new events are queued and no new
    /// connections are accepted.
    closing: AtomicBool,
}

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
    pub outgoing_bytes: Arc<Semaphore>,
    /// The peer's state. Refer to the table at the top of this file. The lock is held only while
    /// the state is read or changed and the matching event is queued, never for the connection's
    /// life. Thus the order of the peer's events is always correct.
    state: Mutex<State>,
    /// Tells the peer's task to stop at once: for example after a failure or a local disconnect.
    /// `close_peer` can run on any thread, while the task waits in `.await` on the runtime. A
    /// function call cannot stop that task. The task waits on this token in its `select!`, and the
    /// token wakes it.
    pub cancel: CancellationToken,
    /// The peer's task cancels this token when it ends. `Drop` of `Node` waits on it, so that
    /// goodbyes go out before the runtime stops.
    pub done: CancellationToken,
}

impl Peer {
    pub fn is_ready(&self) -> bool {
        *lock(&self.state) == State::Ready
    }
}

impl Shared {
    pub(super) fn new() -> Self {
        Self {
            events: Mutex::new(Queue::default()),
            peers: Mutex::new(Peers { map: HashMap::new(), next_id: 1 }),
            closing: AtomicBool::new(false),
        }
    }

    pub(super) fn is_closing(&self) -> bool {
        self.closing.load(Ordering::Acquire)
    }

    pub(super) fn set_closing(&self) {
        self.closing.store(true, Ordering::Release);
    }

    /// Queue an event that is not a log line or a message: for example `Connected` or `Error`.
    /// Nothing is queued after the handle starts to close, because nobody polls then.
    pub(super) fn push(&self, event: Event) {
        if !self.is_closing() {
            lock(&self.events).events.push_back(event);
        }
    }

    /// Queue a log line. `log::QueueLayer` calls this for each line that passes the filter. The
    /// line is dropped when `MAX_QUEUED_LOGS` lines wait already, so the log cannot fill the memory
    /// when the game does not poll.
    pub fn push_log(&self, level: u32, line: String) {
        let mut queue = lock(&self.events);
        if queue.logs >= MAX_QUEUED_LOGS {
            return;
        }
        queue.logs += 1;
        queue.events.push_back(Event::Log(level, line));
    }

    /// Lock the event queue, to look at the next event and maybe take it. `yl_poll` holds this
    /// lock while it copies one event.
    pub fn events(&self) -> Events<'_> {
        Events { queue: lock(&self.events) }
    }

    pub(super) fn clear_events(&self) {
        let mut queue = lock(&self.events);
        queue.events.clear();
        queue.backlog.clear();
        queue.logs = 0;
    }

    /// Add a peer in the Connecting state. Returns the peer and the receiving end of its writer.
    pub(super) fn new_peer(&self) -> (Arc<Peer>, mpsc::Receiver<Write>) {
        let (tx, rx) = mpsc::channel(MAX_OUTGOING_MESSAGES_PER_PEER);
        let mut peers = lock(&self.peers);
        let id = peers.next_id;
        peers.next_id = peers.next_id.wrapping_add(1).max(1);
        let peer = Arc::new(Peer {
            id,
            conn: OnceLock::new(),
            writer: tx,
            outgoing_bytes: Arc::new(Semaphore::new(MAX_OUTGOING_BYTES_PER_PEER)),
            state: Mutex::new(State::Connecting),
            cancel: CancellationToken::new(),
            done: CancellationToken::new(),
        });
        peers.map.insert(id, peer.clone());
        (peer, rx)
    }

    pub(super) fn peer(&self, id: u32) -> Option<Arc<Peer>> {
        lock(&self.peers).map.get(&id).cloned()
    }

    pub(super) fn peer_count(&self) -> usize {
        lock(&self.peers).map.len()
    }

    /// Remove all peers, for `Drop` of `Node`.
    pub(super) fn take_peers(&self) -> Vec<Arc<Peer>> {
        lock(&self.peers).map.drain().map(|(_, peer)| peer).collect()
    }

    /// Move a peer from Connecting to Ready and queue `Connected`. Returns false if the peer is
    /// not Connecting any more: `close_peer` closed it while its handshake finished.
    pub(super) fn mark_ready(&self, peer: &Peer) -> bool {
        let mut state = lock(&peer.state);
        if *state != State::Connecting {
            return false;
        }
        *state = State::Ready;
        self.push(Event::Connected(peer.id));
        true
    }

    /// Queue a message from a peer. A message from a peer that is not Ready (any more) is dropped,
    /// and the result is true. Returns false if the peer already has as much waiting as it may;
    /// the message is dropped then.
    pub(super) fn push_message(&self, peer: &Peer, channel: u8, data: Bytes) -> bool {
        let state = lock(&peer.state);
        if *state != State::Ready || self.is_closing() {
            return true;
        }
        let mut queue = lock(&self.events);
        let backlog = queue.backlog.entry(peer.id).or_default();
        if backlog.messages >= MAX_QUEUED_MESSAGES_PER_PEER || backlog.bytes + data.len() > MAX_QUEUED_BYTES_PER_PEER {
            return false;
        }
        backlog.messages += 1;
        backlog.bytes += data.len();
        queue.events.push_back(Event::Message(peer.id, channel, data));
        true
    }

    /// Close a peer and queue `Disconnected`, exactly once. A second call does nothing.
    ///
    /// A graceful close of a Ready peer still delivers the reliable messages queued so far (the
    /// session's goodbye, typically): the writer task sends them, ends the stream, and then closes
    /// the connection. Any other close stops the peer's task and closes the connection at once.
    pub(super) fn close_peer(&self, peer: &Peer, reason: String, graceful: bool) {
        // The state before this call. The block below changes the state to Closed, but this copy
        // keeps the old value: Connecting or Ready.
        let state_before = {
            let mut state = lock(&peer.state);
            let state_before = *state;
            if state_before == State::Closed {
                return;
            }
            *state = State::Closed;
            lock(&self.peers).map.remove(&peer.id);
            self.push(Event::Disconnected(peer.id, reason.clone()));
            state_before
        };
        debug!(peer = peer.id, "disconnected: {reason}");

        if graceful && state_before == State::Ready && peer.writer.try_send(Write::Finish).is_ok() {
            return;
        }
        peer.cancel.cancel();
        if let Some(conn) = peer.conn.get() {
            conn.close(CLOSE_CODE, CLOSE_REASON_LEFT);
        }
    }
}

/// The locked event queue. Refer to `Shared::events`.
pub struct Events<'a> {
    queue: MutexGuard<'a, Queue>,
}

impl Events<'_> {
    /// The next event, without taking it.
    pub fn front(&self) -> Option<&Event> {
        self.queue.events.front()
    }

    /// Take the next event, and update the counts that limit the queue.
    pub fn pop_front(&mut self) {
        let queue = &mut *self.queue;
        match queue.events.pop_front() {
            Some(Event::Log(..)) => {
                queue.logs -= 1;
            }
            Some(Event::Message(peer, _, data)) => {
                if let Some(backlog) = queue.backlog.get_mut(&peer) {
                    backlog.messages -= 1;
                    backlog.bytes -= data.len();
                    if backlog.messages == 0 {
                        queue.backlog.remove(&peer);
                    }
                }
            }
            _ => {}
        }
    }
}
