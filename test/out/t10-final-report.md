# C10 Circumnavigation — FINAL VERDICT

**Verdict: PASS** (all clauses of docs/ROADMAP.md criterion 10, lines 75-88).

World: seed 1337, `test/out/world-seed1337.json` (terrain sha16 `c80269c44a757a8f`, verified
against both the captured world and the live server hello).

## Clause-by-clause evidence

| Clause (ROADMAP.md #10) | Result | Evidence |
|---|---|---|
| Closed circuiting loop, ≥ 4 great-circle legs | **PASS** | 4 great-circle legs + 2 geodesic joints + 1 zero-length turn joint (below) |
| Single great circle crosses only 4 faces; no generic six-face circle walkable (proven by exhaustive scan) | **CONFIRMED** | `test/out/t10-alpha-scan.jsonl` (216 coarse: 36 six-face candidates, NONE walkable, best 54.5° on Great Crater flanks; dense 0.25° escalation: 24/24 six-face candidates 53-73°; walkable six-face pockets over-length) |
| Crossing every cube face | **PASS** | faces [0,1,2,3,4,5] visited; 7 face crossings (sim + live agree) |
| Totalling ≥ 942 m (at least one full circuit) | **PASS** | design 1065.47 m; sim actual path 1157.59 m; live 237.2 s at 20 Hz |
| Arriving back within 1 m of start | **PASS** | sim 0.0095 m; live 0.1501 m |
| Stayed on the ground — no fall through | **PASS** | sim min h = −0.0000 m (float noise), grounded flag 4706/4735 ticks; live minH 0, maxH 1.3033 m (only during the 2 GDD-AIR scarp flights, below) |
| No seam hitch at face crossing or leg junction | **PASS** | live per-crossing dPos 0.227-0.257 m = one tick of normal travel (4.5/20 = 0.225 m); every delta > 0.375 m falls inside a flight window (`bigDInFlights` assert) |
| No accumulated drift off the surface | **PASS** | live 4717/4746 ticks within 0.05 m of surface (99.4%; remainder = flight hang time); endpoint returns to spawn within 0.15 m |
| Remote players render upright on the local terrain, checked at near-horizon two-client viewpoints | **PASS** | STEP 5 two-client check (below); anti-hardcoded-+Y: min up·spawn_up on far cap (face 3) = −0.9635 ≈ −1 (sim AND live); quat up·radial = 1.000 over all 4746 live snapshots |

## The loop (final design, `test/out/t10-lap-waypoints.json`, 779 waypoints)

Params: A1=30, Q2=50, P3 (colat 28°, az 198.5°), Q4=225.5, Q5=215.
Spawn at (0,150,0); up = normalize(pos) (radial gravity).

| Segment | Type | Length (flat) | maxSlope |
|---|---|---|---|
| leg1 | great circle, spawn → equator (az-30 meridian) | 252.68 m | 43.60° |
| j1 | equatorial geodesic, az 30 → 50 | 55.94 m | 40.83° |
| leg2 | great circle, equator az 50 → P3 (far pole region) | 301.21 m | 47.47° |
| j2 | zero-length tangent turn at P3 | 0 m | — |
| leg3 | great circle, P3 → equator az 225.5 | 174.99 m | 43.56° |
| j3 | equatorial geodesic, az 225.5 → 215 | 26.85 m | 47.54° |
| leg4 | great circle, equator az 215 → spawn | 245.65 m | 48.60° |
| **total** | | **1065.47 m** (≥ 942 m) | **48.92° max** (sim) — passes the 49.5° AND 50.0° gates |

Face crossings (s = arc distance): 2→0 @136.2 m, 0→5 @298.2 m, 5→3 @429.7 m, 3→1 @677.5 m,
1→4 @784.6 m, 4→1 @792.2 m, 1→2 @930.7 m.

Hazards avoided: Home Beacon flank (az 5°), az 162.5 staircase, Great Crater (az 340-20 far
side) — loop corridor chosen from `test/out/t10-corridors.jsonl` (360-meridian scan) and
`test/out/t10-scarp-scan.jsonl` / `t10-scarp-scan-fine.jsonl` (scarp-feature placement).

## STEP 1 — diagnosis of the zero-prefilter-pass scan

- 13,920 candidate loops (4-leg family, A1=40 fixed) at the prefilter: **0 passed** at both the
  49.5° and 50.0° gates (both-gate margins recorded per row in
  `test/out/t10-loop-scan.jsonl`; top-20 dump in `test/out/t10-diag-top20.jsonl`).
- **Failing leg: leg1 (the A1=40 spawn meridian), shared by every candidate**: maxSlope
  53.97-54.26° → margin to the 50.0° gate = 3.97°, to 49.5° = 4.47°; plus steepTouch
  violations (17-25 samples within 4° of >49.5° survey cells). Best overall candidate maxSlope
  = 53.97° (no candidate ≤ 50.0°; none ≤ 49.5°).
- Far-pole reachability (`test/out/t10-corridors.jsonl`, 360 meridians, colat 0→180): **no
  meridian is walkable end-to-end** at either gate (Great Crater + far steep ring). Partial
  corridors open at az 28-30 (spawn side ≤43.8°) and az 198-226 (far side 46.8-49.5°) — this is
  the corridor the final loop uses.
- **Feasibility verdict: feasible at BOTH 49.5° and 50.0°**, once the family is retuned off the
  az-40 meridian onto the az-30 corridor (final loop maxSlope 48.92°).

## STEP 3 — headless sim (`test/t10/sim-lap.ts`, DOM-free, world-seed1337.json)

`node --experimental-strip-types test/t10/sim-lap.ts` → `test/out/t10-sim-result.json`
(walk 4.5 m/s, 4735 ticks, 236.8 s; **all 10 asserts ok: true**):
endpoint 0.0095 m; no fall-through (min h −0.0000 m); 2 GDD-AIR flights; physics bound
maxD 0.4064 m (every delta > 0.375 m inside a flight window); 6 faces; 7 crossings;
upright-farcap min up·spawn = −0.9635 (face 3); upright-spawncap 0.9444; no stall;
maxSlope 48.92°.

**GDD-AIR flights (documented, not fall-through):** 2 brief airborne moments at scarp lips —
sim t2823-2837 @s636.9 (maxH 1.053 m, hang 0.75 s, slope 18°, 0.2 m step 0.222 m, 10 m relief
8.11 m) and t4077-4089 @s920.6 (maxH 1.246 m, hang 0.65 s, slope 15.6°, step 0.231 m, relief
5.88 m). Field-verified as scarp/step features (0.2 m step > 0.10 m), slope ≤ 50° — the GDD
AIR case; zero smooth-slope flights. Audit: `test/out/t10-flight-audit.json`
(verdict "QUALIFYING (GDD AIR cases only)").

## STEP 4 — live scripted walk (real server, WS 127.0.0.1:18080)

`node test/t10/live-lap.mjs` → `test/out/t10-live-result.json` (237.2 s, 4746 snapshots at
median 50.1 ms/tick = 20.0 Hz; **all 9 asserts ok: true**):
- endpoint 0.1501 m; faces [0..5]; 7 crossings; per-crossing dPos 0.227-0.257 m (no seam hitch)
- on-surface: 4717/4746 ticks within 0.05 m; maxH 1.3033 m, minH 0
- deltas: max 0.4064 m, p95 0.2967 m; all big deltas inside the 2 flight windows
- the same 2 GDD-AIR flights as the sim (server-authoritative: t130370-130384 @s635.6,
  t131618-131631 @s919.2; maxH 0.227/0.257 m in the server's higher-fidelity ground contact)
- upright: min up·spawn (face 3) = −0.9635; quat up·radial = 1.000 everywhere (incl. face 3)
- note: this is the POST-FIX run (glue-band walker) — an earlier 12:55 attempt crashed on a
  waypoint decode bug (`test/out/t10-live-run.log`, historical).

Speed/duration: walk 4.5 m/s (GDD walk speed; no sprint/jump), 237.2 s for 1157.6 m.

## STEP 5 — browser near-horizon two-client upright check

Two clients, near-horizon placement (180°-apart clients are mutually invisible — horizon at
r=150, 1.7 m eye = 22.65 m; the remote sits at 22.6 m, i.e. at the horizon):
- **Camera**: real browser page on http://127.0.0.1:3000 (headless Chromium, real WS via
  nginx same-origin /ws). Walked W+A diagonal 6.0 s @ 4.5 m/s (az-315 meridian, flat 1.4° cap)
  to P = (18.99, 147.58, 18.99), facing +X; then held still. HUD at capture:
  `speed 0.0 m/s, nearest 22.6 m, conn online` (22.6 m = the remote at Q; the spawn cluster is
  27.3 m).
- **Remote**: scripted WS client `test/t10/bs-walker.mjs` on 127.0.0.1:18080 (entity
  "QA-C10-bs-walker"), walked the 45.9 m geodesic spawn→Q = (41.42, 143.34, 18.44) (max slope
  20.1° at Q) at 4.5 m/s, 20 Hz inputs on the snapshot cadence; closed-loop stop at
  d(Q) < 0.6 m on the server-authoritative pos — arrived t = 10.5 s (dQ 0.06 m), final dQ
  0.09 m. maxDv per tick: 0.227 m walking (== 4.5/20, no >1-tick jump), 0.014 m holding.
- **Rendered orientation**: the wire quat up-axis = the radial at Q, measured 0.000° off
  (proper rotation; consistent with the full-lap live result quat up·radial = 1.000).
  Deviation from the local terrain normal = 20.31° = exactly the local slope — the GDD
  radial-up design (up = normalize(pos)), which the local player uses too
  (`client/src/scene/world.ts` setLocal: up = normalize(pos)); remote quats are applied
  directly through the ~100 ms interpolation buffer.
- **Visual evidence** (screenshots, `test/out/`): `t10-bs-a-view-b-1.webp` and
  `t10-bs-a-view-b-2.webp` (camera at P: remote "QA-C10-bs-walker" standing at Q at the 22.6 m
  horizon, name tag visible, no drift between frames) and `t10-bs-figure-zoom.webp` (5× zoom:
  upright box rig — torso above legs, feet on the 20° slope).
- Numbers: `test/out/t10-bs-result.json`; pair geometry `test/out/t10-bs-pair.json`; walker
  telemetry `test/out/t10-bs-walker.jsonl` / `t10-bs-walker-final.json`.

## Commands

```
node test/t10/diagnose.mjs                      # STEP 1a top-20 dump
node test/t10/loop-design.mjs scan              # STEP 1b both-gate re-scan (13,920 cands)
node test/t10/corridor-scan.mjs                 # STEP 1d far-pole reachability (360 meridians)
node --experimental-strip-types test/t10/sim-lap.ts   # STEP 3 headless sim
node test/t10/live-lap.mjs                      # STEP 4 live lap (WS :18080)
node test/t10/bs-walker.mjs 18                  # STEP 5 remote walker (WS :18080)
browser: goto http://127.0.0.1:3000/ ; keydown KeyW+KeyA 6.0 s ; keyup ; screenshots
```

## Caveats / product observations (no product changes made — report only)

1. **GDD-AIR scarp flights**: the integrator briefly leaves the surface (maxH 1.05-1.30 m,
   hang 0.65-0.75 s) at two scarp lips on this loop. Classified GDD AIR (step feature, slope
   ≤ 50°) by field audit; identical in client sim and authoritative server. If the criterion is
   read as "grounded flag on every tick", note the grounded flag was 4706/4735 (sim) and
   4717/4746 (live) — the 30 missing ticks are exactly these 2 flight windows.
2. **Radial-up rendering**: bodies stand along normalize(pos), not the terrain normal; on a
   20° slope the body is 20° off the surface. This is the GDD "up = normalize(pos)" design and
   is applied identically to local and remote bodies; the criterion's stated purpose (catch a
   hardcoded +Y) is decisively satisfied (up·spawn = −0.9635 at the antipodal cap, quat
   up·radial = 1.000 everywhere).
