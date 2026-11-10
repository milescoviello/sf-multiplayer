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

# Does a Stick Fight Online router answer there? (Its lobby-list request,
# what the in-game list sends.) A name can resolve and still not be the game
# server, e.g. a DNS record behind a web proxy that drops UDP.
function Test-SfServer($ip, $port) {
    $u = $null
    try {
        $u = New-Object Net.Sockets.UdpClient
        $u.Client.ReceiveTimeout = 1200
        $pkt = [byte[]](0x53,0x46,0x52,0x54,0x52,0,0,1, 3,0, 0x2A,0,0,0)
        for ($i = 0; $i -lt 3; $i++) {
            [void]$u.Send($pkt, $pkt.Length, $ip, [int]$port)
            try { $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0); if ($u.Receive([ref]$ep).Length -gt 0) { return $true } } catch { }
        }
    } catch { } finally { if ($u) { $u.Close() } }
    return $false
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
            $mIp = Resolve-ServerIp $mServer
            if ($mIp -and (Test-SfServer $mIp $mPort)) {
                $server = $mServer; $port = $mPort
                try { $cfg.server = $server; $cfg.port = $port; ($cfg | ConvertTo-Json) | Set-Content (Join-Path $Dir 'config.json') -Encoding UTF8 } catch { }
                Log "server address from manifest: ${server}:${port}"
            } else { Log "manifest server ${mServer}:${mPort} doesn't answer yet: keeping ${server}:${port}" }
        }
        $changed = 0
        foreach ($f in $m.files) {
            if ($f.os -eq 'linux') { continue }
            $rel = ($f.path -replace '/', '\')
            if ($rel.Contains('..') -or [IO.Path]::IsPathRooted($rel)) { Log "skipped unsafe path $($f.path)"; continue }
            $dest = Join-Path $Game $rel
            $have = ''
            if (Test-Path $dest) { $have = (Get-FileHash $dest -Algorithm SHA256).Hash }
            if ($have -ieq $f.sha256) { continue }
            $tmp = "$dest.download"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            try { Invoke-WebRequest -Uri $f.url -OutFile $tmp -UseBasicParsing -TimeoutSec 90 }
            catch { Remove-Item $tmp -Force -ErrorAction SilentlyContinue; Log "download failed for $($f.path) (kept the old file): $($_.Exception.Message)"; continue }
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
if (-not (Test-SfServer $ip $port)) {
    Log "the server ${server} (${ip}:${port}) isn't answering"
    try { [void](New-Object -ComObject WScript.Shell).Popup("The server ($server) isn't answering right now. Stick Fight will start and keep trying to connect.", 8, 'Stick Fight Online', 48) } catch { }
}
$server = $ip
$override = Join-Path $Game 'sf-online'
$gameArgs = "-address $server -port $port --doorstop-enabled true --doorstop-mono-dll-search-path-override `"$override`""
# Stay (hidden) until the game exits: if the lobby restarted under a match,
# the game leaves sf-online\rejoin.txt and quits, and is started again
# straight into that lobby (the game can't rejoin mid-match by itself).
# A restarted game opens behind whatever had focus: Windows only lets the
# foreground app give focus away, and this launcher runs hidden. A tapped Alt
# key lifts that lock for SetForegroundWindow.
function Bring-ToFront($proc) {
    try {
        Add-Type -Namespace SfOnline -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
'@ -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 60 -and $proc.MainWindowHandle -eq [IntPtr]::Zero -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
        $h = $proc.MainWindowHandle
        if ($h -eq [IntPtr]::Zero) { return }
        [SfOnline.Win]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [SfOnline.Win]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [void][SfOnline.Win]::ShowWindow($h, 9)
        [void][SfOnline.Win]::SetForegroundWindow($h)
    } catch { Log "bring to front: $($_.Exception.Message)" }
}
$note = Join-Path $Dir 'rejoin.txt'
Remove-Item $note -Force -ErrorAction SilentlyContinue
$env:SF_RELAUNCH = '1'
for ($run = 0; $run -lt 4; $run++) {
    Log "starting StickFight.exe $gameArgs"
    $p = Start-Process -FilePath (Join-Path $Game 'StickFight.exe') -ArgumentList $gameArgs -WorkingDirectory $Game -PassThru
    if ($run -gt 0) { Bring-ToFront $p }
    $p.WaitForExit()
    if (-not (Test-Path $note)) { break }
    $lobby = ''
    try { $lobby = (Get-Content $note -TotalCount 1).Trim().ToUpper() } catch { }
    Remove-Item $note -Force -ErrorAction SilentlyContinue
    if ($lobby -notmatch '^[A-Z0-9_-]{1,16}$') { break }
    $env:SF_LOBBY = $lobby
    Log "the lobby restarted mid-match: starting the game again into $lobby"
    Start-Sleep -Milliseconds 500
}
