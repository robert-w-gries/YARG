//! One handle's networking: an iroh endpoint, its tokio runtime, its peers and its event queue.
//!
//! - `mod.rs` (this file): `Node`, which the exported functions call.
//! - `shared.rs`: the event queue and the peers, with the peer state machine.
//! - `conn.rs`: the tasks of each connection.
//! - `wire.rs`: the wire format.

mod conn;
mod shared;
mod wire;

use std::{net::SocketAddr, sync::Arc, time::Duration};

use bytes::Bytes;
use iroh::{
    Endpoint, RelayMode, SecretKey,
    endpoint::{Builder, presets},
};
use tokio::{runtime::Runtime, sync::mpsc::error::TrySendError};
use tracing::{Dispatch, debug, info};

pub use self::{
    shared::{Event, Shared},
    wire::{ALPN, MAX_RELIABLE_FRAME, MAX_UNRELIABLE_FRAME},
};
use self::{
    shared::{Peer, Write},
    wire::{KIND_DATA, encode_frame},
};
use crate::{
    config::Config,
    error::{Result, YlError},
    log, ticket,
};

/// The longest an incoming connection may take to handshake. Shorter than our own connects, which
/// may have to wait for a host's addresses to be published.
const INCOMING_TIMEOUT: Duration = Duration::from_secs(10);

/// How long `Drop` waits for the peers' goodbyes, and then for the endpoint to close.
const DROP_GOODBYE_TIMEOUT: Duration = Duration::from_millis(1500);
const DROP_ENDPOINT_TIMEOUT: Duration = Duration::from_millis(500);

pub const CHANNEL_RELIABLE: u8 = 0;
pub const CHANNEL_UNRELIABLE: u8 = 1;

#[allow(dead_code)] // the default; kept to mirror YL_PATH_UNKNOWN
pub const PATH_UNKNOWN: u32 = 0;
pub const PATH_DIRECT: u32 = 1;
pub const PATH_RELAY: u32 = 2;

#[derive(Debug, Default, Clone, Copy)]
pub struct Stats {
    pub connected: bool,
    pub path: u32,
    pub rtt: Option<Duration>,
    pub bytes_sent: u64,
    pub bytes_received: u64,
}

/// The networking of one handle (`yl_handle`). There is one `Node` for each `yl_create`.
///
/// A `Node` has:
/// - an iroh endpoint: the sockets, the endpoint ID (a new key for each node) and the relay
///   connection;
/// - a tokio runtime with two threads, which runs all networking: the accept loop, and one task
///   for each peer;
/// - `Shared`: the event queue for `yl_poll` and the list of peers.
///
/// The exported functions call `Node`'s methods. They never wait for the network: each one queues
/// work for the runtime, or reads `Shared`, and returns. Only `Drop` waits, so that goodbyes go out.
pub struct Node {
    /// `None` only in `Drop`, which takes the runtime to shut it down.
    runtime: Option<Runtime>,
    endpoint: Endpoint,
    shared: Arc<Shared>,
    /// The log dispatcher of this handle. The runtime's threads use it, and `Drop` uses it.
    dispatch: Dispatch,
    ticket_addrs: bool,
    address_lookup: bool,
    connect_timeout: Duration,
}

impl Node {
    pub fn new(config: Config) -> Result<Node> {
        let shared = Arc::new(Shared::new());
        let dispatch = log::dispatch(&config.log_filter, shared.clone()).map_err(YlError::bad_config)?;

        let runtime = {
            let dispatch = dispatch.clone();
            tokio::runtime::Builder::new_multi_thread()
                .worker_threads(2)
                .thread_name("yarg-link")
                .enable_all()
                .on_thread_start(move || {
                    // Make this handle's dispatcher the log dispatcher of this thread, so that iroh's
                    // log lines go to this handle's queue. set_default returns a guard that removes the
                    // dispatcher when it drops. We forget the guard, so the dispatcher stays for the life
                    // of the thread. The runtime's threads end when the handle closes, thus no thread
                    // keeps the dispatcher longer than the handle.
                    std::mem::forget(tracing::dispatcher::set_default(&dispatch));
                })
                .build()
                .map_err(|e| YlError::internal(format!("couldn't start runtime: {e}")))?
        };

        tracing::dispatcher::with_default(&dispatch, || {
            // A new identity, and so a new ticket, for every handle: an invite code stops working once its
            // session ends, and a joiner can't be recognised from one session to the next
            let endpoint = runtime.block_on(Self::bind(&config, SecretKey::generate()))?;
            // Shortened: logs get posted publicly, and the full ID is the invite code
            info!("endpoint {} is up", endpoint.id().fmt_short());

            if config.accept {
                let timeout = config.connect_timeout.min(INCOMING_TIMEOUT);
                runtime.spawn(conn::accept_loop(endpoint.clone(), shared.clone(), timeout, config.max_peers));
            } else {
                runtime.spawn(conn::refuse_loop(endpoint.clone()));
            }

            Ok(Node {
                runtime: Some(runtime),
                endpoint,
                shared,
                dispatch: dispatch.clone(),
                ticket_addrs: config.ticket_addrs,
                address_lookup: config.address_lookup,
                connect_timeout: config.connect_timeout,
            })
        })
    }

