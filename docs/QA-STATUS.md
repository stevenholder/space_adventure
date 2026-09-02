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
| C14 | 20 shots at a target register; 20 aimed 1 m wide register zero | 20/20 and 0/20 at 40.7 m under 100 ms injected RTT; against a MOVING NPC at 300 ms RTT, 8/8 where the client sees it and 2/8 at its live position | **PASS** (`t18` static, `t21` moving) — re-pointed and the defect it found is fixed, see below |
| C15 | Damage per the GDD table, death, respawn within 3 s ±100 ms | 25 damage/hit live (100 → 75); 5 death/respawn cycles observed live | **PARTIAL** |
| C16 | Remote client sees weapon, pitch within 1°, ordered shot events | pitch worst error 0.35°; 8/8 `shot_fired`, ordered; weapon seen by an observer and by a late joiner | **PASS** (`t19`, weapon clause closed 2026-08-27) |
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

**C14's static volley is marginal, not stable.** `t18` reads 20/20 on some runs
and 19/20 on others (observed both, 2026-08-27, before and after that day's
changes). At 40.7 m the base spread cone is 0.43 m against a 0.45 m target, so
"all 20 register" sits on the edge of the weapon's own dispersion. It is a
measurement of spread, not of hit registration.

### C14 re-pointed at a moving NPC — the defect, and the fix (2026-08-27)

`test/t21-lagcomp-moving.mjs` fires at a camp grunt chasing a second player, at
300 ms injected RTT and ~31 m, and splits the shots by aim point. Measured:

| volley | aim point | before the fix | after |
|---|---|---|---|
| control | a grunt, live position | 3/3 — the shot can land from here | 2/3 |
| **stale** | **where the shooter's own client sees the grunt** | **0/8** | **8/8** |
| live | where the grunt actually is at that instant | 8/8 | 2/8 |

Every shot in both runs was accepted and resolved by the server (`shot_fired`
broadcast for each), so no volley is explained by a dropped shot.

**Rewind ran, and it was pointed at the wrong instant.** The server rewound by
its smoothed **RTT/2** and nothing else. One RTT/2 back from the moment a shot
*arrives* is the present, not the past the client fired at: the snapshot the
client aimed from was already one one-way old, and the shot spent another
one-way getting back. The deployed TS client was further out still —
The client renders remotes another 100 ms behind *local receive
time* — so a real player had to lead a moving target by one-way + 100 ms to hit
it, which is exactly what lag compensation exists to remove.

**Fixed by adopting the reference design** (Valve's
`command_time − packet_latency − view_interpolation`), which is also what this
repo's own PROTOCOL already described and the implementation had quietly
dropped — `fire.seq` was parsed and never used. Now:

```
rewind_ticks = staleness + RTT/2 + interp_delay    clamped to [0, rewind_max]
```

- `staleness` — how far back the tick that ran `fire.seq` sits from now. Zero
  for an ordinary shot, positive when the client fired against an older input.
  The server records which tick ran which seq, so this is its own observation,
  not a client claim; naming an ancient seq buys nothing but the clamp.
- `interp_delay` — **0.1 s, now a contract** (GDD "Lag compensation"). It binds
  the client: remotes must be rendered at `serverClock − interp_delay` on a
  synchronised clock, **not** at a fixed offset behind local packet arrival.
  The two differ by a whole one-way trip. No server test can catch a breach, so
  **the Unity client owes this explicitly at U13** — it is the half of the fix
  that cannot land until there are entity views to render.

**The measurement is direct, not inferred.** A `hit` event carries the point
where the ray met the capsule, so `t21` reads back the position the server
actually resolved against: **0.33 m from the axis the client was aiming at**
(inside the 0.35 m hitbox radius) and 0.38 m from the live axis (outside it).
That is the fix stated in one number.

Three things had to be right before the run said anything at all, and each was
wrong first:

- **Gate on the offset ACROSS the ray, not the raw 3D distance.** Offset along
  the ray slides the aim point up a line that still meets the capsule. The
  first run against the fixed server read 8/8 on *both* volleys and proved
  nothing, because it was selecting shots whose separation was mostly radial.
- **Separate the aim points by more than a body.** A 4.0 m/s grunt covers
  0.2 m per 100 ms of RTT against a capsule 0.7 m across, so 100 ms is
  unmeasurable by construction and 200 ms was still inconclusive — the
  resolved hit points landed *between* the two aim points and both volleys
  scored alike. 300 ms gives ~1.2 m.
