//! One handle: an iroh endpoint, its tokio runtime, its peers and its event queue.
//!
//! Each connection carries one bidirectional QUIC stream for reliable messages and QUIC datagrams
//! for unreliable ones. The stream is framed `[u32 length][u8 kind][payload]`, little-endian, where
//! the length counts the kind byte and the payload. Both sides first send a hello frame (payload:
//! u16 transport version); a peer counts as connected once the other side's hello has arrived.

use std::{
    collections::{HashMap, VecDeque},
    net::SocketAddr,
    sync::{
        Arc, Mutex, MutexGuard, OnceLock,
        atomic::{AtomicBool, AtomicU32, AtomicUsize, Ordering},
    },
    time::Duration,
};

use bytes::Bytes;
use iroh::{
    Endpoint, EndpointAddr, RelayMode, SecretKey,
    endpoint::{Connection, ConnectionError, Incoming, ReadExactError, RecvStream, SendStream, presets},
};
use serde::Deserialize;
use tokio::{runtime::Runtime, sync::mpsc};
use tokio_util::sync::CancellationToken;
use tracing::{Dispatch, debug, info, warn};

use crate::{key, log, ticket};

/// ALPN for this protocol. Bump the suffix on any wire-format change.
pub const ALPN: &[u8] = b"yarg-link/3";

const TRANSPORT_VERSION: u16 = 1;

const KIND_HELLO: u8 = 0;
const KIND_DATA: u8 = 1;

/// The largest reliable frame, kind byte included. The game's biggest message is a song setup
/// (replay data for a few players, with their presets), far below this.
pub const MAX_FRAME: usize = 1024 * 1024;

/// A hello is 3 bytes; anything much bigger isn't one.
const MAX_HELLO_FRAME: usize = 64;

/// Incoming connections handshaking at once. More are refused until some finish.
const MAX_PENDING_INCOMING: usize = 8;

/// The longest an incoming connection may take to handshake. Shorter than our own connects, which
/// may have to wait for a host's addresses to be published.
const INCOMING_TIMEOUT: Duration = Duration::from_secs(10);

/// Messages from one peer that may wait for the game to poll them. A peer that sends more than
/// this is disconnected (reliable) or has its datagrams dropped (unreliable), so a flood can't
/// grow the queue without bound while the game is busy, say loading a song.
const MAX_QUEUED_MESSAGES_PER_PEER: usize = 16 * 1024;
const MAX_QUEUED_BYTES_PER_PEER: usize = 32 * 1024 * 1024;

pub const MAX_UNRELIABLE: usize = 1024;

/// Events beyond this many are dropped if they're log lines, so a chatty filter with nobody
/// polling can't grow without bound.
const MAX_QUEUED_LOGS: usize = 4096;

pub const CHANNEL_RELIABLE: u8 = 0;
pub const CHANNEL_UNRELIABLE: u8 = 1;

#[allow(dead_code)] // the default; kept to mirror YL_PATH_UNKNOWN
pub const PATH_UNKNOWN: u32 = 0;
pub const PATH_DIRECT: u32 = 1;
pub const PATH_RELAY: u32 = 2;

/// A failure with its C status code.
#[derive(Debug)]
pub struct Error {
    pub code: i32,
    pub message: String,
}

impl Error {
    pub fn new(code: i32, message: impl Into<String>) -> Self {
        Self { code, message: message.into() }
    }
}

pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, Deserialize)]
#[serde(default)]
pub struct Config {
    pub key_file: Option<String>,
    pub relay: bool,
    pub address_lookup: bool,
    pub bind_addr: Option<String>,
    pub ticket_addrs: bool,
    pub connect_timeout_ms: u64,
    pub log_filter: String,
    /// Accept incoming connections. A game that only joins others turns this off.
    pub accept: bool,
    /// The most peers at once, counting incoming connections still handshaking.
    pub max_peers: usize,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            key_file: None,
            relay: true,
            address_lookup: true,
            bind_addr: None,
            ticket_addrs: false,
            connect_timeout_ms: 20_000,
            log_filter: log::DEFAULT_FILTER.into(),
            accept: true,
            max_peers: 16,
        }
    }
}

