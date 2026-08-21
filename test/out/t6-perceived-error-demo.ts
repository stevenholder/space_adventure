/**
 * C6 product-gap evidence — perceived (rendered) error of the shipped
 * client during sustained sprint at 100 ms injected latency.
 *
 * Offline, deterministic (fixed sim clock, no Date.now), real client code:
 *   - sim:            client/src/sim (step, spawnState, spawnLook, TICK_DT)
 *   - prediction:     client/src/net/predictor.ts (predict/reconcile/renderState)
 *   - terrain:        client/src/mock/mock.ts MockServer().terrain (flat spawn
 *                     plain, wire-quantized radii — the exact struct the sim
 *                     consumes; spawn matches the net path's spawnState)
 *
 * Faithful server model (PROTOCOL.md:87-93 "snapshot with ack_seq=M carries
 * the state AFTER input M was applied"; server steps the world every tick
 * with the current command state — predictor.ts:19-21):
 *   - 20 Hz boundaries B_m = m*50 ms
 *   - at B_m: apply the newest input that arrived since B_{m-1}
 *     (arrival = send + 50 ms one-way), else hold the previous command;
 *     step the world one tick; snapshot {tick:m, ack, W_m} sent at
 *     B_m + delta (generation skew), arrives at B_m + delta + 50
 *   - ack advances only when a new input is first applied
 *   - entity pos/vel are float32-quantized (wire)
 *
 * Client model (shipped main.ts / predictor.ts semantics):
 *   - 50 ms sim loop on grid phi + k*50 (phi = free-running loop phase);
 *     at each step the input is "sent" and predictor.predict(input,k,t_k)
 *   - modes:
 *       shipped-live         : NO reconcile at all — the deployed bundle does
 *                              not wire onSnapshot in startLive (main.ts:149-172;
 *                              bundle: onSnapshot x1 = dispatch site only)
 *       reconcile-at-arrival : predictor.reconcile at snapshot arrival time
 *                              (the intended wiring, main.ts:110-115)
 *       phase-locked-fix     : phi=0 sim grid + reconcile anchored at the
 *                              server boundary (nowMs = arrival - delta)
 *
 * Metric: e(t) = |renderState(t).pos - truth(t)| at 1 kHz for
 * t in [2000, 8000] ms (steady sprint); truth(t) = W_{floor(t/50)} (the
 * server steps discretely at 20 Hz). Snap-back = rendered along-track
 * delta < -5 mm over a 0.5 ms sample.
 *
 * Self-validation (must reproduce the t6 live evidence before the
 * perceived-error numbers are used):
 *   - dist(P_k frozen at send, snap(ack=k).pos)  ~ 0.375 m   (t6 original metric)
 *   - dist(post @W_k, snap(ack=k+1).pos)         ~ 1e-5 m    (STEP A corrected pairing)
 *   - |post - pre| at reconcile                   ~ 1e-5 m   (t6 reconD p95 2.5e-06)
 */
import {
  ACTION,
  TICK_DT,
  sanitizeLook,
  spawnLook,
  spawnState,
  step,
  type Input,
  type State,
  type Terrain,
  type Vec3,
} from '../../client/src/sim/index.js'
import { Predictor } from '../../client/src/net/predictor.js'
import { MockServer } from '../../client/src/mock/mock.js'

// ------------------------------------------------------------------- config
const terrain: Terrain = new MockServer().terrain
const TICK_MS = 50
const DUR_MS = 8000
const SKIP_MS = 2000 // warm-up: spawn, pipeline fill, accel ramp (sprint v in 0.15 s)
const SAMPLE_MS = 0.5
const ONEWAY_MS = 50
const SPARKBACK_EPS = -0.005 // m per 0.5 ms sample (max forward = 7.5 m/s * 0.5 ms = 3.75 mm)

const f32 = (x: number): number => Math.fround(x)
const v3 = (a: { x: number; y: number; z: number }): [number, number, number] => [a.x, a.y, a.z]
const dist3 = (a: [number, number, number], b: [number, number, number]): number =>
  Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])
