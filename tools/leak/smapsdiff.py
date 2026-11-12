#!/usr/bin/env python3
# smapsdiff.py OLD NEW: which mappings of a process grew (Rss), largest first.
import re, sys
def load(p):
    regs = {}; cur = None
    for line in open(p):
        m = re.match(r"^([0-9a-f]+)-([0-9a-f]+) (\S+) \S+ \S+ \S+\s*(.*)$", line)
        if m:
            cur = (m.group(1), m.group(2)); regs[cur] = {"perm": m.group(3), "name": m.group(4).strip() or "[anon]", "size": (int(m.group(2),16)-int(m.group(1),16))//1024, "rss": 0}
        elif line.startswith("Rss:") and cur: regs[cur]["rss"] = int(line.split()[1])
    return regs
a, b = load(sys.argv[1]), load(sys.argv[2])
rows = []
for k, r in b.items():
    old = a.get(k, {}).get("rss", 0) if k in a else None
    d = r["rss"] - (old or 0)
    if d: rows.append((d, k, r, old is None))
gone = sum(r["rss"] for k, r in a.items() if k not in b)
rows.sort(reverse=True)
tot = sum(d for d, *_ in rows) - gone
print("total Rss change %+d kB (%d mappings changed, %d new, %d gone = %d kB)" % (tot, len(rows), sum(1 for x in rows if x[3]), sum(1 for k in a if k not in b), gone))
for d, k, r, new in rows[:12]:
    print("  %+7d kB  %s-%s %s size=%d kB rss=%d %s%s" % (d, k[0], k[1], r["perm"], r["size"], r["rss"], r["name"][-50:], "  (new)" if new else ""))