    /// Open the iroh endpoint for `config`.
    ///
    /// The endpoint gets these settings:
    /// - the address lookup: n0's DNS (`presets::N0`) or none (`presets::Minimal`);
    /// - the secret key, which gives the endpoint ID;
    /// - the ALPN list (refer to `wire::ALPN`): the endpoint accepts only connections for this
    ///   protocol;
    /// - the relays: n0's public relays, or none;
    /// - the sockets: iroh's default sockets, or only `bind_addr`.
    async fn bind(config: &Config, secret: SecretKey) -> Result<Endpoint> {
        // The presets are different types, so each branch makes its own builder
        let builder = if config.address_lookup {
            Endpoint::builder(presets::N0)
        } else {
            Endpoint::builder(presets::Minimal)
        };
        let mut builder = builder
            .secret_key(secret)
            .alpns(vec![ALPN.to_vec()])
            .relay_mode(if config.relay { RelayMode::Default } else { RelayMode::Disabled });

        if let Some(addr) = config.bind_addr {
            builder = bind_only(builder, addr)?;
        }

        builder.bind().await.map_err(|e| YlError::bind(format!("couldn't open endpoint: {e}")))
    }

    fn runtime(&self) -> &Runtime {
        self.runtime.as_ref().expect("only Drop removes the runtime")
    }

    pub fn shared(&self) -> &Shared {
        &self.shared
    }

    pub fn local_ticket(&self) -> String {
        ticket::format(&self.endpoint.addr(), self.ticket_addrs)
    }

