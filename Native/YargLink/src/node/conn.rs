//! The tasks of one connection: connect or accept, handshake, then read and write until it ends.

use std::{sync::Arc, time::Duration};

use bytes::Bytes;
use iroh::{
    Endpoint, EndpointAddr,
    endpoint::{Connection, ConnectionError, Incoming, RecvStream, SendStream},
};
use tokio::sync::{Semaphore, mpsc};
use tracing::{debug, info};

use super::{
    shared::{CLOSE_CODE, CLOSE_REASON_LEFT, Event, Peer, Shared, Write},
    wire::{ALPN, KIND_DATA, KIND_HELLO, MAX_RELIABLE_FRAME, read_frame, read_hello, send_hello},
};
use crate::node::{CHANNEL_RELIABLE, CHANNEL_UNRELIABLE};

/// Incoming connections handshaking at once. More are refused until some finish.
const MAX_PENDING_INCOMING: usize = 8;

/// How long `Write::Finish` waits for the peer to read everything and close, before it closes.
const FINISH_TIMEOUT: Duration = Duration::from_secs(2);

pub type Established = (Connection, SendStream, RecvStream);

/// Connect and handshake within `timeout`. Failed attempts are retried: a host that just started
/// may not have published its addresses yet, so the first lookup can come back empty.
pub async fn connect_outgoing(endpoint: &Endpoint, addr: EndpointAddr, timeout: Duration) -> Result<Established, String> {
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

async fn connect_incoming(incoming: Incoming) -> Result<Established, String> {
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

/// Accept incoming connections, within the limits: at most `max_peers` peers (with the ones in
/// their handshake), and at most `MAX_PENDING_INCOMING` in their handshake at once.
pub async fn accept_loop(endpoint: Endpoint, shared: Arc<Shared>, timeout: Duration, max_peers: usize) {
    // One permit for each connection in its handshake. The permit goes back when the handshake ends.
    let handshakes = Arc::new(Semaphore::new(MAX_PENDING_INCOMING));
    while let Some(incoming) = endpoint.accept().await {
        if shared.is_closing() {
            incoming.refuse();
            continue;
        }

        let peers = shared.peer_count();
        let handshaking = MAX_PENDING_INCOMING - handshakes.available_permits();
        let permit = match handshakes.clone().try_acquire_owned() {
            Ok(permit) if peers + handshaking < max_peers => permit,
            _ => {
                debug!("refusing an incoming connection ({peers} peers, {handshaking} connecting)");
                incoming.refuse();
                continue;
            }
        };

        let shared = shared.clone();
        tokio::spawn(async move {
            let established = tokio::time::timeout(timeout, connect_incoming(incoming)).await;
            drop(permit);
            match established {
                Ok(Ok((conn, send, recv))) => {
                    if shared.is_closing() {
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
    if !shared.is_closing() {
        shared.push(Event::Error("stopped accepting connections".into()));
    }
}

/// For a game that only joins: nobody gets to connect to it.
pub async fn refuse_loop(endpoint: Endpoint) {
    while let Some(incoming) = endpoint.accept().await {
        debug!("refusing an incoming connection: not accepting any");
        incoming.refuse();
    }
}

/// Serve an established connection until it ends.
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
    info!(peer = peer.id, "connected to {} ({})", conn.remote_id().fmt_short(), path_name(&conn));

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

fn path_name(conn: &Connection) -> &'static str {
    match conn.paths().iter().find(|p| p.is_selected()) {
        Some(path) if path.is_relay() => "relay",
        Some(_) => "direct",
        None => "unknown",
    }
}

/// Write queued frames. Returns `None` after a graceful finish, or why the stream broke.
async fn write_loop(conn: &Connection, mut send: SendStream, mut rx: mpsc::Receiver<Write>) -> Option<String> {
    while let Some(write) = rx.recv().await {
        match write {
            Write::Frame(frame, permit) => {
                if let Err(e) = send.write_all(&frame).await {
                    return Some(format!("connection lost: {e}"));
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
                return None;
            }
        }
    }
    Some("closed".into())
}

async fn read_loop(shared: &Shared, peer: &Peer, mut recv: RecvStream) -> String {
    loop {
        match read_frame(&mut recv, MAX_RELIABLE_FRAME).await {
            Ok(Some((kind, payload))) => {
                match kind {
                    KIND_DATA => {
                        if !shared.push_message(peer, CHANNEL_RELIABLE, Bytes::from(payload)) {
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
