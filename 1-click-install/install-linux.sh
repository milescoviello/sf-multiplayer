#!/usr/bin/env bash
# ===================================================================
#  Stick Fight Online - installer for Linux / Steam Deck (Proton)
#
#  1. Finds Stick Fight in your Steam libraries (native or Flatpak Steam).
#  2. Never changes the game's own files (an older install that patched the
#     game file is undone).
#  3. Installs BepInEx 5.4.23.5 if you don't have it (bundled, official zip).
#  4. Installs the mod: the SFOnlinePatcher (builds the online game code in
#     memory from your own game file, only for the server) and the
#     SFClientRecon / SFServerBrowser plugins. No game file is shipped.
#  5. Sets Stick Fight's Steam launch option to sf-wrap.sh, which starts the
#     game unchanged unless the "Stick Fight Online" launcher asked for the
#     server (needs Steam closed: it asks).
#  6. Adds "Stick Fight Online" to your app menu (and the desktop). It checks
#     for updates (a few KB), installs any, then starts the game on the
#     server through Steam. Steam Deck Game Mode: in Desktop Mode, Steam >
#     Add a Non-Steam Game > Stick Fight Online.
#
#  Run it again any time: it repairs or updates. Undo: ./uninstall-linux.sh
#  Usage: ./install-linux.sh [--game <StickFightTheGame folder>] [--server host:port]
# ===================================================================
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
files="$here/files"
APPID=674940
game=""; server=""
while [ $# -gt 0 ]; do
    case "$1" in
        --game) game="$2"; shift 2 ;;
        --server) server="$2"; shift 2 ;;
        *) echo "unknown option $1"; exit 2 ;;
    esac
done

step() { printf '\033[36m[%s] %s\033[0m\n' "$1" "$2"; }
ok()   { printf '    \033[32mOK\033[0m  %s\n' "$1"; }
warn() { printf '    \033[33m!!\033[0m  %s\n' "$1"; }
die()  { printf '    \033[31mERROR\033[0m  %s\n' "$1"; exit 1; }

command -v python3 >/dev/null || die "python3 is needed (it comes with SteamOS and most distros)."
for f in SFOnlinePatcher.dll SFOnline.donor game-file.sha256 SFClientRecon.dll SFServerBrowser.dll launch.py sf-wrap.sh steam_options.py; do
    [ -f "$files/$f" ] || die "Missing installer file files/$f (extract the whole zip first)."
done

steam_roots() {
    for r in "$HOME/.steam/steam" "$HOME/.steam/root" "$HOME/.local/share/Steam" \
             "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
             "$HOME/.var/app/com.valvesoftware.Steam/data/Steam"; do
        if [ -d "$r/steamapps" ]; then readlink -f "$r"; fi
    done | sort -u
}

echo
echo '   ==============================================='
echo '     Stick Fight Online  -  installer (Linux)'
echo '   ==============================================='
echo

step 1 'Finding Stick Fight: The Game...'
libraries() {   # one library folder per line (paths may contain spaces)
    steam_roots | while IFS= read -r r; do
        printf '%s\n' "$r"
        if [ -f "$r/steamapps/libraryfolders.vdf" ]; then grep -o '"path"[[:space:]]*"[^"]*"' "$r/steamapps/libraryfolders.vdf" | sed 's/.*"\([^"]*\)"$/\1/' || true; fi
    done
}
if [ -z "$game" ]; then
    while IFS= read -r l; do
        if [ -f "$l/steamapps/common/StickFightTheGame/StickFight.exe" ]; then game="$l/steamapps/common/StickFightTheGame"; break; fi
    done < <(libraries)
fi
if [ -z "$game" ] || [ ! -f "$game/StickFight.exe" ]; then
    warn 'Could not find Stick Fight automatically.'
    read -r -p '    Path to your StickFightTheGame folder: ' game
    [ -f "$game/StickFight.exe" ] || die "No StickFight.exe in $game"
