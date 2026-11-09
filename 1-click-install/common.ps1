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
