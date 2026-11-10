# legacy/

Earlier architectures kept for reference. **Nothing in here is in the active data path.** The live server is [`../sf-headless-host/`](../sf-headless-host/) — see the [top-level README](../README.md).

## What's here

| Path | What it was | Why it's here |
|---|---|---|
| [`StickFightDedicatedSrv/`](StickFightDedicatedSrv/) | Go dedicated server, forked from [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv). Carries the v25 relay core, lobby/matchmaking, plus the in-progress v26 authoritative scaffolding (physics + snapshots) from Phase 5. | Documented protocol layouts + spawn-bug fix + map JSON loader. Useful as a protocol reference. |
| [`sf-netcodev2/`](sf-netcodev2/) | BepInEx + Harmony plugin for the v26 client protocol (Go-server-coordinated path). | Harmony patch patterns + InControl input injection learnings still apply to the headless host. |
| [`sf-localcontrol-fix/`](sf-localcontrol-fix/) | Local-control behavior fix from the first round of recon. | Documents how `HasControl` flips between clients during host migration. |
| [`sf-lobby-browser/`](sf-lobby-browser/) | Tiny utility for listing active lobbies on the Go server. | Same. |
| [`StickFightLauncher/`](StickFightLauncher/) | Fork of the community launcher that defaults the connection target to our server. | Superseded by the [1-click installer](../1-click-install/). |
| [`old-launcher/`](old-launcher/) | The June 2026 `SFLauncher.exe` / `sflauncher.sh` and their install scripts (moved from `dist/`). | They never installed the patched game file, so they can't connect. Kept so old links have a trail. |
| [`old-windows-scripts/`](old-windows-scripts/) | The early Windows / Spanish install and launch scripts (`instalar-*`, `jugar-*`, `SFLauncher/` source, lobby `.bat` wrappers; moved from `deploy/` and the repo root). | Replaced by the 1-click installer. |
| [`old-dev-scripts/`](old-dev-scripts/) | Old local-test and deploy helpers (`setup-all.sh`, `launch-sf-bepinex.sh`, `launch-sf-player.sh`, the ALKA-era `oracle-watchdog.sh`). | They target the May / June local setup; the server now runs from the units in [`../deploy/`](../deploy/). |

## Why these aren't deleted

The architectural pivot from "Go server + Unity oracle bridge" to "headless Unity *is* the server" (Path A) happened mid-build. The reverse-engineered protocol details, the spawn-bug fix, the v26 packet scaffolding, and the Harmony patch patterns all came from work in these directories. Deleting them would lose the trail of *why* we ended up on Path A, and the protocol reference docs would have nowhere to point.

If you're contributing to the live code, you almost certainly don't need to read anything in here. If you're debugging a wire-protocol question, [`StickFightDedicatedSrv/packets.go`](StickFightDedicatedSrv/packets.go) and the comments in [`StickFightDedicatedSrv/lobbies.go`](StickFightDedicatedSrv/lobbies.go) are the best documented references for the v25 byte layouts.
