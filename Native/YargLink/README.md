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
- **Limits.** Every peer is treated as potentially hostile. A reliable frame is at most 1 MiB
  (`YL_MAX_RELIABLE`), and a hello at most 64 bytes; a peer that announces more is disconnected
  before its payload is read, and frame buffers grow as data arrives rather than from the header.
  Incoming connections are capped (`max_peers`, and a handful handshaking at once, each within
  10 s); a game that only joins sets `"accept": false` and refuses them all. A peer whose messages
  pile up unpolled (16k messages or 32 MiB) is disconnected; its datagrams are dropped instead.
  The same goes the other way: a peer that stops reading is disconnected once 32 MiB (or 64k
  messages) of reliable data to it waits to go out.
- **Graceful close.** `yl_disconnect` and `yl_destroy` still deliver reliable messages that were
  already queued, so a goodbye followed by a disconnect arrives. The stream is finished first, and
  the connection closes once the peer has read everything (or after 2 s).
- **Tickets** are an endpoint ID, optionally followed by `@addr,addr,relay-url`. A bare ID is short
  and keeps working while its handle lives, even if the addresses change, because iroh finds them
  through n0's DNS. Connect attempts are retried until the timeout, because a host that just started
  may not have published its addresses yet.
- **Identity.** Every handle gets a new, random endpoint ID, and so a new ticket. Nothing is stored
  on disk. A ticket stops working once its handle is destroyed (the game opens one per session), and
  peers can't recognise a game from one handle to the next. `yl_peer_id` reports a connected peer's
  endpoint ID, which the QUIC handshake authenticates; the game uses it to keep a kicked peer out
  for the rest of a session. (Earlier versions had a `key_file` option for a stable ID. It was
  removed, and an old `key_file` member in the config is ignored.)
- **Logging.** Endpoint IDs are logged shortened, and ticket parse errors don't repeat the ticket:
  a full ID is an invite code, and logs get posted publicly.

## Building

```bash
cargo build                   # debug, for tests
cargo test                    # Rust tests: two handles over localhost, no network needed
cargo test -- --ignored       # plus a test through n0's DNS and relays (needs internet)
tests/c/run.sh                # the C harness against the built library
dotnet test ../../Tests/YARG.Link.Tests   # C# transport and session tests, on this library too
```

The toolchain is pinned in `rust-toolchain.toml`.

### Unity plugins

The plugins in `Assets/Plugins/YargLink/` are built with the same tool as YargAudio's:

```bash
dotnet run --project scripts/NativeBuild -- build --library link                # this platform
dotnet run --project scripts/NativeBuild -- package --library link --ref <branch> # all three, on GitHub Actions
```

`build` runs the Rust tests, builds the release library, checks it, runs the C harness and the C#
tests on it, and copies it into the Unity project. `package` runs `.github/workflows/native-link.yml`
on a pushed branch and downloads all three plugins. Commit the binaries afterwards.

| Platform | Target | Notes |
|---|---|---|
| Windows x64 | `x86_64-pc-windows-msvc` | Static CRT, so players don't need the VC++ redistributable. |
| Linux x64 | `x86_64-unknown-linux-gnu` | Built in `ubuntu:20.04` (glibc 2.31, like YargAudio). Outside that image, NativeBuild runs the build in a docker container of it, so Linux needs Docker. |
| macOS universal | `aarch64-apple-darwin` + `x86_64-apple-darwin` | Joined with `lipo`, minimum macOS 11.0. Stripped by the linker, so the linker's ad-hoc signature stays valid; never re-signed with `codesign`. |

The build is reproducible: CI rebuilds the library and fails if the committed Linux or macOS plugin
differs by a byte (`--verify-committed-plugin`). This relies on the pinned toolchain, `--locked`,
and `--remap-path-prefix` for the source and cargo directories, which rustc would otherwise embed in
panic messages. Windows is only tested, not compared, as with YargAudio, because the MSVC linker on
CI runners changes with the runner image.

A plain `cargo build --release` is fine for local testing, but it links against the host's glibc
and doesn't match the committed plugin.

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