const dot3 = (a: [number, number, number], b: [number, number, number]): number =>
  a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
function norm3(a: [number, number, number]): [number, number, number] | null {
  const n = Math.hypot(a[0], a[1], a[2])
  return n > 1e-9 ? [a[0] / n, a[1] / n, a[2] / n] : null
}
function pct(sorted: number[], q: number): number | null {
  if (!sorted.length) return null
  const i = (sorted.length - 1) * q
  const lo = Math.floor(i)
  const hi = Math.ceil(i)
  return lo === hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (i - lo)
}

const SPRINT: Input = { moveX: 0, moveY: 1, lookDir: spawnLook(), actionMask: ACTION.SPRINT }
const IDLE: Input = { moveX: 0, moveY: 0, lookDir: spawnLook(), actionMask: 0 }

// ------------------------------------------------------- faithful server
interface ServerSnap {
  tick: number
  ack: number
  arriveMs: number
  pos: [number, number, number]
  vel: [number, number, number]
}
interface ServerModel {
  snaps: ServerSnap[]
  truth: (t: number) => [number, number, number]
  tangent: (t: number) => [number, number, number] | null
}
function buildServer(phi: number, delta: number): ServerModel {
  const snaps: ServerSnap[] = []
  let st: State = spawnState(terrain)
  let look = spawnLook()
  let cmd = IDLE
  let ack = 0
  const nBound = Math.floor(DUR_MS / TICK_MS) + 2
  const W: [number, number, number][] = []
  const prevPos: [number, number, number][] = []
  for (let m = 0; m <= nBound; m++) {
    const B = m * TICK_MS
    const kmax = Math.floor((B - ONEWAY_MS - phi) / TICK_MS) // newest k with send+50 <= B
    if (kmax >= 0 && kmax >= ack) {
      cmd = SPRINT
      ack = Math.max(ack, kmax)
    }
    st = step(st, cmd, terrain, TICK_DT, look)
    look = sanitizeLook(cmd, look)
    prevPos.push(v3(st.pos))
    W.push(v3(st.pos))
    if (m > 0) snaps.push({
      tick: m,
      ack,
      arriveMs: B + delta + ONEWAY_MS,
      pos: [f32(st.pos.x), f32(st.pos.y), f32(st.pos.z)],
      vel: [f32(st.vel.x), f32(st.vel.y), f32(st.vel.z)],
    })
  }
  return {
    snaps,
    truth: (t) => W[Math.min(Math.max(Math.floor(t / TICK_MS), 0), W.length - 1)],
    tangent: (t) => {
      const m = Math.min(Math.max(Math.floor(t / TICK_MS), 1), W.length - 1)
      return norm3([W[m][0] - W[m - 1][0], W[m][1] - W[m - 1][1], W[m][2] - W[m - 1][2]])
    },
  }
}

