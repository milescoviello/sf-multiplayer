#!/usr/bin/env python3
# Compare bot 1's own track of player A with bot 2's copy of it, from the
# SF_SMOOTH_TRACE files, per physics step.
#   shake  RMS distance of the body from its own ±4-step moving average
#          (high-frequency wobble; physics motion is smooth at that scale)
#   accel  mean |second difference| of position per step
#   lag / err  best time shift of the copy onto the original, and the mean
#          distance left after it
import sys, math
own_f, rem_f, A = sys.argv[1], sys.argv[2], sys.argv[3]
def load(path, local):
    rows = []
    for line in open(path, errors='replace'):
        p = line.strip().split(',')
        if len(p) != 7 or p[1] != 'S' or p[2] != A or p[3] != local: continue
        try: rows.append((int(p[0]) / 1000.0, float(p[4]), float(p[5]), float(p[6])))
        except ValueError: pass
    return rows
def updates(path):
    n = 0
    for line in open(path, errors='replace'):
        p = line.split(',')
        if len(p) == 7 and p[1] == 'U' and p[2] == A: n += 1
    return n
own, rem = load(own_f, '1'), load(rem_f, '0')
WIN = float(sys.argv[4]) if len(sys.argv) > 4 else 40
# only the driven window (the last 40 s of the run)
end = min(own[-1][0], rem[-1][0]) if own and rem else 0
own = [r for r in own if r[0] > end - WIN]; rem = [r for r in rem if r[0] > end - WIN]
def runs(tr):   # split at teleports / gaps
    out, cur = [], []
    for r in tr:
        if cur and (abs(r[3] - cur[-1][3]) > 0.05 or math.hypot(r[1] - cur[-1][1], r[2] - cur[-1][2]) > 1.0):
            out.append(cur); cur = []
        cur.append(r)
    if cur: out.append(cur)
    return [c for c in out if len(c) > 20]
def metrics(tr):
    sh, n, ac, na, sp = 0.0, 0, 0.0, 0, 0.0
    for c in runs(tr):
        for i in range(4, len(c) - 4):
            my = sum(c[j][1] for j in range(i - 4, i + 5)) / 9; mz = sum(c[j][2] for j in range(i - 4, i + 5)) / 9
            sh += (c[i][1] - my) ** 2 + (c[i][2] - mz) ** 2; n += 1
        for i in range(1, len(c) - 1):
            ac += math.hypot(c[i + 1][1] - 2 * c[i][1] + c[i - 1][1], c[i + 1][2] - 2 * c[i][2] + c[i - 1][2]); na += 1
            sp += math.hypot(c[i][1] - c[i - 1][1], c[i][2] - c[i - 1][2])
    return (math.sqrt(sh / n) if n else 0), (ac / na if na else 0), (sp / na if na else 0), na
so, ao, vo, no = metrics(own); sr, ar, vr, nr = metrics(rem)
print(f"own:    shake {so*1000:.1f} mu  accel {ao*1000:.2f} mu/step2  speed {vo*60:.2f} u/s  steps {no}")
print(f"copy:   shake {sr*1000:.1f} mu  accel {ar*1000:.2f} mu/step2  speed {vr*60:.2f} u/s  steps {nr}  updates {updates(rem_f)}")
if so > 0 and ao > 0: print(f"copy/own: shake {sr/so:.2f}  accel {ar/ao:.2f}")
import bisect
ot = [r[0] for r in own]
def own_at(t):
    i = bisect.bisect_right(ot, t)
    if i <= 0 or i >= len(own): return None
    a, b = own[i - 1], own[i]
    if b[0] - a[0] > 0.1: return None
    f = (t - a[0]) / (b[0] - a[0]) if b[0] > a[0] else 0
    return a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f
best = None
for lag in range(0, 500, 5):
    e, n = 0.0, 0
    for r in rem[::3]:
        q = own_at(r[0] - lag / 1000)
        if q is None: continue
        d = math.hypot(r[1] - q[0], r[2] - q[1])
        if d > 5: continue
        e += d; n += 1
    if n > 200 and (best is None or e / n < best[1]): best = (lag, e / n, n)
if best: print(f"lag {best[0]} ms, err {best[1]:.3f} u")
