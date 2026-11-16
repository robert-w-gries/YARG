/*
 * Loads the built library the way the game does (dlopen/LoadLibrary, nothing linked at build
 * time), opens two handles and has them exchange messages over localhost.
 *
 *   tests/c/run.sh                 # builds the library and this harness, then runs it
 *   harness path/to/libyarg_link.so
 */
#if !defined(_WIN32)
#define _POSIX_C_SOURCE 200809L
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../../include/yarg_link.h"

#if defined(_WIN32)
#include <windows.h>
#define LOAD(path) ((void *) LoadLibraryA(path))
#define SYM(lib, name) ((void *) GetProcAddress((HMODULE) (lib), name))
#define SLEEP_MS(ms) Sleep(ms)
#else
#include <dlfcn.h>
#include <time.h>
#define LOAD(path) dlopen(path, RTLD_NOW | RTLD_LOCAL)
#define SYM(lib, name) dlsym(lib, name)
static void SLEEP_MS(int ms) {
    struct timespec ts = { ms / 1000, (ms % 1000) * 1000000L };
    nanosleep(&ts, NULL);
}
#endif

typedef uint32_t (*abi_version_fn)(void);
typedef int (*last_error_fn)(char *, size_t, size_t *);
typedef int (*create_fn)(const char *, yl_handle **);
typedef void (*destroy_fn)(yl_handle *);
typedef int (*local_ticket_fn)(yl_handle *, char *, size_t, size_t *);
typedef int (*connect_fn)(yl_handle *, const char *, uint32_t *);
typedef int (*disconnect_fn)(yl_handle *, uint32_t);
typedef int (*send_fn)(yl_handle *, uint32_t, uint8_t, const uint8_t *, size_t);
typedef int (*poll_fn)(yl_handle *, yl_event *, uint8_t *, size_t);
typedef int (*peer_stats_fn)(yl_handle *, uint32_t, yl_stats *);
typedef int (*peer_id_fn)(yl_handle *, uint32_t, char *, size_t, size_t *);

static abi_version_fn yl_abi_version_;
static last_error_fn yl_last_error_;
static create_fn yl_create_;
static destroy_fn yl_destroy_;
static local_ticket_fn yl_local_ticket_;
static connect_fn yl_connect_;
static disconnect_fn yl_disconnect_;
static send_fn yl_send_;
static poll_fn yl_poll_;
static peer_stats_fn yl_peer_stats_;
static peer_id_fn yl_peer_id_;

static const char *CONFIG =
    "{\"relay\": false, \"address_lookup\": false, \"bind_addr\": \"127.0.0.1:0\","
    " \"ticket_addrs\": true, \"log_filter\": \"warn\"}";

static int failures = 0;

#define CHECK(cond, ...)                                     \
    do {                                                     \
        if (!(cond)) {                                       \
            fprintf(stderr, "FAIL %s:%d: ", __FILE__, __LINE__); \
            fprintf(stderr, __VA_ARGS__);                    \
            fprintf(stderr, "\n");                           \
            failures++;                                      \
            return;                                          \
        }                                                    \
    } while (0)

static const char *last_error(void) {
    static char buf[512];
    size_t len = 0;
    yl_last_error_(buf, sizeof buf - 1, &len);
    buf[len < sizeof buf - 1 ? len : sizeof buf - 1] = 0;
    return buf;
}

/* Wait up to timeout_ms for the next non-log event. Returns 1 on an event, 0 on timeout. */
static int next_event(yl_handle *h, yl_event *ev, uint8_t *buf, size_t cap, int timeout_ms) {
    for (int waited = 0; waited <= timeout_ms; ) {
        int rc = yl_poll_(h, ev, buf, cap);
        if (rc == 1 && ev->type == YL_EVENT_LOG) {
            fprintf(stderr, "  [log %u] %.*s\n", ev->level, (int) ev->len, (const char *) buf);
            continue;
        }
        if (rc == 1) return 1;
        if (rc != 0) {
            fprintf(stderr, "yl_poll: %d (%s)\n", rc, last_error());
            return 0;
        }
        SLEEP_MS(5);
        waited += 5;
    }
    return 0;
}

