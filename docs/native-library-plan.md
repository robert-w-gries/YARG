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

## MVP goals

We aren't maintainers, so the MVP has to be easy to review and easy to say yes to:

- **No major architectural changes.** Online code lives in its own place (`Assets/Script/Integration/Link`,
  `Native/YargLink`). Changes to existing code are small hooks, have no effect while the link is off, and
  reuse existing paths (replay playback, the player list, `EngineManager`) rather than replacing them.
- **No YARG.Core changes.** The `YARG.Core` pin stays on an upstream commit. When something would be easier
  with a Core change, we accept a documented limitation instead (see
  [known limitations](#known-limitations-call-out-in-the-mvp-write-up)), and list the Core change as a
  possible follow-up for the maintainers to decide on.

Both are tradeoffs against polish, and the MVP write-up should say so.

### Where the work stands (2026-11-15, compared with `dev`)

- **YARG.Core: no changes.** The pin is the same as on `dev`.
- **Online code is separate:** about 4,900 lines of C# in `Integration/Link`, plus the Rust library and
  tests. Behind an Experimental setting (`LinkEnable`), off by default.
- **Existing code: 20 files, +675 / −66 lines**, of which 247 are the "Online" main menu entry in
  `MainMenu.prefab` (a `MenuEntry` instance, hidden while the setting is off). Most of the rest are
  `IsRemote` checks (no sounds, input viewer, stem muting or rewinds for friends' tracks), plus a small
  `MenuManager.CurrentMenu` getter and the settings, launch options and text for the link. The ones
  reviewers will look at most closely:
  - **Each player has its own clock.** `BasePlayer.TimeLag` and `PlayerVisualTime` let a friend's track run
    behind the song. Track elements now read the player's time instead of `GameManager.VisualTime`. For local
    players the lag is always 0, so nothing changes for them, but "every track shares one clock" no longer
    holds in the code.
  - **Engine managers for friends.** `GameManager.EngineManagerFor` gives each friend their own
    `EngineManager`. Every player registers through it. The host-authoritative fail meter will route friends'
    engines into the host's main manager instead.
  - **Synced start.** An `await` in `GameManager.Loading` holds on the loading screen until the start is
    scheduled, then adds friends' tracks.
  - **Fail sync.** The fail routine is shared by a local fail and a fail from the link.
  - **Band fail meter.** On the host, friends' engines join the main `EngineManager`. On a client, the main
    `EngineManager` runs with fail off, and `FailMeter` reads `GameManager.BandHappiness` (the host's value
    on a client) instead of `EngineManager.Happiness`. `TrackPlayer.FailFromLink` and
    `GameManager.ReviveFromLink` apply the host's decisions.
- **The biggest ask isn't in the game code: Rust in the native build** (open question 2). It adds a language
  and toolchain to the build, even though it doesn't change YARG's architecture.
- **The fail meter fits the goals.** The host meter uses `EngineManager` as it is, accepting the star
  power score drift. Reviving (`EngineManager.RevivePlayer`) and marking a player failed
  (`EngineContainer.PlayerHasFailed`) are public in Core. Showing the host's meter value on a client took a
  small change in YARG's `FailMeter`, not in Core.

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
| iroh endpoint (a new identity per handle) | Lobby state and UI |
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
- [x] Linux build works with a friend over the internet: direct and relay paths, synced start, highways.

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
  *Superseded (security review, 2026-11-15):* every session now gets a new identity, so codes expire
  with their session and joiners can't be recognised across sessions. Old key files are deleted. See
  "Security review: admission and identity" below.
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

- [x] `native-link.yml` with Windows, Linux and macOS jobs, plus the export, architecture, glibc and signature checks.
- [x] `NativeBuild` `cargo` backend and the byte-identical check.
- [x] Commit the binaries under `Assets/Plugins/YargLink/{Windows,Linux,Mac}` with Unity `.meta` platform settings.
- [ ] Test across platforms: Windows ↔ macOS ↔ Linux.

Phase 3 notes (2026-11-15):

- `NativeBuild` takes `--library audio|link` for both `build` and `package`. The default is `audio`, so
  YargAudio's commands and `native-audio.yml` are unchanged (checked: the YargAudio build and its
  integration test still pass). `LinkBuildCommand` runs the Rust tests, builds the release library
  for each Rust target, checks it (glibc floor and exports on Linux; architectures and exports on
  macOS), runs the C harness (both macOS slices, x86_64 under Rosetta) and the C# tests with
  `YARG_LINK_LIBRARY` pointing at it, then copies it, writes it to `--output`, or verifies the
  committed plugin.
- **Byte-identical builds work on Linux.** Two builds at different source and `CARGO_HOME` paths
  (`/src` with `/root/.cargo`, and GitHub's `/__w/...` with `/github/home/.cargo`) give the same
  bytes. Without `--remap-path-prefix` they can't: the library embeds about 800 crate paths in panic
  locations. A simulated CI job (NativeBuild in `ubuntu:20.04` at GitHub's paths) matched the
  committed `.so`. The flags are passed as `CARGO_ENCODED_RUSTFLAGS`, so paths with spaces work.
  Windows isn't compared, as with YargAudio. On GitHub, the Linux and macOS builds match the
  committed plugins byte for byte.
- Outside `ubuntu:20.04`, NativeBuild builds the Linux library in a docker container of that image,
  with the toolchain and crates cached in docker volumes. That replaces `build-linux.sh`, which
  built without the path remapping.
- macOS: `MACOSX_DEPLOYMENT_TARGET=11.0` (YargAudio's value). Stripping is done by the linker
  (`-Wl,-x`, cargo's strip off) instead of afterwards, so the linker's ad-hoc signature stays
  valid. CI checks that the arm64 slice is `linker-signed`.
- Windows: static CRT plus `/Brepro`, and CI checks with `dumpbin` that the DLL doesn't depend on
  the VC++ runtime.
- `Tests/YARG.Link.Tests/YARG.Link.Tests.csproj` was never committed (`.gitignore` ignores
  `*.csproj`) and has been added with an exception. With `YARG_LINK_REQUIRE_NATIVE=1`, which
  NativeBuild sets, a missing library fails the native tests instead of skipping them.
- Binaries: Windows 10.0 MB, Linux 11.8 MB (newest glibc symbol 2.30), macOS universal 18.0 MB.
  The Windows and macOS plugins are the CI builds (fetched from the run's artifacts).
- CI runs on the fork through a draft PR (`online-native` → `dev`), because `workflow_dispatch`
  only works for workflows on the default branch. First green run: 43684107533, all three jobs.
  macOS: both slices `minos 11.0` with `LC_UUID`, arm64 signature `adhoc,linker-signed`, C harness
  passing on both slices (x86_64 under Rosetta).
- **macOS hang in `LoopbackLinkTransport.Dispose`, fixed.** The accept and UDP threads blocked in
  `AcceptTcpClient` and `UdpClient.Receive`. On Unix, .NET's `Socket.Dispose` wakes such a thread
  with `shutdown()` and waits for it, but on macOS `shutdown()` doesn't wake a listening or
  unconnected UDP socket, so `Dispose` never returned (in the game, it would freeze the main thread).
  The loops now use the async calls. The C# tests run with `--blame-hang-timeout 3m`, so a hang
  fails the job and names the test.
- NativeBuild only sets `CreateNoWindow` on children whose output it captures. Before, the output
  of the other children (cargo, the tests) was lost on Windows; this applied to YargAudio too.

### Phase 4: MVP polish (demo-ready)

- [x] Minimal lobby: host/join by ticket, member list with instrument, difficulty and ready state, leave/kick.
- [x] Copy/paste invite code in the UI (works without Discord).
- [x] Clear error states: incompatible version, song missing, peer lost.
- [x] Connection quality indicator (direct/relay, RTT).
- [x] Host-authoritative band fail meter (see [Band fail meter](#band-fail-meter)). Not yet tested in a real
  song; see the notes.
- [ ] Recorded demo plus a short write-up for maintainers. The write-up must list the
  [known limitations](#known-limitations-call-out-in-the-mvp-write-up).

Phase 4 notes (2026-11-15):

- **Lobby** (`LinkLobby.cs`): "Online" in the main menu, shown only while the Experimental setting is on.
  It's built from the existing dialogs (`ListDialog`, `RenameDialog`, `MessageDialog`), so it needs no new
  prefabs. Without a session it offers "Host a session", "Join with an invite code..." (paste into a
  text field), and "Join with the copied invite code" when the clipboard holds something that looks like
  one. In a session it lists every member: name, host tag, status, each player's instrument and difficulty
  (bots marked), and the connection. The host clicks a friend to remove them, after a confirmation.
  Buttons: Copy Invite Code, Leave Session (End Session for the host), Close. It refreshes twice a second
  and only rebuilds when something changed.
- **No "start" button.** As before, anyone picks a song and everyone in the menu is asked to pick their
  part. The lobby's "ready state" is the member's status: Browsing, Picking a part, Ready (loaded and
  waiting on the loading screen), Playing, Results.
- **Session layer:** a new `state` message (joiner → host, relayed in the roster) carries each member's
  status and players. `ByeMessage` gained a `code` (`left`, `incompatible`, `kicked`, `refused`), so a
  joiner knows whether retrying can help. `LinkSession.Kick`, a host-side `PeerRefused` event, and
  `LinkMember.ConnectionLost` / `LeftReason` (the host can tell "left" from "lost"). The additions are
  optional fields and a new message type, so `LinkProtocol.VERSION` stays 1. Version mismatch reasons
  no longer say "theirs/ours", because both sides show the same text.
- **Sessions start from the lobby.** Turning the setting on no longer hosts automatically.
  `-link-host` (`YARG_LINK_HOST=1`) is new, for development; `-link-join` still works. The default name
  is now the first non-bot profile's name instead of the computer's user name.
- **Error states:** a different version, being removed, and an invalid invite code each show a dialog in
  the menu (a toast elsewhere) and close the session. A lost connection keeps retrying, and the lobby
  says "Can't reach the host (reason). Trying again...". Hosts get a toast when someone with another
  version tried to join. A friend without the song tells everyone: "Robin doesn't have 'Song', so
  they'll sit this one out." Backing out of a friend's song says so too. Members leaving and lost
  connections get different toasts.
- **Connection quality:** "Direct · 34 ms" / "Relay · 120 ms" in the lobby (orange above 150 ms) and next
  to each friend in the in-game scoreboard. Joiners only connect to the host, so other joiners show
  "Through the host".
- **Band fail meter** (`LinkController.BandMeter.cs`): see [Band fail meter](#band-fail-meter).
- **Tested:** 3 new session tests, run on both transports (state through the roster, kick, left vs
  lost), and refusal codes checked in the existing ones. All of `Tests/YARG.Link.Tests` passes. Unity 6000.3.5f2 compiles with no errors. In a
  batch-mode Editor (via the `unity` CLI and Pipeline `script run`) the real game drove the lobby
  against in-process loopback sessions: hosting, a friend joining with parts shown, Ready status, the
  missing-song toast, removing through the confirmation dialog, ending the session, joining by pasting
  a code, leaving, the different-versions dialog, an unreachable host, and an invalid code.
- **Not yet tested:** the band fail meter in a real song (the Editor had no songs or profiles), the
  lobby's look (batch mode can't capture overlay UI), and controller navigation of the lobby list. All
  three need the Windows ↔ macOS ↔ Linux test from phase 3.

Security review: admission and identity (2026-11-15):

- **Invite codes last one session.** The persistent `link/secret.key` is gone (`key_file` was removed
  from the native config, and the game deletes an old key file the first time it opens a transport).
  Every host and joiner endpoint gets a new random identity, so a code that leaked stops working when
  its session ends, and a host can't recognise or dial a joiner later. Joiners also refuse incoming
  connections (`accept: false`). A future Discord join flow needs to hand out each session's code, not a
  stable one.
- **Joiners don't retry after the host ends the session** (`bye` code `left`): the code is dead by then.
  A connection that just dropped is still retried, because the host's endpoint (and so the code) is
  still up.
- **Kick:** `yl_peer_id` (ABI 3) gives the session each peer's endpoint ID (authenticated by the QUIC
  handshake), and `LinkSession` bans the IDs it kicks for the rest of the session, refusing them before
  saying anything. Endpoint IDs are free to make, so this only stops a game that reconnects as itself. The
  remove dialog therefore also offers "Remove and Change Code", which kicks, ends the session and hosts a
  new one with a new code (everyone else has to rejoin with it).
- **Hello order:** the host no longer sends its hello (name, game version) on connect. It waits for the
  joiner's hello, checks protocol, version, cap and ban, and only then replies. The joiner still speaks
  first.
- **Logs:** invite codes are logged as their first 8 characters (`LinkSession.ShortTicket`), the native
  library logs its own ID with `fmt_short`, and bad-ticket errors no longer repeat the input.

### Phase 5: after MVP

- [ ] Discord invite/"Ask to Join" through the bundled SDK.
- [ ] Soft-hash song matching.
- [ ] Data Stream relay inside the game (if still wanted).
- [ ] Reshape history and open the upstream PR.

## Band fail meter

Online players are one band, as in Rock Band: one crowd meter, failed players can be saved by a
bandmate's star power, and when the meter empties the whole band fails on every screen. YARG's local
multiplayer already behaves this way inside `EngineManager`.

Each game mirrors its friends' players by replaying their inputs. A mirror runs about 150 ms behind
its owner, so if every game ran the whole band in its own `EngineManager`, interactions between
players (meter drain, saves, the star power multiplier, unisons) would happen at slightly different
moments on each machine, and the games would disagree. So the host's `EngineManager` is the source of
truth for the meter:

- **Host:** friends' mirrored engines register in the host's main `EngineManager`, so its fail meter
  works like local multiplayer.
- **Clients:** their own `EngineManager` runs with fail off, and friends' mirrors keep their own
  no-fail managers. The host sends its manager's decisions: the band failed (`failed`, already
  implemented), a player failed or the band was revived (to lower or restore that player's highway),
  and the meter value (so the client's meter shows the band's).

Current state (2026-11-15): implemented as above. The host's `EngineManager` events drive
`player_failed` (sent once per failed player until a revive) and `revived`. The meter value goes out about
ten times a second as an unreliable `meter` message. On a client, a failed player's highway goes down
(`TrackPlayer.FailFromLink`), and every engine waits for a revive (`EngineContainer.PlayerHasFailed`), so
the next star power activation saves the band, as in local multiplayer. Only the host's meter can fail
the song, and the host's no fail setting is the one that counts.

## Known limitations (call out in the MVP write-up)

- **Scores can drift with star power.** `EngineManager` can't register an engine for the fail meter
  alone. With friends' engines in the host's main manager, they also count toward its star power band
  multiplier and unisons. A client's star power raises the host's multiplier, but the host's star
  power doesn't raise the client's, because the client's game doesn't share them. Fails and saves stay
  correct on both games; scores differ slightly when star power overlaps. A possible fix is a small
  YARG.Core option to register an engine for the fail meter only, which needs the maintainers' go-ahead.
- **The fail menu only affects the local game.** "Restart" and "continue with no fail" aren't synced.
- **Pause isn't synced.** If a friend pauses, their highway freezes on everyone else's screen until
  they resume.
- **Revives near the fail moment can disagree by a fraction of a second.** Mirrors run about 150 ms behind
  their owner, so star power used right around a fail or revive can count as a revive on one game and as
  plain star power on another. Fails, saves and scores stay with the host and each player's own game;
  only a mirrored highway's star power display can be briefly wrong.
- **Vocals and pro guitar players aren't mirrored.** Their `setup` entry has no replay frame (`frame: -1`,
  from `LinkController.CanMirror`), so friends' games make no highway or engine for them. They still show in
  the lobby and play normally on their own game. The reasons differ:
  - *Pro guitar* has no replay support in YARG.Core: `ReplayFrame` can't serialize its engine parameters
    and stats ("Stat creation not implemented"). Fixing it is a Core change, so it's a follow-up for the
    maintainers.
  - *Vocals* can be serialized; the limit is in YARG. Every vocalist shares one `VocalTrack`, which
    `GameManager.Loading` sets up from the local vocalists (harmony parts included). `CreateRemotePlayers`
    only adds separate `TrackPlayer` highways afterwards, and skips vocals. Mirroring them means adding
    remote singers to the shared track and assigning harmony parts across games. No Core change is needed.

  What happens to the band meter depends on whose game the player is on:
  - **On a client**, the host has no engine for them, so they aren't in the band's meter. They can't fail
    the band, and their star power can't save it. The client's own meter runs with fail off, so for them
    it's effectively no fail.
  - **On the host**, they're local to the host's main `EngineManager`, so they do count: they can fail
    the band and save it, although no friend can see their track.
- **If the host drops mid-song, clients' meters stop updating** and the song can no longer fail for them.
- **The crowd and venue follow each game's own meter**, not the band's, on clients.

## Open questions

1. Do maintainers have a preferred direction for online play (P2P like ours or server-based like the `asimard1` fork)?
2. Will maintainers accept Rust in the native build, or do they want a different language or approach?
3. Is the lobby host-authoritative (the host runs the coordinator, as today), or does any member coordinate?
4. Which minimum macOS version does YARG target?
5. Would maintainers accept a YARG.Core option to register an engine for the fail meter only? It would
   remove the star power score drift described under known limitations.
6. Should the host's vocalists and pro guitarists count toward the band meter while friends can't see
   them? Today a client's can't fail the band and the host's can.
