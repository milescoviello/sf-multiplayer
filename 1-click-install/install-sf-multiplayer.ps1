# ===================================================================
#  Stick Fight Online - installer (sf-multiplayer)
#
#  1. Finds Stick Fight (any Steam library).
#  2. Never changes the game's own files: starting Stick Fight from Steam is
#     the normal game. (An older install that patched the game file is
#     undone: the backup is restored, or Steam re-checks the game.)
#  3. Installs BepInEx 5.4.23.5 if you don't have it (bundled, official zip).
#  4. Installs the mod: the SFOnlinePatcher (BepInEx\patchers\SFOnline\), which
#     builds the online version of the game code in memory from your own
#     game file, only when the game is started for the server, and the
#     SFClientRecon / SFServerBrowser plugins, which also stay idle otherwise.
#     No game file is shipped or replaced.
#  5. Installs the launcher (<game>\sf-online\), lets normal users update the
#     mod's files (updates never need admin) and allows the game through
#     Windows Firewall (no prompt on first start).
#  6. Adds "Stick Fight Online" to the desktop and the Start menu. That
#     shortcut checks for updates (a few KB), installs any, then starts the
#     game on the server with the lobby list open.
#
#  Run it again any time: it repairs or updates. To undo everything run
#  UNINSTALL-sf-multiplayer.bat.
# ===================================================================
param(
    [string]$GamePath = '',
    [string]$Server   = '',      # host:port; default from server.txt
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $Here 'common.ps1')
$Files = Join-Path $Here 'files'

Write-Host ''
Write-Host '   ===============================================' -ForegroundColor Cyan
Write-Host '     Stick Fight Online  -  installer' -ForegroundColor Yellow
Write-Host '   ===============================================' -ForegroundColor Cyan
Write-Host ''

# ------------------------------------------------------------ find the game
Step 1 'Finding Stick Fight: The Game...'
$Sf = $GamePath
if (-not $Sf) { $Sf = Find-StickFight }
if (-not $Sf -or -not (Test-Path (Join-Path $Sf 'StickFight.exe'))) {
    Warn 'Could not find Stick Fight automatically.'
    $Sf = Read-Host '    Paste the path to your StickFightTheGame folder'
    $Sf = $Sf.Trim('"', ' ')
    if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) { throw "No StickFight.exe in $Sf" }
}
$Sf = (Resolve-Path $Sf).Path
Ok $Sf
Wait-GameClosed $Sf

$Managed   = Join-Path $Sf 'StickFight_Data\Managed'
$GameAsm   = Join-Path $Managed 'Assembly-CSharp.dll'
$Backup    = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug      = Join-Path $Sf 'BepInEx\plugins'
$Online    = Join-Path $Sf 'sf-online'
$PatchDir  = Join-Path $Sf 'BepInEx\patchers\SFOnline'
$StockHashes = @(Get-Content (Join-Path $Files 'game-file.sha256') | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim().ToUpper() })   # every stock build the mod knows
foreach ($f in @((Join-Path $Files 'SFOnlinePatcher.dll'), (Join-Path $Files 'SFOnline.donor'), (Join-Path $Files 'SFClientRecon.dll'), (Join-Path $Files 'SFServerBrowser.dll'), (Join-Path $Files 'launch.ps1'), (Join-Path $Files 'launch.vbs'))) {
    if (-not (Test-Path $f)) { throw "Missing installer file: $f (extract the whole zip first)" }
}
New-Item -ItemType Directory -Force -Path $Online | Out-Null

# ------------------------------------------------------------ game file
Step 2 'Keeping the game itself normal (Steam = normal Stick Fight)...'
$gameHash = ''
if (Test-Path $GameAsm) { $gameHash = (Get-FileHash $GameAsm -Algorithm SHA256).Hash }
# Any patched build (an older install of this mod) has the UDP client stock never had.
$gameIsPatched = $false
if (Test-Path $GameAsm) {
    try { $gameIsPatched = [IO.File]::ReadAllText($GameAsm, [Text.Encoding]::ASCII).Contains('UDPReceive') } catch { }
}
if ($gameIsPatched) {
    if (Test-Path $Backup) {
        Copy-Item $Backup $GameAsm -Force
        Remove-Item $Backup -Force
        Ok 'An older install had patched the game file: original restored.'
    } else {
        Warn 'An older install patched the game file and left no backup: asking Steam to re-check Stick Fight.'
        Start-Process 'steam://validate/674940' -ErrorAction SilentlyContinue
    }
} elseif ($StockHashes -notcontains $gameHash.ToUpper()) {
    Warn "Your game file isn't the stock Steam version, and the online mode is built from it."
    Warn 'Steam is re-checking Stick Fight; run this installer again when it has finished.'
    Start-Process 'steam://validate/674940' -ErrorAction SilentlyContinue
} else {
    Ok 'Game file untouched (stock).'
}

