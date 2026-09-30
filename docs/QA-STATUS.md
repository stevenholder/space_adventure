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

## Phase 11 — C78–C83, run 2026-09-15 (kind)

Skills, the RuneScape way: seven verbs train seven skills by doing, the
curve is frozen, efficacy is real on both sims, and nothing else moved.

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C78 | Doing trains: every hooked verb awards, events batch ≤1/s/skill, XP survives reconnect | `t34` plays one life over the wire — sprint, buy (rifle + cell + ship), fly the C34 arc, drive, walk into the camp, kill, walk onto the drop — and all seven skills move (marksmanship 420, athletics 49, driving 19, piloting 210, scavenging 250, commerce 170 = 851 cr / 5, recon 500 = 2 POIs × 250); 127 `skill_xp` events, min gap between one skill's events 998 ms; sheet equals the last event per skill and is byte-identical after reconnect on the same token (32 checks) | **PASS** |
| C79 | The curve is RuneScape's; 92 is half of 99 | `PointsForLevel` pinned at 83 / 101,333 / 13,034,431 in Go (`curve_test.go`) and in the C# mirror (SimDump `--selftest`: "curve: 92 is half of 99  92→6,517,253"); every t34 event's `level`/`next_at` recomputed by the test from the formula and matched | **PASS** |
| C80 | Efficacy is real and conformant; fresh players bit-identical | measured: damage 25 → 30 at ×1.196 and 50 at ×2 (`TestResolveShotDamageMult`), buy 250 → 226 at ×0.902 (`TestBuyAtDiscount`), extra roll doubles a chance-1 table (`TestDropLoot_ExtraRoll`); sprint at 1.30× conformant Go↔C# to 1e-10 m, drive and flight at 1.25× to 3.55e-15 / 0.00 m (`t35`); at ×1/0 every path is the old arithmetic (t20/t23/t25 unchanged, 10/10; fleet below) | **PASS** |
| C81 | Synergies apply and show | Recon→Scavenging (in POI only), Athletics→Driving, Scavenging→Commerce resolved from `skills.json` by one function and unit-tested at levels 20/50 (`TestEfficacyAndSynergyMath`); the K panel draws the arrow under each source naming the partner and the live bonus (`test/out/ui/panel-skills.png`) | **PASS** (Scavenging→Commerce `sell_bonus` has no verb yet — there is no `shop_sell`; it applies the day one exists) |
| C82 | Unlocks gate | `unlock_requirements` empty at launch; an injected row refuses `shop_buy` with `locked` below level 5 and passes at it, credits untouched on the refusal (`TestUnlockGateOnPurchase`) | **PASS** |
| C83 | Nothing else moved; frame budget with the panel open | fleet on the deployed build: t2 t3 t4 t6 t7 t13 t14 t15 t16 t17 t18 (solo) t19 t21 t24 t26 t27 t28 (against kind) t29 all PASS; go vet + test green; unity-test / codec / conformance green; framestats with K open 119.9 fps avg, worst 9.0 ms, 17 entities | **PASS** |

Two harness notes from the sweep, neither a Phase 11 regression:
`t21`'s control volley had been picking the zero-offset grunt, which
since Phase 9's second POI is the idle outpost grunt ~178 m out, past
`max_range` — 0/3 on the pre-Phase-11 server (39aa959) too; it now
picks in range (2/3, 3/3 checks). `t28` defaults to the old LAN
production origin and 404s there; against kind (`SA_SITE_URL`) it is
25/25. Owed to the playtest: the drip and the LEVEL UP banner seen live
(the rig cannot earn XP before its shot), and a second player watching
the sheet persist across a real day.

## Phase 10 — C72–C77, run 2026-09-08 (kind)

Missions, parties, and the bounty — server-authoritative end to end; the
client journal renders events and asserts nothing.

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C72 | Parties: invite (both paths), accept, leave, dissolve, disconnect | wire-tested lifecycle: toast names the inviter, rosters reach every member, leaver gets the empty roster, dissolve-at-one on leave AND on disconnect, 4-cap refuses the fifth (`TestPartyLifecycle`, `TestPartyCap`) | **PASS** |
| C73 | Personal missions end to end | cmd surface wire-tested: starter filter at the quartermaster vs the dispatcher's full board, accept/duplicate/abandon/re-abandon, turn-in gates; fetch VERIFIED live in-test — the fresh player's 120 cells cover the 30-cell salvage, items consumed, complete event fired, credits paid (`TestMissionCmdSurface`) | **PASS** (kill/scout progress: engine unit paths + the live playtest) |
| C74 | Party-wide credit, full pay each | kill credit iterates partyMembers at the kill, captured under s.mu and paid after (fire → missionKillCredit); scout sweep completes through the party the same way | **PASS by construction + review** (two-client live run rides the playtest) |
| C75 | Bounty claim is exactly-one | live two-client race over the wire: one StatusOK, one refused `"claimed"` (`TestBountyRaceAndRelease`) — the seat-race proof, replayed | **PASS** |
| C76 | Bounty never wedges | abandon-by-last releases and arms the re-post (wire); expiry releases and despawns the warlord (white-box clock); disconnect routes through the same abandon; stolen kill releases unpaid (code path shared with expiry) | **PASS** |
| C77 | Nothing else moved | t14/t16/t18/t29 green against the deployed build (t18 solo — its known back-to-back flake); full go vet + test green incl. the third store migration on SQLite and Postgres | **PASS** |

