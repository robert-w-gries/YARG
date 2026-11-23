# online-native: review areas

This file divides the `online-native` branch into areas. Each area has a reviewer with the applicable skills.

## Overview

The branch adds experimental peer-to-peer online play. Friends join a session with an invite code. They play a song together and see the highways, scores and band fail meter of each player.

- **Compare with `dev`:** `dev...online-native` has 39 commits and 117 files (+19,359 / −189). Do not compare with `master`. Local `master` does not have 346 upstream commits from `dev`.
- **New code:** approximately 6,900 lines of C# in `Assets/Script/Integration/Link`, the Rust library in `Native/YargLink`, 1,800 lines of tests and the native build tool.
- **Generated files:** `Cargo.lock` (3,862 lines) and three plugin binaries (40 MB) in `Assets/Plugins/YargLink`. Make sure that they are reproducible. Do not review their content.
- **Changes to existing code:** 32 files, approximately +1,360 / −130, without area 11 and the unrelated fixes. The "Online" menu entry in `MainMenu.prefab` has 247 of these lines. Discord invites in `DiscordController.cs` have approximately 300.
- **Setting:** The `LinkEnable` setting (Experimental) controls all online code. It is off by default.
- **YARG.Core:** No changes. The submodule pin is the same as on `dev`.

`docs/native-library-plan.md` gives the design, the known limitations and the open questions.

## Areas

C# paths start at `Assets/Script/`. Line counts are added lines, without `.meta` files.

| # | Area | Reviewer skills | Main paths | Lines |
| --- | --- | --- | --- | --- |
| 1 | Native P2P transport | Rust, QUIC, FFI | `Native/YargLink/` | 2,600 + `Cargo.lock` |
| 2 | Native build, CI, binaries | Build and release | `scripts/NativeBuild/`, `.github/workflows/native-link.yml`, `Assets/Plugins/YargLink/` | 710 + 3 binaries |
| 3 | C# transport | C# interop | `Integration/Link/Transport/`, `NativeLinkLibrary.cs`, `Tests/YARG.Link.Tests/MemoryLinkTransport.cs` | 870 + 750 tests |
| 4 | Session protocol | Network protocols | `Integration/Link/Session/` | 1,940 + 1,000 tests |
| 5 | Replay data from peers | YARG.Core replays | `ReplayBlobCheck.cs` | 300 + 260 tests |
| 6 | Online controller | Gameplay | `LinkController*.cs` (13 partial files), `RemoteInputFeed.cs` | 2,900 |
| 7 | Friends' tracks | Track, player, visuals | `Gameplay/Player/*`, `TrackElements/*`, `YargPlayer.cs`, `GameManager.Loading.cs` | 250 |
| 8 | Scores and band fail meter | Engine, scores | `GameManager.cs`, `FailMeter.cs`, `Song/BandMeterSync.cs` | 350 |
| 9 | Song time and synced start | Audio, playback | `SongRunner.cs`, `Song/ClockSync.cs`, `Play/ClockCorrector.cs`, `GameManager.Loading.cs` | 300 |
| 10 | Lobby, settings, text, launch options | Menus, localization | `LinkLobby.cs`, `MainMenu.*`, `MenuManager.cs`, settings, `en-US.json`, `CommandLineArgs.cs` | 740 |
| 11 | Window launch arguments | Settings, display | `ScreenHelper.cs`, `SettingsManager*.cs`, `CommandLineArgs.cs` | 60 |
| 12 | Discord invites | Discord Game SDK | `Integration/DiscordController.cs`, `LinkController.cs` | 380 |

### 1. Native P2P transport (Rust)

The library connects to other games with a ticket. It sends reliable data on one QUIC stream and unreliable data as QUIC datagrams. It has no data about songs or players. The C interface is in `include/yarg_link.h`.

Examine these items:

- **FFI safety:** Each export catches panics (`YL_ERR_PANIC`). The library does not call managed code. The game polls events from `Update()`.
- **Limits:** Reliable frames are 1 MiB maximum. A hello is 64 bytes maximum. The library disconnects a peer that has more than 16k unread messages or 32 MiB. It also disconnects a peer that does not read: more than 64k messages or 32 MiB that wait to go out to the peer.
- **Close:** `yl_disconnect` and `yl_destroy` send the queued reliable messages before they close the connection (2 s maximum). Thus `yl_destroy` can block for approximately 2.5 s.
- **Identity:** Each handle gets a new random endpoint ID. The library does not write to the disk. The logs show only a short part of an ID.
- **Dependencies:** iroh and its dependencies in `Cargo.lock`. The library uses the public relays and DNS of n0.

### 2. Native build, CI and binaries

The build tool builds the Rust library with the same procedure as YargAudio. The binaries are in the repository.

Examine these items:

- **Rust in the build:** Do the maintainers accept Rust? This is open question 2 in the plan.
- **YargAudio:** Make sure that the changes to `BuildCommand.cs` and `PackageCommand.cs` do not change the YargAudio build.
- **Reproducible builds:** CI fails if the Linux or macOS plugin is different from the committed file (`--verify-committed-plugin`). CI only tests the Windows plugin.
- **Platforms:** Linux uses glibc 2.31 (`ubuntu:20.04`). macOS has one universal binary with the ad-hoc signature from the linker. Windows uses a static CRT.
- **Size:** Windows 10 MB, Linux 12 MB, macOS 18 MB.
- **Current plugins:** All three plugins are from CI run 44234338625 (commit `2c9936a43`, ABI 3). Before this run, the Windows and macOS plugins were ABI 1 builds. The game did not load them. `1927bd431` also restored the CI-built `libyarg_audio.so` from `dev`. The Editor had built it again on a host with glibc 2.43.

### 3. C# transport

`ILinkTransport` is the interface for the session layer. `NativeLinkTransport` calls the Rust library. It is the only transport in the game. The tests also use `MemoryLinkTransport` (`Tests/YARG.Link.Tests`), which sends messages in memory. If the maintainers do not accept Rust, replace only `NativeLinkTransport`.

Examine these items:

- **Interop:** The buffers for `yl_poll` and the handle lifetime in `Dispose`. `Dispose` calls `yl_destroy` on a new thread, because `yl_destroy` can block. When the game quits, it waits a maximum of 3 s for these threads (`WaitForClosedHandles`).
- **Test transport:** `MemoryLinkTransport` must obey the same contract as the native transport: reliable messages in order, a goodbye before the disconnect, one disconnect for each peer, and the same size limits. The transport and session tests run on the two transports. `Sever` and `SeverAll` cut a connection without a goodbye. `DropUnreliable` loses datagrams.

### 4. Session protocol

The session layer does not use Unity. It controls the hello, the member list, the ready state, the synced start and the game data. Each joiner connects only to the host. The host sends the game data of each member to the other members.

Examine these items:

- **Versions:** Messages are JSON with a `"t"` type. There is no protocol version: games must have the same game version. A game ignores unknown message types.
- **Admission:** A peer must send a hello first. Messages before the hello are 4 KiB maximum. A removed peer cannot join the same session again.
- **Limits:** `LinkLimits` sets the maximum text lengths, 16 players for each member and message rates. All members can use a modified game. This includes the host.
- **Host control:** Is a host-controlled session correct? This is open question 3.

### 5. Replay data from peers

The `setup` message from a friend contains replay data. The YARG.Core replay reader does not check lengths. `ReplayBlobCheck` checks each length and count before the reader parses the data.

Examine these items:

- **Layout:** Make sure that it agrees with the current `ReplayData`, `ReplayFrame`, `YargProfile`, `*EngineParameters` and `*Stats` readers.
- **Versions:** It accepts only one replay version and one profile version. If the Core format changes, friends' tracks do not show until a person updates the check.
- **Alternative:** Do the Core maintainers prefer to repair the reader (`FixedArrayStream.ReadString`) and remove this check?

### 6. Online controller

`LinkController` is a `MonoBehaviour`. It opens the session and sends game events to it.

1. It hosts, joins, leaves and removes members.
2. When a member selects a song, the other members get `load_song` with the song hash. A member without the song, or not in the menu, declines it.
3. During a song, each game sends `setup` one time. Then it sends `inputs` 30 to 120 times each second.
4. It shows a banner at the top of the screen (`LinkBanner`, a uGUI canvas made in code) for a synced pause and for a song that lost its host. The scores of the friends are in the normal score box.

