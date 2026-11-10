#!/usr/bin/env python3
# Drive bot 1's player A in a fixed pattern for DUR s over the gym control
# port (run right 1.2 s, left 1.2 s, a jump every 2.4 s). No observation
# stream: positions come from SF_SMOOTH_TRACE.
import json, socket, sys, time
DUR, A = float(sys.argv[1]), int(sys.argv[2])
s = socket.create_connection(("127.0.0.1", 5556), timeout=3)
s.sendall((json.dumps({"claim": [A]}) + "\n").encode())
t0 = time.time()
while time.time() - t0 < DUR:
    t = time.time() - t0
    mx = 1 if (t % 2.4) < 1.2 else -1
    jp = 1 if (t % 2.4) < 0.15 else 0
    s.sendall((json.dumps({"id": A, "mx": mx, "jp": jp, "at": 0, "bl": 0, "th": 0, "ax": mx, "ay": 0}) + "\n").encode())
    time.sleep(1 / 30)
s.sendall((json.dumps({"release": [A]}) + "\n").encode())
s.close()
