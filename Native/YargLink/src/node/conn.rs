//! The tasks of one connection: connect or accept, handshake, then read and write until it ends.
//!
//! # The life of a peer's task
//!
//! An outgoing connection (`run_outgoing`, from `yl_connect`):
//! 1. `Node::connect` adds the peer (Connecting) and starts the task.
//! 2. The task connects and does the handshake (`connect_outgoing`), within the connect timeout.
//! 3. On success, `run_peer` serves the connection until it ends. On a failure, `close_peer`
//!    closes the peer with the reason.
//!
//! An incoming connection (`accept_loop`, on a handle with `accept`):
//! 1. The accept loop takes the connection if the limits permit it (`admit`), and starts a task.
//! 2. The task accepts and does the handshake (`connect_incoming`), within `INCOMING_TIMEOUT`.
//! 3. On success, the task adds the peer and `run_peer` serves the connection. On a failure, there
//!    is no peer yet, so the task only logs the reason.
//!
//! In all cases, the task holds the peer's `done` guard (`Peer::done_on_drop`) until it ends.

use std::{sync::Arc, time::Duration};

use bytes::Bytes;
use iroh::{
    Endpoint, EndpointAddr,
    endpoint::{Connection, ConnectionError, Incoming, RecvStream, SendStream},
};
use tokio::sync::{OwnedSemaphorePermit, Semaphore, mpsc};
use tracing::{debug, info};

use super::{
    peer::{CloseReason, Peer, Write, path_name, selected_path},
    shared::{CLOSE_CODE, CLOSE_REASON_LEFT, Event, PushResult, Shared},
    wire::{ALPN, FrameKind, MAX_RELIABLE_FRAME, hello_as_host, hello_as_joiner, read_frame},
};
use crate::{YL_CHANNEL_RELIABLE, YL_CHANNEL_UNRELIABLE};

/// Incoming connections handshaking at once. More are refused until some finish.
const MAX_PENDING_INCOMING: usize = 8;

/// How long `Write::Finish` waits for the peer to read everything and close, before it closes.
const FINISH_TIMEOUT: Duration = Duration::from_secs(2);

/// The wait after the first failed connect attempt. Each next wait is two times longer, to at most
/// `CONNECT_RETRY_MAX_DELAY`.
///
/// A connect fails at first when the host started a short time ago: its addresses are not in n0's
/// DNS yet. Publishing takes some seconds. A first wait of 0.5 s gives a fast connect when the host
/// is almost ready, and does not send many lookups when it is not.
const CONNECT_RETRY_FIRST_DELAY: Duration = Duration::from_millis(500);
/// The longest wait between two connect attempts. A longer wait could make the player wait for a
/// host that is ready already. With the default connect timeout (20 s), this gives at most 8
/// attempts, at about 0, 0.5, 1.5, 3.5, 7.5, 11.5, 15.5 and 19.5 s (each attempt also takes time).
const CONNECT_RETRY_MAX_DELAY: Duration = Duration::from_secs(4);

pub type Established = (Connection, SendStream, RecvStream);

/// The task of an outgoing connection. Refer to "The life of a peer's task".
pub async fn run_outgoing(
    shared: Arc<Shared>,
    peer: Arc<Peer>,
    rx: mpsc::Receiver<Write>,
    endpoint: Endpoint,
    addr: EndpointAddr,
    timeout: Duration,
) {
    let _done = peer.done_on_drop();
    let established = tokio::select! {
        // close_peer cancelled the connect (yl_disconnect, or yl_destroy). close_peer already
        // queued the Disconnected event and logged the reason, so there is nothing more to do.
        _ = peer.cancel.cancelled() => return,
        r = connect_outgoing(&endpoint, addr, timeout) => r,
    };
    match established {
        Ok((conn, send, recv)) => run_peer(shared, peer, conn, send, recv, rx).await,
        // The peer never connected. close_peer queues Disconnected with the reason and removes the
        // peer. Then this task ends, and its `_done` guard and `rx` go with it.
        Err(reason) => shared.close_peer(&peer, CloseReason::Failed(reason)),
    }
}

