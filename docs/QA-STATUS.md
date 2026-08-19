# M1 On-Foot — QA Status (stopping point)

Date: 2026-08-18. Build + integration are **COMPLETE and verified**. QA ran a full sweep (2 h 43 m) and is **paused here** for resumption. QA was read-only on product code (server/, client/, art/, deploy/, docs/ untouched); all new code is under `test/`. No commits; `SA_ALLOW_COMMIT` never set. **2026-08-19 update: C9 fixed and re-verified — PASS** (product fix in `server/internal/terrain/generate.go`, field re-captured, t9 green end-to-end); C2–C7/C10 remain open per the resume checklist.

## Build & integration (DONE, verified)
- **Server** (`server/`, Go, authoritative, 20 Hz): protocol (10 message types), cube-sphere terrain (6×65×65, seed 1337, generated once + pre-encoded + cached in `server/internal/server/server.go` `s.terrainF` — NOT regenerated per-connection), on-foot sim (GDD-verbatim + facing-hold), WS gateway, `cmd/server` + `dump` subcommand. `go build/vet/test ./...` green, gofmt clean.
- **Client** (`client/`, Three.js + TS strict, Vite): first-person renderer, terrain-from-wire, prediction + replay, mock mode, `tools/dump.ts` (headless no-DOM sim → JSONL, via `npm run sim:dump`). `?corrupt` dev override. Same-origin `/ws`. tsc strict (3 configs) + `npm run build` green.
- **Art** (`art/`): char.player + rock GLBs (deterministic).
- **Infra** (`deploy/`, kind): `make up` clean; both pods 1/1; `/healthz` ok; client page on **:3000**; handshake green through **nginx same-origin `/ws` (:3000)** AND **direct WS (:18080)**; terrain byte-identical across both paths + across server restarts.

## QA verdicts (final)
| # | Criterion | Verdict |
|---|-----------|---------|
| C1 | `make up` clean, healthz + page + WS reachable | **PASS** |
| C2 | Two clients see each other ≤1 s, on terrain | NOT VERIFIED (harness ready) |
| C3 | Server authority (forced state corrected ≤1 tick) | NOT VERIFIED (harness ready) |
| C4 | Connection-loss despawn ≤10 s | NOT VERIFIED (`.kill()` ready) |
| C5 | Movement conformance (dump diff ≤5%) | NOT VERIFIED — **furthest along** (scripts v1: level/slope/slide) |
| C6 | Prediction quality (100 ms, p95 <0.25 m) | NOT VERIFIED (method locked) |
| C7 | Sustained 20 Hz + p95 <50 ms + 60 fps/10 players | NOT VERIFIED (method locked) |
| C8 | Build gate (go/tsc/npm clean; `make down` clean) | **PASS** |
| C9 | Terrain walkable/varied/navigable | **PASS** (2026-08-19; bugs + resolution below) |
| C10 | Circumnavigation (great-circle lap, all 6 faces) | NOT VERIFIED (method locked; see design note) |

### C9 — the two product bugs (in `server/internal/terrain/generate.go`)
**Bug 1 — closure capture of shared loop var `d` by reference** (`makeLandmarks`, ~line 268): the spire/mesa/greatcrater `apply` closures capture the shared loop var `d`, so all three apply at the **notch** direction (last `d`), not their own. Proof from the captured field: global min 132.21 m sits at the notch dir `[0,0,-1]`, not at greatcrater's placed dir `[0.982,-0.19,-0.013]` (no bowl there: 150.0/153.6); spire cone max 150.0 (spec 188); mesa 151.47 (spec 172). twinpeaks + beacon use fresh vars → correct.
**Fix:** capture per-iteration locals (`dd := d` before each closure) or pass dir as a parameter to a landmark factory, so each of the 6 features applies at its own placed direction.

**Bug 2 — global 150 m floor** (independent of Bug 1): `spireProfile(peak, baseAng, theta)` returns the nominal 150.0 for `theta >= baseAng`, and the spire/twinpeaks closures do `math.Max(cur, spireProfile(...))` with **no footprint early-return** (the other four landmarks have `if theta >= … { return cur }`). So every point is floored ≥150 m: 50.52% of all 25,350 nodes sit at exactly 150.0 m, the GDD ±14 m lowland relief is erased, 4/11 craters are erased/unusable (3 by the floor; crater1 sits 22.8 m from spawn — `placeCraters` has **no spawn-zone rejection**, a secondary flaw), and landmark visibility drops to 11.4% vs the ≥60% criterion (GDD target 66%; only 3 of 6 targets visible).
**Fix:** `spireProfile` returns the underlying `cur` for `theta >= baseAng`; add the footprint early-return to the spire/twinpeaks closures. (GDD step 6, lines 645-648: landmarks state absolute radii and blend via `max()` for rising / `min()` for excavated shapes — they must not floor the underlying terrain.)