// ------------------------------------------------------------- one client
type Mode = 'shipped-live' | 'reconcile-at-arrival' | 'phase-locked-fix'
interface RunResult {
  mode: Mode
  phi: number
  delta: number
  nSamples: number
  err: { p50: number | null; p95: number | null; p99: number | null; max: number | null; mean: number | null }
  snapbacks: number
  validation: {
    sendVsAuth: { p95: number | null; max: number | null } // |P_k@send - snap(ack=k).pos|
    postVsNextAuth: { p95: number | null; max: number | null } // |post@W_k - snap(ack=k+1).pos|
    reconDelta: { p95: number | null; max: number | null } // |post - pre|
  } | null
}
function runClient(mode: Mode, phi: number, delta: number): RunResult {
  const server = buildServer(phi, delta)
  const pred = new Predictor()
  pred.seed(spawnState(terrain), terrain, 0)

  // precomputed event grids
  const nSteps = Math.floor((DUR_MS - phi) / TICK_MS) + 1
  const stepAt = (k: number): number => phi + k * TICK_MS
  const sends = new Map<number, [number, number, number]>() // seq -> P_k frozen at send
  const posts = new Map<number, [number, number, number]>() // ack -> post-reconcile
  const reconDeltas: number[] = []
  const sendVsAuth: number[] = []
  const postVsNextAuth: number[] = []

  let prevRender: [number, number, number] | null = null
  const errs: number[] = []
  let snapbacks = 0

  // walk a 0.5 ms grid; events applied when their time is reached
  let si = 0 // next client step index
  let ai = 0 // next snapshot index
  for (let t = 0; t <= DUR_MS; t += SAMPLE_MS) {
    while (si < nSteps && stepAt(si) <= t) {
      const k = si++
      if (mode !== 'shipped-live' || k === 0) {
        // shipped-live also predicts (the client sim runs regardless)
      }
      const st = pred.predict(SPRINT, k, stepAt(k))
      sends.set(k, v3(st.pos))
    }
    while (ai < server.snaps.length && server.snaps[ai].arriveMs <= t) {
      const s = server.snaps[ai++]
      if (mode === 'shipped-live') continue // deployed bundle drops the snapshot
      const pre = v3(pred.stateRef!.pos)
      const nowMs = mode === 'phase-locked-fix' ? s.arriveMs - delta : s.arriveMs
      const ok = pred.reconcile(
        { id: 1, pos: s.pos, quat: [1, 0, 0, 0], vel: s.vel },
        s.ack,
        nowMs,
      )
      if (ok) {
        const post = v3(pred.stateRef!.pos)
        posts.set(s.ack, post)
        reconDeltas.push(dist3(pre, post))
        const pm = sends.get(s.ack)
        if (pm) sendVsAuth.push(dist3(pm, s.pos))
      }
    }
    // validation pairs (post@ack=k vs next snap ack=k+1) resolved after all posts exist:
    if (t >= SKIP_MS && t <= DUR_MS) {
      const rs = pred.renderState(t)
      if (rs) {
        const r: [number, number, number] = [rs.pos.x, rs.pos.y, rs.pos.z]
        errs.push(dist3(r, server.truth(t)))
        if (prevRender) {
          const tang = server.tangent(t)
          if (tang && dot3([r[0] - prevRender[0], r[1] - prevRender[1], r[2] - prevRender[2]], tang) < SPARKBACK_EPS)
            snapbacks++
        }
        prevRender = r
      }
    }
  }
  for (let k = 0; k < server.snaps.length; k++) {
    const cur = posts.get(server.snaps[k].ack)
    const nxt = posts.get(server.snaps[k + 1]?.ack)
    // post@W_k vs the NEXT snapshot's auth (ack = k+1 in steady state):
    if (cur) {
      const nextSnap = server.snaps[k + 1]
      if (nextSnap && nextSnap.ack === server.snaps[k].ack + 1)
        postVsNextAuth.push(dist3(cur, nextSnap.pos))
    }
  }
  const sorted = [...errs].sort((a, b) => a - b)
  const sva = [...sendVsAuth].sort((a, b) => a - b)
  const pvna = [...postVsNextAuth].sort((a, b) => a - b)
  const rd = [...reconDeltas].sort((a, b) => a - b)
  return {
    mode,
    phi,
    delta,
    nSamples: errs.length,
    err: {
      p50: pct(sorted, 0.5),
      p95: pct(sorted, 0.95),
      p99: pct(sorted, 0.99),
      max: sorted.length ? sorted[sorted.length - 1] : null,
      mean: errs.length ? errs.reduce((a, b) => a + b, 0) / errs.length : null,
    },
    snapbacks,
    validation:
      mode === 'shipped-live'
        ? null
        : {
            sendVsAuth: { p95: pct(sva, 0.95), max: sva.length ? sva[sva.length - 1] : null },
            postVsNextAuth: { p95: pct(pvna, 0.95), max: pvna.length ? pvna[pvna.length - 1] : null },
            reconDelta: { p95: pct(rd, 0.95), max: rd.length ? rd[rd.length - 1] : null },
          },
  }
}