/// Connect and handshake within `timeout`. Failed attempts are retried: a host that just started
/// may not have published its addresses yet, so the first lookup can come back empty.
async fn connect_outgoing(endpoint: &Endpoint, addr: EndpointAddr, timeout: Duration) -> Result<Established, String> {
    let deadline = tokio::time::Instant::now() + timeout;
    let mut delay = CONNECT_RETRY_FIRST_DELAY;
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
                delay = (delay * 2).min(CONNECT_RETRY_MAX_DELAY);
            }
        }
    };

    let handshake = async {
        // The joiner opens the stream. Refer to hello_as_joiner for the order of the hellos.
        let (mut send, mut recv) = conn.open_bi().await.map_err(|e| describe(&e))?;
        hello_as_joiner(&mut send, &mut recv).await?;
        Ok::<_, String>((send, recv))
    };
    match tokio::time::timeout_at(deadline, handshake).await {
        Ok(Ok((send, recv))) => Ok((conn, send, recv)),
        Ok(Err(e)) => Err(e),
        Err(_) => Err("connection timed out".into()),
    }
}

/// Accept an incoming connection and do the handshake as the host.
async fn connect_incoming(incoming: Incoming) -> Result<Established, String> {
    let conn = incoming
        .accept()
        .map_err(|e| describe(&e))?
        .await
        .map_err(|e| format!("handshake failed: {e}"))?;
    // The joiner opens the stream, so the host waits for it. Refer to hello_as_host for the order
    // of the hellos.
    let (mut send, mut recv) = conn.accept_bi().await.map_err(|e| describe(&e))?;
    hello_as_host(&mut send, &mut recv).await?;
    Ok((conn, send, recv))
}

/// Accept incoming connections, within the limits: at most `max_peers` peers (with the ones in
/// their handshake), and at most `MAX_PENDING_INCOMING` in their handshake at once.
///
/// `Node::new` starts this loop only when the config has `accept`. The config is read one time, in
/// `yl_create`, and no function changes it after that. Thus `accept` cannot change while this loop
/// runs: a handle accepts connections for all of its life, or never (then `refuse_loop` runs
/// instead). To change `accept`, the game makes a new handle.
pub async fn accept_loop(endpoint: Endpoint, shared: Arc<Shared>, timeout: Duration, max_peers: usize) {
    // One permit for each connection in its handshake. Refer to `admit`.
    let handshakes = Arc::new(Semaphore::new(MAX_PENDING_INCOMING));

    // `endpoint.accept()` gives the next connection attempt. It does not accept the connection:
    // `connect_incoming` or `refuse` does that.
    while let Some(incoming) = endpoint.accept().await {
        if shared.is_closing() {
            // Drop of Node closes the handle. Refuse the connection: then the other game gets an
            // answer at once, and does not wait for its timeout. Do not stop the loop or close the
            // endpoint here. Drop closes the endpoint when the goodbyes on the open connections
            // are out, and then `accept()` gives None and the loop ends.
            debug!("refusing an incoming connection: the handle closes");
            incoming.refuse();
            continue;
        }
        let Some(permit) = admit(&shared, &handshakes, max_peers) else {
            incoming.refuse();
            continue;
        };
        tokio::spawn(run_incoming(shared.clone(), incoming, permit, timeout));
    }

    // `accept()` gives None only when the endpoint is closed. Drop of Node does that at the end, so
    // then there is nothing to do. Else the endpoint failed: tell the game. Nothing needs a clean
    // up: a closed endpoint closes all its connections, and each peer's task then sees the error
    // and closes its peer.
    if !shared.is_closing() {
        shared.push(Event::Error("stopped accepting connections".into()));
    }
}

