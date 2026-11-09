#!/usr/bin/env bash
# Launch one Stick Fight test client (Proton, windowed, muted) against a server.
# Optional scripted bot drives the local player (needs StickFightGym.dll in the
# install's BepInEx/plugins, from the stickfight-bot repo).
#
# usage: sf-test-client.sh <install-dir> <n> <server-host> [lobby] [bot:1|0]
#   n      — client number 1..4; picks distinct local ports + prefix
#   lobby  — lobby code to join via the router (default MAIN)
# env:
#   SF_HEADLESS=1   run hidden: xvfb-run + -batchmode -nographics (no window)
#   SF_HEADLESS=render  hidden virtual display :5<n>, rendered (screenshots)
#   SF_PROTON       path to Proton's `proton` script
#   SF_STEAM_ROOT   STEAM_COMPAT_CLIENT_INSTALL_PATH (default ~/.local/share/Steam)
set -u
DIR="$1"; N="$2"; HOST="$3"; LOBBY="${4:-MAIN}"; BOT="${5:-1}"
PROTON="${SF_PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"
PREFIX="${SF_TEST_PREFIX_ROOT:-$HOME/sf-test}/prefix-c$N"
mkdir -p "$PREFIX"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="${SF_STEAM_ROOT:-$HOME/.local/share/Steam}"
export STEAM_COMPAT_DATA_PATH="$PREFIX"
export WINEDLLOVERRIDES="winhttp=n,b"
export WINEDEBUG=-all
export PROTON_USE_XALIA=0
export SF_LOBBY="$LOBBY"
export SFCLIENTRECON_PORT=$((1400 + N))     # distinct v26 listen port per client (clear of lobby ports 1337-1350)
export SFGYM_PORT=$((5555 + N))             # distinct bot control port per client
export SFGYM_MUTE=1                         # never make sound
export SFGYM_BOT_AUTOSPAWN=0                # never create extra local players
if [ "$BOT" = "1" ]; then
  export SFGYM_BOT_SLOTS=0 SFGYM_BOT_FOLLOW_LOCAL=1 SFGYM_BOT_PROFILE="${SFGYM_BOT_PROFILE:-aggressive}"
fi
cd "$DIR"
if [ "${SF_HEADLESS:-0}" = "render" ]; then
  # Hidden but RENDERED (software Vulkan on a virtual display :<50+n>), for
  # screenshots: DISPLAY=:5N xwd -root | convert xwd:- shot.png
  export SFCLIENTRECON_HEADLESS_CLIENT=0
  exec xvfb-run -n $((50 + N)) --server-args="-screen 0 960x540x24 -ac" "$PROTON" run "$DIR/StickFight.exe" \
    -address "$HOST" -port 1338 -screen-width 960 -screen-height 540 -screen-fullscreen 0 ${SF_RENDER_ARGS:-} -logFile "/tmp/sf-test-unity-c$N.log"
fi
if [ "${SF_HEADLESS:-0}" = "1" ]; then
  export SFCLIENTRECON_HEADLESS_CLIENT=1
  exec xvfb-run -a --server-args="-screen 0 320x240x24" "$PROTON" run "$DIR/StickFight.exe" \
    -batchmode -nographics -address "$HOST" -port 1338 -logFile "/tmp/sf-test-unity-c$N.log"
fi
exec "$PROTON" run "$DIR/StickFight.exe" -address "$HOST" -port 1338 \
  -screen-width 960 -screen-height 540 -screen-fullscreen 0