// --------------------------------------------------------------------- main
const runs: RunResult[] = []
// G1 evidence: the deployed live client (no reconcile at all), t6-equivalent phase
runs.push(runClient('shipped-live', 25, 20))
// G2 evidence: intended wiring (reconcile at arrival), free-running phase sweep
for (const delta of [0, 10, 20]) for (const phi of [0, 5, 10, 15, 20, 25, 30, 35, 40, 45])
  runs.push(runClient('reconcile-at-arrival', phi, delta))
// the fix: phase-locked sim grid + boundary-anchored reconcile
runs.push(runClient('phase-locked-fix', 0, 0))

const t6Ref = {
  originalMetric: { errP50: 0.3749987, errP95: 0.3754437, errMax: 0.7499982 }, // frozen-at-send P_M vs snap(ack M)
  correctedPairing: { p50: 3.66e-6, p95: 7.76e-6, max: 0.375, snapbacks: 0 }, // post@W_M vs snap(ack M+1)
  reconD: { p95: 2.49e-6 },
  source: 'test/out/t6-corrected-pairing.json + test/out/t6-run-2026-08-20T06-32-23-124Z.json',
}
const out = {
  step: 'C6 product-gap evidence — perceived (rendered) error, offline demo with real Predictor/renderState',
  metric:
    'e(t) = |renderState(t).pos - server truth(t)|, 1 kHz, t in [2000,8000] ms, sustained sprint ' +
    '(ACTION.SPRINT, forward), mock-terrain flat spawn plain; truth = W_floor(t/50) (20 Hz step function); ' +
    'server: 100 ms pipeline (50 ms one-way), generation skew delta, float32 wire',
  thresholdM: 0.25,
  t6LiveEvidence: t6Ref,
  runs: runs.map((r) => ({
    ...r,
    err: {
      p50: r.err.p50?.toExponential(3) ?? null,
      p95: r.err.p95?.toExponential(3) ?? null,
      p99: r.err.p99?.toExponential(3) ?? null,
      max: r.err.max?.toExponential(3) ?? null,
      mean: r.err.mean?.toExponential(3) ?? null,
    },
    validation: r.validation
      ? {
          sendVsAuth: { p95: r.validation.sendVsAuth.p95?.toExponential(3), max: r.validation.sendVsAuth.max?.toExponential(3) },
          postVsNextAuth: { p95: r.validation.postVsNextAuth.p95?.toExponential(3), max: r.validation.postVsNextAuth.max?.toExponential(3) },
          reconDelta: { p95: r.validation.reconDelta.p95?.toExponential(3), max: r.validation.reconDelta.max?.toExponential(3) },
        }
      : null,
  })),
}
import { writeFileSync } from 'node:fs'
writeFileSync('test/out/t6-perceived-error.json', JSON.stringify(out, null, 1))

// compact console table
for (const r of runs) {
  const e = (x: number | null) => (x === null ? 'n/a' : x.toExponential(2))
  console.log(
    `${r.mode.padEnd(22)} phi=${String(r.phi).padStart(2)}ms delta=${String(r.delta).padStart(2)}ms  ` +
      `p50=${e(r.err.p50)} p95=${e(r.err.p95)} p99=${e(r.err.p99)} max=${e(r.err.max)} snapbacks=${r.snapbacks}`,
  )
  if (r.validation) {
    const v = r.validation
    console.log(
      `                     validation: sendVsAuth p95=${e(v.sendVsAuth.p95)} max=${e(v.sendVsAuth.max)} | ` +
        `postVsNextAuth p95=${e(v.postVsNextAuth.p95)} max=${e(v.postVsNextAuth.max)} | ` +
        `reconDelta p95=${e(v.reconDelta.p95)}`,
    )
  }
}
console.log('wrote test/out/t6-perceived-error.json')