Examine these items:

- **Structure:** `LinkController` opens the session, sends each event and each message to the class that handles it, and is the only entry point for the game. Each responsibility is a separate class: `SongRequests`, `SyncedStart`, `HostLoss` and `LinkDebugOverlays` for the session, and `SyncedSong` for one song. `SyncedSong` owns the state of the song: our players (`InputSender`), the friends' players (`RemoteSetup`), and the features `BandMeterSync`, `PauseSync`, `ClockSync` and `Departures`. When the song ends, the object goes away with all its state. The decisions that do not need Unity are in `Play/` (`ClockCorrector`, `DepartureTracker`, `HostLossTracker`, `ResumeTiming`, `HostMeterReadings`), with tests in `Tests/YARG.Link.Tests`.
- **Messages:** The game data about songs are typed classes in `Play/PlayMessages.cs` (`PlayProtocol`). Each class checks the values from a friend in `Check`, before a handler gets the message. The wire format did not change in the restructure.
- **Modes:** `CanMirror` does not send pro guitar. The plan gives the cause.
- **Leave and host loss:** `Song/HostLoss.cs` and `Song/Departures.cs`. When the host selects Back to Library, the host can leave with the party (`endsong`) or leave alone. The host removes a friend that left the song or that sent no inputs for 5 s (`dropped`). A joiner without a host shows a countdown and goes back to the library. There is no host migration.
- **Debug overlays:** Two IMGUI boxes (`LinkDebugOverlays`) are debugging aids, off by default. The component is enabled only while one of them is on, so the game makes no IMGUI calls without them: Settings › Debug › Online link score box values (the values of this game against the values in the score box, for each player) and Online link connections (the health of each connection). The band's score and stars are in the normal score box. The old scoreboard overlay was removed, because these two boxes show all of its data.

### 7. Friends' tracks

Each player of a friend becomes a local `TrackPlayer`, or a `VocalsPlayer` on the shared vocal track. It plays the received inputs, like a replay.

Examine these items:

- **Engine time:** `BasePlayer.RemoteGameplayUpdate` moves the engine only to the time of the last received input. `EngineLag` is the delay.
- **Visual time:** A friend's highway is drawn at `GameManager.VisualTime`, as every other highway is. Only the engine of a friend's player runs late (`EngineLag`).
- **Late hits:** `LateHitAllowance` keeps passed notes on a friend's track for a maximum of 1 s.
- **`IsRemote` checks:** A friend's track does not play sounds, mute stems, show the input viewer or rewind.
- **Order:** The game adds friends' tracks after the local tracks. A friend's singer joins the vocal track, or sets it up when no local player sings (`CreateRemoteVocalsPlayer`).
- **Vocals:** A friend's needle uses the time of their engine (`VocalsPlayer.SingClock`), with extra slack (`REMOTE_SING_SLACK`), and hides when their inputs stall. A friend whose vocals mode differs from the track's gets no needle, HUD card or percussion track (`ShownOnTrack`), but their engine runs. Friends' singers take their notes from clean copies of the vocals tracks, because a local singer's modifiers change the chart's track.

### 8. Scores and band fail meter

The players of a friend do not add to the local score. The fail meter of the host is the band meter.

All the online code of `GameManager` is in `GameManager.Online.cs`. The remarks on that class have a table of the authority for each value: each game for its own players' scores, and the host for the band meter. `GameManager.IsOnlinePlay` is the one check for online play. It is true only when the session scheduled a synced start for the song.

Examine these items:

- **Local only:** Band score, band combo, star thresholds, saved scores and `MeanAverageOffset` use only local players. Friends' players must not go into the score database.
- **Engine managers:** `EngineManagerFor` gives each friend a separate `EngineManager`. On the host, friends' players use the main manager. Thus the meter of the host includes all players.
- **Star power:** Each game gives its engines the star power multiplier of the whole band: its own players, plus each friend's player whose game reports star power active (`PlayerInputs.StarPower`, `GameManager.ApplyOnlineBandMultiplier`). It sets the multiplier before each player updates, because `EngineManager` sets its own count when a star power changes. Thus band bonus and the score box multiplier are the same on the host and on a client. Friends' star power arrives a network delay late. On the host, friends' engines still take part in its unisons (item 1 in "YARG.Core follow-up work" in `native-library-plan.md`).
- **Clients:** The main manager of a client has fail off. Only the meter of the host can fail the song.
- **Meter icons:** On a client, `FailMeter` shows an icon for each player in the host's `meter` message, at the host's value (`BandMeterSync.Markers`). A player that the host does not count has no icon.
- **Band total:** The score box shows the band total and stars (`GameManager.ScoreBoxScore` and `ScoreBoxStars`). It adds the score and band bonus that each player's own game reports, for all friends' players, with or without a track (`LinkController.FriendFeeds`). It gets stars from all their cutoffs. A friend's player without a track sends its cutoffs in its setup (`SetupPlayer.StarCutoffs`). It is not in `BandScore` or in saved scores.
- **Score screen:** The header shows the same band total and stars as the score box (`ScoreScreenStats.BandScore` and `BandStars`). It shows only these values. `RecordScores` saves the score of the local players. The card of a friend's player uses the score, band bonus and stars that the friend's game reports (`GameManager.ScoreCardStats`). The other stats come from the mirror. Thus all games show the same numbers.

### 9. Song time and synced start

All games start the song at the same time. Then each joiner adjusts its clock to the host clock.

Examine these items:

- **`ShiftInputTime`:** It moves the input time without a seek. The audio sync follows. Is this safe with ASIO, WASAPI exclusive and changes to song speed?
- **Correction:** It starts above 10 ms, stops below 3 ms, uses a rate of 2% and stops above 2 s.
- **Loading:** The loading screen waits a maximum of 120 s for friends. Practice mode and replays do not wait.

### 10. Lobby, settings, text and launch options

`LinkLobby` uses the existing dialogs. The "Online" menu entry is the only new prefab content. It is hidden when `LinkEnable` is off.

Examine these items:

- **Prefab:** `MainMenu.prefab` has 247 new lines for one `MenuEntry`.
- **Text:** The messages in `LinkController` are English text in the code. Only `en-US.json` has the new keys. Must the messages be localized before the merge?
- **Launch options:** Only Editor and test builds (`YARG_TEST_BUILD`) read the development options (`-link-host`, `-link-join`, `-link-name`, `-discord-app-id` and the `YARG_LINK_*` and `YARG_DISCORD_APP_ID` variables). Release builds ignore them.
- **Pause on focus loss:** A pause online stops all players. Thus online play has a separate setting, "Pause On Focus Loss in Online Mode" (`PauseOnFocusLossOnline`, off by default). In online play (`GameManager.IsOnlinePlay`), the game uses this setting, not the usual setting.

### 11. Window launch arguments

The Unity window arguments (for example, `-screen-width` and `-window-mode`) now override the saved display settings at startup. This change is not online code. Move it to a separate PR.

### 12. Discord invites

`DiscordController` shows the online session as a private Discord party. Friends join from a Discord invite or with "Ask to Join". The link code does not use Discord.

Examine these items:

- **Join secret:** The join secret is the invite code of the session. The party ID is a hash of the code. Thus all members show the same party, and the activity does not show the code. The game does not show a code that is longer than 127 bytes (a code with addresses). A bare endpoint ID is shorter.
- **Join:** When a player accepts an invite, the game joins the session. During a song, the game waits until the player is in the menu. When `LinkEnable` is off, a toast tells the player how to turn it on.
- **Launch command:** Outside the Editor, the game registers its launch command with Discord. Thus Discord can start the game for an invite. `-discord-app-id` selects a different Discord application for tests.
- **Hosting first:** Discord shows Invite and Ask to Join only when there is a party, so the player must host a session first. The Game SDK does not tell the game when the player opens the invite menu in Discord.
- **SDK:** The Discord Game SDK is deprecated. The long-term replacement is the Discord Social SDK.
- **Not tested:** Invites between two computers and two Discord accounts, and the start from Discord in a release build.

## Unrelated fixes