    /// Start a connect to `ticket`, and return the new peer's number at once. The peer's task
    /// queues `Connected` or `Disconnected` later.
    pub fn connect(&self, ticket: &str) -> Result<u32> {
        let addr = ticket::parse(ticket).map_err(YlError::bad_ticket)?;
        if addr.id == self.endpoint.id() {
            return Err(YlError::bad_ticket("that's this game's own ticket"));
        }
        if !self.address_lookup && addr.addrs.is_empty() {
            return Err(YlError::bad_ticket(
                "the ticket has no addresses, and address_lookup is off, so it can't be found",
            ));
        }

        let (peer, rx) = self.shared.new_peer();
        let id = peer.id;
        let (endpoint, shared, timeout) = (self.endpoint.clone(), self.shared.clone(), self.connect_timeout);
        self.runtime().spawn(async move {
            let _done = peer.done.clone().drop_guard();
            let established = tokio::select! {
                _ = peer.cancel.cancelled() => return,
                r = conn::connect_outgoing(&endpoint, addr, timeout) => r,
            };
            match established {
                Ok((conn, send, recv)) => conn::run_peer(shared, peer, conn, send, recv, rx).await,
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

    /// Send `data` to peer `id` on `channel`. Refer to `send_reliable` and `send_unreliable`.
    pub fn send(&self, id: u32, channel: u8, data: &[u8]) -> Result<()> {
        match channel {
            CHANNEL_RELIABLE => self.send_reliable(id, data),
            CHANNEL_UNRELIABLE => self.send_unreliable(id, data),
            _ => Err(YlError::invalid_arg(format!("unknown channel {channel}"))),
        }
    }

    /// Queue a reliable message for the peer's writer task. If too much waits to go out to the
    /// peer already, the peer is disconnected.
    fn send_reliable(&self, id: u32, data: &[u8]) -> Result<()> {
        if data.len() + 1 > MAX_RELIABLE_FRAME {
            return Err(YlError::too_large(format!(
                "reliable message of {} bytes is over the {} byte limit",
                data.len(),
                MAX_RELIABLE_FRAME - 1
            )));
        }
        let Some(peer) = self.ready_peer(id) else { return Ok(()) };

        let frame = encode_frame(KIND_DATA, data);
        // At most MAX_RELIABLE_FRAME + 4 bytes, which fits in a u32
        let bytes = frame.len() as u32;
        let Ok(permit) = peer.outgoing_bytes.clone().try_acquire_many_owned(bytes) else {
            self.close_slow_peer(&peer);
            return Ok(());
        };
        match peer.writer.try_send(Write::Frame(frame, permit)) {
            Ok(()) => {}
            // The peer's task ended; its disconnect is on the way
            Err(TrySendError::Closed(_)) => {}
            Err(TrySendError::Full(_)) => self.close_slow_peer(&peer),
        }
        Ok(())
    }

    /// Disconnect a peer that has too much waiting to go out to it: it stopped reading.
    fn close_slow_peer(&self, peer: &Peer) {
        let _guard = self.runtime().enter();
        self.shared.close_peer(peer, "peer stopped reading what we sent".into(), false);
    }

    /// Send an unreliable message as one QUIC datagram. If the path can't carry it now, it is dropped.
    fn send_unreliable(&self, id: u32, data: &[u8]) -> Result<()> {
        if data.len() > MAX_UNRELIABLE_FRAME {
            return Err(YlError::too_large(format!(
                "unreliable message of {} bytes is over the {MAX_UNRELIABLE_FRAME} byte limit",
                data.len()
            )));
        }
        let Some(peer) = self.ready_peer(id) else { return Ok(()) };
        let Some(conn) = peer.conn.get() else { return Ok(()) };

        let _guard = self.runtime().enter();
        if let Err(e) = conn.send_datagram(Bytes::copy_from_slice(data)) {
            // Unreliable anyway; the read side notices if the connection is gone
            debug!(peer = id, "datagram dropped: {e}");
        }
        Ok(())
    }

    /// The peer to send to. `None` if the peer is unknown or not Ready: the message is then dropped,
    /// and the game learns of the disconnect from `yl_poll`.
    fn ready_peer(&self, id: u32) -> Option<Arc<Peer>> {
        self.shared.peer(id).filter(|peer| peer.is_ready())
    }

    /// The peer's endpoint ID, once its connection is up. Authenticated by the handshake, so a
    /// peer can't claim another's ID (though it can make a new one whenever it likes).
    pub fn peer_id(&self, id: u32) -> Option<String> {
        let peer = self.shared.peer(id)?;
        peer.conn.get().map(|conn| conn.remote_id().to_string())
    }

    /// The statistics of peer `id`. An unknown peer, or a peer that is not Ready, gives
    /// `connected: false` and zeros. The byte counts are of all UDP packets of the connection, with
    /// QUIC's own acknowledgements and keepalives. The path and round-trip time are of the path
    /// that QUIC uses now.
    pub fn stats(&self, id: u32) -> Stats {
        let Some(peer) = self.shared.peer(id) else { return Stats::default() };
        if !peer.is_ready() {
            return Stats::default();
        }
        let Some(conn) = peer.conn.get() else { return Stats::default() };

        let _guard = self.runtime().enter();
        let quic = conn.stats();
        let mut stats = Stats {
            connected: true,
            bytes_sent: quic.udp_tx.bytes,
            bytes_received: quic.udp_rx.bytes,
            ..Stats::default()
        };
        if let Some(path) = conn.paths().iter().find(|p| p.is_selected()) {
            stats.rtt = Some(path.rtt());
            stats.path = if path.is_relay() { PATH_RELAY } else { PATH_DIRECT };
        }
        stats
    }
}

/// Make the endpoint use only one socket, at `addr`. Without this, iroh opens its default sockets:
/// one IPv4 and one IPv6 socket, on all network interfaces, each on a random free port. The
/// default sockets must be removed first (`clear_ip_transports`), else the endpoint keeps them as
/// well as `addr`.
fn bind_only(builder: Builder, addr: SocketAddr) -> Result<Builder> {
    builder
        .clear_ip_transports()
        .bind_addr(addr)
        .map_err(|e| YlError::bad_config(format!("bad bind_addr \"{addr}\": {e}")))
}

impl Drop for Node {
    fn drop(&mut self) {
        self.shared.set_closing();
        let Some(runtime) = self.runtime.take() else { return };

        tracing::dispatcher::with_default(&self.dispatch, || {
            let peers = self.shared.take_peers();
            {
                let _guard = runtime.enter();
                for peer in &peers {
                    self.shared.close_peer(peer, String::new(), true);
                }
            }

            // Let the goodbyes go out, so friends see us leave right away (and why)
            let endpoint = self.endpoint.clone();
            runtime.block_on(async move {
                let _ = tokio::time::timeout(DROP_GOODBYE_TIMEOUT, async {
                    for peer in &peers {
                        peer.done.cancelled().await;
                    }
                })
                .await;
                let _ = tokio::time::timeout(DROP_ENDPOINT_TIMEOUT, endpoint.close()).await;
            });
            runtime.shutdown_timeout(Duration::from_millis(500));
        });

        self.shared.clear_events();
    }
}
