# `dist/`: prebuilt server files

**Players:** you don't need anything here. Download the [installer zip](../sf-multiplayer-StickFight-Installer.zip) and follow the [README](../README.md). The player plugins live in [`1-click-install/files/`](../1-click-install/files) and update themselves.

| File | What it is |
|---|---|
| `SFHeadlessHost.dll` | Server plugin: makes a headless Stick Fight host the match. |
| `SFBoxFix.dll` | Server plugin: crate / physics fixes, loaded next to SFHeadlessHost. |

The online game code isn't a file here or anywhere in the repo: it is Landfall's code. Players' installs build it in memory from their own copy with [`sf-online-patcher`](../sf-online-patcher) (shipped from `1-click-install/files/`).

These are the builds the live server runs; whoever deploys a new server build copies it here too. To set up a server, see [`notes/DEPLOY.md`](../notes/DEPLOY.md).

The old `SFLauncher.exe` / `sflauncher.sh` launchers that used to be here didn't install the patched game file and can't connect. They're in [`legacy/old-launcher/`](../legacy/old-launcher) for reference.