/// A handshake permit for one more incoming connection, or `None` if a limit is full.
///
/// There are two limits:
/// - All connections: the peers, with the connections in their handshake, must stay below
///   `max_peers`. A connection in its handshake is not a peer yet, so it is counted by its permit.
/// - Handshakes: at most `MAX_PENDING_INCOMING` at once. `try_acquire_owned` fails when all
///   permits are out.
fn admit(shared: &Shared, handshakes: &Arc<Semaphore>, max_peers: usize) -> Option<OwnedSemaphorePermit> {
    let peers = shared.peer_count();
    let handshaking = MAX_PENDING_INCOMING - handshakes.available_permits();
    if peers + handshaking >= max_peers {
        debug!("refusing an incoming connection ({peers} peers, {handshaking} connecting)");
        return None;
    }
    let permit = handshakes.clone().try_acquire_owned().ok();
    if permit.is_none() {
        debug!("refusing an incoming connection ({handshaking} connecting)");
    }
    permit
}

/// The task of an incoming connection. Refer to "The life of a peer's task".
///
/// `permit` counts the connection as "in its handshake" for `admit`. The task keeps it until the
/// connection is a peer (then `peer_count` counts it), or until the handshake fails. Thus at each
/// moment, `admit` counts the connection exactly once.
async fn run_incoming(shared: Arc<Shared>, incoming: Incoming, permit: OwnedSemaphorePermit, timeout: Duration) {
    let (conn, send, recv) = match tokio::time::timeout(timeout, connect_incoming(incoming)).await {
        Ok(Ok(established)) => established,
        // There is no peer yet, so there is no Disconnected event. The permit is dropped when the
        // task ends.
        Ok(Err(e)) => return info!("incoming connection failed: {e}"),
        Err(_) => return info!("incoming connection timed out"),
    };
    let Some((peer, rx)) = shared.new_peer() else {
        // The handle started to close during the handshake. Close the connection, so that the
        // other game sees that we left, and does not wait for an idle timeout.
        debug!("closing an incoming connection: the handle closes");
        conn.close(CLOSE_CODE, CLOSE_REASON_LEFT);
        return;
    };
    // peer_count counts the connection now, so the handshake permit can go back
    drop(permit);
    let _done = peer.done_on_drop();
    run_peer(shared, peer, conn, send, recv, rx).await;
}

/// The loop of a handle that does not accept connections (`accept` is off): refuse each
/// connection attempt.
///
/// The loop is necessary. iroh keeps connection attempts in a queue until something takes them
/// with `endpoint.accept()`. Without this loop, an attempt waits in the queue, and the other game
/// waits for its full connect timeout. `refuse` answers at once, before any handshake, and the
/// other game gets "connection refused".
pub async fn refuse_loop(endpoint: Endpoint) {
    while let Some(incoming) = endpoint.accept().await {
        debug!("refusing an incoming connection: not accepting any");
        incoming.refuse();
    }
}

/// Serve an established connection until it ends.
///
/// The steps are:
/// 1. Keep the connection in the peer, and move the peer to Ready (`mark_ready`). If the peer is
///    not Connecting any more, `close_peer` closed it during the handshake: close the connection.
/// 2. Run three loops at once until one ends: the writer (`write_loop`), the reliable reader
///    (`read_loop`) and the unreliable reader (`datagram_loop`). The cancel token also ends the
///    wait. When one ends, `select!` drops the others.
/// 3. Look at why it ended (refer to `Ended`). If the connection ended without a request from
///    this game, close the peer with the reason. Else `close_peer` already ran.
pub async fn run_peer(
    shared: Arc<Shared>,
    peer: Arc<Peer>,
    conn: Connection,
    send: SendStream,
    recv: RecvStream,
    rx: mpsc::Receiver<Write>,
) {
    let _ = peer.conn.set(conn.clone());
    if !shared.mark_ready(&peer) {
        // Disconnected locally while the handshake finished
        conn.close(CLOSE_CODE, CLOSE_REASON_LEFT);
        return;
    }
    let (path, _) = selected_path(&conn);
    info!(peer = peer.id, "connected to {} ({})", conn.remote_id().fmt_short(), path_name(path));

    let ended: Ended = tokio::select! {
        _ = peer.cancel.cancelled() => Ok(()),
        r = write_loop(&conn, send, rx) => r,
        e = read_loop(&shared, &peer, recv) => Err(e),
        e = datagram_loop(&shared, &peer, &conn) => Err(e),
    };

    if let Err(reason) = ended {
        // The connection's own close reason says more than the stream error it caused.
        // close_peer does nothing if the peer is closed already (for example, the peer closed the
        // connection after a graceful finish of ours).
        let reason = match reason {
            CloseReason::Failed(failure) => {
                CloseReason::Failed(conn.close_reason().map(|e| describe(&e)).unwrap_or(failure))
            }
            other => other,
        };
        shared.close_peer(&peer, reason);
    }
}