#[derive(Debug)]
pub enum Event {
    Connected(u32),
    Disconnected(u32, String),
    Message(u32, u8, Bytes),
    Error(String),
    Log(u32, String),
}

#[derive(Debug, Default, Clone, Copy)]
pub struct Stats {
    pub connected: bool,
    pub path: u32,
    pub rtt: Option<Duration>,
    pub bytes_sent: u64,
    pub bytes_received: u64,
}

/// Events waiting for the game, and how many of them are each peer's messages.
#[derive(Default)]
struct Queue {
    events: VecDeque<Event>,
    backlog: HashMap<u32, Backlog>,
}

#[derive(Default)]
struct Backlog {
    messages: usize,
    bytes: usize,
}

/// State shared between the C side and the runtime's tasks.
pub struct Shared {
    events: Mutex<Queue>,
    queued_logs: AtomicU32,
    peers: Mutex<HashMap<u32, Arc<Peer>>>,
    next_id: AtomicU32,
    shutting_down: AtomicBool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    Connecting,
    Ready,
    Closed,
}

/// What the writer task does next.
enum Write {
    Frame(Bytes),
    /// Send what's queued, end the stream, and close once the peer has read it.
    Finish,
}

struct Peer {
    id: u32,
    conn: OnceLock<Connection>,
    writer: mpsc::UnboundedSender<Write>,
    /// Every event for this peer is queued while holding this lock, so the game always sees
    /// connected → messages → disconnected, and nothing after that.
    state: Mutex<State>,
    /// Stops the peer's task at once (connect attempts, failures).
    cancel: CancellationToken,
    /// Fires when the peer's task has finished.
    done: CancellationToken,
}

impl Peer {
    fn is_ready(&self) -> bool {
        *lock(&self.state) == State::Ready
    }
}

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    // A panic elsewhere already turned into YL_ERR_PANIC; keep going with whatever state is there.
    m.lock().unwrap_or_else(|e| e.into_inner())
}

impl Shared {
    fn new() -> Self {
        Self {
            events: Mutex::new(Queue::default()),
            queued_logs: AtomicU32::new(0),
            peers: Mutex::new(HashMap::new()),
            next_id: AtomicU32::new(1),
            shutting_down: AtomicBool::new(false),
        }
    }

    fn push(&self, event: Event) {
        if !self.shutting_down.load(Ordering::Acquire) {
            lock(&self.events).events.push_back(event);
        }
    }

    pub fn push_log(&self, event: Event) {
        if self.queued_logs.load(Ordering::Relaxed) as usize >= MAX_QUEUED_LOGS {
            return;
        }
        self.queued_logs.fetch_add(1, Ordering::Relaxed);
        lock(&self.events).events.push_back(event);
    }

