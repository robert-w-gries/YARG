//! One handle's networking: an iroh endpoint, its tokio runtime, its peers and its event queue.
//!
//! - `mod.rs` (this file): `Node`, which the exported functions call.
//! - `shared.rs`: the event queue and the list of peers, with the peer state machine.
//! - `peer.rs`: one peer, and the operations that need only the peer.
//! - `conn.rs`: the tasks of each connection.
//! - `wire.rs`: the wire format.

mod conn;
mod peer;
mod shared;
mod wire;

use std::{net::SocketAddr, sync::Arc, time::Duration};

use bytes::Bytes;
use iroh::{
    Endpoint, RelayMode, SecretKey,
    endpoint::{Builder, presets},
};
use tokio::runtime::Runtime;
use tracing::{Dispatch, debug, info};

pub use self::{
    peer::Stats,
    shared::{Event, Shared},
    wire::{ALPN, MAX_RELIABLE_FRAME, MAX_UNRELIABLE_FRAME},
};
use self::{
    peer::{CloseReason, Peer, Queued},
    wire::{FrameKind, encode_frame},
};
use crate::{
    YL_CHANNEL_RELIABLE, YL_CHANNEL_UNRELIABLE,
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
/// How long `Drop` waits for the runtime's tasks to stop, after the endpoint is closed.
const DROP_RUNTIME_TIMEOUT: Duration = Duration::from_millis(500);

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
    /// - the sockets: iroh's default sockets (no `bind_addr`), or only `bind_addr`.
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
            builder = replace_default_sockets(builder, addr)?;
        }

        builder.bind().await.map_err(|e| YlError::bind(format!("couldn't open endpoint: {e}")))
    }

    /// The runtime of this node.
    ///
    /// The runtime is an `Option` only so that `Drop` can take it and shut it down. Thus the panic
    /// below can occur only if code calls this function after `Drop` took the runtime. That is not
    /// possible from an exported function: `yl_destroy` runs `Drop` only when no other call has the
    /// node (refer to `yl_destroy`), and the runtime's tasks do not call `Node`'s methods. If you
    /// see this message (`yl_last_error` after `YL_ERR_PANIC`, or a tokio task panic in the log),
    /// look for a new call of a `Node` method in `Drop`, or in a task that keeps the `Node`.
    fn runtime(&self) -> &Runtime {
        self.runtime.as_ref().expect("Node::runtime was called after Drop of Node took the runtime")
    }

    pub fn shared(&self) -> &Shared {
        &self.shared
    }

    /// The ticket of this node: the text that another game gives to `yl_connect` to connect to
    /// this one (`yl_local_ticket`).
    ///
    /// The ticket is this endpoint's ID. With `ticket_addrs`, it also has the direct addresses and
    /// the relay URL that the endpoint knows now (refer to `ticket::format`). Thus the ticket can
    /// change when the network changes, for example when the endpoint finds its public address.
    /// Call this function each time a ticket is necessary: do not keep an old one.
    pub fn local_ticket(&self) -> String {
        ticket::format(&self.endpoint.addr(), self.ticket_addrs)
    }

    /// Start a connect to `ticket`, and return the new peer's number at once. The peer's task
    /// (`conn::run_outgoing`) queues `Connected` or `Disconnected` later.
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

        // new_peer gives None only after Drop of Node started, and no exported function can call
        // this method then (refer to `runtime`)
        let Some((peer, rx)) = self.shared.new_peer() else {
            return Err(YlError::closed("the handle is closing"));
        };
        let id = peer.id;
        let task = conn::run_outgoing(self.shared.clone(), peer, rx, self.endpoint.clone(), addr, self.connect_timeout);
        self.runtime().spawn(task);
        Ok(id)
    }

    pub fn disconnect(&self, id: u32) {
        if let Some(peer) = self.shared.peer(id) {
            self.close_peer(&peer, CloseReason::Local);
        }
    }

    /// Close a peer from an exported function. This is `Shared::close_peer` in the runtime's
    /// context. The exported functions run on the game's thread, which is not a runtime thread,
    /// and iroh can use the runtime (its timers) when a connection closes. The runtime's tasks
    /// call `Shared::close_peer` directly, because they are in the context already.
    fn close_peer(&self, peer: &Peer, reason: CloseReason) {
        let _guard = self.runtime().enter();
        self.shared.close_peer(peer, reason);
    }

    /// Send `data` to peer `id` on `channel`. Refer to `send_reliable` and `send_unreliable`.
    ///
    /// The result is an error only when the arguments are not correct: an unknown channel, or a
    /// message that is too large. A message to a peer that is not Ready (unknown, still connecting
    /// or closed) is dropped, and the result is `Ok`. The header promises this (`yl_send`), for
    /// this reason: a peer can disconnect at any moment, also between the game's last `yl_poll`
    /// and this call, so the game cannot prevent such a send. The game learns of the disconnect
    /// from its `YL_EVENT_PEER_DISCONNECTED` event, not from `yl_send`.
    pub fn send(&self, id: u32, channel: u8, data: &[u8]) -> Result<()> {
        match channel {
            YL_CHANNEL_RELIABLE => self.send_reliable(id, data),
            YL_CHANNEL_UNRELIABLE => self.send_unreliable(id, data),
            _ => Err(YlError::invalid_arg(format!("unknown channel {channel}"))),
        }
    }

    /// Queue a reliable message for the peer's writer task.
    fn send_reliable(&self, id: u32, data: &[u8]) -> Result<()> {
        if data.len() + 1 > MAX_RELIABLE_FRAME {
            return Err(YlError::too_large(format!(
                "reliable message of {} bytes is over the {} byte limit",
                data.len(),
                MAX_RELIABLE_FRAME - 1
            )));
        }
        if let Some(peer) = self.shared.ready_peer(id) {
            match peer.queue_frame(encode_frame(FrameKind::Data, data)) {
                Queued::Yes => {}
                // The peer's task ended; its disconnect is on the way
                Queued::WriterEnded => {}
                Queued::Full => self.close_peer(&peer, CloseReason::SlowReader),
            }
        }
        Ok(())
    }

    /// Send an unreliable message as one QUIC datagram. If the path can't carry it now, it is
    /// dropped: unreliable messages can be lost.
    fn send_unreliable(&self, id: u32, data: &[u8]) -> Result<()> {
        if data.len() > MAX_UNRELIABLE_FRAME {
            return Err(YlError::too_large(format!(
                "unreliable message of {} bytes is over the {MAX_UNRELIABLE_FRAME} byte limit",
                data.len()
            )));
        }
        if let Some(conn) = self.shared.ready_peer(id).and_then(|peer| peer.conn.get().cloned()) {
            let _guard = self.runtime().enter();
            if let Err(e) = conn.send_datagram(Bytes::copy_from_slice(data)) {
                // The read side notices if the connection is gone
                debug!(peer = id, "datagram dropped: {e}");
            }
        }
        Ok(())
    }

    /// The peer's endpoint ID, once its connection is up. Refer to `Peer::endpoint_id`.
    pub fn peer_id(&self, id: u32) -> Option<String> {
        self.shared.peer(id)?.endpoint_id()
    }

    /// The statistics of peer `id`, or `None` if the peer is not connected now: it is unknown,
    /// still connecting, or closed. `yl_peer_stats` gives `YL_STATS_NOT_CONNECTED` for `None`.
    ///
    /// The byte counts are of all UDP packets of the connection, with QUIC's own acknowledgements
    /// and keepalives. The path and round-trip time are of the path that QUIC uses now.
    pub fn stats(&self, id: u32) -> Option<Stats> {
        let peer = self.shared.peer(id)?;
        let _guard = self.runtime().enter();
        peer.stats()
    }
}

