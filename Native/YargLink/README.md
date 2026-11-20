# YargLink

YargLink is the peer-to-peer transport for online play in YARG. It is a C library. Inside, it uses
[iroh](https://iroh.computer): QUIC connections with NAT hole punching and end-to-end encryption.
When two games cannot connect directly, iroh sends the encrypted packets through a relay server.

YargLink is only a pipe for bytes. It does these tasks:

- It connects to other games with a ticket.
- It sends reliable bytes and unreliable bytes.
- It gives events: connects, disconnects, messages, errors and log lines.

YargLink has no data about songs, players or scenes. The session layer
(`Assets/Script/Integration/Link/Session`) gives the bytes their meaning. Thus a new gameplay
feature does not need a new build of this library.

## Reliable and unreliable bytes

Each message goes on one of two channels. The caller selects the channel for each message.

| Channel | `YL_CHANNEL_*` | Delivery | Largest message | QUIC carries it on | YARG uses it for |
|---|---|---|---|---|---|
| Reliable | `RELIABLE` (0) | Each message arrives one time, in the order that it was sent to that peer. A lost packet is sent again. | 1 MiB − 1 byte (`YL_MAX_RELIABLE`) | One bidirectional stream for each connection | Hellos, the roster, song setups, inputs, the start of a song, pause and fail events |
| Unreliable | `UNRELIABLE` (1) | A message can be lost, can arrive two times, or can arrive out of order. A lost packet is not sent again. | 1,024 bytes (`YL_MAX_UNRELIABLE`) | One QUIC datagram for each message | Data that the next message replaces, for example the values of the band's fail meter |

Use the reliable channel when each message must arrive. Use the unreliable channel when a late
message has no value. A lost packet on the reliable channel stops the messages after it until the
packet arrives again. The unreliable channel does not stop.

## Files

- `include/yarg_link.h`: the C interface. This is the contract with the game.
- `src/lib.rs`: the exported functions, the handle registry and the pointer checks.
- `src/config.rs`: the `yl_create` configuration, its defaults and its checks.
- `src/error.rs`: the error type, with the `YL_ERR_*` code of each error.
- `src/ticket.rs`: tickets (the invite codes).
- `src/log.rs`: log lines into the event queue.
- `src/node/`: the networking of one handle.
  - `mod.rs`: `Node`, which the exported functions call.
  - `shared.rs`: the event queue, the peers and the peer state machine.
  - `conn.rs`: the tasks of each connection.
  - `wire.rs`: the wire format.
- `tests/c/`: a C harness. It loads the built library in the same way as the game.
- The C# bindings are in `Assets/Script/Integration/Link/Transport/NativeLinkTransport.cs`.

## Design

- **No callbacks.** Each handle has a tokio runtime with two worker threads. The game calls
  `yl_poll` from `Update()`. Thus the library never calls managed code from a foreign thread.
- **Nothing unwinds.** Each exported function catches panics and returns `YL_ERR_PANIC`. Each error
  is a status code, and `yl_last_error` gives its message. The build uses `panic = "unwind"`, never
  `abort`, because an abort stops the game.
- **Handles are numbers.** A `yl_handle *` is not a memory address. The library keeps a registry of
  the live handles. A wrong, old or destroyed handle gives an error, not a memory error.
- **Pointer checks come first.** Each function checks its pointer arguments for null before it does
  other work. Only `yl_create`'s config and `yl_send`'s data (when the length is 0) can be null.
- **The configuration is strict.** An unknown member or a bad value gives `YL_ERR_BAD_CONFIG`. The
  library does not use the defaults in place of a bad configuration.
- **Logs go through the event queue** (`YL_EVENT_LOG`). The `log_filter` configuration selects
  the log lines.
- **Wire format.** The ALPN is `yarg-link/3`. Each connection has one bidirectional QUIC stream for
  reliable messages. The stream has frames: `[u32 length][u8 kind][payload]`. Unreliable messages
  are QUIC datagrams. The two games send a hello frame first. A peer is connected when its hello
  arrives.
- **Limits.** The library treats each peer as possibly hostile.
  - A reliable frame is at most 1 MiB (`YL_MAX_RELIABLE`). A hello is at most 64 bytes. If a peer
    announces a larger frame, the library disconnects it before it reads the payload. Frame buffers
    grow as the data arrives. They do not grow from the length in the header.
  - Incoming connections have limits: `max_peers`, at most 8 in their handshake at one time, and
    10 s for each handshake. A game that only joins does not set `"accept"`, so it refuses all
    incoming connections.
  - When the game does not poll, at most 16k messages or 32 MiB from one peer can wait. Then the
    library disconnects the peer, or for datagrams, drops them.
  - When a peer does not read, at most 32 MiB or 64k reliable messages to it can wait. Then the
    library disconnects the peer.
- **Graceful close.** `yl_disconnect` and `yl_destroy` still send the reliable messages that wait to
  go out. Thus a goodbye arrives before the disconnect. The library ends the stream first. The
  connection closes when the peer has read all the data, or after 2 s.
- **Tickets.** A ticket is an endpoint ID. It can also have `@addr,addr,relay-url` after the ID. A
  ticket that is only an ID is short, and it stays correct while its handle is open, also when the
  addresses change: iroh finds the addresses through n0's DNS (`address_lookup`). Without
  `address_lookup`, the ticket must have the addresses (`ticket_addrs`), and `yl_create` checks this
  rule. A connect tries again until its timeout, because a new host possibly has not published its
  addresses yet.
- **Identity.** Each handle gets a new, random endpoint ID, and thus a new ticket. Nothing goes on
  the disk. A ticket stops when its handle closes (the game opens one handle for each session). Thus
  a peer cannot recognize a game from one handle to the next. `yl_peer_id` gives the endpoint ID of
  a connected peer, and the QUIC handshake makes sure that it is correct. The game uses it to keep
  a kicked peer out for the rest of a session. (Earlier versions had a `key_file` option for a
  stable ID. It is removed, and `yl_create` refuses a config that has it.)
- **Logs do not have invite codes.** The library logs only short forms of endpoint IDs. A ticket
  parse error does not repeat the ticket. A full ID is an invite code, and people post logs in
  public.

## Rate limits

The library has no rate limit of its own. The limits are in these places:

- **Each peer, in the game.** `LinkSession` limits what each member can send: 250 messages and
  512 KiB each second on average, with a burst of 4 s (`LinkSessionOptions`). A peer that sends more
  is disconnected. The host's limit for each joiner is larger, because the host sends the data of
  all members. These limits are in the session layer because it knows what the game needs.
- **Each peer, in this library.** The queue limits above stop a peer that sends too much while the
  game does not poll. They are limits on memory, not on rate.
- **The relay server.** A relay carries the packets of many games, also of games that do not use
  YARG. Thus its rate limit must be on the server: the `iroh-relay` configuration
  (`limits.client.rx`) limits the bytes from each client. A game cannot enforce a limit on other
  clients of a relay. `docs/native-library-plan.md` ("Relays and address lookup") has the plan for
  our own relay, with its limits and access token.

## Building

```bash
cargo build                   # debug, for tests
cargo test                    # Rust tests: two handles over localhost, no network needed
cargo test -- --ignored       # plus a test through n0's DNS and relays (needs internet)
tests/c/run.sh                # the C harness against the built library
dotnet test ../../Tests/YARG.Link.Tests   # C# transport and session tests, in memory and on this library
```

`rust-toolchain.toml` sets the Rust version.

### Unity plugins

The plugins in `Assets/Plugins/YargLink/` use the same build tool as YargAudio:

```bash
dotnet run --project scripts/NativeBuild -- build --library link                # this platform
dotnet run --project scripts/NativeBuild -- package --library link --ref <branch> # all three, on GitHub Actions
```

`build` does these steps: it runs the Rust tests, builds the release library, checks it, runs the
C harness and the C# tests on it, and copies it into the Unity project. `package` runs
`.github/workflows/native-link.yml` on a branch that you pushed, and downloads the three plugins.
After that, commit the binaries.

| Platform | Target | Notes |
|---|---|---|
| Windows x64 | `x86_64-pc-windows-msvc` | Static CRT, so players do not need the VC++ redistributable. |
| Linux x64 | `x86_64-unknown-linux-gnu` | Built in `ubuntu:20.04` (glibc 2.31, as YargAudio). Outside that image, NativeBuild runs the build in a Docker container of it. Thus Linux needs Docker. |
| macOS universal | `aarch64-apple-darwin` + `x86_64-apple-darwin` | Joined with `lipo`, minimum macOS 11.0. The linker strips the library, so the linker's ad-hoc signature stays valid. Do not sign it again with `codesign`. |

The build is reproducible. CI builds the library again, and fails if the committed Linux or macOS
plugin is different by one byte (`--verify-committed-plugin`). This needs the pinned toolchain,
`--locked`, and `--remap-path-prefix` for the source and cargo directories. Without the remap,
rustc puts those paths in panic messages. CI only tests the Windows plugin and does not compare it,
as with YargAudio, because the MSVC linker changes with the CI runner image.

A plain `cargo build --release` is correct for local tests. But it links against the glibc of
your computer, so it is not the same as the committed plugin.

## Using it from Unity

Unity cannot unload native libraries. As with YargAudio, the Editor loads a copy of the plugin from
the temp directory, with a timestamp in its name. Thus you can build the plugin again while the
Editor runs. The Editor uses the new build only after a restart. To load a library from a different
location, set `YARG_LINK_LIBRARY`, for example to `Native/YargLink/target/debug/libyarg_link.so`.

To test a session without the lobby, use the development launch options. Only the Editor and test
builds (File > Make Test Build, which sets `YARG_TEST_BUILD`) read them. Release builds ignore them.
`LinkController.OpenFromLaunchOptions` reads them.

```bash
# Host: starts a session. The invite code goes on the clipboard (the log has only its short form).
./YARG -link-host -link-name Host
# Friend: paste the host's invite code
./YARG -link-join <invite code> -link-name Friend
```

The environment variables `YARG_LINK_HOST=1`, `YARG_LINK_JOIN` and `YARG_LINK_NAME` do the same.
Two copies on one computer also work, because each copy gets its own endpoint ID.
