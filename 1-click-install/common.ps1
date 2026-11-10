# Shared by install / uninstall. PowerShell 5.1 (stock Windows 10/11).

function Find-StickFight {
    $candidates = @()
    $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame')
    $candidates += (Join-Path $env:ProgramFiles 'Steam\steamapps\common\StickFightTheGame')
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            $candidates += (Join-Path $steam 'steamapps\common\StickFightTheGame')
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path $vdf) {
                foreach ($line in Get-Content $vdf) {
                    if ($line -match '"path"\s+"([^"]+)"') {
                        $lib = $matches[1] -replace '\\\\', '\'
                        $candidates += (Join-Path $lib 'steamapps\common\StickFightTheGame')
                    }
                }
            }
        }
    } catch { }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'StickFight.exe'))) { return $c }
    }
    return $null
}

function Step($n, $txt) { Write-Host ("[{0}] {1}" -f $n, $txt) -ForegroundColor Cyan }
function Ok($txt)       { Write-Host ("    OK  {0}" -f $txt) -ForegroundColor Green }
function Warn($txt)     { Write-Host ("    !!  {0}" -f $txt) -ForegroundColor Yellow }

function Wait-GameClosed($exeDir) {
    while (Get-Process StickFight -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($exeDir, [StringComparison]::OrdinalIgnoreCase) }) {
        Warn 'Stick Fight is running. Close it, then press Enter.'
        [void](Read-Host)
    }
}

# Shortcut names/places, shared so the uninstaller removes exactly what we made.
$ShortcutName = 'Stick Fight Online.lnk'
function Get-ShortcutPaths {
    $paths = @()
    $paths += (Join-Path ([Environment]::GetFolderPath('Desktop')) $ShortcutName)
    $paths += (Join-Path ([Environment]::GetFolderPath('Programs')) $ShortcutName)
    return $paths
}

# The firewall rule for THIS copy of the game only (another copy on the same
# PC, e.g. a second Steam library, keeps its own rule).
function Get-SfFirewallRules($exe) {
    Get-NetFirewallRule -DisplayName 'Stick Fight Online' -ErrorAction SilentlyContinue | Where-Object {
        $prog = ($_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue).Program
        $prog -and ([IO.Path]::GetFullPath($prog) -ieq [IO.Path]::GetFullPath($exe))
    }
}

# 'x64' or 'x86' from a Windows binary's PE header ('' if unreadable).
# Stick Fight's Steam build is 64-bit since its 2026 update (32-bit before),
# and BepInEx's loader (winhttp.dll) must match the game's.
function Get-PeArch($path) {
    try {
        $fs = [IO.File]::OpenRead($path)
        try { $buf = New-Object byte[] 4096; [void]$fs.Read($buf, 0, 4096) } finally { $fs.Close() }
        $pe = [BitConverter]::ToInt32($buf, 0x3C)
        if ($pe -lt 0 -or $pe + 6 -gt 4096) { return '' }
        switch ([BitConverter]::ToUInt16($buf, $pe + 4)) { 0x8664 { return 'x64' } 0x14c { return 'x86' } default { return '' } }
    } catch { return '' }
}

# Put the BepInEx loader for $arch from the bundled zip into $gameDir.
function Set-BepInExLoader($zipPath, $gameDir) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($z.GetEntry('winhttp.dll'), (Join-Path $gameDir 'winhttp.dll'), $true) } finally { $z.Dispose() }
}
