#!/bin/bash
# On the VM: do clients follow the server's weapons? Bots in CASUAL on scene $1;
# 3 weapons dropped; positions (server vs bots) after they land, then after the
# server shoves its copies sideways.
SCENE=${1:-6}
L=/tmp/sf-oracle-plugin-11342.log
bridge() { python3 -c "import socket,sys; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.settimeout(3); s.sendto(sys.argv[1].encode(),('127.0.0.1',11342)); print(s.recv(65000).decode())" "$1"; }
dump() {
  for c in 1401 1402; do echo mapobjs > /tmp/sf-cmd-$c.txt; done
  bridge '{"cmd":"mapobjs"}' | python3 -c "import json,sys; print(json.load(sys.stdin)['info'].replace(' | ','\n'))" > /tmp/mo-srv.txt
  sleep 2
  for c in 1401 1402; do awk '/\[dbg\] MAPOBJS/{b=""; on=1; next} on && /\[dbg\] end mapobjs/{last=b; on=0; next} on{b=b $0 "\n"} END{printf "%s", last}' /tmp/sf-console-$c.log > /tmp/mo-$c.txt; done
  python3 - "$1" <<'PY'
import re,sys
def load(p):
    return {m.group(1):(float(m.group(2)),float(m.group(3))) for m in (re.match(r'W #(\d+) p=\(([-\d.]+),([-\d.]+)\)',l) for l in open(p)) if m}
s,a,b=load('/tmp/mo-srv.txt'),load('/tmp/mo-1401.txt'),load('/tmp/mo-1402.txt')
print("%s: server has %d weapons, bots %d / %d" % (sys.argv[1], len(s), len(a), len(b)))
for k in sorted(set(s)|set(a)|set(b), key=int):
    f=lambda d: ("(%6.2f,%6.2f)" % d[k]) if k in d else "      -       "
    err=lambda d: ("%.2f" % ((d[k][0]-s[k][0])**2+(d[k][1]-s[k][1])**2)**.5) if k in d and k in s else "-"
    print("   #%-5s srv %s  b1 %s err %s  b2 %s err %s" % (k, f(s), f(a), err(a), f(b), err(b)))
PY
}
until grep -a -q "receive thread started" $L 2>/dev/null; do sleep 2; done
for i in 1 2; do cp /tmp/SFClientRecon.dll ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp && mv -f ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll; done
n0=$(grep -a -c "\[version\] slot" $L)
SF_BOT_AI=0 SFGYM_NO_FRAMES=1 ~/sf-test/bots.sh start 2 CASUAL >/dev/null
for t in $(seq 1 60); do [ $(grep -a -c "\[version\] slot" $L) -ge $((n0+2)) ] && break; sleep 3; done
sleep 6; bridge '{"cmd":"start"}' >/dev/null; sleep 10
n1=$(grep -a -c "Broadcast StartMatch" $L)
bridge "{\"cmd\":\"mapNow\",\"scene\":$SCENE}" >/dev/null
for t in $(seq 1 60); do [ $(grep -a -c "Broadcast StartMatch" $L) -gt $n1 ] && break; sleep 1; done
sleep 2
sleep 8   # natural sky drops
sleep 5; dump "landed"
bridge '{"cmd":"pushWeapons","fz":1.5}'
sleep 7; dump "7 s after the server shoved its copies"
grep -a "\[weapons\] server copy" $L | tail -1 | cut -c1-140
