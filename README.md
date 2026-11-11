<div align="center">

# Stick Fight Online

**Stick Fight: The Game on a dedicated server.**
No player hosts the match, so the lobby keeps running no matter who lags or leaves.

### [Download the installer (.zip)](https://github.com/milescoviello/sf-multiplayer/raw/main/sf-multiplayer-StickFight-Installer.zip)

Windows · Linux / Steam Deck (beta) · needs Stick Fight: The Game on Steam

**Open beta:** it works, and we need testers. See [what to test](#beta-what-we-need-tested).

</div>

---

## Play in 3 steps (Windows)

1. **Download** the zip above and **extract it** (right-click → *Extract All*). Don't run it from inside the zip.
2. **Double-click `INSTALL-sf-multiplayer.bat`**, say *Yes* to the admin prompt, and wait for **INSTALL COMPLETE**.
3. **Start the game with the new "Stick Fight Online" shortcut** (on your desktop and in the Start menu).

The game opens with the lobby list on screen. Pick a lobby and press **JOIN**. That's it.

- **Updates are automatic.** The shortcut checks for a new version every time you start it. You never download the zip again.
- **Steam still starts the normal game.** The mod only switches on when you use the Stick Fight Online shortcut.

## Play on Linux / Steam Deck (beta)

> Not yet tested on a real Steam Deck. If you have one, please try it and [tell us how it went](#report-a-bug).

1. **Extract the zip** and open a terminal in the `StickFight-Online` folder (in the file manager: right-click → *Open Terminal Here*).
2. **Run `./install-linux.sh`.** It asks to close Steam, because it can only set Stick Fight's launch option while Steam is closed. Say yes.
3. **Start "Stick Fight Online"** from your app menu or desktop.

**Steam Deck Game Mode:** in Desktop Mode, open Steam → *Add a Non-Steam Game* → pick **Stick Fight Online**. After that it's in your library in Game Mode.

## Lobbies

| Lobby | Players | What it is |
|---|---|---|
| **MAIN** | up to 4 | Normal Stick Fight rules. |
| **CASUAL** | up to 4 | Same as MAIN, so two groups can play at once. |
| **COMP** | 1v1 | Competitive rules from the [DSF](https://discord.gg/nrzMBA6XVc) Discord (below). Needs the latest version, which the shortcut keeps you on. |
| **REPLAY** | watch only | Watch recorded COMP matches. |

**COMP rules:** 100 HP, no health regen, high weapon spawn rate, the DSF weapon and map pool, random spawns, two sets of first-to-25. When two players die at nearly the same time, the server uses each player's ping to decide who died first, so there are no draws. If someone disconnects mid-round, the round is replayed. The server evens out ping: the round start, weapon drops and who gets a contested weapon reach both players as if they had the same ping. It also throws out impossible damage (too much, or a hit from too far away) and packet floods.

## In the game

| Key | Does |
|---|---|
| **F2** | Open / close the lobby list (works in the menu and during a match) |
| **Esc** | Close the lobby list |
| **F3** | Player list (during a match) |
| **F4** | Show / hide the win counter (during a match) |
| **Enter** | Chat (type a command, then Enter) |

**Starting a match:** same as normal Stick Fight. With 2 or more players in the lobby, fight; when only one is left standing, the match starts. Or type `/start` in chat.

**Chat commands:** type `/help` in chat for the full list. The useful ones:

| Command | Does |
|---|---|
| `/score` or `/ping` | Score (in COMP) and everyone's ping |
| `/next` | Skip to the next map (not in COMP) |
| `/map 12` | Switch to map 12 (not in COMP; `/listmaps` lists them) |
| `/discount` | COMP: take back the last point (e.g. after a lag-out) |
| `/lobbies` | What the other lobbies are doing |

**Watching replays:** join **REPLAY**, press **L** to list the recorded matches, **1-9** to watch one, **R** to restart it.

## Something wrong?

| Problem | Fix |
|---|---|
| The game started but there's no lobby list | You started it from Steam. Use the **Stick Fight Online** shortcut. If you did use the shortcut, start it once more, and if that doesn't help, run the installer again: it fixes the mod loader for Stick Fight's 64-bit Steam build. |
| Windows SmartScreen or antivirus blocks the installer, or files vanish after installing | BepInEx (the mod loader) is a common false positive. Add an exclusion for your Stick Fight folder, then run the installer again. |
| The installer can't find Stick Fight | It asks for the folder. Steam libraries on another drive are at `<drive>:\SteamLibrary\steamapps\common\StickFightTheGame`. |
| Stuck on "Connecting to the server…" | Check your internet, and that nothing blocks outgoing UDP. Press F2 and join again. |
| "Your client is out of date" | You started the game some other way. Use the shortcut: it updates first. |
| The game runs slowly on an old PC | If Stick Fight stays under 40 fps, the mod turns its post-processing (bloom, colour grading, antialiasing) off for that session by itself: on a 2009 MacBook Pro that took the game from 10 to 27 fps. A lower resolution in the game's settings helps too. To keep the full look anyway, create an empty file `BepInEx/sf-full-graphics.on`. |
| Linux: Stick Fight starts as the normal game | The launch option didn't get set (Steam was open). Run `./install-linux.sh` again, or set it by hand in Steam → Stick Fight → Properties → Launch Options: `"<game>/sf-online/sf-wrap.sh" %command%` |

### Report a bug

[Open an issue](https://github.com/milescoviello/sf-multiplayer/issues) and say what happened and roughly when. Attach these two files from your Stick Fight folder (in Steam: right-click Stick Fight → *Manage* → *Browse local files*):

- `BepInEx/LogOutput.log` (what the mod did, plus your frame rate and connection)
- `sf-online/launch.log` (what the launcher and updater did)

## Beta: what we need tested

These are the things the test bots can't check. Real players can:

- **Blocking:** does the shield show where the other player is actually blocking?
- **Feel at real pings:** do other players move smoothly when you're far from the server?
- **Steam Deck / Linux:** does the installer work on a real Deck?
- **Snakes on maps with crates:** do the snakes push the crates?

## Uninstall

- **Windows:** double-click `UNINSTALL-sf-multiplayer.bat` (from the extracted zip).
- **Linux:** run `./uninstall-linux.sh`.

**What the installer changes:** nothing in the game's own files. It adds the mod loader (BepInEx) if you don't have it, two mod files in `BepInEx/plugins`, the online patcher in `BepInEx/patchers/SFOnline`, a `sf-online` folder in the game folder, the shortcuts, and on Windows a firewall rule for Stick Fight. The uninstaller removes only what it added. Other mods you have are left alone.

## How it works

In normal Stick Fight, one player's PC hosts the match. Everyone else connects through them, so when the host lags, everyone lags, and when the host leaves, the lobby is gone.

Here, the match runs on a server instead:

```
 your PC ──────┐
 friend's PC ──┼──► sf.milescoviello.com:1338 ──► MAIN · CASUAL · COMP · REPLAY
 friend's PC ──┘     (one port for every lobby)     (each lobby is its own copy of
                                                     Stick Fight running on the server)
```

- **The server runs Stick Fight's own game code**, without graphics, so the rules and physics are the real game's.
- **The mod on your PC** connects to that server instead of a player host, shows the lobby list, and smooths how other players move on your screen when their updates arrive unevenly. The networking changes this needs are made in memory, from your own copy of the game, each time you start Stick Fight Online: no game files are shipped or replaced.
- **Updates:** each time you start the shortcut, it reads a small file list from this repo and downloads only the files that changed. It checks every download against its checksum before using it.

---

## For developers

| Folder | What it is |
|---|---|
| [`sf-headless-host/`](sf-headless-host) | **Server plugin** (BepInEx + Harmony). Makes a headless copy of Stick Fight act as the match host for every connected player, and adds lobbies, COMP rules, replays and anti-cheat. |
| [`sf-box-fix/`](sf-box-fix) | Server plugin with crate / physics fixes. |
| [`sf-online-patcher/`](sf-online-patcher) | **Player patcher** (BepInEx preloader). At an online launch, builds the online game code in memory from the player's own game file; a normal launch is untouched. |
| [`sf-client-recon/`](sf-client-recon) | **Player plugin.** Connects to the server, smooths remote players, logs performance. |
| [`sf-server-browser/`](sf-server-browser) | **Player plugin.** The in-game lobby list (F2), scoreboard, English / Spanish. |
| [`sf-router/`](sf-router) | **Router (Go).** One public UDP port (1338) in front of all lobbies; also answers the lobby list. `go test -race ./...` |
| [`1-click-install/`](1-click-install) | Windows and Linux installers, the launcher / updater, and `build.sh`. |
| [`deploy/`](deploy) | What the live server runs: systemd units (`sf-router`, `sf-oracle` = MAIN, `sf-lobby@` = the other lobbies) and `start-oracle-server.sh`. |
| [`tools/`](tools) | Test and analysis tools, including [`tools/smoothness/`](tools/smoothness) (remote-player smoothness bench) and [`tools/sf-online-donor/`](tools/sf-online-donor) (builds and verifies the patcher's donor file). |
| [`maps/`](maps) | Map data dumped from the game (spawns, killboxes). |
| [`notes/`](notes), [`legacy/`](legacy) | Design notes (the [index](notes/README.md) says which are current) and earlier architectures. |

**Build a plugin:** each plugin folder needs a `refs/` folder with `Assembly-CSharp.dll` and `UnityEngine.dll` (from your own `StickFight_Data/Managed/`) and `BepInEx.dll` + `0Harmony.dll` (from BepInEx 5.4). They aren't in this repo because the game files are Landfall's. Then run `dotnet build -c Release` in the plugin folder. The game is Unity 5.6 on Mono 2.0, so `tools/check-mono2-landmines.sh` flags C# that compiles but breaks at runtime.

**Ship an update to players:** build the plugins, run `1-click-install/build.sh` (it copies the files, writes `manifest.json` with each file's SHA-256 and rebuilds the zip), then commit and push to `main`. Every player gets it the next time they start the shortcut.

**Run your own server:** a Linux machine with Proton, a copy of Stick Fight with BepInEx and the two server plugins, and the units in [`deploy/`](deploy). Step by step: [`notes/DEPLOY.md`](notes/DEPLOY.md). Players only need UDP 1338 (the router) to reach it.

**Wire format:** Stick Fight's own messages, sent as plain UDP instead of Steam P2P: `[u32 timestamp][u8 msgType][body][u64 steamID][u8 channel]`. The last 9 bytes carry what Steam P2P used to supply. The server adds message types 39+ for world snapshots, input and its own control messages. Details: [`notes/PROTOCOL.md`](notes/PROTOCOL.md).

**Where things stand:** [`WHATS_NEW.md`](WHATS_NEW.md) is the change log (newest first) and [`NEXT_STEPS.md`](NEXT_STEPS.md) the current state and open work. Both are kept current.

Issues and PRs welcome.

## Credits

### Team

| Name | Role | Contact |
|------|------|---------|
| **kit** | **Lead & maintainer.** Dedicated server & VPS oracle, headless host, v26 protocol, single-port relay/router, multi-lobby, monitoring, anti-cheat, hosting & deploy. | GitHub: [@milescoviello](https://github.com/milescoviello) |
| **ALKA** | **Major early contributor — client side (now departed).** Client mod, in-game lobby/GUI, crate & box physics, FPS uncap, the Windows installer, and live testing & QA. Has since moved on to a separate edition for the Spanish-speaking community. | GitHub: [@AlkaPrime12](https://github.com/AlkaPrime12) |

This project is led and maintained by kit — the dedicated server, backend, protocol, router, and hosting. ALKA contributed a great deal on the client side early on (the client mod, in-game lobby/GUI, and crate/box physics) before stepping away from this project, and that work is still part of what ships here. The split of work is visible in each author's commits — see the [commit history](https://github.com/milescoviello/sf-multiplayer/commits/main).

### Other

- **JoshuaDoes** ([@JoshuaDoes](https://github.com/JoshuaDoes)) — original [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since July 2022). This repo started as a fork; the v25 relay + lobby + matchmaking core in [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv) is from that project.
- **Landfall Games** — for making Stick Fight in the first place. This is an unofficial community project not affiliated with or endorsed by Landfall.
- **The SF competitive Discord** ([DSF](https://discord.gg/nrzMBA6XVc)) — for the ask + the testing.

## License

Code original to this repo is MIT-licensed (see [`LICENSE`](LICENSE)). The forked upstream code under [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv) carried no clear license — using under good-faith interpretation that it was released for community use; will swap to a clean BSD/MIT if upstream confirms or relicense if requested.

Reference DLLs and decompiled Assembly-CSharp source are **not** included; they remain copyright Landfall and you must obtain them from your own purchased copy of the game.
