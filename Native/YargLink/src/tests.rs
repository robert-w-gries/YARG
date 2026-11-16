//! Two handles in one process talking over localhost, through the exported C functions.
//! No relays and no DNS, so these run offline.

use std::{
    ffi::CString,
    ptr,
    time::{Duration, Instant},
};

use super::*;

const LOCAL_CONFIG: &str = r#"{
    "relay": false,
    "address_lookup": false,
    "bind_addr": "127.0.0.1:0",
    "ticket_addrs": true,
    "connect_timeout_ms": 5000,
    "log_filter": "warn,yarg_link=debug"
}"#;

struct Handle(*mut YlHandle);

impl Handle {
    fn new(config: &str) -> Handle {
        let config = CString::new(config).unwrap();
        let mut h = ptr::null_mut();
        let rc = unsafe { yl_create(config.as_ptr(), &mut h) };
        assert_eq!(rc, YL_OK, "yl_create: {}", last_error());
        assert!(!h.is_null());
        Handle(h)
    }

    fn ticket(&self) -> String {
        let mut len = 0usize;
        assert_eq!(unsafe { yl_local_ticket(self.0, ptr::null_mut(), 0, &mut len) }, YL_OK);
        let mut buf = vec![0u8; len];
        assert_eq!(unsafe { yl_local_ticket(self.0, buf.as_mut_ptr().cast(), len, &mut len) }, YL_OK);
        String::from_utf8(buf).unwrap()
    }

    fn connect(&self, ticket: &str) -> Result<u32, i32> {
        let ticket = CString::new(ticket).unwrap();
        let mut peer = 0;
        match unsafe { yl_connect(self.0, ticket.as_ptr(), &mut peer) } {
            YL_OK => Ok(peer),
            rc => Err(rc),
        }
    }

    fn send(&self, peer: u32, channel: u8, data: &[u8]) -> i32 {
        unsafe { yl_send(self.0, peer, channel, data.as_ptr(), data.len()) }
    }

    fn stats(&self, peer: u32) -> YlStats {
        let mut stats = YlStats::default();
        assert_eq!(unsafe { yl_peer_stats(self.0, peer, &mut stats) }, YL_OK);
        stats
    }

    /// Next non-log event, waiting up to `timeout`.
    fn next(&self, timeout: Duration) -> Option<(YlEvent, Vec<u8>)> {
        let deadline = Instant::now() + timeout;
        let mut buf = vec![0u8; 64];
        loop {
            let mut ev = YlEvent::default();
            match unsafe { yl_poll(self.0, &mut ev, buf.as_mut_ptr(), buf.len()) } {
                1 if ev.kind == YL_EVENT_LOG => {
                    println!("[log {}] {}", ev.level, String::from_utf8_lossy(&buf[..ev.len as usize]));
                }
                1 => return Some((ev, buf[..ev.len as usize].to_vec())),
                0 if Instant::now() >= deadline => return None,
                0 => std::thread::sleep(Duration::from_millis(5)),
                YL_ERR_BUFFER_TOO_SMALL => buf.resize(ev.len as usize, 0),
                rc => panic!("yl_poll returned {rc}: {}", last_error()),
            }
        }
    }

    fn expect(&self, kind: u32) -> (YlEvent, Vec<u8>) {
        let (ev, data) = self.next(Duration::from_secs(10)).unwrap_or_else(|| panic!("no event {kind}"));
        assert_eq!(ev.kind, kind, "unexpected event {ev:?} {:?}", String::from_utf8_lossy(&data));
        (ev, data)
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { yl_destroy(self.0) };
    }
}

fn last_error() -> String {
    let mut buf = vec![0u8; 512];
    let mut len = 0;
    unsafe { yl_last_error(buf.as_mut_ptr().cast(), buf.len(), &mut len) };
    buf.truncate(len.min(512));
    String::from_utf8_lossy(&buf).into_owned()
}

/// Connect `a` to `b`, returning (b's peer number at a, a's peer number at b).
fn pair(a: &Handle, b: &Handle) -> (u32, u32) {
    let at_a = a.connect(&b.ticket()).expect("connect");
    let (ev, _) = a.expect(YL_EVENT_PEER_CONNECTED);
    assert_eq!(ev.peer, at_a);
    let (ev, _) = b.expect(YL_EVENT_PEER_CONNECTED);
    (at_a, ev.peer)
}

