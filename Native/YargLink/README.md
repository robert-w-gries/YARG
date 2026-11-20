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
  the log lines. The default filter drops the library's debug lines. YARG asks for them when the
  player sets the minimum log level to Debug or Trace (`LinkController.NativeLogFilter`).
- **Wire format.** The ALPN is `yarg-link/3`. Each connection has one bidirectional QUIC stream for
  reliable messages. The stream has frames: `[u32 length][u8 kind][payload]`. Unreliable messages
  are QUIC datagrams. The two games send a hello frame first: the game that connects sends first,
  and the host answers. A peer is connected when the hellos are done. A frame of an unknown kind,
  or a second hello, disconnects the peer: a game with the same ALPN never sends them. If you
  change the wire format, change the ALPN too. The test `wire_format_is_unchanged` fails to remind
  you.
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
  clients of a relay, so our own relay must set this limit and use an access token.

## Public relays: risks

When `"relay"` is `true` (the default), the library uses the public relays of n0. A relay sends
the traffic only when hole punching is not possible. Then the stats show `PATH_RELAY`, and the
lobby shows "Relay". The relay cannot read the packets, because they are encrypted from end to
end. The public relays are for development and tests. A release must use our own relay first.

### Timing

- **Clock offset.** `ClockOffsetEstimator` uses half of the round trip as the latency in one
  direction. On a relayed path, each packet goes through the home relay of the game that receives
  it. Two games can have different home relays. Then the two directions have different latencies.
  This causes a constant error in the offset, and the estimator cannot find this error. The synced
  start and the clock correction have the same error.
- **Jitter.** A relay sends the packets on TCP (HTTPS or WebSocket). If one packet is lost, the
  packets after it must wait. Thus the hits of a friend arrive later and in groups (`RemoteInputFeed`
  lag). The clock estimate is also less accurate.
- **Path changes.** During a song, iroh can change a connection from relayed to direct, or from
  direct to relayed. Then the round trip changes quickly, and a clock correction can occur.
  `LogPathChanges` writes each change to the log.
- **Two relays.** A joiner sends all data through the host. Thus the data from one joiner to a
  different joiner can go through two relays.

### Rate limits

- n0 does not publish the rate limits of the public relays. n0 can change these limits at any
  time. Each relay limits the bytes that it receives from each connection. If a client sends more
  than the limit, the relay decreases the speed of that client. The relay does not disconnect the
  client. The limit has no effect on the other clients.
- During a song, each member sends its inputs, the band meter and its state. The inputs are
  reliable, with a maximum of 120 messages each second. The band meter is 10 messages each second.
  The state is 4 messages each second. For each player, this is approximately 2 KB to 50 KB each
  second. We did not measure this value yet.
- The host sends the inputs of each member to all other joiners. Thus the host sends the most
  data. With 4 members, the host sends 9 streams of inputs.
- A rate limit is not a real cause of a disconnect during a song. The highest risk is a relayed
  host in a full session. In that session, a rate limit can increase the latency. The reliable
  inputs are not lost. They wait in a queue.

### Availability and privacy

- The public relays have no SLA. If a relay stops or starts again, all relayed connections through
  it stop at the same time. If the host is relayed, each joiner loses the connection to the host.
- The relay can see the IP addresses and the endpoint IDs of the two games. It can also see when
  the games send data, and how much data they send.
- When `"address_lookup"` is `true`, the library also publishes the addresses of each endpoint in
  the DNS of n0.
- Address lookup and the relays both use n0. If a ticket has only an ID, a session must have n0
  for the two services.

### iroh versions and the public relays

- n0 updates the public relays to the newest iroh release. Usually, this occurs in 24 hours or
  less after the release.
- In iroh 1.x, the wire protocol is compatible across all minor versions and patch versions. Only
  a new major version can make the wire protocol incompatible.
- If a relay change makes the wire protocol incompatible, the new relays get new URLs. Thus older
  clients can continue to use the old URLs.
- n0 supports only the newest stable release on the public relays. Older versions can possibly
  operate, but n0 can stop their support at any time.
- The time between two iroh minor versions is 4 weeks or more. The time between two major versions
  is 6 months or more.
- Patch releases (also security fixes) are only for the newest minor version.
- n0 does not have a channel for notices about relay changes. To find changes, read the
  [release policy](https://docs.iroh.computer/about/release-policy), the
  [changelog](https://iroh.computer/changelog) and the
  [GitHub releases](https://github.com/n0-computer/iroh/releases).

Thus a 1.x version will not stop and operate incorrectly. But only the newest version has support.

We do not update iroh for each new release. Each update causes work: you must build the three
plugins again, commit them, and do a test through the relays. Update iroh only for one of these
causes:

- A security advisory for a crate in `Cargo.lock`.
- A new feature or a fix that YargLink must have.
- A new major version.
- n0 stops the support of our version on the public relays.

The `audit` job in `.github/workflows/native-link.yml` runs `cargo-audit` on `Cargo.lock`. It runs
on each pull request that changes YargLink, and one time each week. If a crate has a security
advisory, the job fails. An unmaintained crate gives only a warning, and the job does not fail.

To update iroh, change its version in `Cargo.toml`. Then do these steps in `Native/YargLink`:

```bash
cargo update -p iroh --precise <version>
cargo test
cargo test -- --ignored       # a test through n0's DNS and relays
```

Push your branch. Then, in the root of the repository, build the plugins:

```bash
dotnet run --project scripts/NativeBuild -- package --library link --ref <your branch>
```

Then commit `Cargo.toml`, `Cargo.lock` and the three plugins.

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
