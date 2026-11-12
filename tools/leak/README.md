# Memory leak tools (server VM)

- `smapsdiff.py OLD NEW`: diff two copies of `/proc/PID/smaps` of a lobby and list
  the mappings whose resident size changed, largest first. Found that the growth was
  in Wine's process heap, not the managed heap or Unity objects.
- `blkcount.py PID`: count 32-byte in-use Wine heap blocks (the XInput leak's
  allocations) in a process's memory; take two counts a few minutes apart for a rate.
  Before the fix every lobby added 249 a second; after it, 0.

Both read another process's memory (`/proc/PID/mem`), which works on the server VM
(`kernel.yama.ptrace_scope = 0`).
