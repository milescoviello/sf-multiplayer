#!/bin/sh
# Stick Fight Online: the game's Steam launch option on Linux / Steam Deck,
#   "<game>/sf-online/sf-wrap.sh" %command%
# A normal Steam launch runs the game unchanged (no mod loader, nothing
# added). When the "Stick Fight Online" launcher started it (it leaves a
# one-shot note with the server address, valid for 2 minutes), this one
# launch loads BepInEx and the online game file from sf-online/ and goes to
# the server.
DIR=$(dirname "$(readlink -f "$0")")
FLAG="$DIR/.online-launch"
if [ -f "$FLAG" ]; then
    age=$(( $(date +%s) - $(stat -c %Y "$FLAG" 2>/dev/null || echo 0) ))
    read -r addr port < "$FLAG"
    rm -f "$FLAG"
    if [ "$age" -ge 0 ] && [ "$age" -le 120 ] && [ -n "$addr" ] && [ -n "$port" ]; then
        # Proton shows / as drive Z:, and Doorstop wants a Windows path.
        ovr="Z:$(printf '%s' "$DIR" | tr '/' '\\')"
        export WINEDLLOVERRIDES="winhttp=n,b${WINEDLLOVERRIDES:+;$WINEDLLOVERRIDES}"
        echo "$(date +%Y-%m-%dT%H:%M:%S) sf-wrap: online launch for $addr:$port" >> "$DIR/launch.log"
        # If the lobby restarts under a match, the game leaves rejoin.txt and
        # quits; start it again straight into that lobby (at most 3 times).
        note="$DIR/rejoin.txt"; rm -f "$note"
        export SF_RELAUNCH=1
        run=0
        while :; do
            "$@" -address "$addr" -port "$port" --doorstop-enabled true --doorstop-mono-dll-search-path-override "$ovr"
            rc=$?
            [ -f "$note" ] && [ "$run" -lt 3 ] || exit "$rc"
            lobby=$(head -c 32 "$note" | tr -cd 'A-Za-z0-9_-' | tr a-z A-Z); rm -f "$note"
            [ -n "$lobby" ] || exit "$rc"
            export SF_LOBBY="$lobby"; run=$((run + 1))
            echo "$(date +%Y-%m-%dT%H:%M:%S) sf-wrap: the lobby restarted mid-match: starting the game again into $lobby" >> "$DIR/launch.log"
            sleep 1
        done
    fi
fi
exec "$@"
