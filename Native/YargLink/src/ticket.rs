//! Tickets: what one game hands another to connect.
//!
//! A ticket is an endpoint ID, optionally followed by `@` and a comma-separated list of direct
//! addresses (`192.0.2.1:4000`, `[2001:db8::1]:4000`) and relay URLs (`https://...`):
//!
//! ```text
//! 3b6a...e1f0
//! 3b6a...e1f0@192.168.1.20:51234,https://euw1-1.relay.iroh.network./
//! ```
//!
//! A bare endpoint ID is short and never goes stale, so it makes a good invite code; iroh finds
//! the addresses through n0's DNS. Addresses help when that lookup is off (tests, LAN-only).

use std::{net::SocketAddr, str::FromStr};

use iroh::{EndpointAddr, EndpointId, RelayUrl, TransportAddr};

pub fn parse(ticket: &str) -> Result<EndpointAddr, String> {
    let ticket = ticket.trim();
    if ticket.is_empty() {
        return Err("empty ticket".into());
    }

    let (id, addrs) = match ticket.split_once('@') {
        Some((id, addrs)) => (id, Some(addrs)),
        None => (ticket, None),
    };
    let id = EndpointId::from_str(id.trim()).map_err(|e| format!("not an endpoint ID ({e}): {id}"))?;

    let mut addr = EndpointAddr::new(id);
    for part in addrs.into_iter().flat_map(|a| a.split(',')).map(str::trim) {
        if part.is_empty() {
            continue;
        }
        if part.starts_with("http://") || part.starts_with("https://") {
            let url = RelayUrl::from_str(part).map_err(|e| format!("bad relay URL ({e}): {part}"))?;
            addr = addr.with_relay_url(url);
        } else {
            let ip = SocketAddr::from_str(part).map_err(|e| format!("bad address ({e}): {part}"))?;
            addr = addr.with_ip_addr(ip);
        }
    }
    Ok(addr)
}

pub fn format(addr: &EndpointAddr, with_addrs: bool) -> String {
    let mut out = addr.id.to_string();
    if !with_addrs {
        return out;
    }

    let parts: Vec<String> = addr
        .addrs
        .iter()
        .filter_map(|a| match a {
            TransportAddr::Ip(ip) => Some(ip.to_string()),
            TransportAddr::Relay(url) => Some(url.to_string()),
            _ => None,
        })
        .collect();
    if !parts.is_empty() {
        out.push('@');
        out.push_str(&parts.join(","));
    }
    out
}

#[cfg(test)]
mod tests {
    use iroh::SecretKey;

    use super::*;

    #[test]
    fn bare_id_round_trips() {
        let id = SecretKey::generate().public();
        let ticket = format(&EndpointAddr::new(id), true);
        assert_eq!(ticket, id.to_string());
        assert_eq!(parse(&ticket).unwrap(), EndpointAddr::new(id));
    }

    #[test]
    fn addresses_round_trip() {
        let id = SecretKey::generate().public();
        let addr = EndpointAddr::new(id)
            .with_ip_addr("127.0.0.1:4000".parse().unwrap())
            .with_ip_addr("[::1]:4001".parse().unwrap())
            .with_relay_url("https://relay.example.com./".parse().unwrap());
        let ticket = format(&addr, true);
        assert!(ticket.starts_with(&format!("{id}@")));
        assert_eq!(parse(&ticket).unwrap(), addr);
        assert_eq!(format(&addr, false), id.to_string());
    }

    #[test]
    fn whitespace_is_ignored() {
        let id = SecretKey::generate().public();
        let parsed = parse(&format!("  {id} @ 127.0.0.1:4000 , \n")).unwrap();
        assert_eq!(parsed, EndpointAddr::new(id).with_ip_addr("127.0.0.1:4000".parse().unwrap()));
    }

    #[test]
    fn rejects_garbage() {
        assert!(parse("").is_err());
        assert!(parse("loopback:127.0.0.1:36120").is_err());
        assert!(parse("not-a-key").is_err());
        let id = SecretKey::generate().public();
        assert!(parse(&format!("{id}@nowhere")).is_err());
    }
}