# ------------------------------------------------------------ BepInEx
Step 3 'BepInEx (mod loader)...'
$Arch = Get-PeArch (Join-Path $Sf 'StickFight.exe')
if (-not $Arch) { $Arch = 'x64' }
$bepZip = Get-ChildItem (Join-Path $Here 'bepinex') -Filter "BepInEx_win_${Arch}_*.zip" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $bepZip) { throw "Missing bepinex\BepInEx_win_${Arch}_*.zip in the installer folder." }
$hasBep = (Test-Path (Join-Path $Sf 'BepInEx\core\BepInEx.dll')) -and (Test-Path (Join-Path $Sf 'winhttp.dll'))
if ($hasBep) {
    if ((Get-PeArch (Join-Path $Sf 'winhttp.dll')) -ne $Arch) {
        Set-BepInExLoader $bepZip.FullName $Sf
        Ok "BepInEx loader switched to $Arch (the game is $Arch; the old one never loaded)."
    } else { Ok "Already installed ($Arch)." }
} else {
    Expand-Archive -Path $bepZip.FullName -DestinationPath $Sf -Force
    Set-Content -Path (Join-Path $Online 'installed-bepinex.txt') -Value $bepZip.Name
    Ok ("Installed {0}" -f $bepZip.Name)
}
$doorstop = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path $doorstop) {
    $ini = Get-Content $doorstop -Raw
    # Only the [General] "enabled" key (not debug_enabled & co).
    if ($ini -match '(?m)^\s*enabled\s*=\s*false') {
        Copy-Item $doorstop "$doorstop.sf-online.bak" -Force
        Set-Content $doorstop ($ini -replace '(?m)^(\s*)enabled\s*=\s*false', '${1}enabled = true') -NoNewline
        Ok 'BepInEx enabled (it was switched off).'
    }
}
if ((Test-Path (Join-Path $Sf 'MelonLoader')) -or (Test-Path (Join-Path $Sf 'version.dll'))) {
    Warn 'MelonLoader found too. If the online menu fails, rename version.dll for a test.'
}

# ------------------------------------------------------------ mod files
Step 4 'Installing the mod...'
New-Item -ItemType Directory -Force -Path $Plug | Out-Null
New-Item -ItemType Directory -Force -Path $PatchDir | Out-Null
Copy-Item (Join-Path $Files 'SFOnlinePatcher.dll') (Join-Path $PatchDir 'SFOnlinePatcher.dll') -Force
Copy-Item (Join-Path $Files 'SFOnline.donor') (Join-Path $PatchDir 'SFOnline.donor') -Force;   Ok 'SFOnlinePatcher (builds the online game code from your own copy, only for the server)'
$oldOnline = Join-Path $Online 'Assembly-CSharp.dll'                               # older installs shipped a game file
if (Test-Path $oldOnline) { Remove-Item $oldOnline -Force; Ok 'Removed the old online game file' }
$oldPre = Join-Path $Sf 'BepInEx\patchers\SFOnlinePreloader.dll'                  # test builds only
if (Test-Path $oldPre) { Remove-Item $oldPre -Force }
Copy-Item (Join-Path $Files 'SFClientRecon.dll')   (Join-Path $Plug 'SFClientRecon.dll') -Force;   Ok 'SFClientRecon.dll'
Copy-Item (Join-Path $Files 'SFServerBrowser.dll') (Join-Path $Plug 'SFServerBrowser.dll') -Force; Ok 'SFServerBrowser.dll'
foreach ($srv in @('SFHeadlessHost.dll', 'SFBoxFix.dll')) {     # server-only: never on a player's PC
    $p = Join-Path $Plug $srv
    if (Test-Path $p) { Move-Item $p "$p.server-only" -Force; Warn "$srv moved aside (server-only)." }
}

