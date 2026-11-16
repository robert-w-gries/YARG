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
#define YL_ABI_VERSION 2

/* The largest payload YL_CHANNEL_UNRELIABLE takes. Real paths often allow a little more. */
#define YL_MAX_UNRELIABLE 1024

/* The largest payload YL_CHANNEL_RELIABLE takes. A peer that sends more is disconnected. */
#define YL_MAX_RELIABLE (1024 * 1024 - 1)

/* Status codes */
#define YL_OK 0
#define YL_ERR_INVALID_ARG (-1)       /* null pointer, bad UTF-8, unknown channel, ... */
#define YL_ERR_BAD_CONFIG (-2)        /* yl_create's JSON is malformed */
#define YL_ERR_BIND (-3)              /* couldn't open the endpoint's sockets */
#define YL_ERR_BAD_TICKET (-4)        /* yl_connect's ticket can't be parsed */
#define YL_ERR_BUFFER_TOO_SMALL (-5)  /* the size needed is reported; nothing was consumed */
#define YL_ERR_TOO_LARGE (-6)         /* payload over YL_MAX_UNRELIABLE or YL_MAX_RELIABLE */
#define YL_ERR_CLOSED (-7)            /* the handle is shutting down */
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
#define YL_EVENT_LOG 5               /* level; payload: the log line (UTF-8) */

/* Log levels (yl_event.level) */
#define YL_LOG_ERROR 1
#define YL_LOG_WARN 2
#define YL_LOG_INFO 3
#define YL_LOG_DEBUG 4
#define YL_LOG_TRACE 5

/* Paths (yl_stats.path) */
#define YL_PATH_UNKNOWN 0
#define YL_PATH_DIRECT 1 /* hole-punched, LAN or loopback */
#define YL_PATH_RELAY 2

typedef struct yl_handle yl_handle;

typedef struct yl_event {
    uint32_t type;    /* YL_EVENT_* */
    uint32_t peer;    /* for peer events */
    uint32_t channel; /* for YL_EVENT_MESSAGE */
    uint32_t level;   /* for YL_EVENT_LOG */
    uint64_t len;     /* payload size in bytes */
} yl_event;

typedef struct yl_stats {
    uint32_t connected; /* 1 while the peer is connected */
    uint32_t path;      /* YL_PATH_* */
    int64_t rtt_us;     /* round-trip time in microseconds, or -1 if not measured yet */
    uint64_t bytes_sent;     /* UDP payload bytes, including QUIC overhead */
    uint64_t bytes_received;
} yl_stats;

/* YL_ABI_VERSION of the loaded library. */
YL_API uint32_t yl_abi_version(void);

/*
 * Describe the last failure on this thread as UTF-8 (not null-terminated). *len receives the full
 * length, even when it's more than cap and the text is cut short. Returns YL_OK.
 */
YL_API int yl_last_error(char *buf, size_t cap, size_t *len);

/*
 * Open an endpoint. config_json is a JSON object (or NULL for the defaults) with these optional
 * members:
 *   "key_file"          path of the secret key, created if missing, so the endpoint ID (and so the
 *                       ticket) stays the same between runs. If another process holds the key, a
 *                       temporary one is used instead. Without it, every handle gets a new ID.
 *   "relay"             use n0's public relays (default true)
 *   "address_lookup"    publish/resolve addresses via n0's DNS, so bare endpoint IDs work as
 *                       tickets (default true)
 *   "bind_addr"         e.g. "127.0.0.1:0"; default is every interface on a random port
 *   "ticket_addrs"      put direct addresses in yl_local_ticket (default false; needed when
 *                       address_lookup is off)
 *   "connect_timeout_ms" give up on a yl_connect after this long (default 20000)
 *   "log_filter"        tracing filter for YL_EVENT_LOG, e.g. "warn,yarg_link=debug"
 *                       (default "warn,yarg_link=info"; "off" for none)
 *   "accept"            accept incoming connections (default true). A game that only joins
 *                       others turns this off, so nobody can connect to it.
 *   "max_peers"         the most peers at once, counting incoming connections that are still
 *                       handshaking; further incoming connections are refused (default 16)
 *
 * Incoming connections get at most 10 seconds (or connect_timeout_ms, if shorter) to handshake,
 * and only a few may handshake at once. A peer whose messages pile up unpolled (thousands of
 * them, or tens of megabytes) is disconnected; its datagrams are dropped instead.
 */
YL_API int yl_create(const char *config_json, yl_handle **out);

/* Close every connection, stop all threads and free the handle. NULL is ignored. */
YL_API void yl_destroy(yl_handle *h);

/* What another game passes to yl_connect to reach this one (UTF-8, not null-terminated). */
YL_API int yl_local_ticket(yl_handle *h, char *buf, size_t cap, size_t *len);

/*
 * Start connecting (ticket: null-terminated UTF-8). Returns at once with the new peer's number in
 * *peer_out; the outcome arrives as YL_EVENT_PEER_CONNECTED or YL_EVENT_PEER_DISCONNECTED.
 */
YL_API int yl_connect(yl_handle *h, const char *ticket, uint32_t *peer_out);

/*
 * Close a connection. A YL_EVENT_PEER_DISCONNECTED still follows. Unknown peers are ignored.
 */
YL_API int yl_disconnect(yl_handle *h, uint32_t peer);

/*
 * Queue a message. Sending to a peer that isn't connected (any more) is silently dropped; the
 * disconnect arrives via yl_poll. So is an unreliable message the current path can't carry.
 */
YL_API int yl_send(yl_handle *h, uint32_t peer, uint8_t channel, const uint8_t *data, size_t len);

/*
 * Take the next event. Returns 1 if *ev and buf were filled, 0 if there's nothing to take, or
 * YL_ERR_BUFFER_TOO_SMALL with ev->len set to the size needed (the event stays queued; poll again
 * with a bigger buffer). buf may be NULL when cap is 0.
 */
YL_API int yl_poll(yl_handle *h, yl_event *ev, uint8_t *buf, size_t cap);

/* Statistics for a peer. Unknown or disconnected peers report connected = 0. */
YL_API int yl_peer_stats(yl_handle *h, uint32_t peer, yl_stats *out);

#ifdef __cplusplus
}
#endif

#endif /* YARG_LINK_H */
