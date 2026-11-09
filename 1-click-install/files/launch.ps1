# Stick Fight Online launcher (installed into <game>\sf-online\).
# Started hidden by launch.vbs from the "Stick Fight Online" shortcut:
#  1. reads config.json (server, port, manifestUrl);
#  2. fetches the update manifest (a few KB) and installs any mod file whose
#     SHA-256 differs, verifying each download before it replaces anything;
#  3. starts the game on the server (patched game file from sf-online\ for
#     this launch only; Steam launches stay the normal game).
# No network, or GitHub down: it just starts the game. Log: launch.log.
$ErrorActionPreference = 'Continue'
$Dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Game = Split-Path -Parent $Dir
$Log  = Join-Path $Dir 'launch.log'
function Log($m) { try { Add-Content -Path $Log -Value ("{0} {1}" -f (Get-Date -Format s), $m) } catch { } }
if ((Test-Path $Log) -and (Get-Item $Log).Length -gt 256KB) { Remove-Item $Log -Force -ErrorAction SilentlyContinue }

function Resolve-ServerIp($name) {
    $parsed = $null
    if ([Net.IPAddress]::TryParse($name, [ref]$parsed)) { return $name }
    try { return ([Net.Dns]::GetHostAddresses($name) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString } catch { return $null }
}

$cfg = $null
try { $cfg = Get-Content (Join-Path $Dir 'config.json') -Raw | ConvertFrom-Json } catch { Log "config.json unreadable: $($_.Exception.Message)" }
$server = '127.0.0.1'; $port = 1338
if ($cfg) { if ($cfg.server) { $server = $cfg.server }; if ($cfg.port) { $port = [int]$cfg.port } }

$running = Get-Process StickFight -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Game, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { Log 'game already running: not updating, not starting a second copy'; exit 0 }

if ($cfg -and $cfg.manifestUrl) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $sep = '?'; if ($cfg.manifestUrl.Contains('?')) { $sep = '&' }
        $resp = Invoke-WebRequest -Uri ($cfg.manifestUrl + $sep + 't=' + [DateTime]::UtcNow.Ticks) -UseBasicParsing -TimeoutSec 6
        $m = $resp.Content | ConvertFrom-Json
        # The manifest may carry the server address: a moved server reaches
        # every install with the next start (saved for offline starts too).
        if ($m.server -and (($m.server -ne $server) -or ([int]$m.port -ne $port))) {
            $mServer = $m.server; $mPort = $port; if ($m.port) { $mPort = [int]$m.port }
            if (Resolve-ServerIp $mServer) {
                $server = $mServer; $port = $mPort
                try { $cfg.server = $server; $cfg.port = $port; ($cfg | ConvertTo-Json) | Set-Content (Join-Path $Dir 'config.json') -Encoding UTF8 } catch { }
                Log "server address from manifest: ${server}:${port}"
            } else { Log "manifest server $mServer doesn't resolve yet: keeping ${server}:${port}" }
        }
        $changed = 0
        foreach ($f in $m.files) {
            $rel = ($f.path -replace '/', '\')
            if ($rel.Contains('..') -or [IO.Path]::IsPathRooted($rel)) { Log "skipped unsafe path $($f.path)"; continue }
            $dest = Join-Path $Game $rel
            $have = ''
            if (Test-Path $dest) { $have = (Get-FileHash $dest -Algorithm SHA256).Hash }
            if ($have -ieq $f.sha256) { continue }
            $tmp = "$dest.download"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            Invoke-WebRequest -Uri $f.url -OutFile $tmp -UseBasicParsing -TimeoutSec 90
            $got = (Get-FileHash $tmp -Algorithm SHA256).Hash
            if ($got -ine $f.sha256) { Remove-Item $tmp -Force; Log "checksum mismatch for $($f.path), kept the old file"; continue }
            Move-Item -Force $tmp $dest
            $changed++
            Log "updated $($f.path)"
        }
        Log ("manifest {0}: {1} file(s) updated" -f $m.version, $changed)
    } catch { Log "update check failed (starting anyway): $($_.Exception.Message)" }
}

# The patched Assembly-CSharp lives in sf-online\ and is used only for this
# launch (BepInEx's Doorstop puts that folder first when Mono loads it); a
# Steam launch loads the game's own file. Doorstop is forced on here so the
# server works even if the player switched BepInEx off for normal play.
# The game only takes an IP address: resolve a host name first.
$ip = Resolve-ServerIp $server
if (-not $ip) {
    Log "can't resolve $server"
    try { [void](New-Object -ComObject WScript.Shell).Popup("Can't find the server ($server). Check your internet connection and try again.", 0, 'Stick Fight Online', 48) } catch { }
    exit 1
}
$server = $ip
$override = Join-Path $Game 'sf-online'
$gameArgs = "-address $server -port $port --doorstop-enabled true --doorstop-mono-dll-search-path-override `"$override`""
Log "starting StickFight.exe $gameArgs"
Start-Process -FilePath (Join-Path $Game 'StickFight.exe') -ArgumentList $gameArgs -WorkingDirectory $Game