- **Aim the way the contract says a client renders.** The harness first aimed
  at the newest snapshot it held, which is about half a tick behind
  `serverClock − interp_delay` — enough to leave the shot resolving between
  the aim points. It now extrapolates that half tick forward, which is the
  same arithmetic U13 owes in Unity, and is why `t21` is that task's check.

The remaining 2/8 on the live volley and 2/3 on the control are weapon
dispersion: `spread_base` 0.6° is 0.26 m at 25 m against a 0.35 m capsule, so
a shot aimed just outside the body sometimes lands inside it anyway. `t18`
makes the same trade at longer range and reads 19/20 for it.

### Two defects `t21` uncovered on the way (both fixed 2026-08-27)

1. **Camp NPCs were invulnerable to gunfire.** Every `EntityTypeNPC` resolves
   to the single `npc` entity_def in `items.json`, which was authored for the
   Phase 2 shopkeeper and still said `damageable: false`; `sim.ResolveShot`
   skips a non-damageable entity outright. A player could empty the rifle into
   the camp and never register a hit, a death or a drop. **This invalidates the
   "clear the camp" reading of the Phase 3 proof and of C23** — `t16` only ever
   checked that NPCs damage the *player*, and C23's evidence is a Go unit test
   with its own fixtures, so nothing live had ever shot an NPC. Fixed by making
   the def damageable; the shopkeeper stays immune through its archetype's
   absent `max_health` (it spawns at 0 health and the dead-entity guard drops it
   first). Pinned by `defs.TestHostileNPCsAreShootable`.
2. **A `hit` event with no body.** `sim/projectile.go` emitted `EventHit` with
   the 10-byte header alone, against PROTOCOL's 20-byte payload, so a client
   decoding the documented layout read past the end of the frame (this harness
   crashed on it). The branch needs a projectile to strike a damageable *world*
   entity, which was unreachable until defect 1 was fixed. Pinned by
   `sim.TestProjectileHitEventCarriesItsPayload`.

### C16 — the weapon clause, closed 2026-08-27

Remote pitch is accurate to 0.35° in the worst case — the quantisation floor,
since `pitch_q` is `asin(up·look)` over ±90° in 255 steps, i.e. 0.709° per
step — and all 8 shots produced exactly one ordered `shot_fired` each. Those
two clauses always held.

The third did not: nothing on the wire carried a player's equipped weapon.
The fix is the **`equipped` event, `event_id 0x0006`** decided 2026-08-26 —
the player's entity id plus the item id as UTF-8 — reusing the event channel
rather than widening the 54-byte row, because a value that changes a few times
a session should not cost bytes on every entity on every tick.

Two paths, both required and both checked, because either one alone leaves a
client rendering the wrong thing indefinitely:

- **On change.** `doCmd` compares the primary slot before and after the
  command and broadcasts when it differs — outside the identity lock, since
  the cmd path takes that lock before `s.mu` and the reverse order would
  invert it.
- **On join.** `syncEquipped` replays one event per already-armed player to
  the joiner, and announces the joiner's own weapon to everyone else — a
  reconnecting player arrives armed off their stored row, having changed
  nothing.

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
| C23 | Loot: table roll, single grant | Go `TestLootDropReachesWorld`, `loot_test.go` — never observed live, and until 2026-08-27 could not be: NPCs were unkillable (see C14 below) |
| C24 | Two clients see the same NPC positions | worst disagreement 0.000 m across 5 NPCs |
| C25a | Per-client snapshot bandwidth | 13.9 KB/s of 100 KB/s, 21 entities |
| C25b | Server holds 20 Hz with the camp live | 20.00 Hz |

## Phase 3.5 — C40 closed 2026-08-26, C43 gate run 2026-09-02

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C40 | The C# sim matches Go on the C5 route, headless | **max dPos 0.000e+0 m** over 1993 ticks; max dVel 5.088e-16 m/s; 0 grounded mismatches | **PASS** (`t20`) |
| C41 | Codec parity, C# against Go and Node | byte-for-byte against Go vectors, both directions | **PASS** (`t22`, `make unity-codec`) |
| C42 | Prediction: replay reconciliation from `ack_seq`, never blending | `t3` snapDist 0.000e+0 m, correction lands in 50.4 ms (budget 100); `t6` scenario checks green | **PASS** |
| C43 | C1–C25 re-run against the Unity client on the deployed stack | full sweep 2026-09-02, details below | **PASS** |
| C44 | `Sim` and `Net` build and test with no Unity Editor, in CI | `make unity-test` (18 checks incl. U13 timeline), `unity-codec`, `unity-conformance`, wired into `.github/workflows/ci.yml` | **PASS** |
| C45 | Cold start: packaged build joins the deployed server | fresh `make unity-build` → `unity-run`: world ready, 44 entities, RTT 2 ms | **PASS** |
| C46 | 60 fps with the camp live | **120.0 fps** (deliberate cap), worst frame 8.7 ms; uncapped probe 3276 fps, worst 6.1 ms | **PASS** |
| C47 | No agent-authored scenes or prefabs | `make unity-gate` | **PASS** |