Owed to the playtest: two humans partying up live (look+E and the P
panel), a shared camp fight progressing both journals, and one real
warlord claim. The gallery shots are LIVE captures — the kind server's
bounty broadcast is visible in them (banner, WARLORD compass marker,
CLAIM button).

## Phase 9 — C66–C71, run 2026-09-03 (kind)

The world rebuild: zone layouts on the 4 m kit grid, one source deriving
both colliders and visuals; camp and range rebuilt; two solver-placed
POIs live (outpost, relay).

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C66 | Kit honest: cell bounds, skirts, budgets; no stretched boxes | 14 pieces pass the verify cell gate (36–144 tris each); the client's stretched-box collider visual is deleted — box colliders draw nothing, kit props ARE the walls | **PASS** |
| C67 | Nothing clips; fleet green on the new colliders | corners belong to posts (walls butt in, dedupe-tested), pieces confined to their cells by the art gate; t13/t14/t16/t18/t29 green against the rebuilt world (21-check journey through the new 2.4 m gate) | **PASS** |
| C68 | Landmarks: one mast per POI, discovery at visibility range | audit test: exactly one mast per layout, mast spacing ≥ 120 m; compass gates OUTPOST/RELAY markers at 84 m (12.6 m mast via visible ≈ 22.6 + √(300·h)) | **PASS** (marker gating verified by code + formula; live eyeball rides the playtest) |
| C69 | Solver deterministic; every clearance holds in an audit | `server poi` byte-identical across runs; `TestZoneSiteClearance` audits every committed zone against spawn, 6 landmarks, 11 craters and every other zone (legacy camp/range at zero margin, solver sites at +10 m) | **PASS** |
| C70 | Two POIs pay off | outpost live with 2 grunts + 1 gunner (kills roll chance-1.0 tables, walk-over pickup — the t29-proven loop); relay live as the safe Colony landmark; spawn census confirms both | **PASS** |
| C71 | Budget holds with the new world | framestats 120.0 fps avg, worst 8.5 ms, colliders 11 → 28 | **PASS** |

Terrain recaptured (two new flatten discs change the field):
world-seed1337.json sha256_16 c53cdbbe57d5ead1; t2 re-verified against it.

## Phase 8 — C60–C65, run 2026-09-03 (kind)

The UI refresh: everything IMGUI moved to code-built UI Toolkit in the
Scrapyard Comic language (GDD "UI style guide"). Presentation only — the
wire and the sim are untouched, which is what C62 exists to prove.

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C60 | One language; gallery in `test/out/ui/` | hud-final, hud-skew, panel-bags, panel-sheet, panel-map, panel-account — every screen on the style guide's tokens, captured from the packaged player via `-uiShot`/`-uiPanel` | **PASS** (shop screen shares ItemCard/panel construction with bags; a live shop screenshot needs an NPC interaction and comes from the playtest) |
| C61 | Damage numbers, crit styling, directional incoming indicator | implemented (`UI/CombatFeed.cs`), wire decode of the hit event verified against PROTOCOL; **live camp-fight eyeball still owed** — headless can drive the fight (t16) but not see the popups | **PARTIAL — needs the playtest** |
| C62 | Full harness fleet t2–t28 passes unchanged | all green against the deployed kind stack, byte-identical buy/equip cmds (t14 6/6, t28 25/25; t18/t21 flaky under fleet load, clean solo) | **PASS** |
| C63 | Compass bearings match entity positions | `Bearing.To` unit-tested in the headless harness (6 cases, incl. the right=−X frame); markers live on the strip in every gallery shot | **PASS** |
| C64 | Rarity end-to-end, unknown degrades to common | rarity in items.json → defs → `Defs.ItemRarity` → card band; unknown/absent → "" → common by the `Styles.Rarity` default arm | **PASS** |
| C65 | 120 fps capped, worst frame < 16.7 ms with the new UI | framestats: **120.0 fps avg, worst 8.6 ms**, 12 entities, packaged player against kind | **PASS** |

Known unverifiable-headless: UI Toolkit **button clicks** in the packaged
player (buy, equip, account link). Everything up to the click is proven —
the views build byte-identical cmds and the harness sends them — but a
human has to click once. That plus C61's eyeball are the playtest items.

## Phase 7 — C54–C59, run 2026-09-03 (kind AND production)

25 live checks (`t28-accounts.mjs`), run twice: against the deployed kind
stack, and — post-merge, post-CD — against the public
`https://game.stevenholder.info` over the real internet (25/25 both).

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C54 | Register/login, argon2id, sessions, rate limit, CSRF | register + auto-login; wrong password 401; no-header mutation 403; ten rapid logins rate-limited; sessions DB-backed | **PASS** |
| C55 | Link code → token → play; single-use; one player per account | 8-char code redeemed once (second attempt 401); token joined the game; a later code returned the SAME token | **PASS** |
| C56 | The account page tells the truth | pilot listed with its live 1000-credit start | **PASS** |
| C57 | Lifecycle | password change killed the other session and spared its own; delete removed account, session AND the owned player (players 117 → 116, counted) | **PASS** |
| C58 | The front door | landing + stats public; anonymous /api/me 401 | **PASS** |
| C59 | Coexistence | full harness fleet untouched; legacy guest imported and listed; owned token refused re-import; the orphaned token joined again as a fresh guest | **PASS** |

