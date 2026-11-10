# `deploy/`: what the live server runs

The systemd units and helper scripts on the server. They expect the repo copied to `~/sf-multiplayer/` and the game install under `~/sf-oracle/` (see [`notes/DEPLOY.md`](../notes/DEPLOY.md) for setting up a server from scratch).

Players don't need anything here: they use the [1-click installer](../README.md).

| File | What it does |
|---|---|
| [`sf-router.service`](sf-router.service) | The router on public UDP **1338**: one port in front of every lobby, plus the in-game lobby list. Stats on `127.0.0.1:8081`. |
| [`sf-oracle.service`](sf-oracle.service) | The **MAIN** lobby (game port 1337, bridge 11337). Drop-ins in [`sf-oracle.service.d/`](sf-oracle.service.d): register MAIN with the router, restart daily. |
| [`sf-lobby@.service`](sf-lobby@.service) | The other always-on lobbies: `sf-lobby@COMP`, `@CASUAL`, `@REPLAY`. Each reads its settings from `~/sf-oracle/lobbies/<CODE>.env`. |
| [`start-oracle-server.sh`](start-oracle-server.sh) | Starts one headless Stick Fight under Proton (used by both unit types and by `launch-lobby.sh`). |
| [`register-main-lobby.sh`](register-main-lobby.sh) | Adds / removes a lobby in the router's registry (`/tmp/sf-lobbies`) when its unit starts / stops. |
| [`sf-lobbies.service`](sf-lobbies.service) | The control plane (`serve-lobbies.py`): lobby create, restart and stop, plus a reaper for idle created lobbies. Needs `~/sf-oracle/sf-control.env` (`SF_CONTROL_TOKEN`, kept out of git). |
| [`sudoers-sf-lobby-restart`](sudoers-sf-lobby-restart) | Lets the control plane restart the lobby units (the in-game RST button) without a password. |
| [`sf-oracle-watchdog.service`](sf-oracle-watchdog.service) + [`.timer`](sf-oracle-watchdog.timer) + [`sf-oracle-watchdog.sh`](sf-oracle-watchdog.sh) | Every 2 minutes, pings each lobby with a real game packet and restarts any that stopped answering. |
| [`sf-monitor.service`](sf-monitor.service) | Read-only telemetry dashboard on `127.0.0.1:8090` (see [`monitoring/MONITOR_ACCESS.md`](../monitoring/MONITOR_ACCESS.md)). |

## Lobby settings

COMP's `~/sf-oracle/lobbies/COMP.env`, as an example:

```
SFHEADLESS_PORT=1341
SFHEADLESS_BRIDGEPORT=11341
SF_MAX_PLAYERS=2
SF_SPECTATORS=2
SF_HP=100
SF_REGEN=0
SF_WEAPON_SPAWN=high
SF_WEAPONS="PISTOL,REVOLVER,..."
SF_MAPS="Woods1,Woods2,..."
SF_COMP=1
SF_ANTICHEAT_ENFORCE=1
```

REPLAY uses `SF_REPLAY_MODE=1` and `SF_REPLAY_SOURCE=/home/miles/sf-replays/COMP`. CASUAL only sets its ports.

## Common commands

```bash
sudo systemctl restart sf-oracle sf-lobby@COMP sf-lobby@CASUAL sf-lobby@REPLAY   # after a server plugin update
systemctl list-units 'sf-*'                                                    # what's running
journalctl -u sf-lobby@COMP -n 100                                             # one lobby's boot / crash output
tail -f /tmp/sf-oracle-plugin-11341.log                                        # COMP's plugin log (bridge port in the name)
```

The Windows scripts that used to live here (the Spanish `instalar-*` / `jugar-*` installers, `SFLauncher`) were replaced by the 1-click installer and moved to [`legacy/old-windows-scripts/`](../legacy/old-windows-scripts).
