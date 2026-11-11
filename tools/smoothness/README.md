# Remote-player smoothness bench

Runs on the server VM with the test bots in `~/sf-test/c1..c3` (Proton game
copies with SFClientRecon + StickFightGym), installed next to these scripts.

    bash abrun.sh interp     # this mod's remote smoothing
    bash abrun.sh stock      # SF_REMOTE_INTERP=0

`abrun.sh` restarts the CASUAL lobby, starts two bots (`SF_BOT_AI=0
SFGYM_NO_FRAMES=1`: no scripted AI, no observation frames, or the bots drop to a
few fps), starts a match (`nso.py start`, loopback bridge), drives bot 1 for
40 s (`drive.py`, gym control port) and runs `trace_an.py` on both bots'
`SF_SMOOTH_TRACE` files: per physics step, bot 1's own body position vs bot 2's
copy of it.

- **accel** (jitter): mean change of the per-step movement, copy / original.
- **shake**: RMS distance from a ±4-step moving average, copy / original.
- **lag / err**: best time shift of the copy onto the original, and the mean
  distance left after it.

Internet-like paths: netem on `lo` for port 1338 only, without reordering
(`rate` keeps order; plain jitter reorders packets, which real paths rarely do):

    sudo tc qdisc add dev lo root handle 1: prio bands 4
    sudo tc qdisc add dev lo parent 1:4 handle 40: netem delay 40ms 10ms distribution normal rate 1gbit
    sudo tc filter add dev lo parent 1:0 protocol ip prio 1 u32 match ip dport 1338 0xffff flowid 1:4
    sudo tc filter add dev lo parent 1:0 protocol ip prio 1 u32 match ip sport 1338 0xffff flowid 1:4
    sudo tc qdisc del dev lo root     # when done

Send times (client 0.7.8): `SF_REMOTE_STAMPS=0` makes the receiving bot ignore
them, `SF_REMOTE_STAMP_GAIN` (0-1.5, default 1) scales how much of an update's
lateness is projected. Results at 40±10 ms, 3 pairs: error 0.52 -> 0.28 u,
accel 1.49 -> 1.34, shake 1.05 -> 1.2, lag the same; gain 0.5 kept the shake
and lost half the error gain.

Both scripts wait until the bots' router flows are gone and refuse to restart
CASUAL if anyone else is in it.

`predcrate.sh stock|predicted`: Desert8 (crate-built), two bots, bot 1 driven
for 30 s, then each bot lists its crates: how many fell out of the world and
how far each is from the server's pose (`SF_CRATES_PREDICTED=1` = predicted).

A real client can be compared too: create `BepInEx/smooth-trace.on` in its
game folder, and run `trace_an.py <bot1 trace> <its smooth-trace.csv> <slot> [seconds]`.
