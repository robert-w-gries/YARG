# YARG online multiplayer: native library plan

*Status: accepted · 2026-11-14*

## Summary

The yarg-link sidecar proved that P2P synced play works: hole-punched iroh
connections, synced song start, remote highways driven by streamed inputs,
live scores. To turn this into a polished feature (lobby, Discord invites) that
can be merged into YARG upstream, we are changing the architecture:

- **Session logic moves into YARG (C#).** Lobby, synced-start coordination,
  Discord invite/join, and all gameplay sync live next to the game state they
  touch.
- **Networking stays in Rust, in-process.** The iroh transport becomes a
  native library (`cdylib`) loaded by YARG. It is a thin, game-agnostic
  "pipe": connect, send reliable/unreliable bytes, poll events, report stats.
- **Development moves to the YARG fork.** This repository is paused. The Rust
  crate will live in the fork at `Native/YargLink/`, next to
  `Native/YargAudio/`.

The first milestone is a **minimal viable product** that we can show upstream
maintainers to demonstrate that it works.

## Why not keep the sidecar

| Problem | Effect on a polished product |
|---|---|
| Second process the user launches from a terminal, plus a hand-copied endpoint ID | Not acceptable for a lobby or one-click Discord join |
| Lobby state would be split between the sidecar and the game | Every feature touches `LinkController.cs`, `protocol.rs`, and `hub.rs`, and both sides must stay in sync |
| Discord join events arrive in the game process | The join secret has to be relayed over localhost before connecting |
| The game ↔ sidecar link is localhost UDP | `Broadcast` is documented as reliable and ordered, but nothing guarantees it |
| Process management | Port conflicts on 36110, orphaned or crashed sidecars, firewall prompts, a second executable to sign (worse on macOS: separate notarization and Gatekeeper prompts) |

The protocol had already drifted toward the game owning the meaning
(`Broadcast` carries opaque data). The sidecar was effectively transport plus
`coordinator.rs`.

## Why not pure C#

iroh is the main technical advantage: NAT hole-punching, end-to-end
encryption, free public relays as a fallback, and stable endpoint IDs that work
well as invite codes. There is no .NET implementation; the official FFI
bindings are Python, Swift and Kotlin. A C# replacement (for example
LiteNetLib plus our own STUN, relay and encryption) is a lot more work for a
worse result.

## Architecture

### Responsibilities

| Rust: `Native/YargLink` (cdylib) | C#: YARG |
|---|---|
| iroh endpoint, secret key storage | Lobby state and UI |
| Connect/accept by ticket or endpoint ID | Synced-start coordinator (ported from `coordinator.rs`) |
| Reliable ordered channel (QUIC stream) | Discord invite/join: join secret ↔ endpoint ticket |
| Unreliable channel (QUIC datagrams) | Gameplay sync: input streams, remote highways, scoreboard |
| RTT and direct/relay path statistics | Message schema and versioning |
| Optional Data Stream relay | Settings, localization |

Rust knows nothing about songs, scenes or players. All message meaning is in
C#, so features can be built without rebuilding the native library.

### Native interface (sketch)

A small C interface with opaque handles, polled from Unity's main thread. No
callbacks from Rust into C#.

```c
// All functions return a status code; 0 = ok. Never unwinds.
int  yl_create(const char *config_json, yl_handle **out);   // owns a tokio runtime
void yl_destroy(yl_handle *h);                              // stops all tasks, closes the endpoint
int  yl_local_ticket(yl_handle *h, char *buf, size_t cap, size_t *len);
int  yl_connect(yl_handle *h, const char *ticket, uint32_t *peer_out);
int  yl_disconnect(yl_handle *h, uint32_t peer);
int  yl_send(yl_handle *h, uint32_t peer, uint8_t channel /* 0=reliable,1=unreliable */,
             const uint8_t *data, size_t len);
int  yl_poll(yl_handle *h, yl_event *ev, uint8_t *buf, size_t cap); // 1 = event, 0 = empty
int  yl_peer_stats(yl_handle *h, uint32_t peer, yl_stats *out);    // rtt, path, bytes
```

Events: `PeerConnected`, `PeerDisconnected{reason}`, `Message{peer, channel,
len}`, `Error{code}`. `yl_poll` writes the payload into a buffer the caller
provides, and a status code reports when that buffer is too small.

