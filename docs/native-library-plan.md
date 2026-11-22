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

### Where the work stands (2026-11-18, compared with `dev`)

- **YARG.Core: no changes.** The pin is the same as on `dev`.
- **Online code is separate:** about 6,900 lines of C# in `Integration/Link`, plus the Rust library and
  tests. Behind an Experimental setting (`LinkEnable`), off by default.
- **Existing code: 32 files, about +1,360 / −130 lines** (not counting the screen setting fix and the two
  unrelated fixes under [Branch strategy](#branch-strategy-yarg-fork)). 247 of them are the "Online" main
  menu entry in `MainMenu.prefab` (a `MenuEntry` instance, hidden while the setting is off), and about 300
  are Discord invites in `DiscordController.cs`. Most of the rest are `IsRemote` checks (no sounds, input
  viewer, stem muting or rewinds for friends' tracks), the hidden online pause options, the band score
  box, a small `MenuManager.CurrentMenu` getter and the settings, launch options and text for the link.
  The ones reviewers will look at most closely:
  - **One place for online gameplay code.** `GameManager.Online.cs` has all the online members of
    `GameManager`. `GameManager.IsOnlinePlay` is the one check: it is true only when the session scheduled a
    synced start for the song. The other GameManager code calls the link through `GameManager.Link`, which is
    `null` when the song is not online play.
  - **A friend's engine runs behind the song.** `BasePlayer.EngineLag` is how far a friend's engine is
    behind, because their inputs arrive a network delay late. Their highway is drawn at the song's
    `GameManager.VisualTime`, like every other highway, and `LateHitAllowance` keeps passed notes so their
    late hits still find them.
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
  - **Discord invites.** `DiscordController` advertises the online session as a Discord party and joins
    a session from an accepted invite. It only reads `LinkController`; nothing in the link depends on
    Discord.
- **The biggest ask isn't in the game code: Rust in the native build** (open question 2). It adds a language
  and toolchain to the build, even though it doesn't change YARG's architecture.
- **The fail meter fits the goals.** The host meter uses `EngineManager` as it is, accepting the star
  power score drift. Reviving (`EngineManager.RevivePlayer`) and marking a player failed
  (`EngineContainer.PlayerHasFailed`) are public in Core. Showing the host's meter value on a client took a
  small change in YARG's `FailMeter`, not in Core.

### Before sharing with maintainers

- [ ] **Finish reviewing all code.** Still to do: the rest of the node code (after the split into
  `src/node/`) and all of `LinkController`.
- [ ] **Direct connections only during gameplay**, so synced songs never depend on the public n0 relays.
  A relayed connection can still join a session and use the lobby, but can't join a synced song. Long
  term this will likely flip: a privacy option that requires relayed connections, through a YARG-owned
  relay server (see [Relays and address lookup](#relays-and-address-lookup)).
- [ ] TODO

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

## Compared with the server-based fork

The [`asimard1/YARG` `feature/merged-multiplayer`](https://github.com/asimard1/YARG/tree/feature/merged-multiplayer)
branch takes the other approach (open question 1). It has a central ASP.NET lobby service at
`lobbies.yarg.online` (REST plus SignalR over WebSockets, with JWT auth) and a dedicated LiteNetLib UDP
game server. All gameplay traffic goes through that server, which fans each message out to the other
players (`Assets/Script/Online/GameClientSession.cs`, `LobbyGameOrchestrator.cs`). Besides hole
punching, encryption, relays and invite codes (above), iroh gives us these benefits over it. They are
worth repeating in the MVP write-up.

**Latency and path**

- **One hop instead of two.** Their inputs go client → game server → client. A direct iroh connection
  goes straight to the friend. That matters because mirrors already run about 150 ms behind their owner.
- **LAN play stays on the LAN.** iroh finds local addresses and uses them. Their stack sends two players
  in the same house out to the internet server and back. A ticket that carries addresses also works with
  no internet access.
- **It keeps choosing the best path.** iroh tries IPv4 and IPv6 addresses at the same time and switches
  between relay and direct paths as conditions change. Their client uses the first DNS result for the
  game server only (`LobbyGameOrchestrator.ParseEndpoint`).
- **Network changes don't drop the session.** QUIC moves the connection to the new address when a laptop
  changes Wi-Fi or a hotspot changes IP. A LiteNetLib peer is tied to one IP and port.

**Reachability**

- **Networks that block UDP.** LiteNetLib only speaks UDP, so school, hotel and corporate networks that
  block it can't play. iroh's relay falls back to HTTPS/WebSocket on port 443.

**Security**

- **Their game token is sent unencrypted.** `GameClientSession.ConnectAsync` writes the JWT into
  LiteNetLib's connection request, and LiteNetLib doesn't encrypt anything. Anyone on the network path can
  read the token and reuse it until it expires, and can read or forge game packets. Only their lobby
  traffic has TLS. Every byte we send runs over QUIC with TLS 1.3, and the handshake proves each peer's
  endpoint ID, which is why a kick by endpoint ID can't be spoofed.
- **No accounts or tokens.** They need an auth service (a dev-auth stub today, in
  `OnlineAccessTokenProvider.cs`) and per-game tokens with expiry. We need neither.

**Operations and longevity**

- **Servers are a fallback, not a requirement.** Their game only works while the lobby service and game
  servers are up and someone pays for them, and load grows with every concurrent game. Our relay only
  carries traffic when hole punching fails. If it goes down, direct connections keep working, and anyone
  can run `iroh-relay`.
- **Simpler for upstream to accept.** Maintainers don't take on a backend to run, secure and moderate
  (their branch includes lobby chat, which needs moderation on the server).
- **Testing without servers.** `cargo test` and the C# tests run two endpoints over localhost. Theirs
  needs the lobby service running.

**Transport features**

- **No head-of-line blocking.** Every one of their sends is `ReliableOrdered`, including high-rate whammy
  and vocal pitch samples, so one lost packet holds up everything behind it, note misses included.
  LiteNetLib has sequenced and unreliable modes, so this is partly how they use it, but QUIC datagrams
  and separate streams make the right choice easy. We already send unreliable messages as datagrams.
- **Congestion control and connection stats** (RTT, path type) come built in, with no tuning like their
  15 s `DisconnectTimeout` to survive GC pauses.

**Where their approach is stronger**

- **Lobby discovery.** A server can list public lobbies. P2P needs some directory for that (see
  [Future network features](#future-network-features)).
- **Host drop and cheating.** A neutral authoritative server survives any one player leaving and can
  check results. Our "host drops mid-song" limitation doesn't exist for them (host migration in
  [Phase 5](#phase-5-after-mvp) would remove it).
- **IP privacy.** Direct connections show each player's IP address to the others. Theirs only show it to
  the server.
- **No second language.** It's pure C#, with no Rust toolchain (open question 2).

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

- `NativeLinkTransport`: the Rust library. The game uses only this one.
- `MemoryLinkTransport`: in memory, in `Tests/YARG.Link.Tests` only, never in
  the game. The transport and session suites run on it and on the native
  transport, so the contract has two implementations. It can also cut a
  connection without a goodbye and drop datagrams on purpose.

Everything above this interface (`LinkSession`, the lobby, the coordinator,
the Discord glue) has no idea which transport it is running on. If upstream
rejects Rust, only `NativeLinkTransport` gets replaced.

*Changed 2026-11-18:* until then the game also had `LoopbackLinkTransport`
(plain TCP and UDP, for development, chosen by `-link-transport loopback` or a
`loopback:` ticket, with a fallback to it when a platform had no native
library). It was removed: every platform now ships the native plugin, two
copies on one machine work over native, and a plain, unencrypted listener on
port 36120 had no place in release builds. `-link-transport`, `-link-port`
and `-link-lan` went with it.

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

## Relays and address lookup

**Decision (2026-11-16):** use n0's public relays through the MVP and cross-device testing. Before a
public release, run our own relay and keep n0's public relays as a fallback.

### Where n0 infrastructure is used today

- **Relays:** `RelayMode::Default` (`Native/YargLink/src/node/mod.rs`) uses n0's public relays. A relay
  only carries traffic when hole punching fails (`PATH_RELAY` in the stats). Relayed traffic stays
  end-to-end encrypted.
- **Address lookup:** `presets::N0` also uses n0's DNS/pkarr service, which is how a bare endpoint ID
  ticket becomes reachable addresses. Tickets that carry `@addr,addr,relay-url` work without it.

### Why not rely on the public relays for release

- n0 says the public relays are for development and testing, not production. They are free,
  rate-limited, and come with no uptime or performance guarantees.
- n0 can upgrade or change them at any time. We pin iroh (1.4.1), so a relay-side change could break
  players with no change on our side.
- Bandwidth isn't the concern. Relayed sessions carry hellos, roster and state messages, a song setup
  at song start, and input streams for a few players. That should be kilobytes per second per session
  (not yet measured).

### Options for release

| Option | Cost | Notes |
|---|---|---|
| Self-hosted `iroh-relay` | One small VPS per region | Open source. Needs a domain and TLS (built-in ACME), with TCP 80/443 open and UDP 7842 for QUIC address discovery. |
| n0 shared relays (Pro plan) | Subscription | Multi-tenant, 5 MB/s rate limit, 100 GB egress/month, 10,000 concurrent connections, API-key auth. |
| n0 dedicated relays | About $199/month per region on top of Pro (check current pricing) | Single-tenant, no shared rate limits. |

### Release work

- Switch to `RelayMode::Custom` with our relay(s) first and n0's public relays after them in the relay
  map. Start with one region; add NA/EU/Asia if relayed latency needs it.
- Make relay URLs configurable (setting or command-line argument) so forks and self-builders can
  point at their own.
- Decide on address lookup: self-host `iroh-dns-server`, keep n0's DNS, or rely on full-address
  tickets.
- Add the abuse protection below (per-client rate limit, shared token, monitoring).
- Measure relayed bandwidth per session to size the VPS and the rate limit.

### Self-hosting cost estimate

Estimated 2026-11-16 for 25–50 users a month. The traffic figures are guesses until relayed
bandwidth is measured.

- **Traffic:** 50 users × ~10 hours a month = 500 user-hours. At a generous 20 KB/s per user, counted
  in and out of the relay, that's ~150 MB per relayed user-hour. If every connection were relayed,
  that's ~75 GB a month. With hole punching succeeding most of the time (10–30% relayed), it's more
  like 5–25 GB. Entry-level VPS plans include 1–20 TB, so bandwidth isn't what decides the cost.
- **CPU and memory:** `iroh-relay` is one Rust binary. 1 vCPU and 512 MB–1 GB of RAM is plenty.

| Item | Option | Approx. cost |
|---|---|---|
| VPS, one region | Hetzner CX22 (20 TB transfer in the EU, less at US locations) | ~$4–5/month |
| | DigitalOcean / Vultr / Linode basic tier (0.5–1 TB) | $4–6/month |
| | Oracle Cloud Always Free (ARM) | $0, but accounts sometimes get reclaimed, so it isn't reliable enough for a release |
| Domain | Needed for TLS, or use a subdomain of one we already own | ~$10–15/year, or $0 |
| TLS | `iroh-relay`'s built-in ACME (Let's Encrypt) | $0 |
| `iroh-dns-server` (optional) | Can share the same VPS | $0 extra |

- **Total:** about $4–7 a month plus the domain, so roughly $60–100 a year. n0's dedicated relays
  (~$199 a month per region) cost 30–50× that for no benefit at this scale. Check current prices
  before choosing; several providers raised prices in 2025.
- **Time:** 1–2 hours to set up (DNS record, ports, systemd unit, config). After that, OS updates and
  upgrading `iroh-relay` to match our pinned iroh version.
- **More regions:** NA, EU and Asia together would be about $15–20 a month. Only worth it if players
  report high latency on relayed connections.
- **Downtime:** clients fall back to n0's public relays, so an outage doesn't stop people playing.

### Abuse protection

Relayed traffic is end-to-end encrypted and the client is open source, so the relay can't see what's
sent and can't truly tell YARG from other clients. Any token we ship can be extracted. The goal is to
make the relay useless for anything other than the game, and to put a ceiling on what abuse can cost us.
All of the settings below exist in `iroh-relay` 1.4.1.

1. **Per-client rate limit (most important).** Game traffic is a few KB/s. A cap a few times the
   game's peak makes the relay useless for file transfer, video or proxying. Text messages sent
   through it would still get through, but they cost us nothing.
   ```toml
   [limits.client.rx]
   bytes_per_second = 100000   # placeholder: set from measurements
   max_burst_bytes  = 500000
   ```
   Size it for the **host**: joiners only connect to the host, so a relayed host carries the roster and
   every joiner's inputs. Measure a full session with the most players first. `limits.accept_conn_limit`
   and `accept_conn_burst` also limit how fast new connections are accepted.
2. **Shared token.** Stops other iroh apps and scanners from using our relay as a free public relay,
   which is the most likely misuse. Server: `access.shared_token = ["..."]` (or the
   `IROH_RELAY_ACCESS_TOKEN` environment variable). Client: `RelayConfig::with_auth_token(...)` on our
   entry in the `RelayMode::Custom` map. It only stops casual use, because anyone can read it. Make it
   a build or config value next to the relay URL so forks can set their own, and change it between
   releases if it gets copied. Not implemented yet: `node/mod.rs` only uses `RelayMode::Default` or
   `Disabled`.
3. **HTTP access check (only if misuse shows up).** `access.http = { url = "...", bearer_token = "..." }`
   asks our own service about each endpoint ID that connects. A `200` with `true` lets it in. That
   gives a denylist and kill switch we can change without restarting the relay. It can't check that a
   client is really YARG, and every handle gets a fresh endpoint ID, so bans are weak. Real gating
   would need something like signing in with Discord to get a short-lived pass, which is overkill at
   this scale.
4. **Limit the bill and watch usage.** Use a host with a lot of included transfer and cheap overage,
   and turn on billing alerts. Set `metrics_addr` and watch relayed bytes: a jump with no rise in play
   time means misuse. Logs show which endpoint IDs connect, for a denylist. Open only TCP 80/443 and
   UDP 7842.
5. **Legal.** The relay forwards encrypted packets and stores nothing. A short acceptable-use note and
   an abuse contact on the relay's domain covers abuse reports.

Plan: do 1, 2 and 4 when the relay launches, and add 3 only if the metrics show misuse.

Sources: iroh docs on [relays](https://docs.iroh.computer/concepts/relays),
[rate limiting](https://docs.iroh.computer/relays/rate-limiting),
[adding a relay](https://docs.iroh.computer/add-a-relay),
[shared relays](https://docs.iroh.computer/iroh-services/relays/shared) and
[managed relays](https://docs.iroh.computer/iroh-services/relays/managed).

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| **Upstream rejects a Rust toolchain.** The team works in C++, CMake and .NET. | Raise it with maintainers before the PR. Fit Rust into `NativeBuild` and CI exactly the way YargAudio is built. Keep the transport swappable behind `ILinkTransport`. |
| **Overlap with other multiplayer work.** A separate fork (`asimard1/feature-multiplayer`) has a server-based SignalR lobby (`LobbyHubSession`, `LobbyViewMenu`, soft-hash library matching). It is another experimental implementation and, as far as we know, maintainers have not chosen it. | Mention it in the maintainer discussion and borrow good ideas (for example soft-hash matching). Our design needs no server, which sets it apart. |
| **Byte-identical build check** (`--verify-committed-plugin`) | Pin the toolchain with `rust-toolchain.toml`, use `--locked`, `--remap-path-prefix`, `codegen-units=1`, `CARGO_INCREMENTAL=0`. Prove it on macOS first. |
| **Unity cannot unload native libraries.** Rebuilding needs an Editor restart, and Windows locks the DLL. | C# work uses the committed plugin with no rebuild; the session layer is tested on `MemoryLinkTransport`. Rebuild native code only when the transport itself changes. |
| **Native state outlives play mode and domain reloads** | Handle-based API, one runtime per handle, `yl_destroy` that is reliable and safe to call twice, torn down from `OnDestroy` and `Application.quitting`. |
| **Panics or crashes inside the game process** | `catch_unwind` around every exported function, no `panic=abort`, errors returned as status codes. Log through a polled log-event channel. |
| **Threading** | Rust never calls into C#. C# polls from `Update()`. This also keeps a future IL2CPP switch easy (the project is Mono today). |
| **Binary size** (iroh + tokio + rustls) | `opt-level="s"`, `lto="fat"`, `strip=true`, `panic="unwind"`, iroh default features trimmed. Measure early; expect several MB per platform, about double for the macOS universal file. |
| **Version mismatch between friends** | Version handshake on connect: the exact game build. Refuse incompatible peers with a clear message. |
| **Discord Game SDK is deprecated** | Invites use Activity join in the bundled `DiscordGameSDK` (see the phase 5 notes). The long-term path is Discord's Social SDK. The Discord glue stays in `DiscordController`, and invites also work through a plain copyable ticket. |
| **n0's public relays aren't meant for production** (rate-limited, no uptime guarantee, can change at any time) | Fine for the MVP. Before release, run our own relay with n0's as a fallback; see [Relays and address lookup](#relays-and-address-lookup). |
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

The screen setting fix (`ScreenHelper`, `CommandLineArgs`, `SettingsManager`) is also a separate
commit. It is a side fix that multiplayer doesn't need: Unity's window arguments (`-screen-fullscreen`,
`-screen-width`, `-screen-height`, `-window-mode`, `-popupwindow`) now win over the saved Fullscreen Mode
and Resolution at startup, which makes it easier to run two instances side by side when testing. It isn't
counted in the existing-code totals above, and it can go upstream as its own PR or be left out.

Two more fixes on the branch aren't online code either, and are also left out of the totals: a
`FocusHandler` null reference when the game starts unfocused (`368405d39`), and the unison display
showing phrases that lost players to a drop-out (`1f9de9f15`). Both also happen in local play.
`docs/online-review-areas.md` describes them; each can go upstream as its own PR.

## Planned steps

### Phase 0: preparation

- [x] Tag `sidecar-prototype`, add the `upstream` remote, rebase, create `online-native`. (2026-11-14: `upstream/dev` hadn't moved, so no rebase was needed.)
- [ ] Open a discussion with YARG maintainers covering: the feature, the Rust toolchain, binary size, byte-identical builds, the swappable transport, and how this relates to other multiplayer/lobby work.

### Phase 1: session layer in C#, on the loopback transport

- [x] Define `ILinkTransport`, `LinkEvent`, `PeerId`, `Delivery`, `PeerStats`.
- [x] Implement `LoopbackLinkTransport`. (Removed 2026-11-18 in favour of a test-only in-memory transport; see [C# interface](#c-interface).)
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
- Handshake: a `hello` with the exact game version, member ID, and a host flag. Mismatches
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
  with their session and joiners can't be recognised across sessions. See
  "Security review: admission and identity" below.
- *(Loopback superseded 2026-11-18: the game uses only the native transport.)* `LinkController` uses the native transport by default. `-link-transport loopback` (or a `loopback:`
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
- **Band fail meter** (`Song/BandMeterSync.cs`): see [Band fail meter](#band-fail-meter).
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
  from the native config). Only development builds wrote the key file, so the game does not delete old
  key files.
  Every host and joiner endpoint gets a new random identity, so a code that leaked stops working when
  its session ends, and a host can't recognise or dial a joiner later. Joiners also refuse incoming
  connections (`accept: false`). The Discord join flow hands out each session's code (see the phase 5
  notes).
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

- [x] Discord invite/"Ask to Join" through the bundled SDK.
- [ ] Soft-hash song matching.
- [ ] Data Stream relay inside the game (if still wanted).
- [ ] Self-hosted iroh relay with n0's public relays as a fallback, plus configurable relay URLs and an
  address lookup decision (see [Relays and address lookup](#relays-and-address-lookup)).
- [ ] Relay abuse protection: per-client rate limit, shared relay token in the client, and usage
  monitoring (see [Abuse protection](#abuse-protection)).
- [ ] Rejoin after a drop: a dropped player reconnects with the same ticket, and the host sends the song
  position and current state. The host keeps the dropped player's slot for a short time and recognises
  them by endpoint ID (endpoint IDs are per session, so this only works within the same session).
- [ ] Host migration: when the host leaves, the next member in roster order becomes host and the others
  reconnect to it. Removes the "host drops mid-song" limitation. Every member needs the meter state, which
  the host already sends about ten times a second.
- [ ] Reshape history and open the upstream PR.
- [ ] Pick from [Future network features](#future-network-features) once the MVP is reviewed.

Phase 5 notes (2026-11-17):

- **Discord invites** (`DiscordController.cs`). While the game is in an online session (hosting, or let
  in by the host), its Discord activity advertises a private party. The join secret is the session's
  invite code, and the party ID is a hash of it, so everyone in the session shows the same party without
  the code being visible to anyone who can see the activity. Party size is the member count; supported
  platforms is Desktop (left at zero, Discord offers "Join from a supported device"). An invite code
  longer than Discord's 127-byte secret (one with addresses) isn't advertised, because a shortened code
  would send friends somewhere else. A bare endpoint ID fits.
- **Joining from Discord.** Accepting an invite, or having a request to join accepted, joins the session.
  Mid-song, it waits until the player is back in the menu (with a toast). With the link off, a toast
  says how to turn it on. Outside the Editor, the game registers its launch command so Discord can start
  it for an invite.
- **Reaching Discord.** When Discord isn't running at startup, the game tries again when it gets focus
  or goes online, and logs the failure only once. `-discord-app-id` (or `YARG_DISCORD_APP_ID`) uses
  another Discord application, for testing with a private app; the launch command it registers then
  carries the same option.
- **Inviting needs a session.** Discord only offers Invite and Ask to Join while there's a party to
  advertise, so the player hosts first. The Game SDK doesn't tell the game when someone opens Discord's
  invite menu, so it can't host in response to it.
- **Pause on focus loss.** Pausing online pauses the whole band, so online play has its own setting,
  "Pause On Focus Loss in Online Mode" (off by default). The normal setting is ignored while the link is
  enabled.
- **Leaving a song and losing the host** (`Song/HostLoss.cs`). The host's Back to Library asks
  whether to leave with the party (`endsong`, every joiner goes back to the library) or alone. The host
  drops out a friend who left the song or whose inputs stopped for 5 s and tells everyone (`dropped`);
  a dropped friend returns to the library, even if they reconnect. A joiner whose host left the song,
  ended the session or went silent sees a countdown and returns to the library (after 5 s for a lost
  connection, 10 s for a host that left), instead of waiting in a pause that would never resume.
- **Native library hardening.** A peer that stops reading is now disconnected once 32 MiB or 64k
  reliable messages wait to go out to it, matching the limit on the incoming side. `read_frame` reads
  the kind byte on its own, so a payload (up to 1 MiB) is no longer copied twice, and the log cap (4096
  lines) is now checked and counted in one atomic step. The ABI stays at 3.
- **Closing off the main thread.** `yl_destroy` can block for about 2.5 s while goodbyes go out.
  `NativeLinkTransport.Dispose` now destroys the handle on its own thread, and the game waits up to 3 s
  for those threads when it quits (`NativeLinkTransport.WaitForClosedHandles`), so goodbyes still go out.
- **Plugins.** All three plugins are CI builds of `2c9936a43` (run 44234338625). The Windows and macOS
  plugins had still been ABI 1 builds from before `yl_peer_id`, which the game refused to load.
  `libyarg_audio.so` had been rebuilt by the Editor on a newer host (glibc floor 2.43); the CI-built copy
  from `dev` is back.
- **Not yet tested:** Discord invites between two machines and accounts, and the Discord-started launch
  in a release build (the Editor skips registering the command).
- **ABI 4 (second review of the library).** A `yl_handle *` is now a number in a registry, not an
  address, so a wrong or destroyed handle gives an error (`YL_ERR_CLOSED` for a destroyed one), and
  `yl_destroy` waits for calls on other threads. Pointer arguments are checked first, and output
  buffers may no longer be NULL (also with `cap` 0). `yl_event.level` is now `log_level`. The config is
  strict: unknown members (also the old `key_file`) and values out of range give `YL_ERR_BAD_CONFIG`,
  `"accept"` is off by default, and `address_lookup` off needs `ticket_addrs` on. Tickets have a length
  and address limit. `node.rs` is split into `src/node/`. The wire format and ALPN did not change.
  The committed plugins are ABI 3 builds, and the game refuses them until they are built again.
- **Session player limit.** A session has at most 32 players (`LinkLimits.MAX_SESSION_PLAYERS`, the
  number of highway slots). A host refuses a new joiner when the session has 32 players, and a game
  adds no highway after the 32nd. The 8-joiner limit (`MaxMembers`) stays, for the network.

## Future network features

None of these are needed for the MVP. They are listed to show what the iroh endpoint makes cheap. A
feature that only needs messages fits the existing pipe: the session layer adds a message kind, and the
native library doesn't change. A feature that needs a separate iroh protocol (such as iroh-blobs) runs
on the same endpoint under its own ALPN, so it reuses the same hole-punched paths, relays and
encryption, but it does add functions to the C interface.

| Feature | How | Notes |
|---|---|---|
| **Lobby and in-song chat** | New reliable message kind on the existing stream. The host relays chat to the lobby, like roster updates. | No server to moderate: mute and block are local, keyed by the sender's authenticated endpoint ID. Keep a length limit and a rate limit per peer. |
| **Official song sharing** | [iroh-blobs](https://github.com/n0-computer/iroh-blobs) on the same endpoint. **Only official YARG songs** (the setlists the YARC Launcher installs, found through `PathHelper.SetlistPath`) can be shared. A player who is missing one fetches its files from the host or any member who has it, as a BLAKE3-hashed collection. | **Nothing else is ever offered or accepted:** no custom songs, no songs from the player's own song folders, and no files added to the setlist folder by hand. A file being in the setlist folder isn't enough, because anyone can copy files there and a modified client can claim anything. Both sides check each collection against a list of official song hashes that the game trusts, not one the sender provides. The sender only offers songs on the list, and the receiver refuses any request or download that isn't on it before saving anything. Downloads are checked against the hash as they arrive and can resume. It also helps players who skipped a setlist in the launcher. Audio is tens of MB, so it needs a progress UI and an accept on the receiving side. Check that an iroh-blobs release supports our iroh version (1.4.1). See open question 7. |
| **Replay and score sharing** | iroh-blobs, or a reliable message for small replays. After a song, each player's replay file is offered to the others. | Lets the score screen open friends' replays. Reuses YARG's replay format. |
| **LAN lobby list** | iroh's local network (mDNS) address lookup. Games on the same network advertise open lobbies. | Join with no invite code and no internet. Off by default (it announces the game on the network). |
| **Public lobby directory** | A small directory that lists open lobbies by ticket, or publishing lobby records through pkarr/DNS. | Covers the lobby browser that a server-based design gets for free. The directory only stores tickets; game traffic still goes peer to peer. |
| **Spectators** | Joiners that send no inputs and get mirrors of every player. For many spectators, fan out with iroh-gossip instead of the host sending to each one. | Spectators already fit the roster model as players with no track. |
| **Voice chat** | Opus frames as unreliable datagrams. | Easy on the network side; the hard parts are audio capture, echo cancellation and mixing with the song, so it's the lowest priority. |

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
ten times a second as an unreliable `meter` message. It also carries each counted player's value, grouped by
member (`players: { member: [happiness or null, ...] }` by setup index). A client's fail meter shows an
icon for every player in that list, at the host's values: its own players, friends' mirrored players, and
friends' players with no highway there (vocals, pro guitar), which get their instrument's icon. Until the
first `meter` message, a client shows its own players at its own values. On a client, a failed player's highway goes down
(`TrackPlayer.FailFromLink`), and every engine waits for a revive (`EngineContainer.PlayerHasFailed`), so
the next star power activation saves the band, as in local multiplayer. Only the host's meter can fail
the song, and the host's no fail setting is the one that counts.

## Known limitations (call out in the MVP write-up)

- **Band star power is set from outside YARG.Core.** Each game counts its own players' star power and
  the star power that friends report in the `inputs` message (`sp`), and gives that multiplier to its
  engines before each player updates (`GameManager.ApplyOnlineBandMultiplier`). Thus every game gives
  band bonus from the whole band and shows the same multiplier. Friends' star power arrives a network
  delay late, and a replay doesn't have it. On the host, friends' engines still take part in its
  unisons. A cleaner fix needs YARG.Core (see [YARG.Core follow-up work](#yargcore-follow-up-work),
  items 1 and 2).
- **Online play hides the menu options that would leave friends behind.** When friends are in the session,
  gameplay sets `GameManager.IsOnlinePlay` when the synced start is scheduled, and the pause, fail and quick
  settings menus hide Restart, Practice, Skip and the no fail options (`GenericPause.BuildOnlineOptions`,
  matched by the method each button calls). The score screen hides Restart too
  (`ScoreScreenStats.IsOnlinePlay`). The no fail setting is ignored in online play
  (`GameManager.NoFailMode` is always `Off`), because the band's meter is shared. Back to Library stays.
- **Pause is synced, but the pause menu's other options aren't.** Anyone pausing pauses everyone
  (`LinkController.PauseSync`, `PauseCoordinator`). Resume marks a player ready; once everyone is, the
  host resumes all games from one input time a second before the latest pause, at the same moment.
  Quitting and settings in the pause menu only affect the local game; a friend who quits to the menu
  stops being waited for, and the others carry on without them.
- **Each play of a song is separate.** Every message about a synced song carries a play ID
  (`InputSender.PlayId`) next to the song hash, and a game sends `left` when its `GameManager` is destroyed.
  Messages from another play of the same song are ignored, so a game that reloads gameplay (which the
  hidden options above did) can't mix with friends' songs. Restarting together isn't supported.
- **Revives near the fail moment can disagree by a fraction of a second.** Mirrors run about 150 ms behind
  their owner, so star power used right around a fail or revive can count as a revive on one game and as
  plain star power on another. Fails, saves and scores stay with the host and each player's own game;
  only a mirrored highway's star power display can be briefly wrong.
- **Vocals and pro guitar players aren't mirrored.** Their `setup` entry has no replay frame (`frame: -1`,
  from `InputSender.CanMirror`), so friends' games make no highway or engine for them. They still show in
  the lobby and play normally on their own game. The reasons differ:
  - *Pro guitar* has no replay support in YARG.Core: `ReplayFrame` can't serialize its engine parameters
    and stats ("Stat creation not implemented"). Fixing it is a Core change (see
    [YARG.Core follow-up work](#yargcore-follow-up-work), item 3).
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
- **If the host drops mid-song, the song ends for the clients.** There's no host migration yet, and the
  host runs the band meter and synced pause. A client whose host left the song or stopped sending gets a
  countdown and returns to the library (see the phase 5 notes). A host that leaves can take the party
  with it, or leave alone, which ends the song for the others the same way.
- **The crowd and venue follow each game's own meter**, not the band's, on clients.

## YARG.Core follow-up work

The branch doesn't change YARG.Core (see [MVP goals](#mvp-goals)), so these items wait for the
maintainers' go-ahead. Each one names the workaround in YARG that it would replace.

1. **Register an engine for the fail meter only.** Today an engine joins all of an `EngineManager`
   or none of it: star power count, band multiplier, unisons, band combo and the fail meter. The host
   puts friends' mirrored engines in its main manager for the meter, so they also raise its band
   multiplier and take part in its unisons. A client keeps friends in separate managers, so its
   players get no band bonus from them. The result is that band bonus depends on who is host
   (`GameManager.EngineManagerFor`). Possible shapes:
   - An option on `EngineManager.Register` (or the `EngineContainer`) that leaves the engine out of
     `UpdateStarPowerCount`, `UpdateBandMultiplier` and the unison events, and keeps it in the
     happiness and revive logic.
   - Or the reverse: a separate fail meter object that several managers can share.

   With this change, friends no longer give band bonus on any game. Every game then agrees, and online
   play gives no band bonus between games.
2. **Count friends' star power in the band multiplier.** YARG does this today from outside
   YARG.Core: `GameManager.ApplyOnlineBandMultiplier` sets each engine's band multiplier from local
   star power plus the `sp` field of friends' `inputs` messages, before each player updates. In YARG.Core,
   this could be an `EngineManager.ExternalStarPowerCount` that `BandMultiplier` adds to
   `_starpowerCount`. Then `EngineManager` would not overwrite the value when a local star power
   changes in the middle of an engine update. Open points for the maintainers:
   - The reported state arrives about a network delay late, so the bonus starts and ends a little late.
   - Band bonus then depends on data that isn't in the replay. A replay or `ReplayAnalyzer` run would
     not give the same band bonus unless the replay also records the external count over time.
3. **Replay support for pro guitar.** `ReplayFrame` can't serialize pro guitar engine parameters and
   stats ("Stat creation not implemented"), so `InputSender.CanMirror` doesn't mirror pro guitar.
   The same gap affects normal replays. Any new game mode needs the same support before it can be
   mirrored.
4. **Bounds checks in the replay reader.** The replay reader trusts the lengths and counts in the
   data. A friend's `setup` message is untrusted, so `ReplayBlobCheck` checks every length before
   the reader runs. It accepts only one replay version and one profile version, and must be updated
   with each format change. If the reader checked bounds itself, the check could be removed.
5. **A fail meter that cannot fail.** In online play, a client's main `EngineManager` and each friend's
   own manager must never fail the song. `GameManager.InitializeOnlineHappiness` gets this with the
   `noFail` flag of `EngineManager.InitializeHappiness`, which also means "the player selected no fail".
   An explicit mode (for example, a meter that only shows values that another game decides) would state
   the online rule in Core, and `OnNoFailModeChanged` could not change it by accident.
6. **Copy any stats object.** The copy constructors exist only on each stats type, so
   `GameManager.ScoreCardStats` copies a friend's stats with a type switch before it puts in their
   reported score. A public virtual `BaseStats.Clone()` would replace the switch and pick up new
   game modes on its own. This is small and optional.

## Open questions

1. Do maintainers have a preferred direction for online play (P2P like ours or server-based like the `asimard1` fork)?
   See [Compared with the server-based fork](#compared-with-the-server-based-fork).
2. Will maintainers accept Rust in the native build, or do they want a different language or approach?
3. Is the lobby host-authoritative (the host runs the coordinator, as today), or does any member coordinate?
4. Which minimum macOS version does YARG target?
5. Which of the changes under [YARG.Core follow-up work](#yargcore-follow-up-work) would maintainers
   accept? Item 1 removes the star power score drift described under known limitations.
6. Should the host's vocalists and pro guitarists count toward the band meter while friends can't see
   them? Today a client's can't fail the band and the host's can.
7. For official song sharing, where does the trusted list of official song hashes come from (for example, a
   manifest published with the official setlists), and do maintainers want peer-to-peer sharing of official
   songs at all, or should a missing song send the player to the launcher instead?
