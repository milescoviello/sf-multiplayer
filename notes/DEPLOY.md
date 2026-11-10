# Running a server

How the live server is set up, and how to set up another one. The live server is a Debian 13 VM (6 vCPU, 8 GB) running headless Stick Fight under Proton Experimental. The systemd units are in [`../deploy/`](../deploy) (overview in [`../deploy/README.md`](../deploy/README.md)).

## What you need

- A Linux machine with 32-bit library support (`dpkg --add-architecture i386` on Debian / Ubuntu).
- About 700 MB of RAM per lobby and a third of a core per empty lobby (more with players in it). The live server runs four lobbies.
- Your own copy of Stick Fight: The Game, with BepInEx 5.4 and the two server plugins.
- A Proton install (copied from a desktop Steam install; no Steam client is needed on the server).
- UDP port **1338** reachable from the internet. That is the only public port.

Packages on the live server (Debian 13):

```bash
sudo dpkg --add-architecture i386 && sudo apt-get update
sudo apt-get install -y xvfb libc6:i386 libstdc++6:i386 libgl1:i386 libvulkan1:i386 python3
```

`xvfb` is required: Wine inside Proton needs an X display even in `-batchmode -nographics`; without one the game hangs at 0% CPU.

## Layout on the server

```
~/sf-oracle/
├── install/                 the Stick Fight install the lobbies run
│   ├── StickFight_Data/Managed/Assembly-CSharp.dll   = dist/Assembly-CSharp.srv.v25.dll (the patched game file)
│   └── BepInEx/plugins/     SFHeadlessHost.dll, SFBoxFix.dll (server plugins only)
├── proton/                  Proton (the `proton` script + its files)
├── runtime/                 empty stand-in for the Steam runtime path
├── lobbies/<CODE>.env       per-lobby settings for sf-lobby@<CODE>
├── sf-control.env           SF_CONTROL_TOKEN for lobby create / restart (keep it out of git)
└── prefix-<bridge port>/    one wineprefix per lobby, created on first start
~/sf-multiplayer/            a copy of this repo (the units run scripts from here)
~/sf-router                  the router binary (go build ./cmd/sf-router in sf-router/)
~/sf-replays/<LOBBY>/        recorded matches
```

## Ports

| Port | Bound to | What |
|---|---|---|
| UDP 1338 | all interfaces | **The router: the only port players use.** |
| UDP 1337, 1341, 1342, 1343 | all interfaces (only the router needs them) | Game ports of MAIN, COMP, CASUAL, REPLAY |
| UDP 11337, 11341, ... | 127.0.0.1 | Each lobby's bridge port (game port + 10000) |
| TCP 8080 | all interfaces | `serve-lobbies.py` control plane (create / restart need the token). Don't forward it. |
| TCP 8081 | 127.0.0.1 | Router stats |
| TCP 8090 | 127.0.0.1 | `sf-monitor` dashboard |

Only forward UDP 1338 from your router or firewall.

## First install

```bash
# 1. Copy the game install, Proton and this repo to the server.
rsync -az ~/StickFight-server-install/ SERVER:~/sf-oracle/install/
rsync -az ~/.local/share/Steam/steamapps/common/Proton\ -\ Experimental/ SERVER:~/sf-oracle/proton/
rsync -az --exclude=.git --exclude='**/bin/' --exclude='**/obj/' --exclude=refs --exclude=legacy ./ SERVER:~/sf-multiplayer/

# 2. Build the router (needs Go) and put it at ~/sf-router.
ssh SERVER 'cd ~/sf-multiplayer/sf-router && go build -o ~/sf-router ./cmd/sf-router'

# 3. Lobby settings and the control token.
ssh SERVER 'mkdir -p ~/sf-oracle/lobbies'      # then write COMP.env etc. (example in deploy/README.md)
ssh SERVER 'umask 077; echo "SF_CONTROL_TOKEN=$(head -c 24 /dev/urandom | base64)" > ~/sf-oracle/sf-control.env'

# 4. Install and start the units.
ssh SERVER 'cd ~/sf-multiplayer/deploy && sudo cp sf-*.service sf-*.timer /etc/systemd/system/ \
  && sudo cp -r sf-oracle.service.d /etc/systemd/system/ \
  && sudo install -m 0440 -o root -g root sudoers-sf-lobby-restart /etc/sudoers.d/sf-lobby-restart \
  && sudo systemctl daemon-reload \
  && sudo systemctl enable --now sf-router sf-lobbies sf-oracle sf-lobby@COMP sf-lobby@CASUAL sf-lobby@REPLAY sf-monitor sf-oracle-watchdog.timer'
```

The units assume the user `miles` and `/home/miles/...` paths; change `User=` and the paths for another account.

## Check that it works

Ask the router for the lobby list, the same request the game sends:

```bash
python3 -c "
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(2)
s.sendto(b'SFRTR\x00\x00\x01\x03\x00\x2a\x00\x00\x00', ('SERVER', 1338))
print(s.recvfrom(2048)[0])"
```

The reply lists each lobby with its player count and capacity (format in [`PROTOCOL.md`](PROTOCOL.md#router-control-framing-select--not-a-msgtype)).

## Updating the server plugins

Never overwrite a DLL that a running lobby has loaded. Copy the new one next to it, move it into place, then restart the lobbies:

```bash
scp sf-headless-host/bin/Release/SFHeadlessHost.dll SERVER:~/sf-oracle/install/BepInEx/plugins/SFHeadlessHost.dll.new
ssh SERVER 'cd ~/sf-oracle/install/BepInEx/plugins && mv SFHeadlessHost.dll.new SFHeadlessHost.dll \
  && sudo systemctl restart sf-oracle sf-lobby@COMP sf-lobby@CASUAL sf-lobby@REPLAY'
```

Then copy the same DLL into `dist/` and commit it, so `dist/` matches what runs. Player-side updates don't touch the server: they go out through `1-click-install/build.sh` and a push to `main`.

## Where to look when things break

| Symptom | First place to check |
|---|---|
| A lobby won't start | `journalctl -u sf-lobby@COMP -n 100` (or `-u sf-oracle` for MAIN): Proton boot errors print here |
| Plugin log | `/tmp/sf-oracle-plugin-<bridge port>.log` (MAIN = 11337, COMP = 11341, ...) |
| Unity log | `/tmp/sf-oracle-unity-<bridge port>.log`: very large; a flood means an exception every frame |
| A lobby stopped answering | The watchdog restarts it within 2 minutes: `journalctl -u sf-oracle-watchdog` |
| Players can't see the server | Is UDP 1338 forwarded? Run the lobby-list check above from outside your network. |
| Load and memory over time | `~/sf-multiplayer/monitoring/sfmon-report` (see [`../monitoring/MONITOR_ACCESS.md`](../monitoring/MONITOR_ACCESS.md)) |

Known issue: the game occasionally crashes natively under Wine (see [`CRASH_INVESTIGATION.md`](CRASH_INVESTIGATION.md)). systemd restarts the lobby, MAIN also restarts once a day (`sf-oracle.service.d/restart-daily.conf`), and the watchdog catches a lobby that hangs without exiting.