These fixes are not online code. Move them to a separate PR.

- **`FocusHandler` at startup** (`368405d39`, `Persistent/FocusHandler.cs`): Unity sends the first `OnApplicationFocus` before `Start`. At that time `SettingsManager.Settings` is null. Thus a game that starts without focus logged a `NullReferenceException`, and "Mute on Focus Loss" and "Background FPS Cap" did not apply. The handler now waits until the settings load and then applies the current focus state. This is not the online pause-on-focus setting in `GameManager`.
- **Unison display after a drop-out** (`Gameplay/HUD/UnisonBonus/UnisonDisplay.cs`): When a player drops out, `EngineManager.Unregister` removes the player from the next unisons. But `UnisonDisplay` selected its phrases one time, at the song start. Thus with "Multiplayer Only", the display still showed for a unison that had only one player, or no players. This also occurred in local play. The display now hides a phrase that has fewer players than the setting requires. Also, `ResetState` stopped the reset of all the players after a player without unison state (`return` instead of `continue`).

## Items for all areas

- **Layers:** Each layer uses only the layer below it: Rust library (1), transport (3), session (4), play messages and decisions (`Play/`, part of 6), controller (6), game hooks (7 to 10). The transport, session and play code must not use Unity, YARG.Core or gameplay types.
- **Setting off:** When `LinkEnable` is off, areas 7 to 10 and 12 must operate the same as on `dev`. Each reviewer must examine this in their files.
- **Peers that are not safe:** The protection is in areas 1, 3, 4 and 5. One reviewer must examine these four areas together. Commits `8af572acf` and `6d3b90e7c` show the changes from the security review.
- **Shared files:** `GameManager.Loading.cs` has changes for areas 7, 8 and 9. `CommandLineArgs.cs` and `SettingsManager*.cs` have changes for areas 10, 11 and 12. `LinkController.cs` has changes for areas 6 and 12.
- **YARG.Core:** Some limitations exist because the branch does not change Core. The Core maintainers must select the changes that they want. The list is in "YARG.Core follow-up work" in `native-library-plan.md`.
- **Tests:** `native-link.yml` runs `YARG.Link.Tests` when `Native/YargLink/`, the plugins, `Integration/Link/Transport/`, `Integration/Link/Session/`, `Integration/Link/Play/`, `Tests/YARG.Link.Tests/`, `scripts/NativeBuild/` or the workflow change. These are all the files that the tests compile or load. The other files in `Integration/Link` use Unity, and no CI test compiles them. CI does not run `YARG.Link.Replay.Tests`, which tests `ReplayBlobCheck.cs`. Should CI run these tests when `ReplayBlobCheck.cs` or the YARG.Core pin changes?

## Maintenance tasks

If the maintainers merge the branch, these tasks continue after the merge. Each list starts with the event that starts the task.

### Regular tasks

- **Relays:** The game uses the public relays of n0. n0 can change these relays at any time. Before a public release, operate a YARG relay (`iroh-relay`). The plan gives the costs and the settings. Keep the public relays of n0 as a fallback.
- **Relay server:** If YARG operates a relay, install the operating system updates. Use the same `iroh-relay` version as the iroh version in `Cargo.lock` (1.4.1).
- **Rust dependencies:** Examine `Cargo.lock` for security problems. If you update a dependency, build the three plugins again with `scripts/NativeBuild/` and commit them.
- **Plugin check:** CI makes sure that the Linux and macOS plugins agree with the source. CI does not do this check for the Windows plugin. Do this check manually.
- **Discord:** If Discord changes or removes the Game SDK, move `DiscordController` to the Social SDK. The invite code works without Discord.
- **Toolchains:** The Linux build uses glibc 2.31 (`ubuntu:20.04`). The macOS build uses the same Xcode version as YargAudio. If the YargAudio toolchain changes, change the YargLink toolchain also. Do not sign the macOS plugin again with `codesign`.

### When YARG.Core changes

- **Replay format:** `ReplayBlobCheck` accepts only one replay version and one profile version. If Core changes the replay, profile, engine parameter or stats format, update the check. If you do not update it, friends' tracks do not show.
- **Replay tests:** CI does not run `YARG.Link.Replay.Tests`. Run them manually after a Core update.
- **Game modes:** `InputSender.CanMirror` sets which game modes show as a friend's track. A new game mode needs replay support in Core before it can show.

