# QA Status — the regression gate

Verdicts for every acceptance criterion, and the caveats that outlive them.

**This document defines what C43 re-verifies.** Phase 3.5 rebuilds the client
in Unity, and its phase gate is "C1–C25 re-run against the new client and all
pass" — which is only meaningful if what each criterion asserts, and the value
it passed at, is written down. That is what this file is for. Evidence dumps,
fixed-bug postmortems and adjudication history were removed 2026-08-26; they
are in git history if ever needed.

## Phase 1 — C1–C10 (verified 2026-08-20)

| # | Asserts | Bar | Measured |
|---|---|---|---|
| C1 | `make up` from clean, healthz + page + WS reachable | reachable | PASS |
| C2 | Two clients see each other, on terrain | ≤ 1 s; ≤ 0.05 m off surface | 4.5–5.2 ms; 2.79e-9 m |
| C3 | Server authority corrects forced state | ≤ 1 tick | 0.000 m at 49.55 ms, tick delta 1 |
| C4 | Connection-loss despawn | ≤ 10 s | hard-kill 1002.5 ms; silent-timeout 10000.99 ms |
| C5 | Go↔TS movement conformance, tick-aligned | ≤ 5% | maxDPos 3.93e-13 m, 0 grounded mismatches |
| C6 | Prediction quality at 100 ms RTT | p95 < 0.25 m | p95 7.61e-06 m, 0 snap-backs |
| C7 | Sustained tick + latency + client fps | 20 Hz, p95 < 50 ms, 60 fps ×10 | 20.0005–20.0007 Hz, 0 gaps/dupes; p95 3.60 ms; 59.96 fps |
| C9 | Terrain walkable, varied, navigable | field audit | PASS (`c80269c44a757a8f`) |
| C10 | Circumnavigation, closed loop, all 6 faces | ≥ 942 m, endpoint < 1 m, grounded | 1065.47 m; sim 0.0095 m, live 0.1501 m; 99.4% of ticks within 0.05 m |

There is no C8 — the number was never issued.

C7's p95 is the post-NodePort figure. The original 6.65/17.51 ms carried
`kubectl port-forward`'s userspace-proxy jitter; replacing it with kind
`extraPortMappings` also took C4's despawn from 1003 ms to 1.2 ms.

## Phase 2 — C11–C18: verdicts were never recorded

`docs/tasks/phase2-wave3.md` briefed a Phase 2 regression section for this
file and it was never written. The criteria themselves are defined in
`docs/ROADMAP.md`, and Phase 2 was exercised end to end by `t14` (buy and
shoot) and `t15` (persistence) — but no per-criterion verdict table exists.

**This is a hole in the C43 gate** and should be filled before Phase 3.5
starts, by re-running the Phase 2 harnesses against the TS client while it is
still alive and recording what they assert.

## Phase 3 — C19–C25 (verified 2026-08-25)

| # | Asserts | Measured |
|---|---|---|
| C19a | NPC notices a player inside aggro radius and pursues | player closed to 8.5 m; NPC moved 4.8 m off post |
| C19b | NPC leashes back to its post, stays on the surface | returned to 1.03 m of post (limit 2.5), \|pos\|=157.2 m |
| C20/21 | Camp NPCs damage a player who stands in range | 9 hit events, health 100 → 0, at 8.5 m |
| C21b | Gunner projectiles are entities the client receives | PASS |
| C22 | Player dies and respawns at spawn with full health | 5.0 s (GDD `respawn_delay`), 0.00 m from spawn |
| C23 | Loot: table roll, single grant | Go `TestLootDropReachesWorld`, `loot_test.go` |
| C24 | Two clients see the same NPC positions | worst disagreement 0.000 m across 5 NPCs |
| C25a | Per-client snapshot bandwidth | 13.9 KB/s of 100 KB/s, 21 entities |
| C25b | Server holds 20 Hz with the camp live | 20.00 Hz |

## Harnesses, for the C43 re-run

All under `test/`. These speak the wire protocol directly and carry their own
independent codec, so they survive the Unity rebuild untouched — they are the
headless client that makes the port safe.

- **Libs:** `lib/ws.mjs` (raw-TCP RFC-6455 client, incl. `kill`, ns timestamps),
  `lib/wire.mjs` (independent codec, written from `PROTOCOL.md` only),
  `lib/field.mjs` (independent GDD terrain sampling).
- **Per-criterion:** `t2`–`t4` (visibility, authority, despawn), `t5/` (Go↔TS
  conformance route + diff), `t6` (prediction), `t7` (sustain), `t9-terrain.py`,
  `t10/` (circumnavigation), `t12`/`t13` (codec + collider parity), `t14` (buy
  and shoot), `t15` (persistence), `t16` (camp fight), `t17` (Phase 3 QA).
- **Captured world:** `test/out/world-seed1337.json`, sha256_16
  `c80269c44a757a8f` — terrain determinism across restarts is proven against it.
- **C5 caveat:** the Go dump uses the f64 field, the TS dump the u16 wire field
  (~15 µm quantization, negligible under the 5% bar). The C# port inherits this.

## Notes that matter more than the verdicts

**C25 passes with interest culling NOT wired.** `server/internal/server/interest.go`
exists and is tested, but is deliberately not in `encodeSnapshot`: wiring it
turns one shared snapshot body per tick into per-client bodies, which is a real
performance decision. At 13.9 KB/s against a 100 KB/s budget the filter is not
needed yet. This is evidence the budget holds *without* it, not evidence it
works.

**C24's 0.000 m is weaker than it looks.** Both clients decode the same
snapshot bytes, so agreement on raw positions is near-tautological. It proves
consistent delivery, not that interpolation agrees. A stronger check would
sample both clients' *rendered* positions mid-motion.

**C19b was a spec bug, found by this pass.** The GDD state table only left
AGGRO when distance-from-post exceeded `leash_radius`. An NPC that chased a
short way and then lost the player was inside its leash radius (so never
disengaged) and had no target (so never moved) — measured standing motionless
17.9 m from its post for 60 s. Added `AGGRO/ATTACK → LEASH on target lost`
with a 2 s grace period, to both the GDD and `brain.go`.

## Three harness defects that produced false failures

Recorded because each looked exactly like a product bug:

1. **No heartbeat.** The harness sent no pings, and the server drops
   connections silent for 10 s. A 60 s wait killed the socket, and every later
   check read stale entity data — reporting 0 hit events, a 0.0 s respawn and
   0.00 Hz. Three plausible product failures, one dead socket.
2. **Unverified arrival.** The first structure walked in, retreated, and walked
   in *again*; the second approach stalled 74.5 m short. C20/21 then measured a
   player standing outside aggro range, and C22 had no death to observe.
   Restructured so death itself returns the player to spawn — one traversal,
   and each check runs from a state the previous one established.
3. **Sampling a flag at one instant.** `died` was read before the death
   arrived, reporting `died=false` for a player sitting at the spawn point with
   full health. Deaths are latched from the event stream now.

A fourth was pure positioning: stopping 17.5 m from a grunt leaves the gunners
~31 m away, outside their 30 m aggro, so no projectile was ever provoked. The
approach closes to 9 m.
