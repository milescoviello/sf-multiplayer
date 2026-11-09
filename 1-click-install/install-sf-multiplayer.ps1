# ===================================================================
#  Stick Fight Online - installer (sf-multiplayer)
#
#  1. Finds Stick Fight (any Steam library).
#  2. Backs up your original Assembly-CSharp.dll (once, never overwritten).
#  3. Installs BepInEx 5.4.23.5 if you don't have it (bundled, official zip).
#  4. Installs the patched Assembly-CSharp + the SFClientRecon and
#     SFServerBrowser plugins.
#  5. Installs the launcher (<game>\sf-online\) and lets normal users update
#     the mod's files, so updates never need admin again.
#  6. Adds "Stick Fight Online" to the desktop and the Start menu. That
#     shortcut checks for updates (a few KB), installs any, then starts the
#     game on the server.
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
$TargetAsm = Join-Path $Managed 'Assembly-CSharp.dll'
$Backup    = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug      = Join-Path $Sf 'BepInEx\plugins'
$Online    = Join-Path $Sf 'sf-online'
$Patched   = Join-Path $Files 'Assembly-CSharp.srv.v25.dll'
foreach ($f in @($Patched, (Join-Path $Files 'SFClientRecon.dll'), (Join-Path $Files 'SFServerBrowser.dll'), (Join-Path $Files 'launch.ps1'), (Join-Path $Files 'launch.vbs'))) {
    if (-not (Test-Path $f)) { throw "Missing installer file: $f (extract the whole zip first)" }
}
New-Item -ItemType Directory -Force -Path $Online | Out-Null

# ------------------------------------------------------------ backup
Step 2 'Backing up your original game file...'
$patchedHash = (Get-FileHash $Patched -Algorithm SHA256).Hash
if (Test-Path $Backup) {
    Ok 'Backup already exists (kept as is).'
} elseif ((Test-Path $TargetAsm) -and ((Get-FileHash $TargetAsm -Algorithm SHA256).Hash -ne $patchedHash)) {
    Copy-Item $TargetAsm $Backup -Force
    Ok 'Saved Assembly-CSharp.dll.vanilla.bak'
} else {
    Warn 'No original to back up (already patched). Steam > Verify integrity restores it if needed.'
}

# ------------------------------------------------------------ BepInEx
Step 3 'BepInEx (mod loader)...'
$hasBep = (Test-Path (Join-Path $Sf 'BepInEx\core\BepInEx.dll')) -and (Test-Path (Join-Path $Sf 'winhttp.dll'))
if ($hasBep) {
    Ok 'Already installed.'
} else {
    $bepZip = Get-ChildItem (Join-Path $Here 'bepinex') -Filter 'BepInEx_win_x86_*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $bepZip) { throw 'Missing bepinex\BepInEx_win_x86_*.zip in the installer folder.' }
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
Copy-Item $Patched $TargetAsm -Force;                                          Ok 'Patched Assembly-CSharp.dll'
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
foreach ($d in @($Plug, $Online)) { & icacls $d /grant $users /T /C /Q | Out-Null }
& icacls $TargetAsm /grant '*S-1-5-32-545:M' /C /Q | Out-Null
Ok 'Updates will not need admin.'

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
