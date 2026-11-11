#!/usr/bin/env python3
# Walk bot 1's player toward slot 1 and punch until c1 logs one of our kills, or DUR s pass.
import json, re, socket, sys, time
DUR = float(sys.argv[1]) if len(sys.argv) > 1 else 45
LOG = "/home/miles/sf-test/c1/BepInEx/LogOutput.log"
def kills():
    return open(LOG, errors="replace").read().count("[kill] our kill")
def pos():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(2)
    s.sendto(json.dumps({"cmd": "rigdiag"}).encode(), ("127.0.0.1", 11342))
    info = s.recv(8000).decode()
    p = {int(a): (float(b), float(c)) for a, b, c in re.findall(r"slot (\d+): .*?reported=\(([-\d.]+),([-\d.]+)\)", info)}
    return p
g = socket.create_connection(("127.0.0.1", 5556), timeout=3)
g.sendall((json.dumps({"claim": [0]}) + "\n").encode())
def send(mx, at, ax, ay=0.0, jp=0):
    g.sendall((json.dumps({"id": 0, "mx": mx, "jp": jp, "at": at, "bl": 0, "th": 0, "ax": ax, "ay": ay}) + "\n").encode())
k0 = kills(); t0 = time.time(); sign = None; last = None; n = 0
while time.time() - t0 < DUR and kills() == k0:
    p = pos()
    if 0 not in p or 1 not in p: time.sleep(0.2); continue
    (y0, z0), (y1, z1) = p[0], p[1]
    dz, dy = z1 - z0, y1 - y0
    if sign is None:
        # learn which way +mx moves us
        for _ in range(12): send(1, 0, 1); time.sleep(1/30)
        p2 = pos(); sign = 1 if p2[0][1] > z0 else -1
        print("+mx moves z", "+" if sign > 0 else "-", flush=True); continue
    d = (1 if dz > 0 else -1) * sign
    near = abs(dz) < 1.6
    for _ in range(6):
        n += 1
        send(0 if near else d, 1 if (near and n % 6 < 3) else 0, d, max(-1.0, min(1.0, dy / 2)), 1 if dy > 1.5 and n % 30 == 0 else 0)
        time.sleep(1/30)
    if time.time() - (last or 0) > 3: last = time.time(); print(f"t={time.time()-t0:.0f}s dz={dz:.1f} dy={dy:.1f}", flush=True)
g.sendall((json.dumps({"release": [0]}) + "\n").encode())
print("kill landed" if kills() > k0 else "no kill", flush=True)