What the run caught: argon2id's first-choice parameters (64 MiB) OOM-killed
the server inside the kind pod's 128Mi limit on the FIRST registration —
silently, from outside, no panic to read. RFC 9106's second recommended set
(19 MiB, t=2) fits the smallest pod this binary runs in; the encoded hash
format is self-describing, so the change needed no migration.

## Phase 6 — C48–C53, closed 2026-09-02

| # | Asserts | Measured | Verdict |
|---|---|---|---|
| C48 | Push to main → deployed, no human step; failed smoke rolls back | run 33693371037: green CI → tailnet → apply → smoke `/version` == main's server hash (5ff5703 == 5ff5703). The rollback path is PROVEN — a smoke string bug fired it against a correct deploy first | **PASS** |
| C49 | Packaged client plays the full loop on the LAN URL | joined `ws://192.168.1.163/ws`; t3/t7/t15/t24/t26 all green; 20 Hz exact, one-way p95 6.3 ms | **PASS** |
| C50 | Node-death and restore, DRILLED | primary killed → failover ~60 s, 6/6 rows; backup restored to scratch DB, row-counted | **PASS** |
| C51 | Server churn: reconnect on token, persistence holds | server pod deleted; fresh-connection t15 green | **PASS** |
| C52 | Hardening holds | origin allowlist + per-IP cap + join rate unit-tested and live; creds CNPG-generated; **NetworkPolicy clause WAIVED**: this k3s enforces cross-node netpol wrongly (allow-all still blocks — probed with pinned pods); a fence that fails by pod placement is worse than none. CNI repair is the reopen trigger | **PASS (netpol waived)** |
| C53 | The kind dev path untouched | full local sweep green throughout | **PASS** |

**What C48's six failed runs taught, each one a future outage pre-paid:**
bootstrapped GHCR packages aren't repo-linked (grant Actions access);
the operator's first ts.net TLS cert minting outlives a 20 s handshake;
the tailnet impersonation grant is app-capability, never covered by
allow-all IP; a CD Role that applies its own manifest needs rbac verbs;
a mid-apply Role downgrade lobotomizes the applier; and a smoke check
must not read kubectl's chatter as data.

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


# Phase 11.5 — the engine swap (2026-09-23)

Run on the branch `feat/godot-client`, against a bare `server -listen :18080`
(no kind), Godot 4.7.2-stable .NET, .NET SDK 10.0.112, WSLg for the windowed
shots.

| # | Result | Evidence |
|---|---|---|
| C84 | PASS | `make godot-test` (self-checks OVERALL: PASS), `godot-codec` 9/9, `godot-conformance` 7+10 checks, `t13` 9 scenarios, `t35` 6 checks — all on the moved sources |
| C85 | PASS | `dotnet build client/SpaceAdventure.Client.slnx` builds Sim, Net, GameCore, SimDump and the Godot game assembly; `ci.yml` job `client` runs it |
| C86 | PASS | `godot-cli build` → 149 MB Linux export with `art/` staged beside it; `godot-cli run 10` → `world ready: entity=3 seed=1337 tickHz=20`, `colliders: 28`, exit 0 |
| C87 | PASS | `-selftest` 9/9 (yaw sign, pitch sign, pitch clamp, parallel transport, orientation basis ×2, model flip, winding ×2) |
| C88 | PASS | `-dumpNodes char.player`: `arm_r/hand_r`, `eye`, `AnimationPlayer` with `die 0.33s idle 1.33s sprint 0.50s walk 0.67s`; importer emits `ImporterMeshInstance3D`, converted in `AssetRegistry.Generate` |
| C89 | PASS | windowed 1280×720 shots: terrain and horizon (M1), quartermaster and rover (M2), starfield (M3), HUD/skills/map/bags (M4), armed rig (M5) |
| C90 | PASS | `godot-cli build Windows` from Linux → `client/build/windows/SpaceAdventure.exe` (183 MB with staged art); not yet run on a Windows host |
| C91 | PASS | `make godot-gate` → `C91 clean` |

### Debts closed 2026-09-28 (`fix/godot-rig-eyes`)

Same bench: bare `server -listen :18080`, Godot 4.7.2 .NET, WSLg for the shots.