### C9 — resolution (2026-08-19)
Both bugs fixed in `server/internal/terrain/generate.go` (netcode): each landmark closure now holds its own direction, and the spire/twinpeaks profiles no longer floor the base terrain; crater placement also gained spawn-zone rejection. The rewrite additionally re-tuned landmark placement to maximize the GDD's 6-cap visibility union (overlap is the cost — the six caps sum to ~66.5% of the sphere at nominal heights), added a spawn-suppression ramp (45.2–60.2 m from spawn) that keeps the beacon band flat, and moved the beacon to 54 m from spawn (GDD 50 m + the band-slope offset; recorded deviation). `server/internal/terrain/` and `test/tools/terrain-probe/terrain/` remain byte-identical (sha256-verified, `test/out/probe-src-*.sha`).
Re-verified end-to-end: live field re-captured (`test/lib/capture-terrain.mjs` against `go run ./cmd/server -listen 127.0.0.1:18081`, world sha256_16 `c80269c44a757a8f`) → `python3 test/t9-terrain.py > test/out/t9-raw.json` → **OVERALL: PASS**, all 8 blocks:
- decoder cross-check 0.0 m; radii [130.44, 187.95] ⊂ [124, 190]; walkable 94.27% (≥70%); spawn disc dev 0.0 m (≤0.5); outward slope ≤1.45°; craters 11 placed / 11 confirmed (6–12), 0 hidden from spawn; landmarks all at spec (spire 187.95, twin 179.92, mesa 172.00, greatcrater rim 169.99 / floor 132.30, notch 169.74 / 138.00, beacon 164.94), min separation 69.9° ≥ 60°; **visibility 0.6604 ≥ 0.60** (GDD 6-cap model, Fibonacci sphere N=100 000; matches the in-repo Go diagnostic 0.6606 at N=200 000).
Notes: the GDD cap half-angle `acos(R/(R+H)) + acos(R/(R+eye))` uses H = r − R (r = absolute radius of the feature); bowl features (greatcrater, notch) take their cap at the RIM (ring max at 60 m), not the ring mean. **Cluster redeployed 2026-08-19** (`make up` rebuilt images, but the unchanged `:latest` tag left the old pods running — `kubectl rollout restart` fixed it): server pod now on the fixed image, terrain payload over `ws://127.0.0.1:18080/ws` verified sha256_16 `c80269c44a757a8f` (re-captured into `test/out/world-seed1337.json` from the cluster); client pod serves a JS bundle byte-identical to the current-source docker build. Port-forwards died with the old pods; both are running again under hub supervision (`sa-pf-client` :3000, `sa-pf-server` :18080, restart-on-failure) and still match the Makefile's `pkill` patterns for `make up`/`make down`.

## Known issues to address on resume (not product code)
1. **Doc drift — `docs/ARCHITECTURE.md`** deployment section is stale: claims no kind in M1; actual is a kind cluster `space-adventure` + nginx `/ws` proxy + host port-forwards 3000:80 / 18080:8080.
2. **Doc drift — `docs/ROADMAP.md`** criterion 5 names the client entry `tools/dump.mjs`; actual is `client/tools/dump.ts` via `npm run sim:dump`.
3. **Doc drift — `client/tools/dump.ts`** header references a nonexistent `server terrain --seed` subcommand (client/ path — fix via frontend agent).
4. **C10 design issue — ROADMAP criterion 10** says "two clients standing on opposite sides of the world each render the other upright." At r=150 with 1.7 m eye height the horizon is ≈23.7 m, so two 180°-apart clients **cannot see each other** — that clause is physically impossible as written. The honest, verifiable part of C10 is: scripted full-lap walk (all 6 face crossings, endpoint <1 m, grounded throughout) + wire-quat upright invariant (the anti-hardcoded-+Y probe) + near-horizon two-client viewpoints. Recommend rewording the ROADMAP clause on resume.

