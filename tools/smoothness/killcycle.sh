#!/bin/bash
# On the VM: fresh bots (new DLL) in CASUAL, a Desert8 round, bot 1 hunts bot 2, print kill logs.
for i in 1 2; do cp /tmp/SFClientRecon.dll ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp && mv -f ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll.tmp ~/sf-test/c$i/BepInEx/plugins/SFClientRecon.dll; done
L=/tmp/sf-oracle-plugin-11342.log
n0=$(grep -a -c "\[version\] slot" $L)
SF_BOT_AI=0 ~/sf-test/bots.sh start 2 CASUAL >/dev/null
for t in $(seq 1 60); do [ $(grep -a -c "\[version\] slot" $L) -ge $((n0+2)) ] && break; sleep 3; done
sleep 5
bash ~/sf-test/d8shots.sh; sleep 3
timeout 70 python3 ~/sf-test/killtest.py ${1:-50} | tail -2
sleep 4
for i in 1 2; do echo "== c$i"; grep -a "\[kill\]" ~/sf-test/c$i/BepInEx/LogOutput.log | tail -4 | cut -c1-220; done
grep -a "\[DEATH\]" $L | tail -2
