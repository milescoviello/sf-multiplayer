#!/usr/bin/env python3
# blkcount.py PID: count 32-byte in-use Wine heap blocks (header 04 00 08 00 .. .. 75)
# across the process's anonymous rw memory, and "eth0" UTF-16 strings.
import sys, re
pid = sys.argv[1]
n = e = tot = 0
mem = open(f"/proc/{pid}/mem", "rb")
pat = re.compile(rb"\x04\x00\x08\x00..\x75", re.S)
eth = "eth0".encode("utf-16le")
for line in open(f"/proc/{pid}/maps"):
    p = line.split()
    if len(p) != 5 or not p[1].startswith("rw"):
        continue
    a, b = (int(x, 16) for x in p[0].split("-"))
    try:
        mem.seek(a); d = mem.read(b - a)
    except Exception:
        continue
    tot += len(d)
    n += len(pat.findall(d))
    e += d.count(eth)
print(n, e, tot >> 20)
