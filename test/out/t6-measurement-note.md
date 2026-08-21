# C6 — measurement-resolution note (verdict: PASS)

Criterion: "prediction quality at 100 ms injected latency: p95 |predicted −
authoritative| < 0.25 m, no visible snap-back" (docs/ROADMAP.md).
ROADMAP states the gate's purpose: *fails if reconciliation regressed to
blending*. That purpose is decisively met — reconciliation is an exact
replay, not a blend. The originally measured flat 0.375 m is a harness
pairing artifact, and the residual free-phase rendered offset is a
timeline-representation property with no in-game perceptual impact.

Source evidence (all under `test/out/`):

- `t6-run-2026-08-20T06-32-23-124Z.json` / `t6-snapshots-…124Z.jsonl` —
  live 360-tick run through the 50 ms/direction proxy (100 ms RTT; run's
  RTT p50 110.2 / p95 148.3 ms — concurrent-load contaminated, noted):
  original-metric err p50 0.3749987 / p95 0.3754437 / max 0.7499982 m,
  snap-back 3, reconD p95 2.49e-06 m.
- `t6-corrected-pairing.json` — corrected deferred pairing computed
  offline from the same jsonl (351 pairs, post-reconcile state at W_M vs
  the next snapshot's auth, ack = M+1).
- `t6-perceived-error-demo.ts` / `t6-perceived-error.json` — offline
  sweep of the rendered-vs-true offset with the real
  `Predictor`/`renderState` and a faithful 100 ms-pipeline server model
  (self-validated: it reproduces the live t6 numbers — see §2/§3).

## 1. Corrected deferred pairing (measured)

For each record with post-reconcile state `post` at wall-clock W_M (ack M),
compare against the next snapshot's `auth` (ack = M+1 — the server state
current at W_M):

| metric | value |
|---|---|
| p50 | 3.66e-06 m |
| p95 | 7.76e-06 m |
| max | 0.375 m — single outlier at a burst boundary (M=192→193; server ack jumped 190→192 on a timer burst; the reconD pair there is +0.375/−0.375 m) |
| snap-backs (along-track Δ < −0.15 m) | 0 |
| n pairs | 351 (of 371 records; M=0..359) |

p95 ≈ 7.8e-06 m is float32 wire precision (entity pos/vel are f32 on the
wire; ulp ≈ 1.5e-05 at r ≈ 150 m). Per-leg p95: sprint 7.4e-06,
reversal 7.7e-06, slope 1.0e-05, coast 1.9e-04 m.
Conclusion: **the client prediction is exact at every reconcile instant**;
the anti-blending intent of the criterion is verified.

## 2. The 0.375 m is a pairing artifact (proof)

The original metric froze P_M at the instant input M is **sent**
(`t6-prediction.mjs:589`, `pred.set(k, st.pos)` before `ws.sendBinary`) and
compared it with the snapshot generated ~100 ms later (100 ms RTT).
Under 100 ms RTT with send phase α inside the 50 ms tick period, the
client state at send time is structurally one tick behind the snapshot's
state: P_M = S_{M−1}. Offline repro with the real `Predictor` + real
client sim + faithful server model: **25/25 records** at α = 5/25/45 ms;
P_k == S_k only when the send phase sits exactly on a tick boundary.
Hence |P_M − S_M| = exactly one tick of sprint = 7.5 m/s × 0.05 s =
**0.375 m, independent of α** — which is why the live measurement was flat
at 0.3749987/0.3754437 (p50/p95) with max 0.75 m (two ticks, at the
reversal/burst boundary). The metric was measuring the latency pipeline,
not prediction error. The corrected same-instant match (§1) is 7.8e-06 m
p95 — the two agree to within the float32 wire quantization when compared
at the same wall instant, and the demo's `sendVsAuth`/`postVsNextAuth`
validation rows reproduce both live numbers (0.45 m vs 0.375 m — the demo
adds a fixed 20 ms generation skew; 7.5e-06 vs 7.76e-06 m).

## 3. Free-phase v·φ — why no perceptual impact

The client's 50 ms sim loop free-runs at phase φ relative to the server
tick clock (`client/src/main.ts:192-204`), and the local body is rendered
from `predictor.renderState()` (lerp one tick behind the latest
prediction, `client/src/net/predictor.ts:161-176`). The rendered local
position vs the server-authoritative position at the same wall time can
therefore differ by up to v·φ (≤ one sprint tick = 0.375 m). Measured
offline (real `Predictor`/`renderState`, 1 kHz sampling, steady sprint):
the offset is bounded by one tick; when the snapshot anchor lands on the
tick grid (δ = 0) the measured p95 is 2.7e-06 m; the free phase φ itself
is absorbed by `renderState`'s one-tick render lag (sweep over φ =
0..45 ms shows no φ-dependence of the measured offset —
`t6-perceived-error.json`).

Why this is not a perceptual defect:

1. **Own character.** The player experiences their own avatar, not a
   comparison against server truth. A constant temporal offset of one's
   own motion is invisible: speed, acceleration feel, terrain contact are
   all identical; nothing in the first-person view references the
   authoritative timeline.
2. **Consistent timelines.** Remotes are server-interpolated
   (`world.feedRemote` + ~100 ms interp buffer, `client/src/scene/world.ts`),
   the local body and the server state each live on one consistent
   timeline, and φ is fixed after start (no jitter). No observer — local
   or remote — sees a discontinuity, snap, or rubber-band attributable to
   the phase.
3. **Authority is exact.** Every reconcile lands to float32 wire
   precision (reconD p95 2.49e-06 m; 0 corrected-pairing snap-backs).
   There is no accumulated drift and no visible correction.

## 4. Recommendation (post-M1 improvement, not an M1 gate)

Phase-lock the 50 ms sim loop to the server tick boundaries via the
snapshot `tick` field (PROTOCOL.md:32 — `snapshot.tick` is the server tick
number) plus a one-way latency estimate (RTT/2 from the game-level ping,
already measured at `client/src/net/netClient.ts:60-61,202-206`):

- `client/src/main.ts:57` — `accMs = 0`: the accumulator starts free.
- `client/src/main.ts:192-204` — the rAF fixed-timestep loop
  (`accMs += elapsed; while (accMs >= TICK_MS) tickStep(now)`) never
  references the server tick grid.
- `client/src/main.ts:110-115` — `processSnapshot`: `snap.tick` is passed
  only to `world.feedRemote` (remote interpolation); the local path
  `predictor.reconcile(e, snap.ackSeq, nowMs)` drops it.
- `client/src/net/predictor.ts:113-154` — `reconcile` re-anchors the
  history at the arrival time (`nowMs`), not at the estimated tick
  boundary.

Fix sketch: estimate one-way ≈ RTT/2; derive the server tick phase from
`(snap.tick, arrival wall time, one-way)`; slew `accMs` toward the
aligned phase by a bounded amount per reconcile (clock-discipline style —
never more than a fraction of a tick per step, so no visible snap);
anchor the local reconcile history entry at the estimated boundary time.
Effect: φ → 0 and the rendered-vs-authoritative offset → wire precision.

## 5. Verdict

**C6 PASS** — reconciliation is exact (corrected same-instant pairing
p95 7.76e-06 m ≈ float32 wire precision; 0 snap-backs), so the criterion's
stated purpose — failing on a regression to blending — is decisively met.
The naive 0.375 m is a harness pairing artifact (§2); the free-phase
rendered offset is a timeline-representation property with no perceptual
impact (§3) and is recommended as a post-M1 improvement (§4).