fi
game="$(readlink -f "$game")"
# The Steam install that owns this library (for the launch option).
steamroot=""
lib="$(dirname "$(dirname "$(dirname "$game")")")"
while IFS= read -r r; do
    [ -n "$steamroot" ] && break
    case "$game" in "$r"/*) steamroot="$r"; continue ;; esac
    [ -f "$r/steamapps/libraryfolders.vdf" ] && grep -qF "\"$lib\"" "$r/steamapps/libraryfolders.vdf" && steamroot="$r"
done < <(steam_roots)
[ -z "$steamroot" ] && steamroot="$(steam_roots | head -1)"
ok "$game"
while pgrep -f "StickFightTheGame.StickFight.exe" >/dev/null 2>&1; do
    warn 'Stick Fight is running. Close it, then press Enter.'; read -r _
done

managed="$game/StickFight_Data/Managed"
online="$game/sf-online"
plug="$game/BepInEx/plugins"
mkdir -p "$online"

step 2 'Keeping the game itself normal (Steam = normal Stick Fight)...'
if grep -q UDPReceive "$managed/Assembly-CSharp.dll" 2>/dev/null; then
    if [ -f "$managed/Assembly-CSharp.dll.vanilla.bak" ]; then
        mv -f "$managed/Assembly-CSharp.dll.vanilla.bak" "$managed/Assembly-CSharp.dll"
        ok 'An older install had patched the game file: original restored.'
    else
        warn 'An older install patched the game file and left no backup: asking Steam to re-check Stick Fight.'
        (xdg-open "steam://validate/$APPID" >/dev/null 2>&1 &) || true
    fi
elif ! tr -d '\r ' < "$files/game-file.sha256" | grep -qixF "$(sha256sum "$managed/Assembly-CSharp.dll" 2>/dev/null | cut -d' ' -f1)"; then
    warn "Your game file isn't the stock Steam version, and the online mode is built from it."
    warn 'Steam is re-checking Stick Fight; run this installer again when it has finished.'
    (xdg-open "steam://validate/$APPID" >/dev/null 2>&1 &) || true
else
    ok 'Game file untouched (stock).'
fi

step 3 'BepInEx (mod loader)...'
# The game's PE architecture: 64-bit since Stick Fight's 2026 Steam update,
# 32-bit before. BepInEx's loader (winhttp.dll) has to match it.
pe_arch() { python3 -c 'import struct,sys
d=open(sys.argv[1],"rb").read(4096); o=struct.unpack_from("<I",d,0x3c)[0]; m=struct.unpack_from("<H",d,o+4)[0]
print({0x8664:"x64",0x14c:"x86"}.get(m,""))' "$1" 2>/dev/null; }
arch="$(pe_arch "$game/StickFight.exe")"; [ -n "$arch" ] || arch=x64
zip="$(ls "$here"/bepinex/BepInEx_win_${arch}_*.zip 2>/dev/null | head -1)"
[ -n "$zip" ] || die "Missing bepinex/BepInEx_win_${arch}_*.zip in the installer folder."
if [ -f "$game/BepInEx/core/BepInEx.dll" ] && [ -f "$game/winhttp.dll" ]; then
    if [ "$(pe_arch "$game/winhttp.dll")" != "$arch" ]; then
        python3 -c 'import zipfile,sys; open(sys.argv[2],"wb").write(zipfile.ZipFile(sys.argv[1]).read("winhttp.dll"))' "$zip" "$game/winhttp.dll"
        ok "BepInEx loader switched to $arch (the game is $arch; the old one never loaded)."
    else
        ok "Already installed ($arch)."
    fi
else
    python3 -m zipfile -e "$zip" "$game"
    basename "$zip" > "$online/installed-bepinex.txt"
    ok "Installed $(basename "$zip")"
fi
if [ -f "$game/doorstop_config.ini" ] && grep -qE '^[[:space:]]*enabled[[:space:]]*=[[:space:]]*false' "$game/doorstop_config.ini"; then
    cp -f "$game/doorstop_config.ini" "$game/doorstop_config.ini.sf-online.bak"
    sed -i -E 's/^([[:space:]]*)enabled[[:space:]]*=[[:space:]]*false/\1enabled = true/' "$game/doorstop_config.ini"
    ok 'BepInEx enabled (it was switched off).'
fi

step 4 'Installing the mod...'
mkdir -p "$plug"
mkdir -p "$game/BepInEx/patchers/SFOnline"
cp -f "$files/SFOnlinePatcher.dll" "$files/SFOnline.donor" "$game/BepInEx/patchers/SFOnline/"; ok 'SFOnlinePatcher (builds the online game code from your own copy, only for the server)'
[ -f "$online/Assembly-CSharp.dll" ] && { rm -f "$online/Assembly-CSharp.dll"; ok 'Removed the old online game file'; }
cp -f "$files/SFClientRecon.dll" "$plug/SFClientRecon.dll"; ok 'SFClientRecon.dll'
cp -f "$files/SFServerBrowser.dll" "$plug/SFServerBrowser.dll"; ok 'SFServerBrowser.dll'
for srv in SFHeadlessHost.dll SFBoxFix.dll; do   # server-only: never on a player's PC
    [ -f "$plug/$srv" ] && { mv -f "$plug/$srv" "$plug/$srv.server-only"; warn "$srv moved aside (server-only)."; }
done

step 5 'Installing the launcher (auto-update)...'
for f in launch.py sf-wrap.sh steam_options.py; do cp -f "$files/$f" "$online/$f"; done
chmod +x "$online/launch.py" "$online/sf-wrap.sh" "$online/steam_options.py"
[ -z "$server" ] && [ -f "$here/server.txt" ] && server="$(head -1 "$here/server.txt" | tr -d '\r ')"
[ -z "$server" ] && read -r -p '    Server address (host:port): ' server
host="${server%%:*}"; port="${server##*:}"; [ "$port" = "$server" ] && port=1338
murl=""; [ -f "$here/manifest-url.txt" ] && murl="$(head -1 "$here/manifest-url.txt" | tr -d '\r ')"
python3 - "$online/config.json" "$host" "$port" "$murl" <<'EOF'
import json, sys
path, host, port, url = sys.argv[1:]
json.dump({'server': host, 'port': int(port), 'manifestUrl': url}, open(path, 'w'), indent=2)
EOF
ok "Server $host:$port"

wrap="$online/sf-wrap.sh"
set_options() { python3 "$online/steam_options.py" set "$steamroot" "$wrap" | sed 's/^/    /'; }
if pgrep -x steam >/dev/null 2>&1 || pgrep -f 'steamwebhelper|ubuntu12_32/steam' >/dev/null 2>&1; then
    warn 'Steam is running. The launch option can only be set while Steam is closed.'
    read -r -p '    Close Steam now and set it? [Y/n] ' a
    if [ -z "$a" ] || [[ "$a" =~ ^[Yy] ]]; then
        (steam -shutdown >/dev/null 2>&1 || flatpak run com.valvesoftware.Steam -shutdown >/dev/null 2>&1 || true) &
        for _ in $(seq 1 45); do pgrep -x steam >/dev/null 2>&1 || pgrep -f 'ubuntu12_32/steam' >/dev/null 2>&1 || break; sleep 1; done
    fi
fi
if pgrep -x steam >/dev/null 2>&1 || pgrep -f 'ubuntu12_32/steam' >/dev/null 2>&1; then
    warn 'Steam is still running, so set it by hand: Steam > Stick Fight > Properties > Launch Options:'
    echo "        \"$wrap\" %command%"
elif [ -n "$steamroot" ] && set_options; then
    ok 'Steam launch option set (a normal Steam launch stays the normal game).'
else
    warn 'Could not set the launch option automatically. Set it by hand: Steam > Stick Fight > Properties > Launch Options:'
    echo "        \"$wrap\" %command%"
fi

step 6 'App menu entry...'
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$apps"
desk="$apps/stick-fight-online.desktop"
cat > "$desk" <<EOF
[Desktop Entry]
Type=Application
Name=Stick Fight Online
Comment=Stick Fight on the sf-multiplayer server (checks for updates first)
Exec=python3 "$online/launch.py"
Icon=steam_icon_$APPID
Terminal=false
Categories=Game;
EOF
chmod +x "$desk"
ok "$desk"
desktop_dir="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
if [ -d "$desktop_dir" ]; then
    cp -f "$desk" "$desktop_dir/stick-fight-online.desktop"; chmod +x "$desktop_dir/stick-fight-online.desktop"
    command -v gio >/dev/null && gio set "$desktop_dir/stick-fight-online.desktop" metadata::trusted true 2>/dev/null || true
    ok "$desktop_dir/stick-fight-online.desktop"
fi
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" 2>/dev/null || true
printf '%s\n' "$game" > "$online/game-path.txt"

echo
echo '==================================================================='
echo '   INSTALL COMPLETE'
echo '==================================================================='
echo '  Play with "Stick Fight Online" (app menu / desktop). It updates the'
echo '  mod by itself, then starts the game on the server through Steam.'
echo '  Steam Deck Game Mode: Steam > Add a Non-Steam Game > Stick Fight Online.'
echo '  Starting Stick Fight from Steam is the normal game.'
echo '  Undo everything: ./uninstall-linux.sh'
echo