/// Make the endpoint use only one socket, at `addr`, in place of iroh's default sockets.
///
/// Without this, iroh opens its default sockets: one IPv4 and one IPv6 socket, on all network
/// interfaces, each on a random free port. `bind_addr` adds a socket and does not remove the
/// default sockets. Thus `clear_ip_transports` removes them first, else the endpoint keeps them as
/// well as `addr`.
fn replace_default_sockets(builder: Builder, addr: SocketAddr) -> Result<Builder> {
    builder
        .clear_ip_transports()
        .bind_addr(addr)
        .map_err(|e| YlError::bad_config(format!("bad bind_addr \"{addr}\": {e}")))
}

impl Drop for Node {
    /// Close the handle. The steps, in this order:
    /// 1. `close_all_peers`: set `closing`, and close each peer gracefully. A graceful close does not
    ///    cut the connection. It puts `Write::Finish` in the peer's writer channel, behind the
    ///    reliable messages that the game sent before `yl_destroy` (its goodbye). The channel is
    ///    first in, first out, and one task writes it, so the goodbye goes out before the finish.
    ///    A peer that is not Ready has no goodbye, and closes at once.
    /// 2. Wait, at most `DROP_GOODBYE_TIMEOUT`, until each peer's task ends. A task ends when the
    ///    peer has read everything and closed the connection (or after `FINISH_TIMEOUT`).
    /// 3. Close the endpoint, at most `DROP_ENDPOINT_TIMEOUT`. This closes each connection that
    ///    is still open.
    /// 4. Stop the runtime, at most `DROP_RUNTIME_TIMEOUT`, and clear the event queue.
    fn drop(&mut self) {
        let Some(runtime) = self.runtime.take() else { return };

        tracing::dispatcher::with_default(&self.dispatch, || {
            let peers = {
                let _guard = runtime.enter();
                self.shared.close_all_peers()
            };

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
            runtime.shutdown_timeout(DROP_RUNTIME_TIMEOUT);
        });

        self.shared.clear_events();
    }
}