3. **Stale HUD in background tabs**: a browser tab left idle for minutes can hold a frozen
   HUD (last computed `nearest`) even with the WS live; re-running the walk on a fresh page
   fixed it. Not a product bug per se, but worth knowing when automating browser checks.

## Evidence index

- `test/out/t10-final-report.json` — machine-readable version of this report
- `test/out/t10-alpha-scan.jsonl` — single-circle infeasibility (coarse + dense)
- `test/out/t10-loop-scan.jsonl` — 16,020 survey + 13,920 candidate rows (both-gate margins)
- `test/out/t10-diag-top20.jsonl` — top-20 failing candidates (per-leg maxSlope + dr)
- `test/out/t10-corridors.jsonl` — 360-meridian reachability
- `test/out/t10-scarp-scan.jsonl`, `t10-scarp-scan-fine.jsonl` — scarp placement
- `test/out/t10-lap-waypoints.json` — final loop (779 waypoints, 1065.47 m)
- `test/out/t10-sim-result.json` — sim run (10/10 asserts)
- `test/out/t10-live-result.json` — live run (9/9 asserts, 4746 snapshots)
- `test/out/t10-flight-audit.json` — GDD-AIR flight classification
- `test/out/t10-bs-result.json` — STEP 5 two-client check
- `test/out/t10-bs-a-view-b-1.webp`, `t10-bs-a-view-b-2.webp`, `t10-bs-figure-zoom.webp` — screenshots