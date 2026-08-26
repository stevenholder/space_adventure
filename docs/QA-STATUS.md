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
| C9 | Terrain walkable, varied, navigable | field audit | PASS |
| C10 | Circumnavigation, closed loop, all 6 faces | ≥ 942 m, endpoint < 1 m, grounded | 1065.47 m; sim 0.0095 m, live 0.1501 m; 99.4% of ticks within 0.05 m |

There is no C8 — the number was never issued.

C7's p95 is the post-NodePort figure. The original 6.65/17.51 ms carried
`kubectl port-forward`'s userspace-proxy jitter; replacing it with kind
`extraPortMappings` also took C4's despawn from 1003 ms to 1.2 ms.

## Phase 2 — C11–C18 (verified 2026-08-26)

Recorded 2026-08-26. The Phase 2 brief that was supposed to write this section
(`docs/tasks/phase2-wave3.md:79`) was never executed, so a third of the C43
gate had no evidence behind it. Filling it found three defects — two in the
criteria themselves, one in the cluster.

**The stack was stale when this run started.** The running pods predated
`1d2aadc` by 11.5 hours, so they were serving a server without that commit's
`ai/brain.go` fix. Every number below was taken after a `make up` rebuild.

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C11 | Progress survives a reconnect; a fresh token does not inherit it | same token → credits 750, `primary=weapon.pulse`, 2 items; fresh token → 1000, `{}`, 1 item | **PASS** (`t15`) |
| C11b | Store suite passes on SQLite *and* Postgres; migration twice is a no-op | `TestMigrate_Postgres`, `TestOpenPostgres`, `TestPlayerRoundTripPostgres` all ran (verified not skipped) beside their SQLite twins; `Migrate` called twice succeeds | **PASS**, one caveat |
| C12 | Unaffordable and forged purchases refused, inventory unchanged | `insufficient_credits` (status 3); forged opcode (status 1); malformed body (status 2); credits 750 → 749 on a 1-credit probe, so nothing was deducted | **PASS** (`t18`) |
| C13 | Colliders agree Go↔TS; no penetration or tunnelling | 9 scenarios bit-exact, dPos = dVel = 0.00e+0 | **PASS** (`t13`) |
| C14 | 20 shots at a target register; 20 aimed 1 m wide register zero | 20/20 and 0/20 at 40.7 m under 100 ms injected RTT | **PASS** (`t18`), criterion amended — see below |
| C15 | Damage per the GDD table, death, respawn within 3 s ±100 ms | 25 damage/hit live (100 → 75); 5 death/respawn cycles observed live | **PARTIAL** |
| C16 | Remote client sees weapon, pitch within 1°, ordered shot events | pitch worst error 0.35°; 8/8 `shot_fired`, ordered; **weapon: not on the wire** | **FAIL** (`t19`) |
| C18 | Build gates clean; ≤ 54 B/entity; 60 fps; no DB call on the tick path | `go build/vet/test ./...` clean (160 tests, 7 packages), `tsc --noEmit` clean, `npm run build` clean; 54 B/entity exactly, worst case over 2122 snapshots | **PARTIAL** |

C17 is absent from the table on purpose: `t14` completes list → buy → equip at
2.04 m from the shopkeeper and Go's `TestHandleCmdOutOfRange` covers the
refusal, but the look-cone and on-screen prompt clauses are client-side and
were never tested. The prompt clause retires with the TS client.

### C11b caveat — the dev Secret is committed

The mechanism is right: the server takes `DATABASE_URL` only through a
`secretKeyRef`, and no DSN is baked into an image or into the Deployment. But
the Secret itself is in the repo (`deploy/manifests/30-postgres.yaml`) with a
fixed localhost-only kind password, documented in place as dev-only with the
note that a real deployment supplies it out-of-band. The criterion's letter
says "no DSN in a committed manifest"; its intent is met and its letter is not.

### C14 — two deviations, both forced by the world

Neither is a product defect, and both were found by writing the test rather
than by reading the criterion:

- **"From 30 m" is unreachable.** The range is a walled lane: side walls run
  its length and the targets stand 15–25 m in, so 30 m from a target is a
  position *outside* the mouth. A straight-line walker presses against a side
  wall and stops — the first run of `t18` read 19/20 at 40.7 m and looked like
  a lag-compensation failure. Routes here are solved, not assumed, and the
  router works over terrain rather than colliders. The measurement is taken at
  the firing line a walker can actually reach, and the distance is reported.
- **Shots must be spaced ~520 ms, not the 150 ms fire interval.**
  `spread_per_shot` is 0.35° against `spread_decay` 3.0°/s, so firing at the
  minimum interval holds the cone near 0.95° — 0.68 m at this range, wider
  than the 0.45 m target. At base spread (0.6°, 0.43 m) every aimed round
  lands. Firing at the minimum interval measures 18/20; the criterion's
  "all register" quietly assumes aimed single shots.

**And the criterion does not test what it claims.** Its own note says "rewind
is what makes the first number 20 and not 12" — but the target is *static*, so
the rewound position equals the live one and lag compensation is a no-op. C14
as written cannot distinguish a server with rewind from one without.
Phase 3 supplies moving NPCs; re-pointing C14 at one would make it a real
lag-compensation test. Raised for Phase 3.5.

### C16 — one clause has no implementation

A second client **cannot** see another player's equipped weapon, because
nothing carries it. The 54-byte entity row has no weapon field, a player's
`spawn` payload is the name only, and `shot_fired` carries origin, direction
and distance. Server-side, `client.EquippedWeapon` exists solely to notice a
re-equip and reset the magazine (`server/internal/server/server.go:614`); it
is never encoded. The TS client makes no attempt to render one.

The other two clauses hold, and hold well: remote pitch is accurate to 0.35°
in the worst case — which is the quantisation floor, since `pitch_q` is
`asin(up·look)` over ±90° in 255 steps, i.e. 0.709° per step — and all 8 shots
produced exactly one ordered `shot_fired` each.

**Decided 2026-08-26: a new `equipped` event, `event_id 0x0006`** — the
player's entity id plus the item id as UTF-8, broadcast when the primary slot
changes and replayed once per armed player when a client joins. It reuses the
existing event channel rather than widening the 54-byte row, because a value
that changes a few times a session should not cost bytes on every entity on
every tick. **`t19` stays red until it lands** — a criterion that is not met
should not have a green test.

### C15 and C18 — what is not measured

- C15's 3.0 s ±100 ms respawn boundary is covered by Go's
  `TestTargetRespawnExactBoundary`, not live. Live evidence is 5 death and
  respawn cycles in `t18`, each inside a 3.3 s wait.
- C18's "60 fps with 10 players, 1 NPC and 8 targets" is a browser measurement
  that retires with the TS client; it should be restated against the Unity
  build in Phase 3.5. The "no database call on the tick path" clause — tick
  duration unchanged with 200 ms of injected Postgres latency — has never been
  run.

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
  and shoot), `t15` (persistence), `t16` (camp fight), `t17` (Phase 3 QA),
  `t18` (currency authority, hit registration under latency, snapshot budget),
  `t19` (remote fidelity — fails on C16's unimplemented weapon clause).
- **Captured world:** `test/out/world-seed1337.json`, wire-field sha256_16
  **`74f45a52c2998dcf`** (re-confirmed live on both WS paths, 2026-08-26).
  Terrain determinism across restarts is proven against it. The M1 value was
  `c80269c44a757a8f`; the field legitimately changed when the Phase 2/3 zones
  landed, because siting a zone flattens the terrain under it. A mismatch here
  means non-determinism only if no zone moved.
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