#[test]
fn reliable_messages_arrive_in_order_both_ways() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, at_b) = pair(&a, &b);

    for i in 0..200u32 {
        assert_eq!(a.send(at_a, 0, &i.to_le_bytes()), YL_OK);
    }
    for i in 0..200u32 {
        let (ev, data) = b.expect(YL_EVENT_MESSAGE);
        assert_eq!((ev.peer, ev.channel), (at_b, 0));
        assert_eq!(data, i.to_le_bytes());
    }

    assert_eq!(b.send(at_b, 0, b"back"), YL_OK);
    let (_, data) = a.expect(YL_EVENT_MESSAGE);
    assert_eq!(data, b"back");
}

#[test]
fn large_reliable_message_needs_bigger_buffer() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, _) = pair(&a, &b);

    let big: Vec<u8> = (0..300_000u32).map(|i| i as u8).collect();
    assert_eq!(a.send(at_a, 0, &big), YL_OK);

    // A too-small buffer reports the size and leaves the event queued
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut ev = YlEvent::default();
    loop {
        let mut small = [0u8; 16];
        match unsafe { yl_poll(b.0, &mut ev, small.as_mut_ptr(), small.len()) } {
            1 if ev.kind == YL_EVENT_LOG => continue,
            YL_ERR_BUFFER_TOO_SMALL if ev.kind == YL_EVENT_LOG => {
                let mut line = vec![0u8; ev.len as usize];
                assert_eq!(unsafe { yl_poll(b.0, &mut ev, line.as_mut_ptr(), line.len()) }, 1);
            }
            YL_ERR_BUFFER_TOO_SMALL => break,
            0 if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(5)),
            rc => panic!("unexpected {rc} {ev:?}"),
        }
    }
    assert_eq!(ev.len as usize, big.len());

    let (_, data) = b.expect(YL_EVENT_MESSAGE);
    assert_eq!(data, big);
}

#[test]
fn unreliable_messages_arrive() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, _) = pair(&a, &b);

    // Datagrams may be lost, even on localhost; keep sending until one arrives
    let payload = vec![7u8; MAX_UNRELIABLE];
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        assert_eq!(a.send(at_a, 1, &payload), YL_OK);
        if let Some((ev, data)) = b.next(Duration::from_millis(50)) {
            assert_eq!((ev.kind, ev.channel), (YL_EVENT_MESSAGE, 1));
            assert_eq!(data, payload);
            break;
        }
        assert!(Instant::now() < deadline, "no datagram arrived");
    }

    assert_eq!(a.send(at_a, 1, &vec![0u8; MAX_UNRELIABLE + 1]), YL_ERR_TOO_LARGE);
    assert_eq!(a.send(at_a, 2, b"x"), YL_ERR_INVALID_ARG);
}

#[test]
fn stats_report_direct_path_and_rtt() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, _) = pair(&a, &b);
    a.send(at_a, 0, b"hi");
    b.expect(YL_EVENT_MESSAGE);

    let stats = a.stats(at_a);
    assert_eq!(stats.connected, 1);
    assert_eq!(stats.path, node::PATH_DIRECT);
    assert!(stats.rtt_us >= 0);
    assert!(stats.bytes_sent > 0);

    let unknown = a.stats(9999);
    assert_eq!(unknown.connected, 0);
}

#[test]
fn local_disconnect_is_reported_on_both_sides() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, at_b) = pair(&a, &b);

    assert_eq!(unsafe { yl_disconnect(a.0, at_a) }, YL_OK);
    let (ev, reason) = a.expect(YL_EVENT_PEER_DISCONNECTED);
    assert_eq!(ev.peer, at_a);
    assert_eq!(reason, b"disconnected locally");

    let (ev, reason) = b.expect(YL_EVENT_PEER_DISCONNECTED);
    assert_eq!(ev.peer, at_b);
    assert!(String::from_utf8_lossy(&reason).contains("closed by peer"), "{reason:?}");

    // Exactly once, and sends to it are dropped quietly
    assert_eq!(unsafe { yl_disconnect(a.0, at_a) }, YL_OK);
    assert_eq!(a.send(at_a, 0, b"gone"), YL_OK);
    assert!(a.next(Duration::from_millis(200)).is_none());
    assert_eq!(a.stats(at_a).connected, 0);
}

