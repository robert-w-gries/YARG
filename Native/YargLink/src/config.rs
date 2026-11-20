//! The configuration of `yl_create`: the JSON members, their defaults, and the checks on them.
//!
//! `yl_create` does not use the defaults in place of a bad configuration. It returns
//! `YL_ERR_BAD_CONFIG`. A default can be less safe than what the caller asked for (for example
//! `accept`), so a silent fallback can hide a problem.

use std::{net::SocketAddr, time::Duration};

use serde::Deserialize;

use crate::{
    error::{Result, YlError},
    log,
};

/// The default `connect_timeout_ms`: 20 seconds.
///
/// A connect can need this much time when the host started a short time ago. Then the joiner
/// must wait until the host's addresses are in n0's DNS (some seconds), and then hole punching
/// can take some more seconds, before the relay is used. A connect that takes longer than 20 s
/// usually does not succeed, and a player who waits longer thinks that the game stopped. A
/// caller with other needs gives its own value.
const DEFAULT_CONNECT_TIMEOUT_MS: u64 = 20_000;

/// The longest `connect_timeout_ms`: 10 minutes. A longer value is almost certainly a mistake.
const MAX_CONNECT_TIMEOUT_MS: u64 = 10 * 60 * 1000;

/// The default `max_peers`: 16.
///
/// YARG gives its own value (refer to `LinkController.TransportMaxPeers`), so this default is for
/// other callers and for tests. 16 is more than a group of friends needs, and it limits the cost
/// of a host that is full: each peer has its own tasks and connection, and up to 32 MiB of
/// messages can wait in each direction for each peer.
const DEFAULT_MAX_PEERS: usize = 16;

/// The most `max_peers`. Each peer has a connection and tasks, so a very large value is a mistake.
const MAX_MAX_PEERS: usize = 1024;

/// The longest `log_filter`, in bytes.
const MAX_LOG_FILTER_LEN: usize = 1024;

/// The JSON object, as the caller gives it. `Config::from_json` checks it.
#[derive(Debug, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct ConfigJson {
    relay: bool,
    address_lookup: bool,
    bind_addr: Option<String>,
    ticket_addrs: bool,
    connect_timeout_ms: u64,
    log_filter: String,
    accept: bool,
    max_peers: usize,
}

impl Default for ConfigJson {
    fn default() -> Self {
        Self {
            relay: true,
            address_lookup: true,
            bind_addr: None,
            ticket_addrs: false,
            connect_timeout_ms: DEFAULT_CONNECT_TIMEOUT_MS,
            log_filter: log::DEFAULT_FILTER.into(),
            accept: false,
            max_peers: DEFAULT_MAX_PEERS,
        }
    }
}

/// A checked configuration. Refer to `yl_create` in `yarg_link.h` for each member.
#[derive(Debug, Clone)]
pub struct Config {
    /// Use n0's public relays when there is no direct path.
    pub relay: bool,
    /// Publish this endpoint's addresses in n0's DNS, and find other endpoints there. With this,
    /// a ticket can be only an endpoint ID.
    pub address_lookup: bool,
    /// One socket at this address, in place of iroh's default sockets. `None` (the JSON has no
    /// `bind_addr`) keeps iroh's default sockets: one IPv4 and one IPv6 socket on all network
    /// interfaces, each on a random free port. Refer to `Node::bind`.
    pub bind_addr: Option<SocketAddr>,
    /// Put the direct addresses and the relay URL in `yl_local_ticket`.
    pub ticket_addrs: bool,
    pub connect_timeout: Duration,
    pub log_filter: String,
    /// Accept incoming connections. Only a game that hosts sets this.
    pub accept: bool,
    /// The most peers at once, with the incoming connections that are still in their handshake.
    pub max_peers: usize,
}

impl Default for Config {
    fn default() -> Self {
        Self::check(ConfigJson::default()).expect("the default configuration passes its checks")
    }
}

impl Config {
    /// Read and check the JSON object of `yl_create`. Empty text gives the defaults.
    pub fn from_json(text: &str) -> Result<Config> {
        if text.trim().is_empty() {
            return Ok(Config::default());
        }
        let json: ConfigJson = serde_json::from_str(text).map_err(|e| YlError::bad_config(format!("bad config: {e}")))?;
        Self::check(json)
    }