### C# interface

```csharp
public interface ILinkTransport : IDisposable
{
    string LocalTicket { get; }
    PeerId Connect(string ticket);
    void Disconnect(PeerId peer);
    void Send(PeerId peer, ReadOnlySpan<byte> data, Delivery delivery);
    bool TryPoll(out LinkEvent ev);          // called from Update()
    PeerStats GetStats(PeerId peer);
}
```

Implementations:

- `NativeLinkTransport`: the Rust library, used in builds.
- `LoopbackLinkTransport`: managed localhost/LAN UDP, used for development
  (two game instances on one machine without rebuilding native code) and for
  tests. It does no NAT traversal and is not meant for release.

Everything above this interface (`LinkSession`, the lobby, the coordinator,
the Discord glue) has no idea which transport it is running on. If upstream
rejects Rust, only `NativeLinkTransport` gets replaced.

## Platform builds

Follow the existing YargAudio pattern (`.github/workflows/native-audio.yml`,
`scripts/NativeBuild`, binaries committed under `Assets/Plugins/<Lib>/`).

| Platform | Target | Notes |
|---|---|---|
| Windows x64 | `x86_64-pc-windows-msvc` | `-C target-feature=+crt-static` so no VC++ redistributable is needed |
| Linux x64 | `x86_64-unknown-linux-gnu` | glibc floor 2.31, matching YargAudio and Unity 6. Build in an `ubuntu:20.04` container with rustup, or use `cargo zigbuild --target x86_64-unknown-linux-gnu.2.31`. No OpenSSL (iroh uses rustls). Check with `objdump -T` that no symbol needs a glibc newer than 2.31. |
| macOS universal | `aarch64-apple-darwin` + `x86_64-apple-darwin`, combined with `lipo` | Same pinned Xcode 26.0.1 as YargAudio. Keep the linker's own ad-hoc signature and never re-sign with `codesign`. Check `LC_UUID`, check both architectures, test the x86_64 half under Rosetta. Set `MACOSX_DEPLOYMENT_TARGET` to Unity's minimum. |

The CI workflow `native-link.yml` copies `native-audio.yml`. `scripts/NativeBuild`
gains a `cargo` backend so maintainers keep a single build entry point.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| **Upstream rejects a Rust toolchain.** The team works in C++, CMake and .NET. | Raise it with maintainers before the PR. Fit Rust into `NativeBuild` and CI exactly the way YargAudio is built. Keep the transport swappable behind `ILinkTransport`. |
| **Overlap with other multiplayer work.** A separate fork (`asimard1/feature-multiplayer`) has a server-based SignalR lobby (`LobbyHubSession`, `LobbyViewMenu`, soft-hash library matching). It is another experimental implementation and, as far as we know, maintainers have not chosen it. | Mention it in the maintainer discussion and borrow good ideas (for example soft-hash matching). Our design needs no server, which sets it apart. |
| **Byte-identical build check** (`--verify-committed-plugin`) | Pin the toolchain with `rust-toolchain.toml`, use `--locked`, `--remap-path-prefix`, `codegen-units=1`, `CARGO_INCREMENTAL=0`. Prove it on macOS first. |
| **Unity cannot unload native libraries.** Rebuilding needs an Editor restart, and Windows locks the DLL. | Do most development on `LoopbackLinkTransport`. Rebuild native code only when the transport itself changes. |
| **Native state outlives play mode and domain reloads** | Handle-based API, one runtime per handle, `yl_destroy` that is reliable and safe to call twice, torn down from `OnDestroy` and `Application.quitting`. |
| **Panics or crashes inside the game process** | `catch_unwind` around every exported function, no `panic=abort`, errors returned as status codes. Log through a polled log-event channel. |
| **Threading** | Rust never calls into C#. C# polls from `Update()`. This also keeps a future IL2CPP switch easy (the project is Mono today). |
| **Binary size** (iroh + tokio + rustls) | `opt-level="s"`, `lto="fat"`, `strip=true`, `panic="unwind"`, iroh default features trimmed. Measure early; expect several MB per platform, about double for the macOS universal file. |
| **Version mismatch between friends** | Version handshake on connect: protocol version plus game build. Refuse incompatible peers with a clear message. |
| **Discord Game SDK is deprecated** | Check that Activity join/invite still works in the bundled `DiscordGameSDK`. The long-term path is Discord's Social SDK. Keep Discord glue isolated so invites also work through a plain copyable ticket. |
| **Song matching across machines** | Today we match on exact `SongEntry.Hash`. Look at soft-hash matching (see the overlapping work above) for charts that differ trivially. |