#[test]
fn destroying_a_handle_disconnects_its_peers() {
    let a = Handle::new(LOCAL_CONFIG);
    let b = Handle::new(LOCAL_CONFIG);
    let (_, at_b) = pair(&a, &b);

    drop(a);
    let (ev, _) = b.expect(YL_EVENT_PEER_DISCONNECTED);
    assert_eq!(ev.peer, at_b);
}

#[test]
fn failed_connect_is_reported() {
    let a = Handle::new(LOCAL_CONFIG);
    let gone = Handle::new(LOCAL_CONFIG);
    let ticket = gone.ticket();
    drop(gone);

    let config = LOCAL_CONFIG.replace("5000", "1500");
    let a2 = Handle::new(&config);
    let peer = a2.connect(&ticket).expect("connect starts");
    let (ev, reason) = a2.expect(YL_EVENT_PEER_DISCONNECTED);
    assert_eq!(ev.peer, peer);
    assert!(!reason.is_empty());
    drop(a);
}

impl Handle {
    fn peer_id(&self, peer: u32) -> Result<String, i32> {
        let mut len = 0usize;
        let rc = unsafe { yl_peer_id(self.0, peer, ptr::null_mut(), 0, &mut len) };
        if rc != YL_OK {
            assert_eq!(len, 0);
            return Err(rc);
        }
        let mut buf = vec![0u8; len];
        assert_eq!(unsafe { yl_peer_id(self.0, peer, buf.as_mut_ptr().cast(), len, &mut len) }, YL_OK);
        Ok(String::from_utf8(buf).unwrap())
    }

    /// Every log line queued so far, plus the other events.
    fn drain_logs(&self) -> Vec<String> {
        let mut lines = Vec::new();
        let mut buf = vec![0u8; 4096];
        loop {
            let mut ev = YlEvent::default();
            match unsafe { yl_poll(self.0, &mut ev, buf.as_mut_ptr(), buf.len()) } {
                1 => lines.push(String::from_utf8_lossy(&buf[..ev.len as usize]).into_owned()),
                0 => return lines,
                YL_ERR_BUFFER_TOO_SMALL => buf.resize(ev.len as usize, 0),
                rc => panic!("yl_poll returned {rc}"),
            }
        }
    }
}

/// The endpoint ID part of a ticket.
fn id_of(ticket: &str) -> &str {
    ticket.split('@').next().unwrap()
}

#[test]
fn peer_id_is_the_other_sides_endpoint_id() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, at_b) = pair(&a, &b);
    assert_eq!(a.peer_id(at_a).unwrap(), id_of(&b.ticket()));
    assert_eq!(b.peer_id(at_b).unwrap(), id_of(&a.ticket()));

    // A short buffer still reports the full length
    let mut len = 0usize;
    let mut small = [0u8; 4];
    assert_eq!(unsafe { yl_peer_id(a.0, at_a, small.as_mut_ptr().cast(), small.len(), &mut len) }, YL_OK);
    assert_eq!(len, 64);

    assert_eq!(a.peer_id(9999), Err(YL_ERR_INVALID_ARG));
    assert_eq!(unsafe { yl_disconnect(a.0, at_a) }, YL_OK);
    a.expect(YL_EVENT_PEER_DISCONNECTED);
    assert_eq!(a.peer_id(at_a), Err(YL_ERR_INVALID_ARG));
}