    /// Look at the next event and decide whether to take it.
    pub fn with_front<R>(&self, f: impl FnOnce(Option<&Event>) -> (R, bool)) -> R {
        let mut queue = lock(&self.events);
        let (result, take) = f(queue.events.front());
        if take {
            match queue.events.pop_front() {
                Some(Event::Log(..)) => {
                    self.queued_logs.fetch_sub(1, Ordering::Relaxed);
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
        result
    }

    fn new_peer(&self) -> (Arc<Peer>, mpsc::UnboundedReceiver<Write>) {
        let (tx, rx) = mpsc::unbounded_channel();
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let peer = Arc::new(Peer {
            id,
            conn: OnceLock::new(),
            writer: tx,
            state: Mutex::new(State::Connecting),
            cancel: CancellationToken::new(),
            done: CancellationToken::new(),
        });
        lock(&self.peers).insert(id, peer.clone());
        (peer, rx)
    }

    fn peer(&self, id: u32) -> Option<Arc<Peer>> {
        lock(&self.peers).get(&id).cloned()
    }

    /// Move a peer from connecting to ready and report it. False if it was closed meanwhile.
    fn mark_ready(&self, peer: &Peer) -> bool {
        let mut state = lock(&peer.state);
        if *state != State::Connecting {
            return false;
        }
        *state = State::Ready;
        self.push(Event::Connected(peer.id));
        true
    }

    /// Queue a message from a peer, unless it's been closed. False if the peer already has as much
    /// waiting as it may; the message is dropped then.
    fn push_message(&self, peer: &Peer, channel: u8, data: Bytes) -> bool {
        let state = lock(&peer.state);
        if *state != State::Ready || self.shutting_down.load(Ordering::Acquire) {
            return true;
        }
        let mut queue = lock(&self.events);
        let backlog = queue.backlog.entry(peer.id).or_default();
        if backlog.messages >= MAX_QUEUED_MESSAGES_PER_PEER
            || backlog.bytes + data.len() > MAX_QUEUED_BYTES_PER_PEER
        {
            return false;
        }
        backlog.messages += 1;
        backlog.bytes += data.len();
        queue.events.push_back(Event::Message(peer.id, channel, data));
        true
    }

    /// Close a peer and report it, exactly once. A graceful close still delivers the reliable
    /// messages queued so far (the session's goodbye, typically).
    fn close_peer(&self, peer: &Peer, reason: String, graceful: bool) {
        let previous = {
            let mut state = lock(&peer.state);
            let previous = *state;
            if previous == State::Closed {
                return;
            }
            *state = State::Closed;
            lock(&self.peers).remove(&peer.id);
            self.push(Event::Disconnected(peer.id, reason.clone()));
            previous
        };
        debug!(peer = peer.id, "disconnected: {reason}");

        if graceful && previous == State::Ready && peer.writer.send(Write::Finish).is_ok() {
            return;
        }
        peer.cancel.cancel();
        if let Some(conn) = peer.conn.get() {
            conn.close(0u32.into(), b"left");
        }
    }
}

pub struct Node {
    runtime: Option<Runtime>,
    endpoint: Endpoint,
    shared: Arc<Shared>,
    dispatch: Dispatch,
    ticket_addrs: bool,
    connect_timeout: Duration,
    _key: Option<key::StoredKey>,
}

impl Node {
    pub fn new(config: Config) -> Result<Node> {
        let shared = Arc::new(Shared::new());
        let dispatch = log::dispatch(&config.log_filter, shared.clone())
            .map_err(|e| Error::new(crate::YL_ERR_BAD_CONFIG, e))?;

        let runtime = {
            let dispatch = dispatch.clone();
            tokio::runtime::Builder::new_multi_thread()
                .worker_threads(2)
                .thread_name("yarg-link")
                .enable_all()
                .on_thread_start(move || {
                    // The runtime's threads live exactly as long as the handle, and log to it
                    std::mem::forget(tracing::dispatcher::set_default(&dispatch));
                })
                .build()
                .map_err(|e| Error::new(crate::YL_ERR_INTERNAL, format!("couldn't start runtime: {e}")))?
        };

        tracing::dispatcher::with_default(&dispatch, || {
            let (secret, stored) = Self::load_key(&config);
            let endpoint = runtime.block_on(Self::bind(&config, secret))?;
            info!("endpoint {} is up", endpoint.id());

            if config.accept {
                let timeout = config.connect_timeout().min(INCOMING_TIMEOUT);
                runtime.spawn(accept_loop(endpoint.clone(), shared.clone(), timeout, config.max_peers));
            } else {
                runtime.spawn(refuse_loop(endpoint.clone()));
            }

            Ok(Node {
                runtime: Some(runtime),
                endpoint,
                shared,
                dispatch: dispatch.clone(),
                ticket_addrs: config.ticket_addrs,
                connect_timeout: config.connect_timeout(),
                _key: stored,
            })
        })
    }

    fn load_key(config: &Config) -> (SecretKey, Option<key::StoredKey>) {
        let Some(path) = config.key_file.as_deref() else {
            return (SecretKey::generate(), None);
        };
        match key::load_or_create(path.as_ref()) {
            Ok(Some(stored)) => (stored.key.clone(), Some(stored)),
            Ok(None) => {
                warn!("{path} is in use by another process; using a temporary identity");
                (SecretKey::generate(), None)
            }
            Err(e) => {
                warn!("couldn't load {path} ({e}); using a temporary identity");
                (SecretKey::generate(), None)
            }
        }
    }

    async fn bind(config: &Config, secret: SecretKey) -> Result<Endpoint> {
        let mut builder = if config.address_lookup {
            Endpoint::builder(presets::N0)
        } else {
            Endpoint::builder(presets::Minimal)
        };
        builder = builder
            .secret_key(secret)
            .alpns(vec![ALPN.to_vec()])
            .relay_mode(if config.relay { RelayMode::Default } else { RelayMode::Disabled });

        if let Some(bind) = config.bind_addr.as_deref() {
            let addr: SocketAddr = bind
                .parse()
                .map_err(|e| Error::new(crate::YL_ERR_BAD_CONFIG, format!("bad bind_addr \"{bind}\": {e}")))?;
            builder = builder
                .clear_ip_transports()
                .bind_addr(addr)
                .map_err(|e| Error::new(crate::YL_ERR_BAD_CONFIG, format!("bad bind_addr \"{bind}\": {e}")))?;
        }

        builder.bind().await.map_err(|e| Error::new(crate::YL_ERR_BIND, format!("couldn't open endpoint: {e}")))
    }

    fn runtime(&self) -> &Runtime {
        self.runtime.as_ref().expect("runtime lives until drop")
    }

    fn check_open(&self) -> Result<()> {
        if self.shared.shutting_down.load(Ordering::Acquire) {
            return Err(Error::new(crate::YL_ERR_CLOSED, "handle is shutting down"));
        }
        Ok(())
    }

    pub fn shared(&self) -> &Shared {
        &self.shared
    }

    pub fn local_ticket(&self) -> String {
        if self.ticket_addrs {
            ticket::format(&self.endpoint.addr(), true)
        } else {
            self.endpoint.id().to_string()
        }
    }

    pub fn connect(&self, ticket: &str) -> Result<u32> {
        self.check_open()?;
        let addr = ticket::parse(ticket).map_err(|e| Error::new(crate::YL_ERR_BAD_TICKET, e))?;
        if addr.id == self.endpoint.id() {
            return Err(Error::new(crate::YL_ERR_BAD_TICKET, "that's this game's own ticket"));
        }

        let (peer, rx) = self.shared.new_peer();
        let id = peer.id;
        let (endpoint, shared, timeout) = (self.endpoint.clone(), self.shared.clone(), self.connect_timeout);
        self.runtime().spawn(async move {
            let _done = peer.done.clone().drop_guard();
            let established = tokio::select! {
                _ = peer.cancel.cancelled() => return,
                r = connect_outgoing(&endpoint, addr, timeout) => r,
            };
            match established {
                Ok((conn, send, recv)) => run_peer(shared, peer, conn, send, recv, rx).await,
                Err(reason) => shared.close_peer(&peer, reason, false),
            }
        });
        Ok(id)
    }

    pub fn disconnect(&self, id: u32) {
        let _guard = self.runtime().enter();
        if let Some(peer) = self.shared.peer(id) {
            self.shared.close_peer(&peer, "disconnected locally".into(), true);
        }
    }

    pub fn send(&self, id: u32, channel: u8, data: &[u8]) -> Result<()> {
        if channel != CHANNEL_RELIABLE && channel != CHANNEL_UNRELIABLE {
            return Err(Error::new(crate::YL_ERR_INVALID_ARG, format!("unknown channel {channel}")));
        }
        if channel == CHANNEL_UNRELIABLE && data.len() > MAX_UNRELIABLE {
            return Err(Error::new(
                crate::YL_ERR_TOO_LARGE,
                format!("unreliable message of {} bytes is over the {MAX_UNRELIABLE} byte limit", data.len()),
            ));
        }
        if channel == CHANNEL_RELIABLE && data.len() + 1 > MAX_FRAME {
            return Err(Error::new(crate::YL_ERR_TOO_LARGE, format!("message of {} bytes is too large", data.len())));
        }
        self.check_open()?;

        let Some(peer) = self.shared.peer(id) else { return Ok(()) };
        if !peer.is_ready() {
            return Ok(());
        }

        if channel == CHANNEL_RELIABLE {
            let frame = encode_frame(KIND_DATA, data);
            let _ = peer.writer.send(Write::Frame(frame));
            return Ok(());
        }

        let Some(conn) = peer.conn.get() else { return Ok(()) };
        let _guard = self.runtime().enter();
        if let Err(e) = conn.send_datagram(Bytes::copy_from_slice(data)) {
            // Unreliable anyway; the read side notices if the connection is gone
            debug!(peer = id, "datagram dropped: {e}");
        }
        Ok(())
    }

    pub fn stats(&self, id: u32) -> Stats {
        let Some(peer) = self.shared.peer(id) else { return Stats::default() };
        let mut stats = Stats { connected: peer.is_ready(), ..Stats::default() };
        if stats.connected
            && let Some(conn) = peer.conn.get()
        {
            let _guard = self.runtime().enter();
            // Everything on the wire, including QUIC's own acknowledgements and keepalives
            let quic = conn.stats();
            stats.bytes_sent = quic.udp_tx.bytes;
            stats.bytes_received = quic.udp_rx.bytes;
            if let Some(path) = conn.paths().iter().find(|p| p.is_selected()) {
                stats.rtt = Some(path.rtt());
                stats.path = if path.is_relay() { PATH_RELAY } else { PATH_DIRECT };
            }
        }
        stats
    }
}

impl Config {
    fn connect_timeout(&self) -> Duration {
        Duration::from_millis(self.connect_timeout_ms.max(1))
    }
}

impl Drop for Node {
    fn drop(&mut self) {
        self.shared.shutting_down.store(true, Ordering::Release);
        let Some(runtime) = self.runtime.take() else { return };

        tracing::dispatcher::with_default(&self.dispatch, || {
            let peers: Vec<_> = lock(&self.shared.peers).drain().map(|(_, p)| p).collect();
            {
                let _guard = runtime.enter();
                for peer in &peers {
                    self.shared.close_peer(peer, String::new(), true);
                }
            }

            // Let the goodbyes go out, so friends see us leave right away (and why)
            let endpoint = self.endpoint.clone();
            runtime.block_on(async move {
                let _ = tokio::time::timeout(Duration::from_millis(1500), async {
                    for peer in &peers {
                        peer.done.cancelled().await;
                    }
                })
                .await;
                let _ = tokio::time::timeout(Duration::from_millis(500), endpoint.close()).await;
            });
            runtime.shutdown_timeout(Duration::from_millis(500));
        });

        let mut queue = lock(&self.shared.events);
        queue.events.clear();
        queue.backlog.clear();
    }
}

fn encode_frame(kind: u8, payload: &[u8]) -> Bytes {
    let mut frame = Vec::with_capacity(5 + payload.len());
    frame.extend_from_slice(&((1 + payload.len()) as u32).to_le_bytes());
    frame.push(kind);
    frame.extend_from_slice(payload);
    frame.into()
}

/// Read one frame of at most `max` bytes. `Ok(None)` means the stream ended cleanly between frames.
async fn read_frame(recv: &mut RecvStream, max: usize) -> std::result::Result<Option<(u8, Vec<u8>)>, String> {
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

    // Grow the buffer as data arrives, so a header alone can't make us allocate the whole frame
    let mut frame = Vec::with_capacity(len.min(64 * 1024));
    while frame.len() < len {
        match recv.read_chunk(len - frame.len()).await.map_err(|e| e.to_string())? {
            Some(chunk) => frame.extend_from_slice(&chunk),
            None => return Err("stream ended in the middle of a frame".into()),
        }
    }
    let kind = frame.remove(0);
    Ok(Some((kind, frame)))
}

async fn send_hello(send: &mut SendStream) -> std::result::Result<(), String> {
    send.write_all(&encode_frame(KIND_HELLO, &TRANSPORT_VERSION.to_le_bytes()))
        .await
        .map_err(|e| format!("handshake failed: {e}"))
}

async fn read_hello(recv: &mut RecvStream) -> std::result::Result<(), String> {
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

type Established = (Connection, SendStream, RecvStream);

/// Connect and handshake within `timeout`. Failed attempts are retried: a host that just started
/// may not have published its addresses yet, so the first lookup can come back empty.
async fn connect_outgoing(
    endpoint: &Endpoint,
    addr: EndpointAddr,
    timeout: Duration,
) -> std::result::Result<Established, String> {
    let deadline = tokio::time::Instant::now() + timeout;
    let mut delay = Duration::from_millis(500);
    let conn = loop {
        debug!("connecting to {}", addr.id.fmt_short());
        let attempt = tokio::time::timeout_at(deadline, endpoint.connect(addr.clone(), ALPN)).await;
        match attempt {
            Err(_) => return Err("connection timed out".into()),
            Ok(Ok(conn)) => break conn,
            Ok(Err(e)) => {
                if tokio::time::Instant::now() + delay >= deadline {
                    return Err(format!("couldn't connect: {e}"));
                }
                debug!("couldn't connect ({e}); retrying in {delay:?}");
                tokio::time::sleep(delay).await;
                delay = (delay * 2).min(Duration::from_secs(4));
            }
        }
    };

    let handshake = async {
        // The joiner opens the stream; QUIC only announces it once data is written, which the hello does
        let (mut send, mut recv) = conn.open_bi().await.map_err(|e| describe(&e))?;
        send_hello(&mut send).await?;
        read_hello(&mut recv).await?;
        Ok::<_, String>((send, recv))
    };
    match tokio::time::timeout_at(deadline, handshake).await {
        Ok(Ok((send, recv))) => Ok((conn, send, recv)),
        Ok(Err(e)) => Err(e),
        Err(_) => Err("connection timed out".into()),
    }
}

async fn connect_incoming(incoming: Incoming) -> std::result::Result<Established, String> {
    let conn = incoming
        .accept()
        .map_err(|e| describe(&e))?
        .await
        .map_err(|e| format!("handshake failed: {e}"))?;
    let (mut send, mut recv) = conn.accept_bi().await.map_err(|e| describe(&e))?;
    read_hello(&mut recv).await?;
    send_hello(&mut send).await?;
    Ok((conn, send, recv))
}

/// Counts an incoming connection as handshaking for as long as it lives.
struct Pending(Arc<AtomicUsize>);

impl Drop for Pending {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::AcqRel);
    }
}

async fn accept_loop(endpoint: Endpoint, shared: Arc<Shared>, timeout: Duration, max_peers: usize) {
    let pending = Arc::new(AtomicUsize::new(0));
    while let Some(incoming) = endpoint.accept().await {
        if shared.shutting_down.load(Ordering::Acquire) {
            incoming.refuse();
            continue;
        }

        let peers = lock(&shared.peers).len();
        let handshaking = pending.load(Ordering::Acquire);
        if handshaking >= MAX_PENDING_INCOMING || peers + handshaking >= max_peers {
            debug!("refusing an incoming connection ({peers} peers, {handshaking} connecting)");
            incoming.refuse();
            continue;
        }
        pending.fetch_add(1, Ordering::AcqRel);
        let pending = Pending(pending.clone());

        let shared = shared.clone();
        tokio::spawn(async move {
            let established = tokio::time::timeout(timeout, connect_incoming(incoming)).await;
            drop(pending);
            match established {
                Ok(Ok((conn, send, recv))) => {
                    if shared.shutting_down.load(Ordering::Acquire) {
                        return;
                    }
                    let (peer, rx) = shared.new_peer();
                    let _done = peer.done.clone().drop_guard();
                    run_peer(shared, peer, conn, send, recv, rx).await;
                }
                Ok(Err(e)) => info!("incoming connection failed: {e}"),
                Err(_) => info!("incoming connection timed out"),
            }
        });
    }
    if !shared.shutting_down.load(Ordering::Acquire) {
        shared.push(Event::Error("stopped accepting connections".into()));
    }
}

/// For a game that only joins: nobody gets to connect to it.
async fn refuse_loop(endpoint: Endpoint) {
    while let Some(incoming) = endpoint.accept().await {
        debug!("refusing an incoming connection: not accepting any");
        incoming.refuse();
    }
}

/// Serve an established connection until it ends.
async fn run_peer(
    shared: Arc<Shared>,
    peer: Arc<Peer>,
    conn: Connection,
    send: SendStream,
    recv: RecvStream,
    rx: mpsc::UnboundedReceiver<Write>,
) {
    let _ = peer.conn.set(conn.clone());
    if !shared.mark_ready(&peer) {
        // Disconnected locally while the handshake finished
        conn.close(0u32.into(), b"left");
        return;
    }
    let path = conn
        .paths()
        .iter()
        .find(|p| p.is_selected())
        .map(|p| if p.is_relay() { "relay" } else { "direct" })
        .unwrap_or("unknown");
    info!(peer = peer.id, "connected to {} ({path})", conn.remote_id().fmt_short());

    let failure = tokio::select! {
        _ = peer.cancel.cancelled() => None,
        r = write_loop(&conn, send, rx) => r,
        r = read_loop(&shared, &peer, recv) => Some(r),
        r = datagram_loop(&shared, &peer, &conn) => Some(r),
    };

    if let Some(failure) = failure {
        // The connection's own close reason says more than the stream error it caused
        let reason = conn.close_reason().map(|e| describe(&e)).unwrap_or(failure);
        shared.close_peer(&peer, reason, false);
    }
}

/// Write queued frames. Returns `None` after a graceful finish, or why the stream broke.
async fn write_loop(conn: &Connection, mut send: SendStream, mut rx: mpsc::UnboundedReceiver<Write>) -> Option<String> {
    while let Some(write) = rx.recv().await {
        match write {
            Write::Frame(frame) => {
                if let Err(e) = send.write_all(&frame).await {
                    return Some(format!("connection lost: {e}"));
                }
            }
            Write::Finish => {
                // Closing right away would discard data the peer hasn't acknowledged yet. Instead
                // end the stream; the peer closes the connection once it has read everything.
                let _ = send.finish();
                let _ = tokio::time::timeout(Duration::from_secs(2), conn.closed()).await;
                conn.close(0u32.into(), b"left");
                return None;
            }
        }
    }
    Some("closed".into())
}

async fn read_loop(shared: &Shared, peer: &Peer, mut recv: RecvStream) -> String {
    loop {
        match read_frame(&mut recv, MAX_FRAME).await {
            Ok(Some((kind, payload))) => {
                match kind {
                    KIND_DATA => {
                        if !shared.push_message(peer, CHANNEL_RELIABLE, payload.into()) {
                            return "peer sent more than the game could take in".into();
                        }
                    }
                    KIND_HELLO => {}
                    _ => {} // from a newer version; skip it
                }
            }
            Ok(None) => return "closed by peer".into(),
            Err(e) => return format!("connection lost: {e}"),
        }
    }
}

async fn datagram_loop(shared: &Shared, peer: &Peer, conn: &Connection) -> String {
    loop {
        match conn.read_datagram().await {
            Ok(data) => {
                if !shared.push_message(peer, CHANNEL_UNRELIABLE, data) {
                    debug!(peer = peer.id, "datagram dropped: too many waiting");
                }
            }
            Err(e) => return describe(&e),
        }
    }
}

fn describe(e: &ConnectionError) -> String {
    match e {
        ConnectionError::ApplicationClosed(close) => {
            if close.reason.is_empty() {
                "closed by peer".into()
            } else {
                format!("closed by peer ({})", String::from_utf8_lossy(&close.reason))
            }
        }
        ConnectionError::TimedOut => "connection timed out".into(),
        ConnectionError::LocallyClosed => "disconnected locally".into(),
        other => format!("connection lost: {other}"),
    }
}
