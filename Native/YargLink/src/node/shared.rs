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
//! # Closing the handle
//!
//! The handle itself has no state machine. It is open until `Drop` of `Node` calls
//! `close_all_peers`, which sets `closing`. `closing` is not a peer state: it is one flag for the
//! whole handle, and the peers still go through their own states while the handle closes.
//!
//! When `closing` is set, nothing new starts. Each function that adds something checks the flag:
//! - `new_peer` adds no peer. It checks the flag under the `peers` lock, and `close_all_peers` sets
//!   the flag under the same lock. Thus no peer can be added after `close_all_peers` took the
//!   peers;
//! - `push`, `push_log` and `push_message` queue no event, because nobody polls any more (the
//!   handle is out of the registry).
//!
//! # Locks
//!
//! Each lock is held only for a short, fixed piece of work. No lock is held across an `.await`.
//! When the code holds two locks, it takes them in this order: a peer's `state`, then `peers`,
//! then `events`.

use std::{
    collections::{HashMap, VecDeque},
    sync::{
        Arc, Mutex, MutexGuard,
        atomic::{AtomicBool, Ordering},
    },
};

use bytes::Bytes;
use iroh::endpoint::VarInt;
use tokio::sync::mpsc;
use tracing::debug;

use super::peer::{CloseReason, Peer, State, Write};
use crate::lock_ignoring_poison as lock;

/// Messages from one peer that may wait for the game to poll them. A peer that sends more than
/// this is disconnected (reliable) or has its datagrams dropped (unreliable), so a flood can't
/// grow the queue without bound while the game is busy, say loading a song.
const MAX_QUEUED_MESSAGES_PER_PEER: usize = 16 * 1024;
/// Bytes of messages from one peer that may wait for the game to poll them.
const MAX_QUEUED_BYTES_PER_PEER: usize = 32 * 1024 * 1024;

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

/// What `push_message` did with a message.
#[derive(Debug, PartialEq, Eq)]
pub(super) enum PushResult {
    /// The message is in the queue.
    Queued,
    /// The message was dropped, because the peer is not Ready (it closes now) or the handle
    /// closes. This is not an error: nobody wants the message. The peer's task stops soon.
    NotWanted,
    /// The message was dropped, because the peer has as many messages waiting as it may
    /// (`MAX_QUEUED_MESSAGES_PER_PEER` or `MAX_QUEUED_BYTES_PER_PEER`).
    Full,
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
    /// Set once, by `close_all_peers`, when the handle starts to close. Refer to "Closing the handle".
    closing: AtomicBool,
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

    /// Queue an event that is not a log line or a message: for example `Connected` or `Error`.
    /// Nothing is queued after the handle starts to close, because nobody polls then.
    pub(super) fn push(&self, event: Event) {
        if !self.is_closing() {
            lock(&self.events).events.push_back(event);
        }
    }

    /// Queue a log line. `log::QueueLayer` calls this for each line that passes the filter. The
    /// line is dropped when `MAX_QUEUED_LOGS` lines wait already, so the log cannot fill the memory
    /// when the game does not poll. As `push`, it queues nothing after the handle starts to close.
    pub fn push_log(&self, level: u32, line: String) {
        if self.is_closing() {
            return;
        }
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
    /// Returns `None` when the handle closes: refer to "Closing the handle".
    pub(super) fn new_peer(&self) -> Option<(Arc<Peer>, mpsc::Receiver<Write>)> {
        let mut peers = lock(&self.peers);
        if self.is_closing() {
            return None;
        }
        let id = peers.next_id;
        peers.next_id = peers.next_id.wrapping_add(1).max(1);
        let (peer, rx) = Peer::new(id);
        let peer = Arc::new(peer);
        peers.map.insert(id, peer.clone());
        Some((peer, rx))
    }

    pub(super) fn peer(&self, id: u32) -> Option<Arc<Peer>> {
        lock(&self.peers).map.get(&id).cloned()
    }

    /// The peer, if it is Ready. `None` if the peer is unknown, still connecting, or closed.
    pub(super) fn ready_peer(&self, id: u32) -> Option<Arc<Peer>> {
        self.peer(id).filter(|peer| peer.is_ready())
    }

    pub(super) fn peer_count(&self) -> usize {
        lock(&self.peers).map.len()
    }

    /// Start to close the handle, for `Drop` of `Node`: set `closing`, and close each peer with
    /// `CloseReason::HandleClosing` (a graceful close). Returns the peers, so that `Drop` can wait
    /// for their tasks to end.
    ///
    /// The flag is set and the peers are taken under one lock. Thus `new_peer` cannot add a peer
    /// after this, and each peer that exists is closed here. The caller must be in the runtime's
    /// context, as for `close_peer`.
    pub(super) fn close_all_peers(&self) -> Vec<Arc<Peer>> {
        let peers: Vec<Arc<Peer>> = {
            let mut peers = lock(&self.peers);
            self.closing.store(true, Ordering::Release);
            peers.map.drain().map(|(_, peer)| peer).collect()
        };
        // The peers lock is free again here: close_peer takes a peer's state lock first (refer to
        // "Locks").
        for peer in &peers {
            self.close_peer(peer, CloseReason::HandleClosing);
        }
        peers
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

    /// Queue a message from a peer. Refer to `PushResult` for the results.
    pub(super) fn push_message(&self, peer: &Peer, channel: u8, data: Bytes) -> PushResult {
        // Hold the state lock while the message is queued, so that no message can come after the
        // peer's Disconnected event (refer to the table at the top of this file)
        let state = lock(&peer.state);
        if *state != State::Ready || self.is_closing() {
            return PushResult::NotWanted;
        }
        let mut queue = lock(&self.events);
        let backlog = queue.backlog.entry(peer.id).or_default();
        if backlog.messages >= MAX_QUEUED_MESSAGES_PER_PEER || backlog.bytes + data.len() > MAX_QUEUED_BYTES_PER_PEER {
            return PushResult::Full;
        }
        backlog.messages += 1;
        backlog.bytes += data.len();
        queue.events.push_back(Event::Message(peer.id, channel, data));
        PushResult::Queued
    }

    /// Close a peer and queue `Disconnected`, exactly once. A second call does nothing.
    ///
    /// The steps are:
    /// 1. Under the peer's state lock: set the state to Closed, remove the peer from the list,
    ///    and queue `Disconnected` with `reason.message()`.
    /// 2. A graceful close (`reason.is_graceful()`) of a Ready peer gives `Write::Finish` to the
    ///    writer task, behind the reliable messages that wait already. The writer task sends
    ///    them, ends the stream, and closes the connection when the peer has read them. Thus the
    ///    game's last messages (its goodbye) arrive.
    /// 3. Any other close cancels the peer's task, and closes the connection at once (if the peer
    ///    has one).
    ///
    /// After this, the peer's task ends, and nothing else is necessary: the task's `done` guard
    /// tells `Drop` of `Node`, and the peer's memory goes with the last `Arc<Peer>`.
    ///
    /// The caller must be in the runtime's context. The runtime's tasks are. The exported
    /// functions use `Node::close_peer`, which enters it.
    pub(super) fn close_peer(&self, peer: &Peer, reason: CloseReason) {
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
            self.push(Event::Disconnected(peer.id, reason.message()));
            state_before
        };
        debug!(peer = peer.id, "disconnected: {}", reason.message());

        if reason.is_graceful() && state_before == State::Ready && peer.writer.try_send(Write::Finish).is_ok() {
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
