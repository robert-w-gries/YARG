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
//! A bare endpoint ID is short and survives address changes, so it makes a good invite code; iroh finds
//! the addresses through n0's DNS. Addresses help when that lookup is off (tests, LAN-only).
//!
//! Errors never include the ticket: it's an invite code, and what the player pasted may not even
//! be one (anything from the clipboard), and errors end up in logs.
//!
//! A ticket comes from outside the game (the clipboard, a Discord invite), so `parse` checks all of
//! it. The length and the number of addresses have limits, the endpoint ID must be a valid key, and
//! each address must be a socket address or a relay URL. Anything else is an error.

use std::{net::SocketAddr, str::FromStr};

use iroh::{EndpointAddr, EndpointId, RelayUrl, TransportAddr};

/// The longest ticket, in bytes. A ticket with a few addresses and a relay URL is about 300 bytes.
pub const MAX_TICKET_LEN: usize = 4096;

/// The most addresses (direct addresses and relay URLs together) in one ticket.
pub const MAX_TICKET_ADDRS: usize = 32;

/// Read a ticket. Spaces around the parts are ignored.
pub fn parse(ticket: &str) -> Result<EndpointAddr, String> {
    if ticket.len() > MAX_TICKET_LEN {
        return Err(format!("ticket is longer than {MAX_TICKET_LEN} bytes"));
    }
    let ticket = ticket.trim();
    if ticket.is_empty() {
        return Err("empty ticket".into());
    }

    let (id, addrs) = match ticket.split_once('@') {
        Some((id, addrs)) => (id, Some(addrs)),
        None => (ticket, None),
    };
    let id = EndpointId::from_str(id.trim()).map_err(|e| format!("not an endpoint ID ({e})"))?;

    let mut addr = EndpointAddr::new(id);
    let parts = addrs.into_iter().flat_map(|a| a.split(',')).map(str::trim).filter(|p| !p.is_empty());
    for (count, part) in parts.enumerate() {
        if count >= MAX_TICKET_ADDRS {
            return Err(format!("ticket has more than {MAX_TICKET_ADDRS} addresses"));
        }
        addr = match TicketPart::parse(part)? {
            TicketPart::Relay(url) => addr.with_relay_url(url),
            TicketPart::Direct(ip) => addr.with_ip_addr(ip),
        };
    }
    Ok(addr)
}

/// One address of a ticket, after `@`.
enum TicketPart {
    Relay(RelayUrl),
    Direct(SocketAddr),
}

impl TicketPart {
    fn parse(part: &str) -> Result<TicketPart, String> {
        if part.starts_with("http://") || part.starts_with("https://") {
            return RelayUrl::from_str(part).map(TicketPart::Relay).map_err(|e| format!("bad relay URL ({e})"));
        }
        // An IPv4 or IPv6 address with a port. The parser accepts no other text, for example no host
        // names. The address is not trusted after this: iroh only sends QUIC packets to it, and the
        // TLS handshake checks that the peer has the key of the endpoint ID.
        if let Ok(ip) = SocketAddr::from_str(part) {
            return Ok(TicketPart::Direct(ip));
        }
        Err("an address is not an IP address with a port or a relay URL".into())
    }
}

/// Make the ticket for `addr`.
///
/// When `with_addrs` is false, the ticket is only the endpoint ID. Another game then finds the
/// addresses through n0's DNS (`address_lookup`).
///
/// When `with_addrs` is true, the ticket is the endpoint ID, then `@`, then the direct addresses and
/// relay URL of `addr`, with commas between them. Another game can then connect without DNS. If
/// `addr` has no addresses, the ticket is only the endpoint ID. The ticket can be long, and it has
/// the local IP addresses of this computer.
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
        assert!(parse(&format!("{id}@localhost:4000")).is_err(), "host names are not addresses");
    }

    #[test]
    fn size_limits() {
        let id = SecretKey::generate().public();
        let many = vec!["127.0.0.1:4000"; MAX_TICKET_ADDRS].join(",");
        assert!(parse(&format!("{id}@{many}")).is_ok());
        assert!(parse(&format!("{id}@{many},127.0.0.1:4001")).unwrap_err().contains("addresses"));
        let long = format!("{id}{}", " ".repeat(MAX_TICKET_LEN));
        assert!(parse(&long).unwrap_err().contains("longer"));
    }

    #[test]
    fn errors_do_not_repeat_the_ticket() {
        let id = SecretKey::generate().public().to_string();
        for bad in [
            "hunter2-correct-horse-battery-staple".to_string(),
            format!("{id}@secret-host-name"),
            format!("{id}@https://user:password@"),
            format!("{}x", &id[..63]),
        ] {
            let error = parse(&bad).unwrap_err();
            assert!(!error.contains("hunter2") && !error.contains("secret-host") && !error.contains("password"), "{error}");
            assert!(!error.contains(&id[..16]), "{error}");
        }
    }
}