## Test harness + evidence (all under `test/`)
- **Libs (built + verified green):** `test/lib/ws.mjs` (RFC-6455 raw-TCP WS client: sendBinary/sendPing/sendClose/**kill**, ns timestamps), `test/lib/wire.mjs` (independent 10-type codec from PROTOCOL.md only), `test/lib/field.mjs` (independent GDD terrain sampling: faceOf/faceUV/sampleRadius/surfaceNormal/slopeDeg), `test/lib/capture-terrain.mjs` (live terrain → world JSON + raw bin).
- **C5 (furthest):** `test/t5/explore.mjs` (located all 6 required terrains), `test/t5/prerun.ts` (route pre-runner, tsx, emits both script variants), `test/t5/legs.json`, `test/t5/script-go.jsonl` + `script-ts.jsonl` (v1: level/slope/slide, 399 ticks; **ledge/jump/seam legs pending**).
- **C9:** `test/t9-terrain.py` (deterministic field audit), `test/tools/terrain-probe/` (byte-identical copy of `server/internal/terrain/*.go` + probe shim → `test/out/terrain-probe`), evidence `test/out/t9-raw.json` + `t9-probe-1337.json`.
- **Captured world:** `test/out/world-seed1337.json` (152 KB, u16 wire field, sha256_16 `c80269c44a757a8f`, re-captured 2026-08-19 post-fix) + `test/out/terrain-raw-….bin`; `test/out/server-dump` (9.4 MB, built from `server/cmd/server` for C5); `test/out/c1-*.log`.
- **Caveat (C5):** Go dump uses the f64 field; TS dump uses the u16 wire field (~15 µm quantization — negligible under the 5% bar).

## Resume checklist (in suggested order)
1. ~~**Fix C9** (GoServer/netcode, `server/internal/terrain/generate.go`)~~ — **DONE 2026-08-19** (see "C9 — resolution" above; `go build/vet/test ./...` green in `server/` and `test/tools/terrain-probe/`).
2. ~~**Re-run `make up` → re-verify C9**~~ — **DONE 2026-08-19** (field re-captured, then re-captured again from the redeployed cluster on :18080 — same sha256_16 `c80269c44a757a8f`). Cluster redeployed same day; see C9 resolution note.
3. **C5 (biggest, furthest):** add `aim` leg to `prerun.ts` (steer at dir `[0.322743,0.946065,-0.028236]`); legs v2 = ledge (assert `stepUps>=1`) + jump (1-tick, assert `airTicks>0`) + seam (assert face 2→0 crossing); then `test/out/server-dump -inputs test/t5/script-go.jsonl -seed 1337 > test/t5/dump-server.jsonl` && `(cd client && npm run sim:dump ../test/t5/script-ts.jsonl ../test/t5/dump-client.jsonl --world ../test/out/world-seed1337.json)`; diff tick-aligned (Go 0-based vs TS 1-based); PASS = max per-tick |dPos| and |dVel| within 5% + grounded agreement.
4. **C2** `test/t2-two-client.mjs` (A via :3000/ws, B +500 ms; B-burst contains A's spawn; A gets B's spawn ≤1 s; on-surface eps 0.05 m).
5. **C4** `test/t4-despawn.mjs` (B `kill()` hard TCP; A records kill→DESPAWN ms ≤10 s + mechanism).
6. **C3** `test/t3-authority.mjs` (tsx; real Predictor vs live server; corrupt stateRef +4 m tangent; next snapshot snaps to <1e-3 m in one step).
7. **C7** `test/t7-sustain.mjs` (11 clients, 60 s; tick continuity + inter-arrival p50/p95; epoch-pinned one-way p95 <50 ms, ±~2 ms uncertainty documented); 60 fps via browser tool + rAF deltas.
8. **C6** `test/lib/proxy.mjs` (raw-TCP, 50 ms/direction) + `test/t6-prediction.mjs` (sprint/reversal/jump-onto-slope; deferred-match p95 <0.25 m; snap-back = along-track delta <−0.15 m, count 0).
9. **C10** extend `prerun.ts` with a lap mode (hold one direction ~942 m); assert wire-quat upright invariant, 6 face crossings, endpoint <1 m, grounded throughout; browser: near-horizon two-client viewpoints + full-lap walk.
10. **Fix the 3 doc-drift items + reword the C10 ROADMAP clause** (docs/ = main; `client/tools/dump.ts` header = frontend).
11. All 10 green → **M1 done**.

## Notes
- **No commits** (pre-commit guardrail blocks them; `SA_ALLOW_COMMIT` not set). All module work uncommitted on `main` (HEAD `c4708b6`).
- Host **:8080** is held by an unrelated process (`llama-server`); the project port map is 3000/18080 only (both green) — the server runs **pod-internal** on :8080 (different netns).
- Conformance reference = ThreeClient's sim; GoServer must match pos/vel/grounded.