#[test]
fn every_handle_has_a_new_identity() {
    // Invite codes stop working with their session, and joiners can't be recognised later
    let first = Handle::new(LOCAL_CONFIG);
    let first_id = id_of(&first.ticket()).to_string();
    drop(first);
    let second = Handle::new(LOCAL_CONFIG);
    assert_ne!(id_of(&second.ticket()), first_id);

    // An old key_file setting is ignored rather than bringing the stable identity back
    let dir = std::env::temp_dir().join(format!("yarg-link-test-key-{}", std::process::id()));
    let key = dir.join("secret.key");
    let config = LOCAL_CONFIG.replacen('{', &format!("{{\"key_file\": {:?},", key.to_str().unwrap()), 1);
    let (x, y) = (Handle::new(&config), Handle::new(&config));
    assert_ne!(x.ticket(), y.ticket());
    assert!(!key.exists());
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn logs_do_not_contain_full_endpoint_ids() {
    let config = LOCAL_CONFIG.replace("warn,yarg_link=debug", "trace");
    let (a, b) = (Handle::new(&config), Handle::new(&config));
    let (at_a, _) = pair(&a, &b);
    a.send(at_a, 0, b"hi");
    b.expect(YL_EVENT_MESSAGE);
    unsafe { yl_disconnect(a.0, at_a) };
    std::thread::sleep(Duration::from_millis(300));

    let ids = [id_of(&a.ticket()).to_string(), id_of(&b.ticket()).to_string()];
    for line in a.drain_logs().into_iter().chain(b.drain_logs()) {
        for id in &ids {
            assert!(!line.contains(id.as_str()), "log line has a full endpoint ID: {line}");
        }
    }
}

#[test]
fn bad_input_is_rejected() {
    let a = Handle::new(LOCAL_CONFIG);
    assert_eq!(a.connect("loopback:127.0.0.1:36120"), Err(YL_ERR_BAD_TICKET));
    assert!(last_error().contains("endpoint ID"), "{}", last_error());
    assert_eq!(a.connect(&a.ticket()), Err(YL_ERR_BAD_TICKET));

    let mut h = ptr::null_mut();
    let bad = CString::new("{ not json").unwrap();
    assert_eq!(unsafe { yl_create(bad.as_ptr(), &mut h) }, YL_ERR_BAD_CONFIG);
    assert!(h.is_null());

    let filter = CString::new(r#"{"log_filter": "=[="}"#).unwrap();
    assert_eq!(unsafe { yl_create(filter.as_ptr(), &mut h) }, YL_ERR_BAD_CONFIG);

    assert_eq!(unsafe { yl_send(ptr::null_mut(), 1, 0, ptr::null(), 0) }, YL_ERR_INVALID_ARG);
    unsafe { yl_destroy(ptr::null_mut()) };
}

#[test]
fn several_peers_at_once() {
    let host = Handle::new(LOCAL_CONFIG);
    let joiners: Vec<_> = (0..3).map(|_| Handle::new(LOCAL_CONFIG)).collect();
    let ticket = host.ticket();

    let mut at_joiners = Vec::new();
    for j in &joiners {
        at_joiners.push(j.connect(&ticket).unwrap());
    }
    let mut at_host = Vec::new();
    for _ in &joiners {
        at_host.push(host.expect(YL_EVENT_PEER_CONNECTED).0.peer);
    }
    for j in &joiners {
        j.expect(YL_EVENT_PEER_CONNECTED);
    }

    for (j, peer) in joiners.iter().zip(&at_joiners) {
        j.send(*peer, 0, &peer.to_le_bytes());
    }
    let mut seen = Vec::new();
    for _ in &joiners {
        let (ev, _) = host.expect(YL_EVENT_MESSAGE);
        seen.push(ev.peer);
    }
    seen.sort();
    at_host.sort();
    assert_eq!(seen, at_host);
}

#[test]
fn messages_sent_right_before_disconnect_still_arrive() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, _) = pair(&a, &b);

    let big = vec![9u8; 200_000];
    assert_eq!(a.send(at_a, 0, &big), YL_OK);
    assert_eq!(a.send(at_a, 0, b"bye"), YL_OK);
    assert_eq!(unsafe { yl_disconnect(a.0, at_a) }, YL_OK);
    a.expect(YL_EVENT_PEER_DISCONNECTED);
    assert!(a.next(Duration::from_millis(100)).is_none(), "nothing after the disconnect");

    let (_, data) = b.expect(YL_EVENT_MESSAGE);
    assert_eq!(data.len(), big.len());
    let (_, data) = b.expect(YL_EVENT_MESSAGE);
    assert_eq!(data, b"bye");
    b.expect(YL_EVENT_PEER_DISCONNECTED);
}