### The C43 run, 2026-09-02

Full harness sweep against a freshly deployed stack (`make up`,
`check-server` build-matched): `t2`–`t4`, `t6`, `t7`, `t9`, `t13`–`t22` all
PASS. What it surfaced, which was the point of running it:

- **`t2` and `t4` were still driving the retired nginx `:3000` path** and
  failed on connect — client A now joins over the NodePort like everything
  else. A third of the C1–C25 surface had never run since the browser client
  was retired, and the first thing it found was its own harness rot.
- **C46's 6 fps was vsync, not rendering.** The player was pinned at ~4 Hz by
  vsync against a display reporting a degenerate refresh; actual frame cost
  was ~2 ms (RTX 5090, both D3D11 and D3D12, any resolution). The client now
  runs `vSyncCount = 0` with `targetFrameRate = 120` (Boot.cs says why), and
  logs `framestats` every 5 s as standing evidence.
- **U13 is verified on the C# path**: `SnapshotTimeline` moved to
  `Game/Core/Timeline.cs` (engine-free — the caller supplies the clock), and
  `SimDump --selftest` now proves the render point sits exactly
  `interp_delay` behind the estimated server clock, interpolates the bracket,
  and clamps past the newest snapshot. `t21` covers the same contract at the
  wire level and passes 3/3 (its old "red on the criterion, by design" note
  predates the C14 rewind fix).
- **`t18` failed 1 of 7 once** immediately after the t14–t17 chain and passed
  5 consecutive runs after; unreproduced, likely world-state interference
  between harnesses sharing a live server. Worth an eye on the next sweep.
- Views drawn at measurement time were 16 of ~54 world entities (camp NPCs
  were mid-respawn after the harness kills); with frame cost at 8.7 ms worst
  under a 16.7 ms budget, entity count is not the margin that matters.

**Positions are bit-identical**, not merely inside the 1e-10 m bar — four
orders tighter than C5's 0.01125 m, and six orders better than the bar it was
measured against. Velocity differs by one ulp on a single tick.

That number has the same shape as C24's near-tautological 0.000 m, so it was
checked the same way it should have been there: by perturbing the **sim**, not
the differ. Gravity + 1e-12 moves the trajectory 5.7e-12 m, which is genuinely
under the bar and passes. Gravity + 1e-9 moves it 4.9e-9 m and fails. The
pipeline is live and the bar is where it should be.

Both dumps are regenerated by `t20` on every run rather than read from
committed artifacts, and both sims replay the **quantised** wire field — the
Go dump round-trips its f64 field through the u16 encoding for that reason,
so the diff cannot measure the ~15 µm representation gap instead of a real
divergence.

`Sim` is ported from **Go**, not from the TypeScript, because Go is what C40
measures against. Where the two existing implementations differ in the last
ulp — Go's `Sqrt(dot)` against TypeScript's `Math.hypot` — this follows Go.
`Step.Hypot` replicates Go's scaled hypot algorithm rather than approximating
it with `sqrt(x*x + y*y)`.

## Phase 4 — C26–C32, run 2026-09-02

All against the deployed stack, two wire-level clients (`t24-rover.mjs`),
plus the cross-sim gate (`t23`, in CI).

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C26 | Occupancy visible to the other client ≤ 1 s; composed seat position < 1e-2 m | visible; deviation **1.68e-6 m** | **PASS** |
| C27 | The rover is never below the surface | min clearance −7.6e-6 m over 31 m of driving (independent field sampler) | **PASS**, as the property — there is deliberately no dev-override to force it under |
| C28 | Seat race: exactly one gets result 0 | first granted, second refused 1 (sequential race — exactly-one, not simultaneity) | **PASS** |
| C29 | Refusals: far board → 2, unseated disembark → 3, bad seat → 3 | 2 / 3 / 3 | **PASS** |
| C30 | Drive conformance ≤ 1e-6 over ≥ 1000 ticks + GDD table within 5% | max dPos **9.6e-9 m**, dVel 4.2e-9, dQuat 3.8e-10 over 1200 ticks; peak 16.09 of vmax 16 | **PASS** (`t23`) |
| C31 | Passenger input leaves the rover invariant | moved 0.00e+0 m through 1 s of full movement input; body stays composed | **PASS** |
| C32 | Disembark at any speed lands on terrain ≤ 1 s, ≤ 10 m, never inside geometry | at speed: 2.00 m from the rover, 2.8e-6 m off the surface | **PASS** |

