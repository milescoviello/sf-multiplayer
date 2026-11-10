#!/bin/bash
# One smoothness run: fresh CASUAL lobby, two bots (scripted AI and observation
# frames off, so they hold ~60 fps), bot 1 driven in a fixed pattern for 40 s;
# then compare bot 1's own track with bot 2's copy (trace_an.py).
MODE="$1"   # stock | interp
L=/tmp/sf-oracle-plugin-11342.log
~/sf-test/bots.sh stop >/dev/null 2>&1
sudo systemctl restart sf-lobby@CASUAL; sleep 3
until grep -q "receive thread started" $L 2>/dev/null; do sleep 2; done
if [ "$MODE" = stock ]; then export SF_REMOTE_INTERP=0; else unset SF_REMOTE_INTERP; fi
export SF_SMOOTH_TRACE=1 SF_BOT_AI=0 SFGYM_NO_FRAMES=1; ~/sf-test/bots.sh start 2 CASUAL >/dev/null
for t in $(seq 1 60); do [ $(grep -c ClientRequestingToSpawn $L) -ge 2 ] && break; sleep 3; done
sleep 5; python3 ~/sf-test/nso.py start >/dev/null
for t in $(seq 1 40); do grep -q "Broadcast StartMatch" $L && break; sleep 2; done
sleep 12
S=$(grep -h "Discovered localSlot" ~/sf-test/c1/BepInEx/LogOutput.log | tail -1 | grep -oE "localSlot=[0-9]" | cut -d= -f2)
python3 ~/sf-test/drive.py 40 $S
echo "== $MODE (bot 1 = slot $S)"

sleep 2
python3 ~/sf-test/trace_an.py ~/sf-test/c1/BepInEx/smooth-trace.csv ~/sf-test/c2/BepInEx/smooth-trace.csv $S
for c in c1 c2; do echo "fps $c: $(grep -h "\[perf\]" ~/sf-test/$c/BepInEx/LogOutput.log | tail -5 | grep -oE "\] [0-9]+ fps" | tr -d "]" | tr "\n" " ")"; done
