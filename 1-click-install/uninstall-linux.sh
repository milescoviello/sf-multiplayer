#!/usr/bin/env bash
# Stick Fight Online - uninstaller for Linux / Steam Deck. Puts Stick Fight
# back the way it was, without touching other mods: removes only our files,
# our part of the Steam launch option, the app entries, and BepInEx only if
# this installer put it there and nothing else is in BepInEx/plugins.
#   ./uninstall-linux.sh [--game <StickFightTheGame folder>]
set -euo pipefail
game=""
[ "${1:-}" = "--game" ] && game="$2"
ok()   { printf '    \033[32mOK\033[0m  %s\n' "$1"; }
warn() { printf '    \033[33m!!\033[0m  %s\n' "$1"; }
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
desktop_dir="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
if [ -z "$game" ] && [ -f "$apps/stick-fight-online.desktop" ]; then
    game="$(sed -n 's/^Exec=python3 "\(.*\)\/sf-online\/launch.py"$/\1/p' "$apps/stick-fight-online.desktop")"
fi
[ -n "$game" ] || read -r -p 'Path to your StickFightTheGame folder: ' game
[ -f "$game/StickFight.exe" ] || { echo "No StickFight.exe in $game"; exit 1; }
online="$game/sf-online"; plug="$game/BepInEx/plugins"
echo 'Stick Fight Online - uninstall'

if [ -f "$online/steam_options.py" ]; then
    done_opts=0
    for r in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/.var/app/com.valvesoftware.Steam/data/Steam"; do
        [ -d "$r/userdata" ] || continue
        if pgrep -x steam >/dev/null 2>&1 || pgrep -f 'ubuntu12_32/steam' >/dev/null 2>&1; then break; fi
        python3 "$online/steam_options.py" unset "$(readlink -f "$r")" "$online/sf-wrap.sh" | sed 's/^/    /' && done_opts=1
    done
    if [ "$done_opts" = 1 ]; then ok 'Removed the launch option'
    else warn "Steam is running: remove \"$online/sf-wrap.sh\" from Stick Fight's Launch Options by hand (or close Steam and run this again)."; fi
fi
for p in SFClientRecon.dll SFServerBrowser.dll; do
    [ -f "$plug/$p" ] && { rm -f "$plug/$p"; ok "Removed $p"; }
done
[ -f "$game/doorstop_config.ini.sf-online.bak" ] && { mv -f "$game/doorstop_config.ini.sf-online.bak" "$game/doorstop_config.ini"; ok 'Restored doorstop_config.ini'; }
if [ -f "$online/installed-bepinex.txt" ]; then
    if [ -z "$(ls -A "$plug" 2>/dev/null)" ] && [ -z "$(ls -A "$game/BepInEx/patchers" 2>/dev/null)" ]; then
        rm -rf "$game/BepInEx" "$game/winhttp.dll" "$game/doorstop_config.ini" "$game/.doorstop_version" "$game/changelog.txt"
        ok 'Removed BepInEx (this installer had added it)'
    else
        warn 'BepInEx kept: other mods are installed in BepInEx/plugins.'
    fi
fi
rm -rf "$online"; ok 'Removed the launcher'
for d in "$apps/stick-fight-online.desktop" "$desktop_dir/stick-fight-online.desktop"; do
    [ -f "$d" ] && { rm -f "$d"; ok "Removed $d"; }
done
echo 'Done. Stick Fight is back to normal.'
