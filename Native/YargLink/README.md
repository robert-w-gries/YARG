# YargLink

The peer-to-peer transport behind YARG's online play: [iroh](https://iroh.computer) (QUIC with NAT
hole punching, end-to-end encryption and n0's public relays as a fallback) behind a small C interface.

It is a game-agnostic pipe. It connects to other games by ticket, sends reliable or unreliable bytes,
and reports events. It knows nothing about songs, players or scenes. The session layer
(`Assets/Script/Integration/Link/Session`) gives the bytes their meaning, so gameplay features never
need a rebuild of this library.

- `include/yarg_link.h`: the C interface (the contract with the game).
- `src/`: the Rust implementation. `node.rs` holds the endpoint, peers and wire format, and `lib.rs`
  holds the exported functions.
- `tests/c/`: a C harness that loads the built library the way the game does.
- C# bindings: `Assets/Script/Integration/Link/Transport/NativeLinkTransport.cs`.

## Design

- **No callbacks.** Each handle owns a tokio runtime with two worker threads. The game polls
  `yl_poll` from `Update()`, so nothing ever calls into managed code from a foreign thread.
- **Nothing unwinds.** Every export catches panics (`YL_ERR_PANIC`), and errors are status codes
  with a message from `yl_last_error`. `panic = "unwind"`, never `abort`.
- **Logging goes through the event queue** (`YL_EVENT_LOG`), filtered by the `log_filter` config.
- **Wire format.** ALPN `yarg-link/3`. Each connection has one bidirectional QUIC stream for reliable
  messages, framed as `[u32 length][u8 kind][payload]`, and QUIC datagrams for unreliable ones.
  Both sides send a hello frame first. A peer counts as connected once the other side's hello
  arrives.
- **Graceful close.** `yl_disconnect` and `yl_destroy` still deliver reliable messages that were
  already queued, so a goodbye followed by a disconnect arrives. The stream is finished first, and
  the connection closes once the peer has read everything (or after 2 s).
- **Tickets** are an endpoint ID, optionally followed by `@addr,addr,relay-url`. A bare ID is short
  and never goes stale, and iroh finds the addresses through n0's DNS. Connect attempts are retried
  until the timeout, because a host that just started may not have published its addresses yet.
- **Identity.** With `key_file`, the endpoint ID (and so the invite code) is the same on every run.
  The key file is locked, so a second copy of the game on the same machine gets a temporary identity
  instead of fighting over the ID.

## Building

```bash
cargo build                   # debug, for tests
cargo test                    # Rust tests: two handles over localhost, no network needed
cargo test -- --ignored       # plus a test through n0's DNS and relays (needs internet)
tests/c/run.sh                # the C harness against the built library
dotnet test ../../Tests/YARG.Link.Tests   # C# transport and session tests, on this library too
```

The toolchain is pinned in `rust-toolchain.toml`.

### Linux plugin

```bash
./build-linux.sh
```

This builds in an `ubuntu:20.04` container (Docker), so the library only needs glibc 2.31. It
checks the glibc floor and copies the result to `Assets/Plugins/YargLink/Linux/x86_64/`. A plain
`cargo build --release` works too, but links against the host's glibc.

Windows and macOS builds come with CI (`native-link.yml`).

## Using it from Unity

Unity can't unload native libraries. Like YargAudio, the Editor loads a timestamped copy of the
plugin from the temp directory, so the plugin can be rebuilt while the Editor runs. The new build
is only used after an Editor restart. Set `YARG_LINK_LIBRARY` to load a library from elsewhere, for
example `Native/YargLink/target/debug/libyarg_link.so`.

To test a session without the lobby UI, see `LinkController`:

```bash
# Host: the invite code is logged and copied to the clipboard
./YARG
# Friend
./YARG -link-join <invite code>
# Two copies on one machine without iroh
./YARG -link-transport loopback
./YARG -link-join loopback:127.0.0.1:36120
```