## Branch strategy (YARG fork)

The `multiplayer` branch is 2 commits (+1,601 / −37 lines across 22 files) on
top of `dev` at `3e2293114` (2026-11-03). Most of it stays relevant under the
new architecture: `RemoteInputFeed`, the player and highway hooks, and the
loading hold in `GameManager.Loading.cs`. Only the transport parts of
`LinkController.cs` (`Open`, `Close`, `Send`, `ReceiveLoop`, and the sidecar
message handling) get replaced.

Plan:

1. Tag the current `multiplayer` tip as `sidecar-prototype` so the working
   sidecar version stays reachable.
2. Add the official YARG repository as an `upstream` remote, fetch, and rebase
   onto current `upstream/dev`.
3. Continue on a new branch `online-native`, created from that rebased
   commit. Don't start from a clean `dev`: that would mean re-applying the
   same 1,600 lines by hand.
4. Before the upstream PR, reshape history into reviewable commits:
   gameplay hooks → session/transport layer → native library and CI → lobby UI
   → Discord.

`Assets/Settings/AddressableAssetsData/link.xml` (added by accident in
`b5db3284e`) was generated Addressables output. It is removed on
`online-native`, and `.gitignore` now covers its actual location. That
`.gitignore` fix is a separate commit, so it can go upstream as its own PR.

## Planned steps

### Phase 0: preparation