static void exchange(void) {
    yl_handle *a = NULL, *b = NULL;
    CHECK(yl_create_(CONFIG, &a) == YL_OK, "create a: %s", last_error());
    CHECK(yl_create_(CONFIG, &b) == YL_OK, "create b: %s", last_error());

    char ticket[1024];
    size_t len = 0;
    CHECK(yl_local_ticket_(b, ticket, sizeof ticket - 1, &len) == YL_OK && len < sizeof ticket, "ticket");
    ticket[len] = 0;
    printf("b's ticket: %s\n", ticket);

    uint32_t at_a = 0;
    CHECK(yl_connect_(a, ticket, &at_a) == YL_OK, "connect: %s", last_error());

    yl_event ev;
    uint8_t buf[4096];
    CHECK(next_event(a, &ev, buf, sizeof buf, 10000) && ev.type == YL_EVENT_PEER_CONNECTED && ev.peer == at_a,
          "a connected (type %u)", ev.type);
    CHECK(next_event(b, &ev, buf, sizeof buf, 10000) && ev.type == YL_EVENT_PEER_CONNECTED, "b connected");
    uint32_t at_b = ev.peer;

    const char *hello = "hello from a";
    CHECK(yl_send_(a, at_a, YL_CHANNEL_RELIABLE, (const uint8_t *) hello, strlen(hello)) == YL_OK, "send");
    CHECK(next_event(b, &ev, buf, sizeof buf, 10000) && ev.type == YL_EVENT_MESSAGE, "b got message");
    CHECK(ev.peer == at_b && ev.channel == YL_CHANNEL_RELIABLE && ev.len == strlen(hello) &&
              memcmp(buf, hello, ev.len) == 0,
          "message contents");

    /* Unreliable: retry, since datagrams may be dropped */
    int got = 0;
    for (int i = 0; i < 100 && !got; i++) {
        const uint8_t dgram[] = { 1, 2, 3, 4 };
        yl_send_(b, at_b, YL_CHANNEL_UNRELIABLE, dgram, sizeof dgram);
        got = next_event(a, &ev, buf, sizeof buf, 50) && ev.type == YL_EVENT_MESSAGE &&
              ev.channel == YL_CHANNEL_UNRELIABLE && ev.len == 4 && buf[3] == 4;
    }
    CHECK(got, "a got a datagram");

    yl_stats stats;
    CHECK(yl_peer_stats_(a, at_a, &stats) == YL_OK && stats.connected == 1, "stats");

    /* The peer's endpoint ID: 64 hex characters */
    char id[128];
    size_t id_len = 0;
    CHECK(yl_peer_id_(a, at_a, id, sizeof id, &id_len) == YL_OK && id_len == 64, "peer id");
    CHECK(yl_peer_id_(a, 9999, id, sizeof id, &id_len) == YL_ERR_INVALID_ARG && id_len == 0, "unknown peer id");
    printf("rtt %lld us, path %u, sent %llu, received %llu\n", (long long) stats.rtt_us, stats.path,
           (unsigned long long) stats.bytes_sent, (unsigned long long) stats.bytes_received);

    /* Buffer too small: size reported, event kept */
    char big[5000];
    memset(big, 'x', sizeof big);
    yl_send_(a, at_a, YL_CHANNEL_RELIABLE, (const uint8_t *) big, sizeof big);
    int rc = 0;
    for (int i = 0; i < 2000 && rc == 0; i++) {
        rc = yl_poll_(b, &ev, buf, sizeof buf);
        if (rc == 1 && ev.type == YL_EVENT_LOG) rc = 0;
        if (rc == 0) SLEEP_MS(5);
    }
    CHECK(rc == YL_ERR_BUFFER_TOO_SMALL && ev.len == sizeof big, "too small (rc %d, len %llu)", rc,
          (unsigned long long) ev.len);
    uint8_t *large = malloc(ev.len);
    CHECK(yl_poll_(b, &ev, large, (size_t) ev.len) == 1 && ev.type == YL_EVENT_MESSAGE, "big message");
    free(large);

    CHECK(yl_disconnect_(a, at_a) == YL_OK, "disconnect");
    CHECK(next_event(a, &ev, buf, sizeof buf, 5000) && ev.type == YL_EVENT_PEER_DISCONNECTED, "a disconnected");
    CHECK(next_event(b, &ev, buf, sizeof buf, 5000) && ev.type == YL_EVENT_PEER_DISCONNECTED, "b disconnected");
    printf("b saw: %.*s\n", (int) ev.len, (const char *) buf);

    yl_destroy_(a);
    yl_destroy_(b);
    yl_destroy_(NULL);
}

#define BIND(name)                                                         \
    do {                                                                   \
        *(void **) &name##_ = SYM(lib, #name);                             \
        if (!name##_) {                                                    \
            fprintf(stderr, "missing export %s\n", #name);                 \
            return 1;                                                      \
        }                                                                  \
    } while (0)

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s <path to yarg_link library>\n", argv[0]);
        return 2;
    }
    void *lib = LOAD(argv[1]);
    if (!lib) {
        fprintf(stderr, "couldn't load %s\n", argv[1]);
        return 1;
    }

    BIND(yl_abi_version);
    BIND(yl_last_error);
    BIND(yl_create);
    BIND(yl_destroy);
    BIND(yl_local_ticket);
    BIND(yl_connect);
    BIND(yl_disconnect);
    BIND(yl_send);
    BIND(yl_poll);
    BIND(yl_peer_stats);
    BIND(yl_peer_id);

    if (yl_abi_version_() != YL_ABI_VERSION) {
        fprintf(stderr, "ABI %u, header says %d\n", yl_abi_version_(), YL_ABI_VERSION);
        return 1;
    }

    exchange();

    if (failures) {
        fprintf(stderr, "%d failure(s)\n", failures);
        return 1;
    }
    printf("ok\n");
    return 0;
}
