# `dist/`: prebuilt server files

**Players:** you don't need anything here. Download the [installer zip](../sf-multiplayer-StickFight-Installer.zip) and follow the [README](../README.md). The player plugins live in [`1-click-install/files/`](../1-click-install/files) and update themselves.

| File | What it is |
|---|---|
| `Assembly-CSharp.srv.v25.dll` | The patched game file: adds `-address` / `-port` and sends the game's messages over plain UDP instead of Steam P2P. The server runs it, and `1-click-install/build.sh` copies it into the player installer (players get it in `<game>/sf-online/`, never over the game's own file). |
| `SFHeadlessHost.dll` | Server plugin: makes a headless Stick Fight host the match. |
| `SFBoxFix.dll` | Server plugin: crate / physics fixes, loaded next to SFHeadlessHost. |

These are the builds the live server runs; whoever deploys a new server build copies it here too. To set up a server, see [`notes/DEPLOY.md`](../notes/DEPLOY.md).

The old `SFLauncher.exe` / `sflauncher.sh` launchers that used to be here didn't install the patched game file and can't connect. They're in [`legacy/old-launcher/`](../legacy/old-launcher) for reference.
