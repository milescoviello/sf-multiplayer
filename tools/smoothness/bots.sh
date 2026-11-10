#!/usr/bin/env bash
# bots.sh start <count> [lobby] [mode] | one <n> <mode> [lobby] | stop | shot <n> <file>
#   mode: 1 = headless (default), render = hidden but rendered on display :5<n>
set -u
export SF_PROTON=$HOME/sf-oracle/proton/proton SF_STEAM_ROOT=$HOME/sf-oracle/runtime SF_TEST_PREFIX_ROOT=$HOME/sf-test
killone() { local i=$1; pkill -f "sf-test.c$i.StickFight.exe"; sleep 2; pkill -9 -f "sf-test.c$i.StickFight.exe"; WINEPREFIX=$HOME/sf-test/prefix-c$i/pfx $HOME/sf-oracle/proton/files/bin/wineserver -k 2>/dev/null; pkill -f "Xvfb :5$i " 2>/dev/null; true; }
reap() { for p in $(pgrep -x Xvfb); do [ "$(ps -o ppid= -p $p | tr -d " ")" = 1 ] && kill $p; done; true; }
launch() { local i=$1 mode=$2 lobby=$3; rm -f /tmp/sf-console-$((1400+i)).log; SF_HEADLESS=$mode setsid nohup $HOME/sf-test/sf-test-client.sh $HOME/sf-test/c$i $i 127.0.0.1 $lobby ${SF_BOT_AI:-1} > $HOME/sf-test/c$i.out 2>&1 < /dev/null & }
case "${1:-}" in
  stop) for i in 1 2 3 4; do killone $i; done; reap ;;
  start) n=${2:-2}; lobby=${3:-MAIN}; mode=${4:-1}; for i in $(seq 1 $n); do killone $i; launch $i $mode $lobby; done; echo "started $n bot(s) in $lobby (mode $mode)" ;;
  one) killone $2; launch $2 ${3:-1} ${4:-MAIN}; echo "client $2 relaunched (mode ${3:-1}) in ${4:-MAIN}" ;;
  shot) DISPLAY=:5$2 xwd -root -silent | convert xwd:- "$3" && echo "saved $3" ;;
  *) echo "usage: bots.sh start <n> [lobby] [mode] | one <n> <mode> [lobby] | stop | shot <n> <file>" ;;
esac