# ------------------------------------------------------------ launcher
Step 5 'Installing the launcher (auto-update)...'
Copy-Item (Join-Path $Files 'launch.ps1') (Join-Path $Online 'launch.ps1') -Force
Copy-Item (Join-Path $Files 'launch.vbs') (Join-Path $Online 'launch.vbs') -Force
$cfgPath = Join-Path $Online 'config.json'
$cfg = $null
if (Test-Path $cfgPath) { try { $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json } catch { $cfg = $null } }
if (-not $Server) {
    $st = Join-Path $Here 'server.txt'
    if (Test-Path $st) { $Server = (Get-Content $st -TotalCount 1).Trim() }
}
if (-not $Server -and $cfg) { $Server = "{0}:{1}" -f $cfg.server, $cfg.port }
if (-not $Server) { $Server = Read-Host '    Server address (host:port)' }
$hostPart, $portPart = $Server.Split(':')
if (-not $portPart) { $portPart = '1338' }
$manifestUrl = ''
$mu = Join-Path $Here 'manifest-url.txt'
if (Test-Path $mu) { $manifestUrl = (Get-Content $mu -TotalCount 1).Trim() }
if (-not $manifestUrl -and $cfg) { $manifestUrl = $cfg.manifestUrl }
$newCfg = [ordered]@{ server = $hostPart; port = [int]$portPart; manifestUrl = $manifestUrl }
($newCfg | ConvertTo-Json) | Set-Content $cfgPath -Encoding UTF8
Ok ("Server {0}:{1}" -f $hostPart, $portPart)

# Updates run as the normal user: let Users modify the mod's own files.
$users = '*S-1-5-32-545:(OI)(CI)M'          # BUILTIN\Users, any Windows language
foreach ($d in @($Plug, $Online, $PatchDir)) { & icacls $d /grant $users /T /C /Q | Out-Null }
# The BepInEx loader too: the launcher swaps it if the game changes architecture.
$loaderFile = Join-Path $Sf 'winhttp.dll'
if (Test-Path $loaderFile) { & icacls $loaderFile /grant '*S-1-5-32-545:M' /C /Q | Out-Null }
Ok 'Updates will not need admin.'
try {
    Get-SfFirewallRules (Join-Path $Sf 'StickFight.exe') | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'Stick Fight Online' -Direction Inbound -Program (Join-Path $Sf 'StickFight.exe') -Protocol UDP -Action Allow -Profile Any | Out-Null
    Ok 'Windows Firewall: Stick Fight allowed (no prompt on first start).'
} catch { Warn ("Firewall rule not added ({0}); Windows may ask once on first start: click Allow." -f $_.Exception.Message) }

# ------------------------------------------------------------ shortcuts
Step 6 'Shortcuts...'
$shell = New-Object -ComObject WScript.Shell
foreach ($lnkPath in (Get-ShortcutPaths)) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $lnk.Arguments = '"' + (Join-Path $Online 'launch.vbs') + '"'
    $lnk.WorkingDirectory = $Sf
    $lnk.IconLocation = (Join-Path $Sf 'StickFight.exe') + ',0'
    $lnk.Description = 'Stick Fight on the sf-multiplayer server (checks for updates first)'
    $lnk.Save()
    Ok $lnkPath
}
$old = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Play-StickFight.bat'   # older installer's shortcut
if (Test-Path $old) { Remove-Item $old -Force; Ok 'Removed the old Play-StickFight.bat' }

Write-Host ''
Write-Host '===================================================================' -ForegroundColor Green
Write-Host '   INSTALL COMPLETE' -ForegroundColor Yellow
Write-Host '===================================================================' -ForegroundColor Green
Write-Host '  Play with the "Stick Fight Online" shortcut (desktop / Start menu).' -ForegroundColor White
Write-Host '  It updates the mod by itself before each start.' -ForegroundColor Gray
Write-Host '  In game: PLAY ONLINE, or F2 for the lobby list.' -ForegroundColor Gray
Write-Host '  Undo everything: UNINSTALL-sf-multiplayer.bat' -ForegroundColor DarkGray
Write-Host ''
if (-not $NoLaunch) {
    $go = Read-Host 'Start Stick Fight now? [Y/n]'
    if ($go -eq '' -or $go -match '^[Yy]') { Start-Process (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList ('"' + (Join-Path $Online 'launch.vbs') + '"') }
}
