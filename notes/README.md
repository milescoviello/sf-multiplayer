# notes/

Design notes, investigations and history. The code lives in the plugin folders; these are the *why*.

**Start with the current docs:** [`../README.md`](../README.md) (what it is, how to play, repo map), [`../NEXT_STEPS.md`](../NEXT_STEPS.md) (current state and open work) and [`../WHATS_NEW.md`](../WHATS_NEW.md) (change log, newest first).

## Current

| Doc | What it covers |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | How the system fits together. The first section is current; the rest is the July 2026 write-up. |
| [`PROTOCOL.md`](PROTOCOL.md) | Wire format: every message type, the extensions, the router's control datagrams. |
| [`ROUTER.md`](ROUTER.md) | The single-port router: routing, binding, running it. |
| [`DEPLOY.md`](DEPLOY.md) | Setting up and updating a server. |

## Still-valid reference (about stock Stick Fight)

| Doc | What it covers |
|---|---|
| [`OBJECT_SYNC.md`](OBJECT_SYNC.md) | How stock Stick Fight syncs crates, platforms and destructibles. |
| [`SF_VANILLA_INSPECTION.md`](SF_VANILLA_INSPECTION.md) | Running two stock instances with Unity Explorer to see what the real game does. |
| [`bug-investigations/`](bug-investigations/) | Root-cause write-ups with evidence. The stock-game findings hold; the "our code" parts describe the code of their date. |

## History

Accurate when written, superseded since. Each one says its date at the top.

- **Guides for the old host:** [`VPS.md`](VPS.md), [`LIVE_TEST_CHECKLIST.md`](LIVE_TEST_CHECKLIST.md), [`MULTI_LOBBY_LIVE.md`](MULTI_LOBBY_LIVE.md), [`ROUTER_LIVE_TEST.md`](ROUTER_LIVE_TEST.md).
- **Reviews and audits:** [`REVIEW_2026-06-10.md`](REVIEW_2026-06-10.md), [`AUDIT_2026-05-23.md`](AUDIT_2026-05-23.md), [`SESSION_2026-05-23.md`](SESSION_2026-05-23.md), [`ORACLE_v0.3.4_STATUS.md`](ORACLE_v0.3.4_STATUS.md), [`SIMPLIFICATION.md`](SIMPLIFICATION.md).
- **Bugs and crashes:** [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md), [`NEXT_FIXES.md`](NEXT_FIXES.md), [`CRASH_INVESTIGATION.md`](CRASH_INVESTIGATION.md).
- **Designs:** [`REPLAY.md`](REPLAY.md) and [`REPLAY_PLAN.md`](REPLAY_PLAN.md) (an early replay design; the replays that shipped work differently, see `sf-headless-host/Plugin.Replay.cs`), [`SERVER_VS_ASSEMBLY_MAP_LOAD.md`](SERVER_VS_ASSEMBLY_MAP_LOAD.md) (in Spanish).
- **Phase 6 build-up (May 2026):** [`phase6/`](phase6/), starting at [`phase6/00-PHASE6-OVERVIEW.md`](phase6/00-PHASE6-OVERVIEW.md).
- **Before the current design:** [`recon/`](recon/) (reverse-engineering notes from the Go-server era), [`design/`](design/) (fix designs from then), [`SUMMARY.md`](SUMMARY.md).
- **Superseded status files:** [`archive/`](archive/), including the June 2026 bug-hunt loop in [`archive/bugs-loop-2026-06/`](archive/bugs-loop-2026-06/).
