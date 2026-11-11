#!/bin/bash
# On the VM: leftovers across map changes. Bots in CASUAL; Halloween10 + 3 dropped
# weapons; Halloween10 again; Desert8. After each change: dup-key errors on the
# server, and gimmick/weapon counts on the server and both bots.
L=/tmp/sf-oracle-plugin-11342.log
bridge() { python3 -c "import socket,sys; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.settimeout(3); s.sendto(sys.argv[1].encode(),('127.0.0.1',11342)); print(s.recv(65000).decode())" "$1"; }
counts() {
  for c in 1401 1402; do echo mapobjs > /tmp/sf-cmd-$c.txt; done
  bridge '{"cmd":"mapobjs"}' | python3 -c "import json,sys; print(json.load(sys.stdin)['info'].replace(' | ','\n'))" > /tmp/mo-srv.txt
  sleep 3
  for c in 1401 1402; do awk '/\[dbg\] MAPOBJS/{b=""; on=1; next} on && /\[dbg\] end mapobjs/{last=b; on=0; next} on{b=b $0 "\n"} END{printf "%s", last}' /tmp/sf-console-$c.log > /tmp/mo-$c.txt; done
  for f in srv 1401 1402; do printf "   %-5s gimmicks %3d  weapons %2d\n" $f $(grep -c "^[A-VX-Z] " /tmp/mo-$f.txt) $(grep -c "^W " /tmp/mo-$f.txt); done
}
mapto() {
  n1=$(grep -a -c "Broadcast StartMatch" $L); d0=$(grep -a -c "same key already exists" $L)
  bridge "{\"cmd\":\"mapNow\",\"scene\":$1}" >/dev/null
  for t in $(seq 1 60); do [ $(grep -a -c "Broadcast StartMatch" $L) -gt $n1 ] && break; sleep 1; done
  sleep 3
  echo "== scene $1 ($2): same-key errors on the server during the change: $(( $(grep -a -c "same key already exists" $L) - d0 ))"
}
until grep -a -q "receive thread started" $L 2>/dev/null; do sleep 2; done
for i in 1 2; do cp /tmp/SFClientRecon.dll ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp && mv -f ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll; done
n0=$(grep -a -c "\[version\] slot" $L)
SF_BOT_AI=0 SFGYM_NO_FRAMES=1 ~/sf-test/bots.sh start 2 CASUAL >/dev/null
for t in $(seq 1 60); do [ $(grep -a -c "\[version\] slot" $L) -ge $((n0+2)) ] && break; sleep 3; done
sleep 6; bridge '{"cmd":"start"}' >/dev/null; sleep 10
mapto 99 "first load"
for k in 1 2 3; do bridge '{"cmd":"dropWeapon","slot":0,"weapon":0}' >/dev/null; done; sleep 4
counts
mapto 99 "same map again"
counts
mapto 12 "Desert8"
counts
grep -a "\[map-reset\]" $L | tail -1 | cut -c1-160
