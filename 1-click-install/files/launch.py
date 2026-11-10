#!/usr/bin/env python3
# Stick Fight Online launcher for Linux / Steam Deck (installed into
# <game>/sf-online/, started by the "Stick Fight Online" app entry):
#  1. reads config.json (server, port, manifestUrl);
#  2. fetches the update manifest (a few KB) and installs any mod file whose
#     SHA-256 differs, verifying each download before it replaces anything;
#  3. leaves a one-shot note for sf-wrap.sh (the game's Steam launch option)
#     and starts Stick Fight through Steam: that one launch goes to the
#     server (BepInEx's SFOnlinePatcher builds the online game code in memory
#     from the game's own file); a normal Steam launch stays the normal game.
# No network, or GitHub down: it just starts the game. Log: launch.log.
import hashlib, json, os, shutil, socket, subprocess, sys, time, urllib.request

DIR = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.dirname(DIR)
LOG = os.path.join(DIR, 'launch.log')
CFG = os.path.join(DIR, 'config.json')
FLAG = os.path.join(DIR, '.online-launch')
APPID = '674940'


def log(msg):
    try:
        if os.path.exists(LOG) and os.path.getsize(LOG) > 256 * 1024:
            os.remove(LOG)
        with open(LOG, 'a') as f:
            f.write('%s %s\n' % (time.strftime('%Y-%m-%dT%H:%M:%S'), msg))
    except OSError:
        pass


def tell(msg):
    # A visible message without a terminal: whatever dialog tool the desktop has.
    for cmd in (['zenity', '--error', '--title=Stick Fight Online', '--text=' + msg],
                ['kdialog', '--title', 'Stick Fight Online', '--error', msg],
                ['notify-send', 'Stick Fight Online', msg]):
        if shutil.which(cmd[0]):
            try:
                subprocess.Popen(cmd)
                return
            except OSError:
                pass
    print(msg, file=sys.stderr)


def resolve(name):
    try:
        socket.inet_aton(name)
        return name
    except OSError:
        pass
    try:
        return socket.getaddrinfo(name, None, socket.AF_INET)[0][4][0]
    except (OSError, IndexError):
        return None


def answers(ip, port):
    # Does a Stick Fight Online router answer there? (Its lobby-list request,
    # what the in-game list sends.) A name can resolve and still not be the
    # game server, e.g. a DNS record behind a web proxy that drops UDP.
    pkt = b'SFRTR\x00\x00\x01\x03\x00\x2a\x00\x00\x00'
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(1.2)
    try:
        for _ in range(3):
            try:
                s.sendto(pkt, (ip, port))
                if s.recvfrom(2048)[0]:
                    return True
            except OSError:
                pass
    finally:
        s.close()
    return False


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 16), b''):
            h.update(block)
    return h.hexdigest().upper()


def fetch(url, timeout):
    req = urllib.request.Request(url, headers={'User-Agent': 'StickFightOnline-launcher'})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def game_running():
    try:
        out = subprocess.run(['pgrep', '-af', 'StickFight.exe'], capture_output=True, text=True).stdout
    except OSError:
        return False
    return any(GAME in line or GAME.replace('/', '\\') in line for line in out.splitlines())