    fn check(json: ConfigJson) -> Result<Config> {
        // Without address lookup, a ticket that is only an endpoint ID cannot be found. Thus the
        // ticket must have the addresses.
        if !json.address_lookup && !json.ticket_addrs {
            return Err(YlError::bad_config(
                "address_lookup is off, so ticket_addrs must be on: else no game can connect with this game's ticket",
            ));
        }

        if json.connect_timeout_ms == 0 || json.connect_timeout_ms > MAX_CONNECT_TIMEOUT_MS {
            return Err(YlError::bad_config(format!(
                "connect_timeout_ms must be from 1 to {MAX_CONNECT_TIMEOUT_MS}, not {}",
                json.connect_timeout_ms
            )));
        }

        if json.max_peers == 0 || json.max_peers > MAX_MAX_PEERS {
            return Err(YlError::bad_config(format!(
                "max_peers must be from 1 to {MAX_MAX_PEERS}, not {}",
                json.max_peers
            )));
        }

        if json.log_filter.len() > MAX_LOG_FILTER_LEN {
            return Err(YlError::bad_config(format!("log_filter is longer than {MAX_LOG_FILTER_LEN} bytes")));
        }

        // No bind_addr is correct, and is not an error: then the endpoint uses iroh's default
        // sockets (refer to Config::bind_addr). A bind_addr must be an IP address with a port.
        let bind_addr = json.bind_addr.as_deref().map(parse_bind_addr).transpose()?;

        Ok(Config {
            relay: json.relay,
            address_lookup: json.address_lookup,
            bind_addr,
            ticket_addrs: json.ticket_addrs,
            connect_timeout: Duration::from_millis(json.connect_timeout_ms),
            log_filter: json.log_filter,
            accept: json.accept,
            max_peers: json.max_peers,
        })
    }
}

fn parse_bind_addr(text: &str) -> Result<SocketAddr> {
    text.parse::<SocketAddr>()
        .map_err(|e| YlError::bad_config(format!("bad bind_addr \"{text}\": {e}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::YL_ERR_BAD_CONFIG;

    fn error(json: &str) -> String {
        let e = Config::from_json(json).unwrap_err();
        assert_eq!(e.code, YL_ERR_BAD_CONFIG);
        e.message
    }

    #[test]
    fn defaults() {
        let config = Config::from_json("  ").unwrap();
        assert!(config.relay && config.address_lookup && !config.ticket_addrs);
        assert!(!config.accept, "only a host accepts connections, and it must ask for that");
        assert_eq!(config.max_peers, DEFAULT_MAX_PEERS);
        assert_eq!(config.connect_timeout, Duration::from_millis(DEFAULT_CONNECT_TIMEOUT_MS));
        assert!(config.bind_addr.is_none());
    }

    #[test]
    fn lookup_off_needs_ticket_addresses() {
        assert!(error(r#"{"address_lookup": false}"#).contains("ticket_addrs"));
        assert!(Config::from_json(r#"{"address_lookup": false, "ticket_addrs": true}"#).is_ok());
    }

    #[test]
    fn values_out_of_range_are_refused() {
        assert!(error(r#"{"connect_timeout_ms": 0}"#).contains("connect_timeout_ms"));
        assert!(error(r#"{"connect_timeout_ms": 600001}"#).contains("connect_timeout_ms"));
        assert!(error(r#"{"max_peers": 0}"#).contains("max_peers"));
        assert!(error(r#"{"max_peers": 1025}"#).contains("max_peers"));
        assert!(error(&format!(r#"{{"log_filter": "{}"}}"#, "a".repeat(1025))).contains("log_filter"));
        assert!(error(r#"{"bind_addr": "nowhere"}"#).contains("bind_addr"));
    }

    #[test]
    fn unknown_members_are_refused() {
        // A misspelt member must not silently become its default
        assert!(error(r#"{"acept": true}"#).contains("acept"));
    }

    #[test]
    fn bad_json_is_refused() {
        error("{");
        error(r#"{"relay": "yes"}"#);
    }
}