#[test]
fn messages_sent_right_before_destroy_still_arrive() {
    let a = Handle::new(LOCAL_CONFIG);
    let b = Handle::new(LOCAL_CONFIG);
    let (at_a, _) = pair(&a, &b);

    assert_eq!(a.send(at_a, 0, b"bye"), YL_OK);
    let started = Instant::now();
    drop(a);
    assert!(started.elapsed() < Duration::from_secs(3), "destroy took {:?}", started.elapsed());

    let (_, data) = b.expect(YL_EVENT_MESSAGE);
    assert_eq!(data, b"bye");
    b.expect(YL_EVENT_PEER_DISCONNECTED);
}

/// A misbehaving peer, speaking the wire format by hand.
struct RawPeer {
    runtime: tokio::runtime::Runtime,
    conn: iroh::endpoint::Connection,
    send: iroh::endpoint::SendStream,
    recv: iroh::endpoint::RecvStream,
    _endpoint: iroh::Endpoint,
}

impl RawPeer {
    /// Connect and open the stream, without sending anything on it yet.
    fn connect(ticket: &str) -> RawPeer {
        let runtime = tokio::runtime::Builder::new_multi_thread().worker_threads(1).enable_all().build().unwrap();
        let addr = ticket::parse(ticket).unwrap();
        let (endpoint, conn, send, recv) = runtime.block_on(async {
            let endpoint = iroh::Endpoint::builder(iroh::endpoint::presets::Minimal)
                .relay_mode(iroh::RelayMode::Disabled)
                .clear_ip_transports()
                .bind_addr("127.0.0.1:0".parse::<std::net::SocketAddr>().unwrap())
                .unwrap()
                .bind()
                .await
                .unwrap();
            let conn = endpoint.connect(addr, node::ALPN).await.unwrap();
            let (send, recv) = conn.open_bi().await.unwrap();
            (endpoint, conn, send, recv)
        });
        RawPeer { runtime, conn, send, recv, _endpoint: endpoint }
    }

    fn write(&mut self, bytes: &[u8]) {
        let send = &mut self.send;
        // The peer may already have hung up; that's what some tests are after
        let _ = self.runtime.block_on(send.write_all(bytes));
    }

    fn hello(&mut self) {
        self.write(&[3, 0, 0, 0, 0, 1, 0]);
    }

    /// Whether the other side closes the connection within `timeout`.
    fn closed_within(&mut self, timeout: Duration) -> bool {
        let (conn, recv) = (&self.conn, &mut self.recv);
        self.runtime.block_on(async {
            tokio::time::timeout(timeout, async {
                // Drain whatever it sent (its hello) until the stream or connection ends
                let mut buf = [0u8; 256];
                while let Ok(Some(_)) = recv.read(&mut buf).await {}
                conn.closed().await;
            })
            .await
            .is_ok()
        })
    }
}

#[test]
fn oversize_frame_closes_the_connection() {
    let host = Handle::new(LOCAL_CONFIG);
    let mut raw = RawPeer::connect(&host.ticket());
    raw.hello();
    host.expect(YL_EVENT_PEER_CONNECTED);

    // Only the header: the host has to give up on the length alone, before any payload arrives
    raw.write(&((MAX_FRAME + 1) as u32).to_le_bytes());
    let (_, reason) = host.expect(YL_EVENT_PEER_DISCONNECTED);
    let reason = String::from_utf8_lossy(&reason).into_owned();
    assert!(reason.contains("over the"), "{reason}");
    assert!(raw.closed_within(Duration::from_secs(5)));
}

#[test]
fn oversize_hello_is_refused() {
    let host = Handle::new(LOCAL_CONFIG);
    let mut raw = RawPeer::connect(&host.ticket());
    let mut frame = 1000u32.to_le_bytes().to_vec();
    frame.push(0);
    frame.extend_from_slice(&[0u8; 999]);
    raw.write(&frame);
    assert!(raw.closed_within(Duration::from_secs(5)));
    assert!(host.next(Duration::from_millis(200)).is_none(), "never reported as connected");
}

#[test]
fn reliable_payload_limit() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, _) = pair(&a, &b);
    assert_eq!(a.send(at_a, 0, &vec![0u8; MAX_FRAME]), YL_ERR_TOO_LARGE);

    let biggest = vec![5u8; MAX_FRAME - 1];
    assert_eq!(a.send(at_a, 0, &biggest), YL_OK);
    let (_, data) = b.expect(YL_EVENT_MESSAGE);
    assert_eq!(data.len(), biggest.len());
}

