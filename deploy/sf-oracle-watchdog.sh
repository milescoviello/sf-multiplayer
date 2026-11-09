#!/usr/bin/env bash
# sf-oracle-watchdog.sh — restart the oracle or a lobby when its UDP port goes DEAF.
#
# systemd's Restart= and the RuntimeMaxSec drop-in both rely on the process
# EXITING. On 2026-06-10 the oracle crashed and then WEDGED: the process stayed
# alive (so systemd saw "active") with the game loop dead and the UDP socket
# bound-but-unanswering. Nothing recovered it for hours. This watchdog probes
# the actual game protocol (healthcheck.py sends a v25 Ping, expects a reply)
# and restarts the unit when the port stops answering despite the unit being
# "active".
#
# Wired via sf-oracle-watchdog.timer (every 2 min). Idempotent, no persistent
# state. Logs to the journal (run by systemd).
set -u

UNIT="${SF_ORACLE_UNIT:-sf-oracle.service}"
HOST="${SF_ORACLE_HEALTH_HOST:-127.0.0.1}"
PORT="${SFHEADLESS_PORT:-1337}"
HEALTHCHECK="${SF_HEALTHCHECK:-/home/miles/sf-multiplayer/healthcheck.py}"
GRACE_SEC="${SF_ORACLE_WARMUP_SEC:-120}"   # don't probe during normal spin-up
PROBE_TRIES="${SF_ORACLE_PROBE_TRIES:-3}"
PROBE_GAP_SEC="${SF_ORACLE_PROBE_GAP:-5}"
PROBE_TIMEOUT="${SF_ORACLE_PROBE_TIMEOUT:-3}"

log() { echo "[sf-oracle-watchdog] $*"; }

# Only police a unit systemd believes is up. If it's activating/failed, leave
# systemd's own Restart= to handle it — don't fight the normal restart cycle.
check_unit() {
    local unit=$1 port=$2
    local state ts_us up_sec age_sec i
    state="$(systemctl is-active "$unit" 2>/dev/null)"
    [ "$state" = "active" ] || return 0

    # Give a freshly-(re)started oracle time to load Wine + Unity + the scene
    # before we expect it to answer Pings, or we'd restart-loop it during spin-up.
    # ActiveEnterTimestampMonotonic is microseconds-since-boot; /proc/uptime is
    # seconds-since-boot. Both are monotonic, so the difference is the unit's
    # active age, immune to wall-clock/NTP jumps.
    ts_us="$(systemctl show "$unit" -p ActiveEnterTimestampMonotonic --value 2>/dev/null)"
    up_sec="$(awk '{print int($1)}' /proc/uptime 2>/dev/null)"
    if [ -n "$ts_us" ] && [ "$ts_us" != "0" ] && [ -n "$up_sec" ]; then
        age_sec=$(( up_sec - ts_us / 1000000 ))
        if [ "$age_sec" -ge 0 ] && [ "$age_sec" -lt "$GRACE_SEC" ]; then
            log "$unit active only ${age_sec}s (< ${GRACE_SEC}s warm-up), skipping probe."
            return 0
        fi
    fi

    # Probe a few times; any single success means healthy.
    i=0
    while [ "$i" -lt "$PROBE_TRIES" ]; do
        if python3 "$HEALTHCHECK" --host "$HOST" --port "$port" --timeout "$PROBE_TIMEOUT" >/dev/null 2>&1; then
            return 0
        fi
        i=$((i+1))
        [ "$i" -lt "$PROBE_TRIES" ] && sleep "$PROBE_GAP_SEC"
    done

    log "$unit ACTIVE but UDP $HOST:$port deaf after ${PROBE_TRIES} probes, restarting it."
    systemctl restart "$unit"
}

# The main oracle, then every always-on lobby (sf-lobby@CODE, its port from
# lobbies/CODE.env). A Unity crash under Wine leaves the process hung in the
# crash handler, so these need the same probe.
check_unit "$UNIT" "$PORT"
LOBBY_ENV_DIR="${SF_LOBBY_ENV_DIR:-/home/miles/sf-oracle/lobbies}"
for unit in $(systemctl list-units 'sf-lobby@*.service' --state=active --no-legend --plain 2>/dev/null | awk '{print $1}'); do
    code="${unit#sf-lobby@}"; code="${code%.service}"
    port="$(sed -n 's/^SFHEADLESS_PORT=//p' "$LOBBY_ENV_DIR/$code.env" 2>/dev/null | head -1)"
    [ -n "$port" ] && check_unit "$unit" "$port"
done
exit 0
