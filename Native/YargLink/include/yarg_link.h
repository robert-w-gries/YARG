/*
 * YargLink: the peer-to-peer transport behind YARG's online play.
 *
 * A thin, game-agnostic pipe over iroh (QUIC with hole punching and relay fallback): connect to
 * another game by ticket, send reliable or unreliable bytes, poll for events. All message meaning
 * lives in the game (Assets/Script/Integration/Link/Session).
 *
 * Threading: every function may be called from any thread, but the game calls them all from
 * Unity's main thread. Networking runs on the library's own threads (one tokio runtime per handle).
 * The library never calls back into the caller; everything it has to say arrives via yl_poll.
 *
 * Errors: every function returns a status code (YL_OK or a negative YL_ERR_*), never unwinds, and
 * never aborts. After a failure, yl_last_error() describes it (per calling thread).
 *
 * Handles: a yl_handle pointer is not a memory address. It is a number that yl_create gives. The
 * library never reads or writes memory through it, so any value is safe to pass: NULL, a random
 * value or a destroyed handle gives YL_ERR_INVALID_ARG or YL_ERR_CLOSED.
 *
 * Pointer arguments: the library checks each pointer for NULL first, before it does any other
 * work. Only yl_create's config_json may be NULL, and yl_send's data when len is 0. The caller
 * makes sure that each other pointer is valid for the size that the function gives.
 *
 * Text and data outputs (yl_last_error, yl_local_ticket, yl_peer_id, yl_poll) use one rule: the
 * caller gives a buffer and its size in bytes (cap), and the library gives the length that it
 * needs (*len, or ev->len for yl_poll). The output is NOT null-terminated. Reasons:
 *   - yl_poll gives binary messages, which can contain zero bytes, so it needs a length. The
 *     text functions use the same rule, so there is only one rule.
 *   - The length is always given, so the caller does not need a terminator to find the end.
 *   - The C# caller reads the bytes with their length (Encoding.UTF8.GetString(buf, 0, len)).
 * The caller has both parts of the output: the pointer is its own buf, and the length is *len. The
 * function does not return a pointer. The text is in buf, from buf[0], and it is cut short when
 * *len > cap. To make a C string, give a buffer that is 1 byte larger than cap, and after the call
 * write 0 at buf[n], where n is the smaller of *len and cap.
 * cap is an argument, not a constant, because the length of a ticket or an event is not fixed: a
 * ticket can have addresses, and a message can be up to YL_MAX_RELIABLE bytes. An endpoint ID
 * (yl_peer_id) is 64 bytes now, but the function does not promise that length.
 */
#ifndef YARG_LINK_H
#define YARG_LINK_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define YL_API __declspec(dllimport)
#else
#define YL_API
#endif

/* Bumped on any incompatible change to this header (or to what its functions promise). */
#define YL_ABI_VERSION 4

/* The largest payload YL_CHANNEL_UNRELIABLE takes, in bytes. It fits in one QUIC datagram on every
 * path. Real paths often allow a little more. */
#define YL_MAX_UNRELIABLE 1024

/* The largest payload YL_CHANNEL_RELIABLE takes, in bytes. A peer that sends more is disconnected. */
#define YL_MAX_RELIABLE (1024 * 1024 - 1)

/* Status codes */
#define YL_OK 0
#define YL_ERR_INVALID_ARG (-1)       /* null pointer, unknown handle, unknown channel, ... */
#define YL_ERR_BAD_CONFIG (-2)        /* yl_create's JSON is malformed, or a value is not permitted */
#define YL_ERR_BIND (-3)              /* couldn't open the endpoint's sockets */
#define YL_ERR_BAD_TICKET (-4)        /* yl_connect's ticket can't be parsed or used */
#define YL_ERR_BUFFER_TOO_SMALL (-5)  /* the size needed is reported; nothing was consumed */
#define YL_ERR_TOO_LARGE (-6)         /* payload over YL_MAX_UNRELIABLE or YL_MAX_RELIABLE */
#define YL_ERR_CLOSED (-7)            /* yl_destroy closed this handle */
#define YL_ERR_PANIC (-8)             /* a bug inside the library; the handle may be unusable */
#define YL_ERR_INTERNAL (-9)

/* Channels */
#define YL_CHANNEL_RELIABLE 0   /* exactly once, in order with other reliable messages to that peer */
#define YL_CHANNEL_UNRELIABLE 1 /* may be lost, duplicated or reordered */

/* Event types */
#define YL_EVENT_PEER_CONNECTED 1    /* peer */
#define YL_EVENT_PEER_DISCONNECTED 2 /* peer; payload: reason (UTF-8) */
#define YL_EVENT_MESSAGE 3           /* peer, channel; payload: the message */
#define YL_EVENT_ERROR 4             /* payload: what went wrong (UTF-8); not tied to a peer */
#define YL_EVENT_LOG 5               /* log_level; payload: the log line (UTF-8) */

