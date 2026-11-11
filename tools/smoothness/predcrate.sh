#!/bin/bash
# Predicted crates on Desert8 (scene 12, a crate-built map): does the level hold
# together on the clients? Two docile bots; 4 s and 8 s after the round starts
# (bots die there quickly, which ends the round), both list their crates: how
# many fell into the void (y < -30; Desert8's bottom row sits near -14) and how
# far each is from the server's pose (err). MODE: predicted | stock
MODE="$1"
L=/tmp/sf-oracle-plugin-11342.log
bridge() { python3 -c "import socket,sys; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.settimeout(2); s.sendto(sys.argv[1].encode(),('127.0.0.1',11342)); print(s.recv(512).decode())" "$1"; }
~/sf-test/bots.sh stop >/dev/null 2>&1
# Never restart CASUAL under a real player: after the bots stop, their router
# flows expire (45 s idle); anything left is someone else.
for i in $(seq 1 30); do n=$(curl -s 127.0.0.1:8081/router/stats | python3 -c 'import json,sys; print(json.load(sys.stdin).get("byCode",{}).get("CASUAL",0))' 2>/dev/null); [ "$n" = 0 ] && break; sleep 3; done
[ "$n" = 0 ] || { echo "someone else is in CASUAL ($n router flows): not restarting it"; exit 1; }
sudo systemctl restart sf-lobby@CASUAL; sleep 3
until grep -q "receive thread started" $L 2>/dev/null; do sleep 2; done; sleep 5
if [ "$MODE" = predicted ]; then export SF_CRATES_PREDICTED=1; else unset SF_CRATES_PREDICTED; fi
export SF_BOT_AI=0 SFGYM_NO_FRAMES=1
~/sf-test/bots.sh start 2 CASUAL >/dev/null
for t in $(seq 1 60); do [ $(grep -c ClientRequestingToSpawn $L) -ge 2 ] && break; sleep 3; done
bridge '{"cmd":"start"}' >/dev/null; sleep 8
n0=$(grep -c "Broadcast StartMatch" $L)
bridge '{"cmd":"mapNow","scene":12}' >/dev/null
# the round starts once both bots have loaded Desert8
for t in $(seq 1 60); do [ $(grep -c "Broadcast StartMatch" $L) -gt $n0 ] && break; sleep 1; done
grep -m1 "MapChange → scene 12" $L | cut -c20-100
dump() {   # $1 = label: both bots list their crates at the same moment
  for c in 1401 1402; do echo boxes > /tmp/sf-cmd-$c.txt; done; sleep 4
  grep -q "Round advance" $L && echo "($1: the round already ended, this may be the next map)"
  for c in 1401 1402; do
    awk '/\[dbg\] BOXES/{b=""; on=1; next} on && /^ +#/{b=b $0 "\n"; next} on && /\[dbg\] total/{last=b; on=0} END{printf "%s", last}' /tmp/sf-console-$c.log > /tmp/boxes-$c.txt
    python3 - /tmp/boxes-$c.txt "$1 bot $c" <<'PY'
import re,sys
rows=[l for l in open(sys.argv[1]) if l.strip().startswith('#')]
void=0; errs=[]
for l in rows:
    m=re.search(r'p=\((-?[\d.]+),(-?[\d.]+)\).*err=([\d.\-]+)', l)
    if not m: continue
    if float(m.group(1)) < -30: void+=1
    if m.group(3) != '-': errs.append(float(m.group(3)))
errs.sort()
med = errs[len(errs)//2] if errs else -1
print("%s: %d crates, %d in the void, err median %.2f, max %.2f, >0.5: %d" % (sys.argv[2], len(rows), void, med, errs[-1] if errs else -1, sum(1 for e in errs if e > 0.5)))
PY
  done
}
echo "== $MODE"
grep -h "\[nso-push\]" ~/sf-test/c1/BepInEx/LogOutput.log | tail -1 | cut -c1-120
dump "round start +4 s"
dump "round start +8 s"
grep -h "box-probe\] scene 12 +8s" $L | cut -c20-200