- [x] Tag `sidecar-prototype`, add the `upstream` remote, rebase, create `online-native`. (2026-11-14: `upstream/dev` hadn't moved, so no rebase was needed.)
- [ ] Open a discussion with YARG maintainers covering: the feature, the Rust toolchain, binary size, byte-identical builds, the swappable transport, and how this relates to other multiplayer/lobby work.

### Phase 1: session layer in C#, on the loopback transport

- [x] Define `ILinkTransport`, `LinkEvent`, `PeerId`, `Delivery`, `PeerStats`.
- [x] Implement `LoopbackLinkTransport`.
- [x] Split `LinkController` into `LinkSession` (peers, version handshake, message routing) and the existing gameplay sync.
- [x] Port `coordinator.rs` to C#: synced start, ready/decline, timeouts, the RTT-corrected start delay.
- [x] Define a versioned message schema; JSON is fine for the MVP.
- [ ] Two local instances complete a synced song with remote highways.

Phase 1 notes (2026-11-14):

- Code is in `Assets/Script/Integration/Link/`: `Transport/` (`ILinkTransport`, `LoopbackLinkTransport`:
  TCP for reliable, UDP for unreliable), `Session/` (`LinkSession`, `SyncStartCoordinator`, `LinkMessages`),
  and `LinkController` (gameplay glue: setups, input streams, remote highways, scoreboard).
- `Transport/` and `Session/` are plain .NET with no Unity dependency. Unit tests (coordinator ported
  from `coordinator.rs`, wire format, loopback transport, multi-member sessions) are in
  `Tests/YARG.Link.Tests`: `dotnet test Tests/YARG.Link.Tests`.
- Topology is still a host-centred star (question 3 answered "host-authoritative" for now). The host
  relays game data between joiners, and the roster carries every member's scene.
- Handshake: a `hello` with protocol version, exact game version, member ID, and a host flag. Mismatches
  are refused with a `bye` that carries the reason.
- Until there is a lobby UI, sessions are set up with launch options: `-link-join <ticket>`,
  `-link-port`, `-link-name`, `-link-lan` (or `YARG_LINK_*` environment variables in the Editor).
  By default a game hosts on port 36120 and falls back to a free port if 36120 is taken.

### Phase 2: native library on Linux

- [x] Move the iroh code from this repo into `Native/YargLink` as a `cdylib` with the interface above.
- [x] Rust unit tests and a C test harness that loads the library and exchanges messages between two handles.
- [x] `NativeLinkTransport` P/Invoke bindings, loaded the same way as `YargAudioBindings.cs`.
- [ ] Linux build works with a friend over the internet: direct and relay paths, synced start, highways.

Phase 2 notes (2026-11-14):

- The crate is `Native/YargLink` (see its README). The interface is the sketch above, plus
  `yl_abi_version`, `yl_last_error` (a per-thread error message) and a `YL_EVENT_LOG` event that
  carries iroh's and our own log lines into `YargLogger`. The header is `include/yarg_link.h`.
- Wire format: ALPN `yarg-link/3`. One bidirectional QUIC stream carries reliable messages as
  `[u32 len][u8 kind][payload]` after a hello in each direction. QUIC datagrams carry unreliable
  messages, at most 1024 bytes (the loopback transport allows 1200).
- Tickets are a bare endpoint ID, found through n0's DNS, optionally followed by `@` and direct
  addresses or relay URLs (tests use these with relays and DNS off). Connect attempts are retried
  until the timeout (20 s), because a host that just started hasn't published its addresses yet.
  Without the retry, joining right after the host started failed at once with "No addressing
  information available".
- `Disconnect` and `Dispose` still deliver reliable messages that were already queued. The session's
  `bye` (refusals, leaving) relies on this, and the loopback transport did it implicitly. This is now
  part of the `ILinkTransport` contract and is tested on both transports.
- The secret key is in `<persistent data>/link/secret.key`, so the invite code stays the same between
  runs. The key file is locked, and a second instance on the same machine gets a temporary ID.
- `LinkController` uses the native transport by default. `-link-transport loopback` (or a `loopback:`
  ticket) picks loopback, and hosting falls back to loopback when the platform has no library yet
  (Windows and macOS until phase 3). The host's invite code is copied to the clipboard. The joiner no
  longer restarts a connection attempt every 5 s (new `LinkSession.IsJoining`), which would have
  stopped slow NAT traversal from ever finishing. Changes between direct and relay paths are logged
  with the round-trip time.
- `Tests/YARG.Link.Tests` runs the transport and session suites on both transports. Native tests load
  `Native/YargLink/target/{debug,release}` and are skipped when it isn't built. Results: 17 Rust tests,
  the C harness, and 52 C# tests pass. Unity 6000.3.5f2 compiles the project with no errors. An opt-in
  Rust test (`cargo test -- --ignored`) connected two handles through n0's DNS by bare endpoint ID in
  about 4 s, but on one machine, so the path was direct.
- The Linux binary is built in `ubuntu:20.04` by `Native/YargLink/build-linux.sh`. Its newest glibc
  symbol is 2.30. It is 11.8 MB, more than the "several MB" estimated above. It doesn't need
  trimming for the MVP, but the size is worth tracking in phase 3 (for example `metrics` is already
  off; `portmapper` could go).

### Phase 3: cross-platform CI

- [ ] `native-link.yml` with Windows, Linux and macOS jobs, plus the export, architecture, glibc and signature checks.
- [ ] `NativeBuild` `cargo` backend and the byte-identical check.
- [ ] Commit the binaries under `Assets/Plugins/YargLink/{Windows,Linux,Mac}` with Unity `.meta` platform settings.
- [ ] Test across platforms: Windows ↔ macOS ↔ Linux.

### Phase 4: MVP polish (demo-ready)

- [ ] Minimal lobby: host/join by ticket, member list with instrument, difficulty and ready state, leave/kick.
- [ ] Copy/paste invite code in the UI (works without Discord).
- [ ] Clear error states: incompatible version, song missing, peer lost.
- [ ] Connection quality indicator (direct/relay, RTT).
- [ ] Recorded demo plus a short write-up for maintainers.

### Phase 5: after MVP

- [ ] Discord invite/"Ask to Join" through the bundled SDK.
- [ ] Soft-hash song matching.
- [ ] Data Stream relay inside the game (if still wanted).
- [ ] Reshape history and open the upstream PR.

## Open questions

1. Do maintainers have a preferred direction for online play (P2P like ours or server-based like the `asimard1` fork)?
2. Will maintainers accept Rust in the native build, or do they want a different language or approach?
3. Is the lobby host-authoritative (the host runs the coordinator, as today), or does any member coordinate?
4. Which minimum macOS version does YARG target?

## This repository

yarg-link (the sidecar) is paused. It stays as the reference implementation of
the prototype protocol and the `fake-game` test tool. The iroh transport code
moves to `Native/YargLink` in the YARG fork, and new work happens there.
