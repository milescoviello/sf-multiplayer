import socket, sys, json, time
cmd = sys.argv[1]; body = {"cmd": cmd}
if cmd == "nso" and len(sys.argv) > 2: body["id"] = int(sys.argv[2])
if cmd in ("mapNow", "forceNextMap") and len(sys.argv) > 2: body["scene"] = int(sys.argv[2])
if cmd == "fireTest": body["slot"] = int(sys.argv[2]); body["weapon"] = int(sys.argv[3])
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(3)
s.sendto(json.dumps(body).encode(), ("127.0.0.1", int(__import__("os").environ.get("BRIDGE", "11342"))))
end = time.time() + 3
while time.time() < end:
    try: d = s.recv(65000).decode()
    except socket.timeout: break
    if '"reply":"snapshot"' in d: continue
    print(d[:900]); break