/*
 * Log levels (yl_event.log_level). Each log event has its own level, as each message event has its
 * own channel. The "log_filter" config of yl_create selects which lines become events; "off" gives
 * none. There is no YL_LOG_NONE: no event has level 0, and "no logs" is a filter, not a level.
 */
#define YL_LOG_ERROR 1
#define YL_LOG_WARN 2
#define YL_LOG_INFO 3
#define YL_LOG_DEBUG 4
#define YL_LOG_TRACE 5

/* Connection states (yl_stats.connected) */
#define YL_STATS_NOT_CONNECTED 0 /* unknown, still connecting, or closed */
#define YL_STATS_CONNECTED 1

/* Paths (yl_stats.path) */
#define YL_PATH_UNKNOWN 0
#define YL_PATH_DIRECT 1 /* hole-punched, LAN or loopback */
#define YL_PATH_RELAY 2

typedef struct yl_handle yl_handle;

typedef struct yl_event {
    uint32_t type;    /* YL_EVENT_* */
    uint32_t peer;    /* for peer events */
    uint32_t channel; /* for YL_EVENT_MESSAGE */
    uint32_t log_level; /* YL_LOG_*, for YL_EVENT_LOG */
    uint64_t len;     /* payload size in bytes */
} yl_event;

typedef struct yl_stats {
    uint32_t connected; /* YL_STATS_CONNECTED while the peer is connected, else YL_STATS_NOT_CONNECTED */
    uint32_t path;      /* YL_PATH_* */
    int64_t rtt_us;     /* round-trip time in microseconds, or -1 if not measured yet */
    uint64_t bytes_sent;     /* UDP payload bytes, including QUIC overhead */
    uint64_t bytes_received;
} yl_stats;

/* YL_ABI_VERSION of the loaded library. */
YL_API uint32_t yl_abi_version(void);

/*
 * Describe the last failure on this thread as UTF-8 (not null-terminated: refer to "Text and data
 * outputs" above). *len receives the full length, even when it's more than cap and the text is cut
 * short. buf and len must not be NULL. Returns YL_OK.
 */
YL_API int yl_last_error(char *buf, size_t cap, size_t *len);

/*
 * Open an endpoint. config_json is a JSON object (or NULL for the defaults) with these optional
 * members. An unknown member, a value of the wrong type or a value that is not permitted gives
 * YL_ERR_BAD_CONFIG. The library does not use the defaults in place of a bad config.
 *
 *   "relay"             Use n0's public relays when no direct path can be made (default true).
 *                       A relay passes the encrypted packets between two games. It cannot read
 *                       them.
 *   "address_lookup"    Use n0's DNS to find games by endpoint ID (default true). Each endpoint
 *                       publishes its direct addresses and its relay there, and yl_connect looks
 *                       up the endpoint ID of a ticket there. Thus a ticket can be only the
 *                       endpoint ID: 64 characters that fit in a Discord invite, and that stay
 *                       correct when the host's addresses change. Without it, a ticket must
 *                       have the addresses ("ticket_addrs").
 *   "bind_addr"         One socket at this address, for example "127.0.0.1:0" (port 0: a free
 *                       port). The default is iroh's: one IPv4 and one IPv6 socket on all network
 *                       interfaces, each on a random free port. All interfaces, so that a game
 *                       on the LAN, and hole punching on the internet, can find each address. A
 *                       random port, so that more than one game on a computer can run, and no
 *                       router or firewall needs a fixed port.
 *   "ticket_addrs"      Put the direct addresses and the relay URL in yl_local_ticket (default
 *                       false). Then a game can connect without DNS. The ticket is longer and
 *                       has this computer's IP addresses.
 *                       Rule: at least one of "address_lookup" and "ticket_addrs" must be on,
 *                       else no game can find this one from its ticket. Both can be on.
 *   "connect_timeout_ms" Stop a yl_connect after this long, from 1 to 600000 (default 20000).
 *   "log_filter"        The tracing filter for YL_EVENT_LOG, for example "warn,yarg_link=debug"
 *                       (default "warn,yarg_link=info"; "off" for none). At most 1024 bytes.
 *   "accept"            Accept incoming connections (default false). Only a game that hosts sets
 *                       this. A game that only joins others keeps it off, so nobody can connect
 *                       to it.
 *   "max_peers"         The most peers at once, from 1 to 1024 (default 16). This counts the
 *                       incoming connections that are still in their handshake. More incoming
 *                       connections are refused. This is a limit of the library, not the party
 *                       size of the game: YARG gives its own value (8 joiners, and 4 more that
 *                       are still saying hello).
 *
 * Incoming connections get at most 10 seconds (or connect_timeout_ms, if shorter) to handshake,
 * and only a few may handshake at once. A peer whose messages pile up unpolled (thousands of
 * them, or tens of megabytes) is disconnected; its datagrams are dropped instead.
 */