### When gameplay code changes

- **Friends' tracks:** A friend's track is a local `TrackPlayer` with `IsRemote` set. If a new player feature plays sounds, mutes stems, shows input or rewinds, add an `IsRemote` check.
- **Song exit:** `GameManager.OnDestroy` calls `LinkController.OnSongExited`. This call tells friends that the player left the song. Each exit from gameplay must destroy the `GameManager`.
- **Pause and fail menus:** Pause is synchronized. In online play, `GenericPause.BuildOnlineOptions` hides the items that leave friends behind (Restart, Practice, Skip and no fail). It finds each item by the name of the method that the button calls. If you rename one of these methods, update the list. If you add a menu item, decide if it must show in online play.
- **No fail:** Online play ignores the no fail setting. Read `GameManager.NoFailMode`, not the setting, in gameplay code.

### When online messages change

- **Versions:** There is no protocol version. Friends must have the same game version. A message change does not need a version change. But all friends must update to the same build.
- **Local builds:** Two local builds with changes that are not committed have the same game version. They can connect, but their messages can be different. When you test a message change, build all test games again.
- **Song messages:** Each message about a song must derive from `SongPlayMessage`, and go out through `SyncedSong.Send`, which adds the song hash and the play ID. Each message that the game receives about a song must go through `SyncedSong.IsFromPlay`. Without this, messages from a restarted song have an effect on the new song.
- **New messages:** Add a class to `Play/PlayMessages.cs`, register it in `PlayProtocol`, and add its case to `LinkController.OnGameData`.
- **Data from friends:** A friend can use a modified game. Apply the `LinkLimits` limits to each new text, list or number from a friend, in the `Check` method of the message.
- **Text:** The messages in `LinkController` are English text in the code. If the maintainers localize the game text, move these messages to the localization files.

## Caveats

These items are open. Some are decisions about gameplay that the maintainers must make. Others are known gaps. The restructure of the controller (`119e4afa`) and the uGUI banner (`68158be3`) did not change them.

### Gameplay decisions

1. **No fail is off in online play, and the host's meter fails the band.** `GameManager.NoFailMode` is always `Off` in online play, and the pause menu hides the no fail items. Thus a player who needs no fail (a beginner, or for accessibility) cannot play online. The weakest player ends the song for all players. In local multiplayer, the group can select no fail. A possible change: a session setting that the host selects, which the host sends with the start. The comment on `TrackPlayer.FailFromLink` says that "the host's no fail setting is the one that counts", but this setting does not exist yet.
2. **Each member can start a song for all members, and the others cannot refuse.** When a member starts a song, each member in the menu or on the score screen goes to difficulty select (`SongRequests.OnLoadSong`). A member on the score screen goes back to the menu first. This resets `GlobalVariables.State` and covers the menu that was open, for example the settings or a profile. The member who started waits on the loading screen for a maximum of 120 s (`SyncedStart.MAX_WAIT_SECONDS`), and cannot start without the others. When two members start a song at the same time, the host cancels the second start ("is already starting"). Possible changes: a prompt to join the song, song selection only by the host, or "Start now" on the loading screen.
3. **When a joiner loses the host, the joiner loses the run.** There is no host migration. A joiner goes back to the library 5 s after it loses the connection to the host, or 12 s after the host leaves the song (`HostLossTracker`). `GameManager.ForceQuitSong` does not save the score. Thus a short network problem on the host ends a long song for all joiners, and they get no score. A smaller change than host migration: go to the score screen with the part of the song that the players played.
4. **One player can keep the band paused.** Each member can pause all members. The song continues only when every member in the song selects Resume (`PauseCoordinator`). There is no timeout and no "resume without them". A player who does not come back keeps the band paused until the others leave. Only the host can take all members to the library ("Leave With Party").
5. **Online scores are saved as usual scores, but online play changes them.**
   - A jump forward of the clock (0.5 s or more, `ClockCorrector.JUMP_THRESHOLD`) skips notes, and these notes are misses.
   - The star power of friends' players gives band bonus to the local players. A replay has no friends, so a replay of an online score can have less band bonus than the saved score.

   Possible changes: mark online scores in the score database, keep them out of the usual high scores, or do not save a score after a jump.
