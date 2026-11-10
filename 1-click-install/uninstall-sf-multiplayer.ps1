# Stick Fight Online - uninstaller. Puts Stick Fight back the way it was,
# without touching other mods:
#  - restores the game file if an older install had patched it;
#  - removes only our files (plugins, launcher, firewall rule);
#  - removes BepInEx only if this installer put it there and nothing else
#    is in BepInEx\plugins;
#  - removes the launcher folder and the shortcuts.
param([string]$GamePath = '')
$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $Here 'common.ps1')

Write-Host 'Stick Fight Online - uninstall' -ForegroundColor Yellow
$Sf = $GamePath
if (-not $Sf) { $Sf = Find-StickFight }
if (-not $Sf) { $Sf = (Read-Host 'Paste the path to your StickFightTheGame folder').Trim('"', ' ') }
if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) { throw "No StickFight.exe in $Sf" }
Wait-GameClosed $Sf

$Managed = Join-Path $Sf 'StickFight_Data\Managed'
$Backup  = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$Plug    = Join-Path $Sf 'BepInEx\plugins'
$Online  = Join-Path $Sf 'sf-online'

# The current installer never touches the game file; an older one did.
if (Test-Path $Backup) {
    Copy-Item $Backup (Join-Path $Managed 'Assembly-CSharp.dll') -Force
    Remove-Item $Backup -Force
    Ok 'Restored your original Assembly-CSharp.dll (from an older install)'
}

foreach ($p in @('SFClientRecon.dll', 'SFServerBrowser.dll')) {
    $f = Join-Path $Plug $p
    if (Test-Path $f) { Remove-Item $f -Force; Ok "Removed $p" }
}
$pre = Join-Path $Sf 'BepInEx\patchers\SFOnlinePreloader.dll'
if (Test-Path $pre) { Remove-Item $pre -Force; Ok 'Removed SFOnlinePreloader.dll' }
$patchDir = Join-Path $Sf 'BepInEx\patchers\SFOnline'
if (Test-Path $patchDir) { Remove-Item $patchDir -Recurse -Force; Ok 'Removed SFOnlinePatcher' }
try { $rules = @(Get-SfFirewallRules (Join-Path $Sf 'StickFight.exe')); if ($rules.Count) { $rules | Remove-NetFirewallRule; Ok 'Removed the firewall rule' } } catch { }
$doorstop = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path "$doorstop.sf-online.bak") { Move-Item "$doorstop.sf-online.bak" $doorstop -Force; Ok 'Restored doorstop_config.ini' }

$weInstalledBep = Test-Path (Join-Path $Online 'installed-bepinex.txt')
$otherPlugins = @(Get-ChildItem $Plug -ErrorAction SilentlyContinue) + @(Get-ChildItem (Join-Path $Sf 'BepInEx\patchers') -ErrorAction SilentlyContinue)
if ($weInstalledBep -and $otherPlugins.Count -eq 0) {
    foreach ($p in @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) {
        $f = Join-Path $Sf $p
        if (Test-Path $f) { Remove-Item $f -Recurse -Force }
    }
    Ok 'Removed BepInEx (this installer had added it)'
} elseif ($weInstalledBep) { Warn 'BepInEx kept: other mods are installed in BepInEx\plugins.' }

if (Test-Path $Online) { Remove-Item $Online -Recurse -Force; Ok 'Removed the launcher' }
# Only shortcuts that start THIS copy (the names are shared by every copy).
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in (Get-ShortcutPaths)) {
    if (-not (Test-Path $lnk)) { continue }
    $args0 = ''
    try { $args0 = $shell.CreateShortcut($lnk).Arguments } catch { }
    if ($args0 -and -not $args0.ToLower().Contains($Online.ToLower())) { Warn "Kept $lnk (it starts another copy of Stick Fight)"; continue }
    Remove-Item $lnk -Force; Ok "Removed $lnk"
}
Write-Host 'Done. Stick Fight is back to normal.' -ForegroundColor Green