YL_API int yl_create(const char *config_json, yl_handle **out);
/*
 * out must not be NULL. On a failure, *out is NULL.
 * Every handle gets a new, random endpoint ID, and so a new ticket: a ticket stops working once its
 * handle is destroyed, and peers can't recognise this game from one handle to the next.
 */

/*
 * Close every connection, stop all threads and free the handle. NULL, an unknown handle and a
 * destroyed handle are ignored. Reliable messages already queued still go out first, so this blocks
 * for up to about 2.5 s when a peer is slow to read them; call it off a thread that must not stall
 * (a game's main thread, say). If another thread is in a call with this handle, this function
 * waits until that call returns. After this function starts, other calls with the handle give
 * YL_ERR_CLOSED.
 */
YL_API void yl_destroy(yl_handle *h);

/*
 * What another game passes to yl_connect to reach this one (UTF-8, not null-terminated: refer to
 * "Text and data outputs" above). buf and len must not be NULL. On a failure, *len is 0.
 */
YL_API int yl_local_ticket(yl_handle *h, char *buf, size_t cap, size_t *len);

/*
 * Start connecting (ticket: null-terminated UTF-8). Returns at once with the new peer's number in
 * *peer_out; the outcome arrives as YL_EVENT_PEER_CONNECTED or YL_EVENT_PEER_DISCONNECTED.
 * The ticket can come from anywhere (the clipboard, an invite), so the library checks all of it: at
 * most 4096 bytes, a valid endpoint ID, and at most 32 addresses, each an IP address with a port or
 * a relay URL. A bad ticket gives YL_ERR_BAD_TICKET, and the message never repeats the ticket.
 * Peer numbers start at 1. On a failure, *peer_out is 0.
 */
YL_API int yl_connect(yl_handle *h, const char *ticket, uint32_t *peer_out);

/*
 * Close a connection. A YL_EVENT_PEER_DISCONNECTED still follows. Unknown peers are ignored.
 */
YL_API int yl_disconnect(yl_handle *h, uint32_t peer);

/*
 * Queue a message. Sending to a peer that isn't connected (any more) is silently dropped; the
 * disconnect arrives via yl_poll. So is an unreliable message the current path can't carry.
 * A peer that stops reading is disconnected once 32 MiB (or 64k messages) of reliable data to it
 * waits to go out, so nothing buffers without bound.
 * data may be NULL only when len is 0 (an empty message). This is the same rule as memcpy's.
 */
YL_API int yl_send(yl_handle *h, uint32_t peer, uint8_t channel, const uint8_t *data, size_t len);

/*
 * Take the next event from the queue.
 *
 * ev and buf must not be NULL. cap is the size of buf in bytes. The function has 3 results:
 *   1                        There was an event. *ev has its fields, ev->len is the size of its
 *                            payload, and buf has the payload (not null-terminated). The event is
 *                            removed from the queue.
 *   0                        No event is waiting. *ev and buf do not change.
 *   YL_ERR_BUFFER_TOO_SMALL  There is an event, but its payload is larger than cap. *ev has its
 *                            fields, and ev->len is the size that the payload needs. The event
 *                            stays in the queue. Call yl_poll again with a buffer of at least
 *                            ev->len bytes.
 * Some events have no payload (YL_EVENT_PEER_CONNECTED, or an empty message). Then ev->len is 0.
 */
YL_API int yl_poll(yl_handle *h, yl_event *ev, uint8_t *buf, size_t cap);

/*
 * The peer's endpoint ID (UTF-8, not null-terminated: refer to "Text and data outputs" above), the
 * same text as the ID part of its ticket.
 * It's authenticated by the connection's handshake, so a peer can't claim another's ID, but anyone
 * can make a new one at will. *len receives the full length. buf and len must not be NULL.
 * Returns YL_ERR_INVALID_ARG (with *len set to 0) if the peer is unknown or its connection isn't up
 * yet; it is available from YL_EVENT_PEER_CONNECTED until the peer disconnects.
 */
YL_API int yl_peer_id(yl_handle *h, uint32_t peer, char *buf, size_t cap, size_t *len);

/*
 * Statistics for a peer. out must not be NULL. A peer that is not connected now (unknown, still
 * connecting, or disconnected) gives YL_OK with connected = YL_STATS_NOT_CONNECTED,
 * path = YL_PATH_UNKNOWN, rtt_us = -1 and byte counts of 0. It is not an error, because a peer can
 * disconnect at any moment.
 */
YL_API int yl_peer_stats(yl_handle *h, uint32_t peer, yl_stats *out);

#ifdef __cplusplus
}
#endif

#endif /* YARG_LINK_H */