6. **The song speed of a joiner changes while it follows the host.** During a correction, the song plays at 98% or 102% speed (`ClockCorrector.SLEW_RATE`), and the audio sync follows. Nobody has listened for a change of pitch or tempo, with and without time stretch. This is most important on connections with a high latency, where corrections occur frequently.
7. **Smaller items:**
   - Friends' pro guitar players have no highway (`InputSender.CanMirror`). They have only an icon on the fail meter.
   - A friend who sings in another vocals mode than the vocal track (solo against harmony) has no needle. The score and the band meter still count them.
   - The hits of friends show a network delay late. Passed notes stay on a friend's track for a maximum of 1 s (`BasePlayer.LateHitAllowance`).
   - Inputs go on the reliable stream, at a maximum of 120 messages each second. Thus a lost packet stops a friend's track for a short time. It does not cause a gap.
   - After the 120 s wait for a synced start, the toast says "Lost the online session", also when the session is connected and a friend was only slow.
   - Each member's `failed` message fails the song for all members in that play (`LinkController.OnFriendFailed`). The handler does not check that the sender is the host. A joiner's meter can fail only when the host is not in the play, but a modified game can send `failed` at any time.

### Existing functionality

- **Shared code:** Some refactors in existing code must operate the same as before. Review them with the setting off:
  - `GameManager.Loading` does the `_loadState == Error` check earlier.
  - `GameManager.PushPauseMenu` is a new method that contains the old code.
  - `PauseCore` uses `resumeRewind` (`Rewinding && _catchUpSongTime == null`) instead of `Rewinding`.
  - `OnSongFailed` calls the new `FailSong`, and is no longer `async void`.
  - `RecordScores`, `BandCombo` and `MeanAverageOffset` use `ActiveLocalPlayers`. Without friends' players, this is the same list as `ActivePlayers`.
- **Pause menu items:** `GenericPause.BuildOnlineOptions` finds the items to hide by the name of the method that each button calls. If someone renames `Restart` or `TogglePractice`, the item shows in online play again, and no error occurs.
- **Always present:** `LinkController` is created at startup, also when online play is off. It has no `OnGUI` now. Its `Update` returns early when the setting is off.

### Code

- **Not tested in a session:** The restructure of the controller and the uGUI banner were tested only with the C# compiler (all of `Assembly-CSharp`) and `YARG.Link.Tests` (154 tests). Nobody played a session with these changes. Before the merge, play a song with two games, and examine a pause, a resume, a host that leaves, a friend that drops out, and the banner over the pause menu.
- **Unity code has no CI tests:** `Play/` (messages, clock correction, drop-outs, host loss, resume timing) has tests. The classes that connect these decisions to the game (`Song/`, `LinkController`) use Unity, and no CI test compiles them.
- **Text:** The lobby, the toasts, the dialogs and the banner use English text in the code. Only the button labels are in `en-US.json`.
- **Lobby:** The lobby is a `ListDialog` with rich text rows. It has no prefab of its own. A lobby with more functions (for example, a ready check or a song vote for item 2) needs a menu of its own.
- **Debug overlays:** The two debug panels use IMGUI. They are off by default, and the game makes no IMGUI calls without them.

## Review order

1. **All maintainers:** Read the summary and open questions 1 to 3 in the plan. If the maintainers do not accept P2P or Rust, much of the branch changes.
2. **Area 2:** This area decides if areas 1 and 3 are acceptable.
3. **Areas 1, 3 and 4:** These areas have tests and do not use gameplay code.
4. **Area 5:** This area needs a reviewer who knows the replay format.
5. **Areas 7, 8 and 9:** These areas change existing code. Review them together.
6. **Area 6:** Review this area after the layers below it and the hooks.
7. **Area 10:** Make sure that nothing changes when the setting is off.
8. **Area 12:** Review this area after area 10.
9. **Area 11:** Move this area to a separate PR.