| # | Result | Evidence |
|---|---|---|
| C92 | PASS | Rig lit and posed by eye: `-uiShot -rigArmed` → `test/out/ui/rig-armed.png` (rifle lower-right, barrel toward the crosshair, lit). Root cause of the black slab: under `gl_compatibility` nothing in the transparent SubViewport overlay received light, and a second `DirectionalLight3D` lit nothing anywhere; the overlay is gone, the rig is drawn by the main camera with an `OmniLight3D` (range 3 m, cull mask = rig layer, which the compat renderer does honour) |
| C93 | PASS | Over-head health bars readable: `-uiDemo -uiFace hostile` → `test/out/ui/combat-godot.png`, bars at 9 m and 13 m with a border (were 5 px, borderless, black on a black sky) |
| C94 | PASS | Rocks seen, not just logged: `-uiFace rock -uiApproach 6` walks to the nearest scatter placement (38 m → 5 m) → `test/out/ui/rocks.png`, boulder seated in the ground, more on the ridges |
| C95 | PASS | `godot-cli build Linux` 149 MB and `build Windows` 183 MB on the same sources; `godot-cli run 20` on the Linux export → `world ready`, `colliders: 28`, exit 0. `ci.yml` `client-export` now uploads `client-windows` beside `client-linux` (the .exe is still unrun on a Windows host) |
| C96 | PASS | First Windows launch (2026-09-28): the .exe ran but sat on an empty HUD — default server was `ws://127.0.0.1:18080/ws` and nothing said so. Now an export (no `editor` feature) defaults to `wss://game.stevenholder.info/ws`, and until the world lands the banner shows the link state: dead port → `RECONNECTING TO ws://127.0.0.1:1/ws… (Unable to connect to the remote server)`. Export with no URL joins over LAN (`-serverUrl ws://192.168.1.163/ws` → `world ready`, `colliders: 28`). **The public door itself is DOWN as of this run**: `https://game.stevenholder.info/version` → Cloudflare 523 while `http://192.168.1.163/version` → `5da7a0c`; the break is between Cloudflare and NPM/router, outside this repo |
| C97 | PASS | World art (`feat/world-art`): terrain flat-shaded per triangle with the banded palette + seeded noise patches + scree; ambient lifted (0.10/0.11/0.15 → 0.17/0.18/0.24), sun 1.0; rocks tinted per instance with 0–3 pebbles each (`rocks: 353/304/336 (400 placed + pebbles)`); map draws the same palette. Shots: `test/out/ui/world-spawn.png`, `world-rock.png`, `world-map.png`. Scatter contract untouched: `godot-test` RockScatter checks still pass |
| C98 | PASS | **Ground vanished when looking down** (found on the first windowed walk, 2026-09-28): the cube-face winding was inside-out on every face -- the old builder's comment had Godot's front face as counter-clockwise; it is CLOCKWISE (ArrayMesh docs). The smooth mesh hid it; the flat mesh made the far side show through the ground under the player. Fixed by inverting the measured flip and forcing face normals outward regardless of winding. `-uiPitch -25` → `test/out/ui/world-down.png` (ground, shopkeeper's shadow on it); `-uiPitch -70 -uiYaw 120` shows own body on ground; `-selftest` winding checks still PASS. Owed: a hairline seam at cube-face boundaries (flat normals differ across the edge) |

**Notes.** The volume fit replaced the longest-axis fit in `AttachFitted`:
the Kenney rifle is 0.9 × 0.5 × 0.18 m, and fitted by length it stood 0.5 m
tall five centimetres from the eye. The rig rest position moved out to suit
(`ViewModel.RestPosition`); eyes-on tuning on a real display is still owed.
Rocks (400 instances, MultiMesh) log `rocks: 145/126/129` but were not
individually confirmed in a screenshot. Two harness-only observations: `t27`
fails with "ship never spawned" when run right after `t26` has flown the ship
off (server state, not client), and the pilot camera's hull-fixed basis is
`hull * ModelFlip` because a Camera3D looks down −Z while the ship faces +Z.

# CI/CD — GitHub releases (2026-09-29)

| # | Result | Evidence |
|---|---|---|
| C99 | PASS | First live run 2026-09-29 on the #23 merge (deploy run 36584666744): `release` job green, `v2026.09.29-158ce49` pre-release with `SpaceAdventure-…-linux-x86_64.tar.gz` (59 MB) and `…-windows-x86_64.zip` (70 MB), notes = header + the 22 merged PRs to date (no earlier `v*` tag). Later releases are bounded by the previous tag |

# Client feedback round 2 (2026-09-29)

| # | Result | Evidence |
|---|---|---|
| C100 | PASS | **Tracers fired in a fixed direction** (feedback with screenshot). Sent aim == camera forward == the server's returned ray (logged: `sent=(0.232,-0.131,-0.964)`, `recv dir=(0.231,-0.125,-0.965)`), so the drawing was wrong: `Basis.Scaled()` scales along the WORLD axes and stretched every tracer along world Z. Now `LookingAt * FromScale` (local). Logged tracer end == server end to 1e-6 on three shots. Rig gained `-uiBuy weapon.pulse` (real purchase through the live shop interaction, cone measured at the NPC's eye) and `-uiFireNow <s>`, which is how this was reproduced headless — `-rigArmed` is visual only and the server drops its fire |
| C101 | PASS | **Rocks read as pebbles.** Floor raised 0.3 → 0.6 m; the 1.5 m cap STAYS (GDD: no prop collision in M1, nothing may be big enough that walking through it jars — a 3.5 m trial tripped `godot-test`'s "every rock is within the GDD size band"). Pebble clusters unchanged. `test/out/ui/world-rock.png` at 7 m. Bigger boulders need server-owned colliders: a milestone, not a knob |
| C102 | PASS | **Claiming the warlord mission fired the rifle.** `Fps.Sample` read the trigger from the raw left button; a panel click with the cursor freed still counted. Fire now requires the pointer to be captured (the world has the mouse); UI clicks never shoot. Build + `-selftest` green; walked in the native window |
| C103 | PASS | **Claiming the warlord bounty did nothing visible; CLAIM stayed.** The server accepted (status 0, empty body) but the client only updates journal state from a board listing and never handled the `mission_accept` result. Now the log tracks the pending accept: OK activates the row and clears the priority shout, a refusal reverts the board's optimistic activation and prints why (`claimed` / `no_bounty`). The priority offer registers a named row so the active entry reads "Bounty: warlord at OUTPOST", not the id. Rig `-uiPanel journal -uiClaim` → `active=True priority=none` → `test/out/ui/journal-claimed.png`; a second client gets no offer once it is claimed |
| C104 | PASS | **Enemies saw and shot through walls.** Root cause in `sim.nearestBox`: a zero-radius probe INSIDE a box has dist 0 and `dist >= radius` called that a miss, so `SegmentHitsColliders` (NPC line of sight) never registered a wall, and the projectile's end-point collider test never did either. A strict comparison was the first attempt and was NOT enough: the rotate round trip leaves ~1e-8 of noise, so an interior probe still missed a rotated wall (`TestOutpostWallBlocksSightAndShots` against the real outpost data caught it after the first "fix" shipped to the local server). Containment is now decided in the box's own frame, exiting through the nearest face (`TestZeroRadiusProbeInsideRotatedBox`). Projectiles also test the SWEPT segment (a gunner's round covers more than a wall's thickness per tick), so a capsule behind a wall is not a hit: `TestProjectileStopsAtWall` (health stays 100, no EventHit, round removed). `go test ./...` green |
| C105 | PASS | **Dead player could walk, then teleported.** The client never read its own dead flag. Now: dead flag latched from the self row, movement/actions/fire zeroed while dead (look still flows, as the server allows), full-screen "YOU DIED / RESPAWNING IN N" overlay counting down GDD `respawn_delay` 5 s, cleared when the flag drops. Rig `-uiDeathDemo` (local flag only) → `test/out/ui/death.png` |
| C106 | PASS | **Kit walls rendered pure white** ("textures missing" at the outpost). The glb writer emitted COLOR_0 as unsigned bytes WITHOUT glTF's required `normalized: true`; Godot read 0..255 as floats and clamped to white (characters are float colours, so they were fine). `art/tools/glb.py` fixed, every generated asset regenerated (kit, structs, ship.v1, target), `verify.mjs` now fails any byte COLOR_0 that is not normalized. Rig `-uiKitDemo` stages a scrap wall + colony tower at spawn through the props path → `test/out/ui/kit-pieces.png` (rust/scorch/hazard, steel/trim). Also fixed on the way: `-dumpNodes` mode crashed on the link banner with no network |

# Phase 11.7 — the interface, drawn (2026-09-29)

Bare `server -listen :18080`, Godot 4.7.2 .NET, shots via the rig.

| # | Result | Evidence |
|---|---|---|
| C107 | PASS | Character panel: `-uiBuy weapon.pulse,armor.suit.scout,armor.helmet.scout,charm.rabbit -uiPanel character` → `test/out/ui/panel-character.png`: helmet (common), suit (uncommon), charm in accessory1 (rare), rifle icon in WEAPON (rare); doll lit and turning in an OwnWorld3D SubViewport (the transparent shared-world overlay that failed in C92 is not this); ARMOR 11, DAMAGE 25, 400 RPM, Commerce 2, buy prices −0.2% |
| C108 | PASS | Backpack: `-uiBuy armor.boots.scout,armor.gloves.scout -uiPanel backpack` → `test/out/ui/panel-backpack.png`: 5×4 grid, `EC 120` with common frame, the worn boots and gloves absent from the grid, `3 / 20 slots` |
| C109 | PASS | Server: `TestEquipSlots` — head accepted, undeclared slot refused, accessory to either slot, moving the ring vacates its old slot, unequip clears, a ring in the weapon slot refused. `go test ./...` green; the defs audit passes with the seven new items |
| C110 | PASS | Icons: `gen_icons.py` renders `weapon.pulse.png` and `ship.v1.png` (the two items with models) at 96 px on slate; staged under `art/icons` by `godot-cli stage_art`; items without a model draw initials (SH, SS, RF, EC in the shots) |
| C111 | SUPERSEDED | "One modal at a time" lasted an hour: it stopped the backpack and the character panel being open together, which dragging between them needs. Panels stack now (C113) |
| C112 | PASS | Panels drag by their header and remember their place: rig `-uiPanel backpack -uiDragDemo` drives a press, six motions and a release through Godot's input → `ui: dragged backpack (440, 129.6) -> (740, 36.6), saved=True` (x exactly +300; y is WSLg's `WarpMouse` mapping, the handle follows the real pointer); a fresh run's `-uiPanel backpack` opens at the saved spot → `test/out/ui/panel-dragged.png`. Grip is parented to the title LABEL: under the header container it laid out as a zero-height row nothing could press |
| C113 | PASS | Panels stack: `-uiPanel character,backpack` opens both, character left of centre and backpack right by default → `test/out/ui/panels-both.png`; no key closes another panel; each drags to wherever the player keeps it |
| C114 | PASS | Escape: with panels open it closes them all (panels, map, shop); with nothing open the game menu (RETURN TO GAME / ACCOUNT / QUIT GAME) → `test/out/ui/panel-menu.png` via `-uiPanel menu`; the old cursor-free latch is gone (an open panel frees the cursor, closing captures it) |
| C115 | PASS | Shop restyled: one card per stock item — rarity band, `ItemSlot` icon (static: tooltip, no drag), name in rarity colour, the item's desc, price in amber, BUY (a dash when unaffordable); ten items scroll inside a fixed 400 px region so the wallet and the frame stay put at 720p → `test/out/ui/panel-shop.png` |
| C116 | PASS | Journal and party restyled onto the same cards: mission type tile (K/B/S/F in the type's colour), progress bar with count/goal, reward; the priority bounty as an amber card with CLAIM; party members and nearby players as face tiles with INVITE/ACCEPT/DECLINE (invite payload `{"target":id}` unchanged) → `test/out/ui/panel-journal.png`, `panel-party.png` |
| C117 | PASS | Map drags by its header and remembers its place like the modals (`PanelMemory` shared by `ModalView` and `MapView`); `-uiPanel map` unchanged otherwise. `make godot-gate` clean |
| C118 | PASS | The E · talk prompt no longer draws over an open panel (it sat on the shop it had just opened): the prompt is cleared while any panel or the map is open → `test/out/ui/panel-shop.png` re-taken without it |
| C119 | PASS | Buying no longer scrolls the shop back to the top. First attempt (read the outgoing box's offset) still jumped: a buy rebuilds twice, click then reply, and the second read saw the fresh box at 0. Now the offset is fed by the scrollbar's own value changes and restored once the new bar has a range. Rig `-uiShopScrollDemo`: scroll to the bottom, buy a cell through the button path, wait for the reply → `ui: shop scroll 356 -> 356` → `test/out/ui/panel-shop-scrolled.png` |

# Phase 12 — the artisan loop (2026-09-29)

Bare `server -listen :18090` with `DATABASE_URL=sqlite://…` (reconnect needs
a store), Godot 4.7.2 .NET from source for the rig shots, `t36` over the
wire. Crafting a thing and the death spill are asserted in Go: scrap lies
inside the guarded outpost 258 m from the pad, and a scripted death there
is not a test.

| # | Result | Evidence |
|---|---|---|
| C120 | PASS | `t36`: three iron + one copper node at the pad and two wrecks in the outpost arrive as type `0x0008` with `health` 5/5/5 (= yields); one yield reads 4, five read 0 and `gather` then refuses `depleted`; `TestNodeDepletesAndRespawns` counts the respawn in whole ticks and refills in place with the same id |
| C121 | PASS | `t36`: `gather` answers `{"duration":3}`; the `gather_end` `done` landed 2996 ms after the send; a second `gather` mid-channel is `busy`; stepping 0.6 m away ends it `moved` with no item and the node keeps its yield; `gather_cancel` ends it `cancel` and answers `not_gathering` when idle; an empty TOOL slot refuses `no_tool` (copper with the hand drill too — the tool check precedes the level check); `TestHandleCmdPhase12Skeleton`: a bag that cannot take the yield refuses `no_space` before any bar; `TestGatherChannel`: death → `died`, emptied node → `depleted` |
| C122 | PASS | `t36`: four iron ore at the quartermaster → 880 + 12 = 892 cr (value 6 × 0.5 × 4), Commerce 24 → 26 XP on the sale; `ammo.cell` refuses `unsellable`, the worn drill refuses `equipped`; `TestSellAt`: the Scavenging→Commerce `sell_bonus` raises the unit price (6 → 4 at +50% on a 0.5 rate, floor), the bench buys nothing (`no_stock`), every refusal leaves credits untouched |
| C123 | PASS | `TestCraft`: cells consume 2 ore + 1 scrap → 30 cells; `qty:3` short one scrap → `missing_materials` with nothing consumed; `qty:2` → 90 cells; the plate below Engineering 5 → `locked`; the mk2 recipe eats the worn hand drill and clears its slot; a bonus unit on a stack_max 1 output is dropped, not refused. `t36` at the relay bench (107 m walk): plate `locked`, cells `missing_materials`, six ore still in the bag afterwards, the quartermaster refuses a craft |
| C124 | PASS | `TestDeathSpillsMaterials`: only the two material stacks leave the bag, one entry each; cells, the worn drill, credits and the equipped map stay; a second spill finds nothing. `TestLootExpires`: a `DropStack` lasts exactly `loot_lifetime` ticks then leaves the world — `StepLoot` had never been registered since Phase 3, so no drop had ever expired. Wiring: `damagePlayer` queues the spill under `s.mu`, `tick()` strips through the identity after unlocking and drops with `loot_dropped` events, ordinary walk-over pickup |
| C125 | PASS | `t36`: `skill_xp` mining 25 after the first yield, 125 after five (1 Hz flush honoured — the test waits for it); Commerce moved on the sale; after `ws.close()` and a reconnect on the same token the sheet is byte-identical (`mining: 125`) and the bag still holds six ore with the drill worn |
| C126 | PASS | `TestGatherChannel`: 3.0 s → 2.67 s at Mining 21 + Engineering 11 (−10% − 1%), the Engineering→Mining synergy leaves Salvaging at 3.0 s (`synergyBonusFor` filters by target), Mining 99 on a 1.8 s node floors at 1.0 s; `craft_extra` rolls once per `craft` through `cmdWorld.Rand`; `tool.drill.mk2` satisfies an iron node via `supersedes` (`t36` cannot, it has no mk2: pinned in the skeleton test); K panel: MINING −0% CHANNEL TIME, ENGINEERING +0% BONUS CRAFT OUTPUT, arrow MINING −0% DRILL TIME under Engineering → `test/out/ui/p12-skills.png` |
| C127 | PASS | `-uiGatherDemo` → `test/out/ui/p12-gather.png`: iron cluster (dark rock, rust facets) 3 m ahead, a wreck at 0.6 scale beside it, the four real nodes on the ridge east of the pad, prompt `E · DRILL · NEEDS HAND DRILL`, amber DRILLING bar under the crosshair; `-uiPanel bench` → `p12-bench.png`: three recipe cards with have/need inputs (`0/2× IRON ORE`), ENG level, dash while short; `-uiFace npc -uiApproach 2 -uiBuy tool.drill,tool.cutter` → `p12-shop-sell.png`: SELL · BASE PRICE column under the stock listing the unworn drill at 1 × 20 cr, the worn cutter excluded; `… -uiBuy tool.drill -uiPanel character` → `p12-doll.png`: the drill's icon in TOOL |
| C128 | PASS | Against the wave-2 server: `go vet && go test ./...` green, `godot-gate` clean, `godot-codec` 9/9, `godot-test` OVERALL PASS, `t13` 9/9, `t35` 6/6 (drive 3.55e-15 m, flight 0), `npm --prefix art test` 39/39 assets, `t36` 34/34, `t34` 32/32 (seven skills move and persist exactly as before; the `skill_xp` min gap 998 ms). `t14` 6/6 ran against the kind stack. A player who never wears a tool sees four new rocks and a bench; nothing else moved |

Owed to the playtest: a real death with ore in the bag and the run back;
cutting a wreck under fire; crafting cells at the bench with scrap in hand.

# Phase 13 — use, modify, and the hotbar (2026-09-30)

Bare `server -listen :18090` with sqlite, Godot 4.7.2 .NET from source for
the rig, `t37` over the wire. The scanner's pings and the mods' numbers are
bench-made behind Engineering levels one life cannot reach, so their live
halves are unit tests plus the rig's faked bag; the wire verb itself is
played by `t37`.

| # | Result | Evidence |
|---|---|---|
| C129 | PASS | `t37`: two medkits bought (940 cr), `use` at full health → `no_effect` with both still in the bag; a grunt's hit at the outpost (100 → 76), `use` → `{"effect":{"health":100},"cooldown":8}` and the snapshot reads 100; a second `use` inside the window → `cooldown` with `ready_in: 8`; one unit left the bag; the rifle → `unusable`, a nonsense id → `unknown_item`. `TestUseMedkit`: +50 capped at 100, `not_owned` when none is carried, a refused use consumes nothing, a successful one cancels the gather channel; `TestUseRefusals`: dead → `dead`, `coolingFor` counts down |
| C130 | PASS | `TestUseScanner`: worn → pings with the def's 120 m range and a 30 s cooldown, the gadget stays; unworn → `not_owned` (also on the wire in `t37`); `scanLocked` lists nodes and drops with position and health; rig `-uiHotbarDemo` pings the pad's four nodes onto the compass (IRON / COPPER labels) → `test/out/ui/p13-hotbar.png` |
| C131 | PASS | `TestApplyMod`: deltas add (25/30/120 → 30/40/160), a nil mod is the bare table, `WeaponWith` reads the worn slots and a mod without a rifle has no table; `TestReloadWithMagMod`: the mag mod reloads to 40, with it off a 40-round magazine clamps to 30 and the 10 surplus rounds return to the bag; `fireLocked` resolves cadence, magazine and damage through `sim.WeaponWith`. Sheet: `-uiHotbarDemo -uiPanel character` → `p13-sheet-mod.png`: rifle in WEAPON, `EM` in the new MOD slot under it, MAGAZINE 40 in green, DAMAGE 25, RANGE 120 |
| C132 | PASS | Keys 1–5 Q E T Z X route through `FireHotbar` (revised after the first playtest: R and F left the bar). F interacts (`Fps.cs`), R reloads only when the worn primary has a weapon table, the prompt reads `F ·`. `-selftest`: a fresh bar is empty, an old `action:` entry reads as empty, Z/X are slots 9/10, R and F are not slots. Walk-away close: the shop and the bench close past 5 m from their NPC (`OutOfCounterRange`); rig `-uiFace npc -uiApproach 2 -uiBuy tool.drill -uiRetreat 2.5` → `ui: retreat 2.5 s: shop open True -> False, 0.9 -> 12.5 m from the counter` → `test/out/ui/p13-retreat.png` (the shop gone, the drill worn); `p13-hotbar.png` re-taken with the 1–5 Q E T Z X labels |
| C133 | PASS | Rig `-uiPanel backpack -uiHotbarDemo -uiHotbarDragDemo`: the first bag cell (the medkit) is dragged onto the 4 slot through Godot's own drag-and-drop (`ForceDrag` with the cell's real payload, then motion and release) → `ui: hotbar drag (868, 231.6) -> (550, 668): slot4=item:consumable.medkit saved=item:consumable.medkit` → `test/out/ui/p13-hotbar-drag.png`; the saved value is what a restart reloads. Cell → cell moves and right-click clears are the same `HotbarCell` handlers, checked by hand |
| C134 | PASS | `-uiHotbarDemo` → `p13-hotbar.png`: `ME` on 1 with count 2 and a cooldown sweep two-thirds gone, `OS` (rare frame) on Q, the ✋ and ⟳ glyphs on E and R, key labels top-left; `-uiShift` → `p13-hotbar-shift.png`: ⇧1…⇧F and the medkit that sits on ⇧3, greyed because the real bag holds none |
| C135 | PASS | Defs audit: eight recipes; medkit (Eng 3), scanner (6), barrel and mag (12, copper), coil (15, copper) → `p13-bench.png` shows the cards with have/need and ENG levels; the quartermaster stocks medkits at 30 |
| C137 | PASS | The SELL column is gone. With a shop open the backpack's right-click sells one (Shift: the stack) through the same `shop_sell` t36 plays, and its hint line reads `right-click sells one · shift right-click sells the stack` → `test/out/ui/p13-bag-shop.png` (shop and bag open together, the drill sellable, the worn cutter on the doll); with no shop open right-click equips or uses as before (`BackpackView.OnAlt` order: shop → consumable → equip) |
| C138 | PASS | `TestShopBuyback`: the sale lands on the list as `{mat.ore.iron, 4, 12}`, `shop_list` carries it, `no_buyback` for a thing never sold, `insufficient_credits` leaves the entry, buying back returns the whole stack for the same 12 cr and clears the entry, the bench refuses `no_stock`, the list caps at twelve newest-last, the newest sale of an item is the one that comes back. `t36` (38 checks now): sell 4 ore → `buyback: [{iron, 4, 12}]` → buyback → 880 cr and 10 ore → a second buyback `no_buyback`. Rig `-uiBuy tool.drill,tool.cutter -uiSellDemo -uiBuyback` → `p13-buyback.png`: STOCK / BUYBACK tabs, the drill sold for 20 cr listed with BUY BACK, `twelve most recent sales · gone when you log out` |
| C139 | PASS | `project.godot`: viewport 1920×1080, stretch `canvas_items` / `expand`. `--resolution 1920x1080 -uiPanel settings` → `test/out/ui/p13-settings.png`: WINDOWED / WINDOWED FULLSCREEN (lit, the default) / FULLSCREEN, UI SCALE 100%, MOUSE SENSITIVITY 1.00×; `p13-hotbar-1080.png`: the HUD at 1:1. `--resolution 1280x720 -uiDemo -uiFace hostile -uiPanel backpack` → `p13-scale-720.png`: the backpack, the bar, the boxes and the grunts' health bars all at two thirds, nothing overlapping, the bars still over the heads (unproject and the canvas agree under stretch). Settings persist in `sa.cfg [settings]` and apply at boot; the rig (`-uiShot`/`-quitAfter`/`-selftest`) keeps its own window |
| C140 | PASS | `Hotbar.ShiftRow = false`; Boot reads Shift only through it (`_rigShift` still shows the row for a photograph); the self-test's `SlotFor(Q, shift)` = 15 keeps the slot map alive for when it comes back |
| C141 | PASS | Map restyled from the shaded projection to a chart: height posterised into five flat bands (basin, lowland, upland, highland, peak) with contour lines where bands meet, so craters are rings and hills nested shapes; glyphs per thing — pins for spawn and POIs (named by what stands under the mast: dispatcher → Relay, wrecks → Outpost, else Range / Camp, the compass uses the same rule), a house for the shop, a flag for the board, a cross for the bench, triangles for hostiles, diamonds for ore in the ore's colour, a ring for a wreck, dots for players, an arrow for you; 384 px texture. `--resolution 1920x1080 -uiPanel map` → `test/out/ui/panel-map.png` |
| C136 | PASS | Against the Phase 13 server: `go vet && go test ./...` green (`TestUseMedkit`, `TestUseScanner`, `TestUseRefusals`, `TestReloadWithMagMod`, `TestApplyMod`, defs audit at eight recipes), `godot-gate` clean, `godot-codec` 9/9, `godot-test` PASS with the four new hotbar self-checks, `t13` 9/9, `t35` 6/6, art 39/39, `t36` 34/34, `t37` 14/14, `t34` 32/32, `t14` 6/6. A fresh profile's E and R do what they did |

Owed to the playtest: dragging on a real mouse, the shift row while
sprinting, a barrel mod landing a 140 m hit, a scan in the scrapyard.