def update(cfg):
    url = cfg.get('manifestUrl')
    if not url:
        return
    sep = '&' if '?' in url else '?'
    m = json.loads(fetch(url + sep + 't=%d' % time.time(), 6))
    # The manifest may carry the server address: a moved server reaches every
    # install with the next start (saved for offline starts too).
    ms, mp = m.get('server'), int(m.get('port') or cfg['port'])
    if ms and (ms != cfg['server'] or mp != cfg['port']):
        mip = resolve(ms)
        if mip and answers(mip, mp):
            cfg['server'], cfg['port'] = ms, mp
            with open(CFG, 'w') as f:
                json.dump(cfg, f, indent=2)
            log('server address from manifest: %s:%d' % (ms, mp))
        else:
            log("manifest server %s:%d doesn't answer yet: keeping %s:%d" % (ms, mp, cfg['server'], cfg['port']))
    changed = 0
    for f in m.get('files', []):
        if f.get('os') == 'windows':
            continue
        rel = f['path']
        if '..' in rel.split('/') or rel.startswith('/') or '\\' in rel:
            log('skipped unsafe path ' + rel)
            continue
        dest = os.path.join(GAME, *rel.split('/'))
        if os.path.exists(dest) and sha256(dest) == f['sha256'].upper():
            continue
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        tmp = dest + '.download'
        try:
            data = fetch(f['url'], 90)
        except Exception as e:  # one missing file doesn't stop the others
            log('download failed for %s (kept the old file): %s' % (rel, e))
            continue
        with open(tmp, 'wb') as out:
            out.write(data)
        if sha256(tmp) != f['sha256'].upper():
            os.remove(tmp)
            log('checksum mismatch for %s, kept the old file' % rel)
            continue
        if rel.endswith(('.sh', '.py')):
            os.chmod(tmp, 0o755)
        os.replace(tmp, dest)
        changed += 1
        log('updated ' + rel)
    log('manifest %s: %d file(s) updated' % (m.get('version'), changed))


def start_steam_game():
    url = 'steam://rungameid/' + APPID
    for cmd in (['steam', url], ['xdg-open', url], ['flatpak', 'run', 'com.valvesoftware.Steam', url]):
        if shutil.which(cmd[0]):
            try:
                subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
                return True
            except OSError:
                continue
    return False


def main():
    cfg = {'server': '127.0.0.1', 'port': 1338, 'manifestUrl': ''}
    try:
        with open(CFG) as f:
            cfg.update(json.load(f))
        cfg['port'] = int(cfg['port'])
    except (OSError, ValueError) as e:
        log('config.json unreadable: %s' % e)
    if game_running():
        log('game already running: not updating, not starting a second copy')
        return 0
    try:
        update(cfg)
    except Exception as e:  # never block the game on an update problem
        log('update check failed (starting anyway): %s' % e)
    # Older installs kept a modified game file here; the patcher replaces it.
    old = os.path.join(DIR, 'Assembly-CSharp.dll')
    if os.path.exists(old):
        try:
            os.remove(old)
            log('removed the old online game file (the patcher builds it now)')
        except OSError:
            pass
    ip = resolve(cfg['server'])
    if not ip:
        log("can't resolve " + cfg['server'])
        tell("Can't find the server (%s). Check your internet connection and try again." % cfg['server'])
        return 1
    if not answers(ip, cfg['port']):
        log("the server %s (%s:%d) isn't answering" % (cfg['server'], ip, cfg['port']))
        tell("The server (%s) isn't answering right now. Stick Fight will start and keep trying to connect." % cfg['server'])
    with open(FLAG, 'w') as f:
        f.write('%s %d\n' % (ip, cfg['port']))
    log('starting Stick Fight through Steam for %s:%d' % (ip, cfg['port']))
    if not start_steam_game():
        os.remove(FLAG)
        tell("Couldn't start Steam. Start Stick Fight Online again once Steam is running.")
        return 1
    # sf-wrap.sh deletes the note when the game starts. Still there after a
    # minute: the launch option isn't set (or Steam never started the game).
    for _ in range(60):
        time.sleep(1)
        if not os.path.exists(FLAG):
            return 0
    try:
        os.remove(FLAG)
    except OSError:
        pass
    log("the game didn't start through sf-wrap.sh: launch option missing?")
    tell('Stick Fight did not start for the server. In Steam: Stick Fight > Properties > '
         'Launch Options must be:\n"%s" %%command%%\n(run install-linux.sh again to set it)' % os.path.join(DIR, 'sf-wrap.sh'))
    return 1


if __name__ == '__main__':
    sys.exit(main())