/// Why `run_peer`'s loops ended.
/// - `Ok(())`: this game ended the connection. `close_peer` already ran: it cancelled the task, or
///   it asked the writer for a graceful finish, which is now done.
/// - `Err(reason)`: the connection ended without a request from this game: it broke, the peer
///   closed it, or the peer misbehaved. `run_peer` closes the peer with this reason.
type Ended = Result<(), CloseReason>;

/// Write the queued frames, in order, until a `Write::Finish`. Refer to `Ended` for the result.
async fn write_loop(conn: &Connection, mut send: SendStream, mut rx: mpsc::Receiver<Write>) -> Ended {
    loop {
        // recv() gives None only when every Sender of the channel is dropped. `Peer::writer` is a
        // Sender, and run_peer holds the Peer for as long as this loop runs. Thus None cannot occur.
        // If it occurs (a bug), close the peer with a clear reason.
        let Some(write) = rx.recv().await else {
            return Err(CloseReason::Failed("internal error: the writer's channel closed".into()));
        };
        match write {
            Write::Frame(frame, permit) => {
                if let Err(e) = send.write_all(&frame).await {
                    return Err(CloseReason::Failed(format!("connection lost: {e}")));
                }
                // The frame's bytes no longer wait to go out
                drop(permit);
            }
            Write::Finish => {
                // Closing right away would discard data the peer hasn't acknowledged yet. Instead
                // end the stream; the peer closes the connection once it has read everything.
                let _ = send.finish();
                let _ = tokio::time::timeout(FINISH_TIMEOUT, conn.closed()).await;
                conn.close(CLOSE_CODE, CLOSE_REASON_LEFT);
                return Ok(());
            }
        }
    }
}

/// Read reliable frames and queue their messages for the game. This loop never ends on a request
/// of this game (the cancel token does that), so the result is always why the connection ended.
async fn read_loop(shared: &Shared, peer: &Peer, mut recv: RecvStream) -> CloseReason {
    loop {
        let frame = match read_frame(&mut recv, MAX_RELIABLE_FRAME).await {
            Ok(Some(frame)) => frame,
            Ok(None) => return CloseReason::Failed("closed by peer".into()),
            Err(e) => return CloseReason::Failed(format!("connection lost: {e}")),
        };
        match frame.kind {
            FrameKind::Data => match shared.push_message(peer, YL_CHANNEL_RELIABLE, Bytes::from(frame.payload)) {
                PushResult::Queued | PushResult::NotWanted => {}
                PushResult::Full => return CloseReason::Flooded,
            },
            // Each side sends one hello, in the handshake. A second hello is not correct
            FrameKind::Hello => return CloseReason::Failed("peer sent a second hello".into()),
        }
    }
}

/// Read datagrams and queue their messages for the game. A datagram that does not fit in the
/// peer's queue is dropped, and the loop continues: unreliable messages can be lost. The result
/// is why the connection ended.
async fn datagram_loop(shared: &Shared, peer: &Peer, conn: &Connection) -> CloseReason {
    loop {
        let data = match conn.read_datagram().await {
            Ok(data) => data,
            Err(e) => return CloseReason::Failed(describe(&e)),
        };
        if shared.push_message(peer, YL_CHANNEL_UNRELIABLE, data) == PushResult::Full {
            debug!(peer = peer.id, "datagram dropped: too many waiting");
        }
    }
}

pub fn describe(e: &ConnectionError) -> String {
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