#[test]
fn join_only_handle_refuses_incoming() {
    let joiner = Handle::new(&LOCAL_CONFIG.replace("\"relay\"", "\"accept\": false, \"relay\""));
    let other = Handle::new(LOCAL_CONFIG);
    let peer = other.connect(&joiner.ticket()).unwrap();
    let (ev, _) = other.expect(YL_EVENT_PEER_DISCONNECTED);
    assert_eq!(ev.peer, peer);
    assert!(joiner.next(Duration::from_millis(200)).is_none());

    // It can still connect out
    let host = Handle::new(LOCAL_CONFIG);
    pair(&joiner, &host);
}

#[test]
fn incoming_connections_are_capped() {
    let host = Handle::new(&LOCAL_CONFIG.replace("\"relay\"", "\"max_peers\": 2, \"relay\""));
    let ticket = host.ticket();
    let mut joiners: Vec<_> = (0..3).map(|_| Handle::new(LOCAL_CONFIG)).collect();

    for j in &joiners[..2] {
        j.connect(&ticket).unwrap();
        j.expect(YL_EVENT_PEER_CONNECTED);
        host.expect(YL_EVENT_PEER_CONNECTED);
    }
    joiners[2].connect(&ticket).unwrap();
    joiners[2].expect(YL_EVENT_PEER_DISCONNECTED);
    assert!(host.next(Duration::from_millis(200)).is_none());

    // A free slot can be used again
    drop(joiners.remove(0));
    host.expect(YL_EVENT_PEER_DISCONNECTED);
    let late = Handle::new(LOCAL_CONFIG);
    late.connect(&ticket).unwrap();
    late.expect(YL_EVENT_PEER_CONNECTED);
}

#[test]
fn flooding_peer_is_disconnected() {
    let (a, b) = (Handle::new(LOCAL_CONFIG), Handle::new(LOCAL_CONFIG));
    let (at_a, at_b) = pair(&a, &b);

    // b doesn't poll, as if the game were busy, while a keeps sending
    let limit = 16 * 1024;
    for i in 0..limit + 100 {
        if a.send(at_a, 0, &(i as u32).to_le_bytes()) != YL_OK {
            break;
        }
    }
    a.expect(YL_EVENT_PEER_DISCONNECTED);

    let mut messages = 0;
    loop {
        let (ev, _) = b.next(Duration::from_secs(10)).expect("an event");
        match ev.kind {
            YL_EVENT_MESSAGE => messages += 1,
            YL_EVENT_PEER_DISCONNECTED => {
                assert_eq!(ev.peer, at_b);
                break;
            }
            kind => panic!("unexpected event {kind}"),
        }
    }
    assert_eq!(messages, limit, "everything up to the limit was kept");
}

/// Over the internet: a bare endpoint ID, found through n0's DNS, with n0's relays available.
/// Needs network access, so it only runs on request: `cargo test -- --ignored`.
#[test]
#[ignore]
fn internet_by_endpoint_id() {
    let config = r#"{"connect_timeout_ms": 30000, "log_filter": "warn,yarg_link=debug"}"#;
    let (a, b) = (Handle::new(config), Handle::new(config));
    let ticket = b.ticket();
    assert!(!ticket.contains('@'), "bare endpoint ID: {ticket}");

    let started = Instant::now();
    let at_a = a.connect(&ticket).unwrap();
    let (ev, reason) = a.next(Duration::from_secs(40)).expect("an event");
    assert_eq!(ev.kind, YL_EVENT_PEER_CONNECTED, "{}", String::from_utf8_lossy(&reason));
    println!("connected in {:?}", started.elapsed());
    b.expect(YL_EVENT_PEER_CONNECTED);

    assert_eq!(a.send(at_a, 0, b"over the internet"), YL_OK);
    let (_, data) = b.expect(YL_EVENT_MESSAGE);
    assert_eq!(data, b"over the internet");

    let stats = a.stats(at_a);
    println!("path {} rtt {} us", stats.path, stats.rtt_us);
}
