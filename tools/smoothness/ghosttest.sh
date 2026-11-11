#!/bin/bash
# On the VM: two docile bots in CASUAL on scene $1; every 4 s the server and both
# bots dump their map gimmicks at once; prints GhostPlatform/MoveAlongPath state
# side by side (server | bot1 | bot2) and a mismatch count.
SCENE=${1:-99}; ROUNDS=${2:-8}
L=/tmp/sf-oracle-plugin-11342.log
bridge() { python3 -c "import socket,sys; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.settimeout(3); s.sendto(sys.argv[1].encode(),('127.0.0.1',11342)); print(s.recv(65000).decode())" "$1"; }
until grep -a -q "receive thread started" $L 2>/dev/null; do sleep 2; done
for i in 1 2; do cp /tmp/SFClientRecon.dll ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp && mv -f ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll; done
n0=$(grep -a -c "\[version\] slot" $L)
SF_BOT_AI=0 SFGYM_NO_FRAMES=1 ~/sf-test/bots.sh start 2 CASUAL >/dev/null
for t in $(seq 1 60); do [ $(grep -a -c "\[version\] slot" $L) -ge $((n0+2)) ] && break; sleep 3; done
sleep 6
bridge '{"cmd":"start"}' >/dev/null; sleep 10
n1=$(grep -a -c "Broadcast StartMatch" $L)
bridge "{\"cmd\":\"mapNow\",\"scene\":$SCENE}" >/dev/null
for t in $(seq 1 60); do [ $(grep -a -c "Broadcast StartMatch" $L) -gt $n1 ] && break; sleep 1; done
echo "round started on scene $SCENE"
for r in $(seq 1 $ROUNDS); do
  for c in 1401 1402; do echo mapobjs > /tmp/sf-cmd-$c.txt; done
  bridge '{"cmd":"mapobjs"}' | python3 -c "import json,sys; print(json.load(sys.stdin)['info'].replace(' | ','\n'))" > /tmp/mo-srv.txt
  sleep 2
  for c in 1401 1402; do awk '/\[dbg\] MAPOBJS/{b=""; on=1; next} on && /\[dbg\] end mapobjs/{last=b; on=0; next} on{b=b $0 "\n"} END{printf "%s", last}' /tmp/sf-console-$c.log > /tmp/mo-$c.txt; done
  python3 - $r <<'PY'
import re,sys
def load(p):
    d={}
    for l in open(p):
        l=l.strip()
        m=re.match(r'([GM]) (\w+) \(([-\d.]+),([-\d.]+)\) p=\(([-\d.]+),([-\d.]+)\) d=(\S*)(.*)',l)
        if m: d[(m.group(1),m.group(3),m.group(4))]=(float(m.group(5)),float(m.group(6)),m.group(7),m.group(8).strip())
    return d
s,a,b=load('/tmp/mo-srv.txt'),load('/tmp/mo-1401.txt'),load('/tmp/mo-1402.txt')
mis=0; n=0; perr=[]
for k,v in sorted(s.items()):
    n+=1
    for o in (a,b):
        w=o.get(k)
        if not w: mis+=1; continue
        if w[2]!=v[2] or w[3]!=v[3]: mis+=1
        perr.append(((w[0]-v[0])**2+(w[1]-v[1])**2)**.5)
perr.sort()
print("dump %s: %d gimmicks on the server (%s), state mismatches vs bots: %d, pos err median %.2f max %.2f" % (sys.argv[1], n, ",".join(sorted(set(k[0] for k in s))), mis, perr[len(perr)//2] if perr else -1, perr[-1] if perr else -1))
for k,v in [kv for kv in sorted(s.items()) if any((o.get(kv[0]) or (0,0,"",""))[2:]!=kv[1][2:] for o in (a,b))]:
    print("   %s %s,%s  srv d=%s %s | b1 %s | b2 %s" % (k[0],k[1],k[2],v[2],v[3], (a.get(k) or ('','','-',''))[2:], (b.get(k) or ('','','-',''))[2:]))
PY
  grep -a -q "Round advance" <(tail -n 300 $L) && { echo "(round ended)"; break; }
  sleep 2
done
