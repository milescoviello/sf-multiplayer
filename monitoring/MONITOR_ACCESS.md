# sf-monitor: access

Read-only host and lobby telemetry on the game server (`sf-monitor.service`). It
never touches the running game. The dashboard listens on **loopback only**
(`127.0.0.1:8090`), so you reach it through SSH; nothing is exposed on the LAN or
the internet.

## 1. Live dashboard
SSH to the server with a local port forward, then open the URL (keep the SSH
session open while you look):

```
ssh -L 8090:127.0.0.1:8090 <user>@<server>
#   then browse to:   http://localhost:8090
```

## 2. Text reports (on the server)
```
~/sf-multiplayer/monitoring/sfmon-report                 # summary + time buckets for today
~/sf-multiplayer/monitoring/sfmon-report --tail 30       # last 30 samples as a table
~/sf-multiplayer/monitoring/sfmon-report --lobby COMP    # one lobby's cpu / memory / clients over time
~/sf-multiplayer/monitoring/sfmon-report --bucket 60     # hourly buckets
~/sf-multiplayer/monitoring/sfmon-report ~/sf-monitor/data/metrics-2026-11-10.jsonl   # one day
```

## 3. Current snapshot / health
```
curl -s localhost:8090/api/now | python3 -m json.tool     # raw live metrics
systemctl status sf-monitor                                # is the monitor up?
```

Notes:
- Lobby CPU is in **cores** (`0.31 cores` = a third of one core); host CPU is % of all cores.
- Data: `~/sf-monitor/data/metrics-YYYY-MM-DD.jsonl` (one file a day, 14 days kept).
- The monitor runs at the lowest priority and only reads `/proc`, the router and
  control-plane stats endpoints, and the lobby log files.
