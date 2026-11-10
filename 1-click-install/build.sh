#!/usr/bin/env bash
# Build the player installer + the auto-update manifest.
#   ./build.sh            (from anywhere in the repo)
# Env:
#   SF_INSTALLER_SERVER   host:port players connect to (written to server.txt)
#   SF_UPDATE_BRANCH      branch the launcher updates from (default main)
# The online game code: SFOnlinePatcher + SFOnline.donor (built and verified by
# tools/sf-online-donor when private/ holds the stock and patched game files;
# otherwise the committed donor is used as is). No game file ever ships.
# Output:
#   1-click-install/files/*          payload (also what the launcher downloads)
#   1-click-install/manifest.json    file list + SHA-256 (the launcher reads it)
#   sf-multiplayer-StickFight-Installer.zip   what players download
# Publishing an update = run this, commit, push. Every player's next start of
# "Stick Fight Online" installs it.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; root="$(dirname "$here")"
repo="milescoviello/sf-multiplayer"; branch="${SF_UPDATE_BRANCH:-main}"
raw="https://raw.githubusercontent.com/$repo/$branch/1-click-install/files"
files="$here/files"
cp "$root/sf-client-recon/bin/Release/SFClientRecon.dll"     "$files/SFClientRecon.dll"
cp "$root/sf-server-browser/bin/Release/SFServerBrowser.dll" "$files/SFServerBrowser.dll"
cp "$root/sf-online-patcher/bin/Release/SFOnlinePatcher.dll" "$files/SFOnlinePatcher.dll"
priv="$root/private"
if [ -f "$priv/Assembly-CSharp.stock.dll" ] && [ -f "$priv/Assembly-CSharp.srv.v25.dll" ] && [ -n "${SF_MANAGED_DIR:-}" ]; then
  out="$(mktemp -d)"
  dotnet run --project "$root/tools/sf-online-donor" -c Release -v q -- \
    "$priv/Assembly-CSharp.stock.dll" "$priv/Assembly-CSharp.srv.v25.dll" "$SF_MANAGED_DIR" "$out" | grep -v warning
  cp "$out/SFOnline.donor" "$files/SFOnline.donor"
  sha256sum "$priv/Assembly-CSharp.stock.dll" | cut -d' ' -f1 | tr a-f A-F > "$files/game-file.sha256"
  rm -f "$out"/*.dll "$out"/SFOnline.donor "$out"/transplant-log.txt; rmdir "$out"
else
  echo "(using the committed SFOnline.donor: set SF_MANAGED_DIR and put the stock + patched game files in private/ to rebuild it)"
fi
[ -f "$files/SFOnline.donor" ] && [ -f "$files/game-file.sha256" ] || { echo "files/SFOnline.donor or game-file.sha256 missing"; exit 1; }
[ -n "${SF_INSTALLER_SERVER:-}" ] && echo "$SF_INSTALLER_SERVER" > "$here/server.txt"
echo "https://raw.githubusercontent.com/$repo/$branch/1-click-install/manifest.json" > "$here/manifest-url.txt"
ver="$(date -u +%Y%m%d-%H%M)-$(git -C "$root" rev-parse --short HEAD)"
entry() { printf '    {"path": "%s", "url": "%s/%s", "sha256": "%s"%s}' "$1" "$raw" "$2" "$(sha256sum "$files/$2" | cut -d' ' -f1 | tr a-f A-F)" "${3:+, \"os\": \"$3\"}"; }
{
  echo '{'
  echo "  \"version\": \"$ver\","
  srv="$(head -1 "$here/server.txt" | tr -d '\r')"
  echo "  \"server\": \"${srv%%:*}\", \"port\": ${srv##*:},"
  echo '  "files": ['
  entry "BepInEx/patchers/SFOnline/SFOnlinePatcher.dll" "SFOnlinePatcher.dll"; echo ','
  entry "BepInEx/patchers/SFOnline/SFOnline.donor" "SFOnline.donor"; echo ','
  entry "BepInEx/plugins/SFClientRecon.dll" "SFClientRecon.dll"; echo ','
  entry "BepInEx/plugins/SFServerBrowser.dll" "SFServerBrowser.dll"; echo ','
  entry "sf-online/launch.ps1" "launch.ps1" windows; echo ','
  entry "sf-online/launch.vbs" "launch.vbs" windows; echo ','
  entry "sf-online/launch.py" "launch.py" linux; echo ','
  entry "sf-online/sf-wrap.sh" "sf-wrap.sh" linux; echo ','
  entry "sf-online/steam_options.py" "steam_options.py" linux; echo
  echo '  ]'
  echo '}'
} > "$here/manifest.json"
stage="$(mktemp -d)"; pkg="$stage/StickFight-Online"
mkdir -p "$pkg/files" "$pkg/bepinex"
cp "$here"/INSTALL-sf-multiplayer.bat "$here"/UNINSTALL-sf-multiplayer.bat "$here"/common.ps1 \
   "$here"/install-sf-multiplayer.ps1 "$here"/uninstall-sf-multiplayer.ps1 "$here"/README-INSTALL.txt \
   "$here"/install-linux.sh "$here"/uninstall-linux.sh \
   "$here"/server.txt "$here"/manifest-url.txt "$pkg/"
cp "$files"/SFClientRecon.dll "$files"/SFServerBrowser.dll "$files"/SFOnlinePatcher.dll "$files"/SFOnline.donor "$files"/game-file.sha256 \
   "$files"/launch.ps1 "$files"/launch.vbs \
   "$files"/launch.py "$files"/sf-wrap.sh "$files"/steam_options.py "$pkg/files/"
cp "$here"/bepinex/BepInEx_win_x86_*.zip "$pkg/bepinex/"
# Landfall's game files must never ship (see notes/README.md, issue #11).
if find "$pkg" -iname 'Assembly-CSharp*.dll' | grep -q .; then echo "refusing to package an Assembly-CSharp*.dll"; exit 1; fi
rm -f "$root/sf-multiplayer-StickFight-Installer.zip"
(cd "$stage" && zip -qr "$root/sf-multiplayer-StickFight-Installer.zip" StickFight-Online)
rm -rf "$stage"
echo "built $ver: $(du -h "$root/sf-multiplayer-StickFight-Installer.zip" | cut -f1) zip, manifest with $(grep -c '"path"' "$here/manifest.json") files"