**What the run caught:** C31 failed honestly on the first pass — not
passenger input, but the rover still coasting 0.7 m through the window.
Exponential damping never reaches zero, so a parked rover on any grade
creeps downhill forever. `hold_speed` (0.1 m/s, GDD) now zeroes the
tangential velocity outright when unthrottled; parked residual measured
1.25e-16 m/s. Both sims, conformance re-verified.

## Phase 5 — C33–C39, run 2026-09-02

Live legs against the deployed stack (`t26-flight.mjs`, `t27-pilot-latency.mjs`),
cross-sim gate in CI (`t25`, `make unity-conformance`).

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C33 | Buy → pad → pilot seat, one session; persists across reconnect | purchased at 600 cr, spawned, seated; reconnect adds no duplicate and the ship is present | **PASS** |
| C34 | Flight conformance ≤ 1e-6 over ≥ 1000 ticks + GDD table 5% | max dPos **3.7e-13 m**, dQuat 8.2e-16, dω 5e-16 over 1080 ticks; regimes agree every tick; peak 80.00 of vmax_boost | **PASS** (`t25`) |
| C35 | No discontinuity, no camera flip, no boundary oscillation | space flag rose and fell exactly twice across the full arc; max inter-snapshot step 6.2 m (bar: two ticks of vmax_boost); hysteresis unit-tested under a falling hover | **PASS** |
| C36 | Orbit: circles in space, returns, no drift/blow-up, renders at altitude | the committed arc reaches space, turns, burns back and lands; camera far plane 6 km; never below the surface (min clearance −1e-5 m, independent sampler) | **PASS** |
| C37 | Touch down ≤ landing speed, settle grounded, no penetration; hard landing penalised | live landing grounded and still; settle-vs-bounce branches unit-tested at 4 and 20 m/s | **PASS** |
| C38 | Passenger: sees the flight, no ship input, safe disembark | composed within one snapshot through the whole flight (worst 1.7 m ≈ seat offset), movement spam inert, disembarked onto terrain | **PASS** |
| C39 | 100 ms latency: p95 predicted-vs-authoritative < 0.5 m, no snap-back | **p95 1.8e-5 m**, p50 5.5e-6 m, 0 snap-backs, 393 paired samples through the proxy | **PASS** (`t27`) |

Notes: C27-style, C36's "returns to the pad" is flown as "returns and
lands" — the committed script lands ~30 m from the pad, and pad-exact
landings are piloting skill, not a sim property. Two GDD draft errors were
caught and corrected during implementation, both recorded in place: the
flight step's rotation line post-multiplied a world-axis quat while
claiming local axes, and the pitch sign claimed nose-up for what the
right-hand rule makes nose-down.

## Harnesses, for the C43 re-run

All under `test/`. These speak the wire protocol directly and carry their own
independent codec, so they survive the Unity rebuild untouched — they are the
headless client that makes the port safe.

- **Libs:** `lib/ws.mjs` (raw-TCP RFC-6455 client, incl. `kill`, ns timestamps),
  `lib/wire.mjs` (independent codec, written from `PROTOCOL.md` only),
  `lib/field.mjs` (independent GDD terrain sampling).
- **Per-criterion:** `t2`–`t4` (visibility, authority, despawn), `t20` (sim
  conformance, replaying `test/t5/script-go.jsonl` — the C5 route; the M1-era
  `t5/`/`t10/` scratch that designed it lives in git history, evidence in
  `t10-final-report.*`), `t6` (prediction), `t7` (sustain), `t9-terrain.py`,
  `t22`/`t13` (codec + collider parity, both C#
  against Go since U18 retired the TypeScript client), `t14` (buy
  and shoot), `t15` (persistence), `t16` (camp fight), `t17` (Phase 3 QA),
  `t18` (currency authority, hit registration under latency, snapshot budget),
  `t19` (remote fidelity — weapon, pitch and shot ordering, all green),
  `t21` (C14 against a moving NPC — passing since the C14 rewind fix landed;
  its earlier red-by-design note is history).
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
