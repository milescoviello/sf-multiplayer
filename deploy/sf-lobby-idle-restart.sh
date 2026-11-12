#!/bin/bash
# Restart an always-on lobby that has run longer than SF_IDLE_RESTART_AFTER_SEC
# (default 20 h) while nobody is in it. Run every 15 min by sf-lobby-idle-restart.timer.
#
# Why: an idle lobby process grows ~30 MB/h (measured 2026-11-12: four idle
# lobbies, +15 MB each per 30 min; the VM has 8 GB and no swap), and the
# headless game has a native crash at ~24 h of uptime (notes/CRASH_INVESTIGATION.md).
# MAIN had a blind 23 h RuntimeMaxSec restart; COMP/CASUAL/REPLAY had none.
# A restart here only happens with 0 router flows for that lobby, so it never
# ends a match; one lobby per run, so the Proton boots don't pile up.
# The 23.5 h RuntimeMaxSec drop-ins stay as the backstop.
set -u
LIMIT=${SF_IDLE_RESTART_AFTER_SEC:-72000}
STATS=${SF_ROUTER_STATS:-http://127.0.0.1:8081/router/stats}
stats=$(curl -s -m 5 "$STATS") || exit 0   # router down: can't tell who's in a lobby, do nothing
now_us=$(awk '{printf "%d", $1 * 1000000}' /proc/uptime)
units="sf-oracle.service $(systemctl list-units --plain --no-legend 'sf-lobby@*.service' | awk '{print $1}')"
for unit in $units; do
  case "$unit" in
    sf-oracle.service) code=MAIN ;;
    *) code=${unit#sf-lobby@}; code=${code%.service} ;;
  esac
  [ "$(systemctl is-active "$unit")" = active ] || continue
  since=$(systemctl show "$unit" -p ActiveEnterTimestampMonotonic --value)
  [ -n "$since" ] && [ "$since" -gt 0 ] 2>/dev/null || continue
  up=$(( (now_us - since) / 1000000 ))
  [ "$up" -ge "$LIMIT" ] || continue
  n=$(printf '%s' "$stats" | python3 -c "import json,sys; print(json.load(sys.stdin).get('byCode', {}).get('$code', 0))" 2>/dev/null) || continue
  if [ "$n" != 0 ]; then
    echo "$code: up $((up / 3600)) h, $n router flow(s): waiting until it's empty"
    continue
  fi
  echo "$code: up $((up / 3600)) h and empty: restarting $unit"
  systemctl restart "$unit"
  exit 0   # one lobby per run
done
