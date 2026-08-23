/**
 * C6 — prediction quality at 100 ms injected latency (docs/ROADMAP.md
 * criterion 6; QA-STATUS resume item 8, method locked).
 *
 * Topology:  harness --(test/lib/proxy.mjs, 50 ms/direction)--> ws://127.0.0.1:18080/ws
 *
 * Method (locked):
 *  - The harness drives the REAL client Predictor (client/src/net/predictor.ts,
 *    imported read-only; same bootstrap as client/tools/dump.ts: the DOM-free
 *    sim + Predictor from client/src). Terrain is decoded from the live wire
 *    `terrain` message with the client's own decodeTerrain; the Predictor is
 *    seeded with the client's spawnState(terrain), exactly as main.ts does.
 *  - THE GATE — same-instant pairing: the client's POST-reconcile state at
 *    ack M is its belief about "now"; the server's belief about that same
 *    "now" arrives in the NEXT snapshot, ack M+1. err = |post_M − auth_{M+1}|.
 *    That difference is real prediction error, and it is what separates exact
 *    replay (clears to float32 wire precision) from blending (leaves a
 *    persistent residual toward the stale anchor) — which is exactly what
 *    criterion 6 says it exists to catch.
 *  - Snap-back: along-track (auth_{M+1} − post_M) < −0.15 m, i.e. the server
 *    pulling the player BACKWARD along their own track. Forward differences
 *    are ordinary (the server is simply ahead); only backward is a visible
 *    lurch. Count must be 0.
 *  - Diagnostic, NOT a gate — cross-instant pairing |P_M − snapshot(ack M)|,
 *    where P_M is the client state when input M was SENT and the snapshot is
 *    the server state ~RTT later. Those are two different instants, so under
 *    sustained sprint it reads a structural one tick of motion
 *    (7.5 m/s × 0.05 s = 0.375 m) regardless of client quality — it did not
 *    move between a 102 ms and a 148 ms RTT run, which is the tell. The
 *    strict same-wall-time metric it reaches for is worse still: the server's
 *    authoritative state is a 20 Hz step function lagging continuous motion
 *    by δ ~ U(0, 50 ms), giving every possible client, including a perfect
 *    one, p95 = 7.5 × 0.05 × 0.95 = 0.356 m > the 0.25 m budget. It measures
 *    the server's tick rate, not prediction. Kept as a reported number so a
 *    change in it stays visible; never used for the verdict.
 *    Full adjudication: test/out/t6-c6-measurement-resolution.md
 *  - Phase pinning: input arrivals must land mid server-tick-period so the
 *    chain identity above holds under timer jitter (an arrival gap spanning
 *    a tick boundary would duplicate/skip one step of a changing input).
 *    Arrivals land at phase p = (anchor + 2·oneWay − ψ) mod 50 ms, where
 *    ψ = median((recvAbs − 50 ms·T) mod 50 ms) over received snapshots
 *    (= (serverPhase + oneWay) mod 50 ms). Attempt 1 uses an arbitrary
 *    anchor; if p_est is within 8 ms of a boundary the run restarts once
 *    with an anchor chosen so p = 25 ms (deterministic, bounded).
 *
 *  PASS ⟺ p95(err) < 0.25 m  AND  snap-back count = 0  AND  scenario checks
 *  (sprint speed, reversal, jump onto a walkable slope) AND  the proxy
 *  actually injected ~100 ms RTT AND  the proxy is transparent (terrain
 *  payload sha identical to a direct capture).
 *
 *  Evidence: test/out/t6-run-<ts>.json, t6-snapshots-<ts>.jsonl,
 *  t6-proxy-<ts>.log.
 */
import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { writeFileSync } from 'node:fs'
import { WSClient } from './lib/ws.mjs'
import {
  MSG,
  encodeHello,
  encodeInput,
  frame,
  decodeHelloAck,
  decodeSnapshot,
} from './lib/wire.mjs'
// Real client code — READ-ONLY imports (same bootstrap as client/tools/dump.ts).
import {
  ACTION,
  TICK_DT,
  decodeTerrain as clientDecodeTerrain,
  sampleRadius,
  slopeAngle,
  spawnLook,
  spawnState,
  step,
  sanitizeLook,
} from '../client/src/sim/index.js'
import { Predictor } from '../client/src/net/predictor.js'
import { PROTOCOL_VERSION } from '../client/src/net/protocol.js'

// ----------------------------------------------------------------- config
const PROXY_PORT = 28092
const TARGET_HOST = '127.0.0.1'
const TARGET_PORT = 18080
const IDLE_TICKS = 40 // 2 s phase calibration (inputs are no-ops at rest)
const LEGS = { sprint: 100, reversal: 80, slope: 100, coast: 40 } // 320 ticks
const TOTAL_TICKS = IDLE_TICKS + LEGS.sprint + LEGS.reversal + LEGS.slope + LEGS.coast // 360
const TICK_MS = 50
const JUMP_LEAD_M = 5.5 // jump this far before the slope starts
const ONE_WAY_MS = 52.5 // proxy 50 ms + ~2.5 ms path (phase estimate only)
const DANGER_EDGE_MS = 8 // arrival phase within this of a tick boundary is unsafe
const TS_TAG = new Date().toISOString().replace(/[:.]/g, '-')
const OUT_RUN = `test/out/t6-run-${TS_TAG}.json`
const OUT_SNAPS = `test/out/t6-snapshots-${TS_TAG}.jsonl`
const OUT_PROXY = `test/out/t6-proxy-${TS_TAG}.log`

// ----------------------------------------------------------------- helpers
const HR_TO_WALL = BigInt(Date.now()) * 1000000n - process.hrtime.bigint()
const nowHrMs = () => Number(process.hrtime.bigint()) / 1e6
const nowWallNs = () => process.hrtime.bigint() + HR_TO_WALL
const mod = (a, m) => ((a % m) + m) % m
const norm3 = (w) => {
  const n = Math.hypot(w[0], w[1], w[2])
  return n < 1e-12 ? [0, 1, 0] : [w[0] / n, w[1] / n, w[2] / n]
}
const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const dist3 = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])
const pct = (sorted, q) => {
  if (!sorted.length) return null
  const i = (sorted.length - 1) * q
  const lo = Math.floor(i)
  const hi = Math.ceil(i)
  return sorted[lo] + (sorted[hi] - sorted[lo]) * (i - lo)
}
const median = (a) => pct([...a].sort((x, y) => x - y), 0.5)
const sha16 = (b) => createHash('sha256').update(b).digest('hex').slice(0, 16)
const v = (a) => ({ x: a[0], y: a[1], z: a[2] })
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const DEG = Math.PI / 180
const UP0 = [0, 1, 0]

// ----------------------------------------------------------------- route
/** Great-circle track unit direction / tangent at arc length s (metres). */
function trackDirAt(azRad, s, R0) {
  const t0 = [Math.sin(azRad), 0, Math.cos(azRad)]
  const th = s / R0
  return [Math.sin(th) * t0[0], Math.cos(th), Math.sin(th) * t0[2]]
}
function trackTangent(azRad, s, R0) {
  const t0 = [Math.sin(azRad), 0, Math.cos(azRad)]
  const th = s / R0
  return norm3([Math.cos(th) * t0[0], -Math.sin(th), Math.cos(th) * t0[2]])
}
/** Signed arc length of a position on the great circle (metres). */
function arcOf(pos, azRad, R0) {
  const t0 = [Math.sin(azRad), 0, Math.cos(azRad)]
  const d = norm3(pos)
  return Math.atan2(dot3(d, t0), dot3(d, UP0)) * R0
}

/**
 * Find great-circle routes out of spawn: a flat run (slope < 8°) followed by
 * a sustained walkable slope (15°..45°), nothing steeper than max_slope and
 * no step > max_step anywhere on the route. Same approach as
 * test/t5/explore.mjs (great-circle walks, longest runs), run on the live
 * wire field with the client's own samplers.
 */
function findRoutes(terrain, nCandidates = 8) {
  const R0 = sampleRadius(terrain, v(UP0))
  const out = []
  for (let az = 0; az < 360; az += 2.5) {
    const azRad = az * DEG
    const samples = []
    for (let s = 0; s <= 90.001; s += 0.5) {
      const d = trackDirAt(azRad, s, R0)
      samples.push({
        s,
        r: sampleRadius(terrain, v(d)),
        slopeDeg: slopeAngle(terrain, v(d)) / DEG,
      })
    }
    let flatEnd = 80
    for (const sm of samples) {
      if (sm.slopeDeg >= 8) {
        flatEnd = sm.s
        break
      }
    }
    // walkable slope run after the flat
    let runStart = -1
    let runEnd = -1
    let slopeRun = null
    for (const sm of samples) {
      if (sm.s < flatEnd) continue
      const inSlope = sm.slopeDeg >= 15 && sm.slopeDeg <= 45
      if (inSlope) {
        if (runStart < 0) runStart = sm.s
        runEnd = sm.s
      } else if (sm.s > flatEnd + 1.0) {
        break
      }
    }
    let ok = runStart >= 0 && runEnd - runStart >= 8 && runEnd - runStart <= 25
    if (ok) {
      for (const sm of samples) {
        if (sm.s > runEnd) break
        if (sm.slopeDeg >= 50) ok = false
      }
      for (let i = 1; ok && i < samples.length; i++) {
        if (samples[i].s > runEnd) break
        if (Math.abs(samples[i].r - samples[i - 1].r) > 0.31) ok = false
      }
    }
    if (ok) slopeRun = { from: runStart, to: runEnd, len: runEnd - runStart }
    out.push({ az, R0, flatLen: flatEnd, slopeRun })
  }
  const candidates = out.filter((r) => r.slopeRun)
  candidates.sort((a, b) => b.flatLen + b.slopeRun.len - (a.flatLen + a.slopeRun.len))
  return candidates.slice(0, nCandidates)
}

const LEG_START = {
  sprint: IDLE_TICKS,
  reversal: IDLE_TICKS + LEGS.sprint,
  slope: IDLE_TICKS + LEGS.sprint + LEGS.reversal,
  coast: IDLE_TICKS + LEGS.sprint + LEGS.reversal + LEGS.slope,
}

/**
 * Simulate the scripted inputs with the real client sim (dry run) to place
 * the jump and validate the scenario. `jumpTick` may be null (pass 1: just
 * map the track position per tick).
 */
function dryRun(terrain, route, jumpTick) {
  const azRad = route.az * DEG
  const R0 = route.R0
  let state = spawnState(terrain)
  let lastLook = spawnLook()
  const sPerTick = []
  const inputs = []
  let maxSpeedSprint = 0
  let minTrackVelReversal = Infinity
  let airTicks = 0
  let airborne = false
  let landing = null
  for (let k = 0; k < TOTAL_TICKS; k++) {
    const pos = [state.pos.x, state.pos.y, state.pos.z]
    const s = arcOf(pos, azRad, R0)
    sPerTick.push(s)
    let moveY = 0
    let mask = 0
    if (k >= LEG_START.sprint && k < LEG_START.coast) {
      mask = ACTION.SPRINT
      moveY = k >= LEG_START.reversal && k < LEG_START.slope ? -1 : 1
    }
    if (jumpTick !== null && (k === jumpTick || k === jumpTick + 1)) mask |= ACTION.JUMP
    const look = trackTangent(azRad, s, R0)
    const input = { moveX: 0, moveY, lookDir: v(look), actionMask: mask }
    state = step(state, input, terrain, TICK_DT, lastLook)
    lastLook = sanitizeLook(input, lastLook)
    inputs.push(input)
    const spd = Math.hypot(state.vel.x, state.vel.y, state.vel.z)
    const T = trackTangent(azRad, s, R0)
    const trackVel = state.vel.x * T[0] + state.vel.y * T[1] + state.vel.z * T[2]
    if (k >= LEG_START.sprint && k < LEG_START.reversal) maxSpeedSprint = Math.max(maxSpeedSprint, spd)
    if (k >= LEG_START.reversal && k < LEG_START.slope) minTrackVelReversal = Math.min(minTrackVelReversal, trackVel)
    if (!state.grounded) {
      airborne = true
      airTicks++
    } else if (airborne) {
      airborne = false
      const sl = arcOf([state.pos.x, state.pos.y, state.pos.z], azRad, R0)
      const slopeL = slopeAngle(terrain, v(norm3([state.pos.x, state.pos.y, state.pos.z]))) / DEG
      landing = { tick: k, s: sl, slopeDeg: slopeL }
    }
  }
  return { inputs, sPerTick, maxSpeedSprint, minTrackVelReversal, airTicks, landing }
}

/**
 * Build the validated 360-tick input script for a route. Returns null when
 * the route cannot produce the scenario.
 */
function buildScript(terrain, route) {
  const pass1 = dryRun(terrain, route, null)
  // jump tick: first tick in the slope leg at/just before JUMP_LEAD_M of flat end
  const target = route.flatLen - JUMP_LEAD_M
  let jumpTick = null
  for (let k = LEG_START.slope; k < LEG_START.slope + LEGS.slope - 10; k++) {
    if (pass1.sPerTick[k] >= target) {
      jumpTick = k
      break
    }
  }
  if (jumpTick === null) return null
  const r = dryRun(terrain, route, jumpTick)
  const checks = {
    flatSlope: route.flatLen >= 42 && route.slopeRun.len >= 8,
    sprint: r.maxSpeedSprint >= 7.0 && r.maxSpeedSprint <= 7.9,
    reversal: r.minTrackVelReversal <= -6.0,
    jump:
      r.landing !== null &&
      r.airTicks >= 10 &&
      r.landing.tick >= jumpTick + 5 &&
      r.landing.s >= route.flatLen - 1.5 &&
      r.landing.slopeDeg >= 8 &&
      r.landing.slopeDeg < 50,
  }
  if (!Object.values(checks).every(Boolean)) return null
  return {
    inputs: r.inputs,
    jumpTick,
    checks,
    stats: {
      maxSpeedSprint: r.maxSpeedSprint,
      minTrackVelReversal: r.minTrackVelReversal,
      airTicks: r.airTicks,
      landing: r.landing,
      flatLen: route.flatLen,
      slopeRun: route.slopeRun,
    },
  }
}

// ----------------------------------------------------------------- direct
/** Direct (no proxy) capture of hello_ack + terrain — the transparency ref. */
async function directCapture() {
  const ws = new WSClient(TARGET_HOST, TARGET_PORT, '/ws')
  const got = { hello: null, terrain: null }
  ws.onMessage = (op, payload) => {
    const g = payload.readUInt16LE(0)
    const p = payload.subarray(2)
    if (g === MSG.HELLO_ACK) got.hello = decodeHelloAck(p)
    else if (g === MSG.TERRAIN) got.terrain = p
  }
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'qa-t6-direct')))
  for (let i = 0; i < 300 && (!got.hello || !got.terrain); i++) await sleep(10)
  ws.sendClose()
  return got
}

/** Decode a wire terrain payload with the client's own decoder. */
function decodeWireTerrain(payload) {
  const u16 = new Uint16Array((payload.length - 10) / 2)
  for (let i = 0; i < u16.length; i++) u16[i] = payload.readUInt16LE(10 + 2 * i)
  return clientDecodeTerrain(payload.readUInt16LE(0), payload.readFloatLE(2), payload.readFloatLE(6), u16)
}

// ----------------------------------------------------------------- proxy
function startProxy() {
  const child = spawn('node', ['test/lib/proxy.mjs', String(PROXY_PORT), TARGET_HOST, String(TARGET_PORT)], {
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  let out = ''
  child.stdout.on('data', (d) => (out += d))
  child.stderr.on('data', (d) => (out += d))
  const ready = new Promise((res, rej) => {
    const iv = setInterval(() => {
      if (out.includes('proxy ready')) {
        clearInterval(iv)
        res()
      }
    }, 20)
    setTimeout(() => {
      clearInterval(iv)
      rej(new Error(`proxy not ready: ${out}`))
    }, 5000)
  })
  return { child, ready, getOut: () => out }
}


// ----------------------------------------------------------------- stats

/**
 * Corrected same-instant pairing — the C6 gate.
 *
 * The obvious metric (|P_M − snapshot(ack M)|) pairs two DIFFERENT instants:
 * P_M is the client state when input M was sent, and snapshot(ack M) is the
 * server state ~RTT later. Under sustained sprint that gap is exactly one
 * tick of motion (7.5 m/s × 0.05 s = 0.375 m), which is why the old metric
 * read a flat 0.375 m no matter what the client did — and why it did not
 * move between a 102 ms and a 148 ms run.
 *
 * Worse, the same-wall-time metric it was reaching for is unachievable by ANY
 * client, including a perfect one: the server's authoritative state is a 20 Hz
 * step function lagging continuous motion by δ ~ U(0, 50 ms), so
 * p95 |R(t) − S(t)| = 7.5 × 0.05 × 0.95 = 0.356 m > 0.25 m for everyone. A
 * criterion no implementation can pass is not a gate.
 *
 * So pair the same instant: the client's POST-reconcile state at ack M is the
 * client's belief about "now", and the server's belief about that same "now"
 * arrives in the NEXT snapshot, ack M+1. That difference is real prediction
 * error, and it is what distinguishes exact replay (clears to wire precision)
 * from blending (leaves a residual toward the stale anchor).
 *
 * Adjudication and the lower-bound proof: test/out/t6-c6-measurement-resolution.md
 */
function correctedPairing(snaps) {
  const errs = []
  let snapbacks = 0
  for (let i = 0; i < snaps.length; i++) {
    const a = snaps[i]
    if (!a.post || !(a.M > 0)) continue
    let b = null
    for (let j = i + 1; j < snaps.length; j++) {
      if (snaps[j].M === a.M + 1 && snaps[j].auth) { b = snaps[j]; break }
    }
    if (!b) continue
    errs.push(dist3(a.post, b.auth)) // both are [x,y,z] arrays
    // Snap-back = the server pulling the player BACKWARD along their own
    // track. A forward difference is just the server being ahead, which is
    // ordinary; only a negative along-track delta is visible as a lurch.
    if (a.trk) {
      const along = (b.auth[0] - a.post[0]) * a.trk[0] +
                    (b.auth[1] - a.post[1]) * a.trk[1] +
                    (b.auth[2] - a.post[2]) * a.trk[2]
      if (along < -0.15) snapbacks++
    }
  }
  if (errs.some((e) => !Number.isFinite(e))) {
    throw new Error(`correctedPairing produced ${errs.filter((e) => !Number.isFinite(e)).length} non-finite errors — pairing is broken, not empty`)
  }
  errs.sort((x, y) => x - y)
  return {
    p50: pct(errs, 0.5),
    p95: pct(errs, 0.95),
    max: errs.length ? errs[errs.length - 1] : null,
    n: errs.length,
    snapbacks,
  }
}

function summarize(attempt) {
  const withErr = attempt.snaps.filter((s) => typeof s.err === 'number')
  const errM0 = withErr.filter((s) => s.M === 0).map((s) => s.err)
  const errsM1 = withErr
    .filter((s) => s.M > 0)
    .map((s) => s.err)
    .sort((a, b) => a - b)
  const d = attempt.snaps.map((s) => s.reconD).filter((x) => typeof x === 'number').sort((a, b) => a - b)
  const rtt = attempt.rtts.sort((a, b) => a - b)
  const nEnt = attempt.snaps.map((s) => s.nEnt)
  return {
    snapshots: attempt.snaps.length,
    snapshotsWithErr: withErr.length,
    errM0: errM0.length ? { p50: median(errM0), max: Math.max(...errM0) } : null,
    // THE GATE: same-instant pairing (post-reconcile at ack M vs auth at M+1).
    err: correctedPairing(attempt.snaps),
    // Diagnostic only, never a gate: the cross-instant pairing, retained so a
    // change in its (structurally ~0.375 m) value is still visible.
    errCrossInstant: {
      p50: pct(errsM1, 0.5),
      p95: pct(errsM1, 0.95),
      max: errsM1.length ? Math.max(...errsM1) : null,
      n: errsM1.length,
    },
    reconD: { min: d.length ? d[0] : null, max: d.length ? d[d.length - 1] : null, p95: pct(d, 0.95) },
    snapbackCount: correctedPairing(attempt.snaps).snapbacks,
    snapbackCountReconcile: attempt.snaps.filter((s) => s.snapback).length,
    rttMs: { p50: pct(rtt, 0.5), p95: pct(rtt, 0.95), min: rtt[0] ?? null, max: rtt[rtt.length - 1] ?? null, n: rtt.length },
    entityCounts: { min: Math.min(...nEnt), max: Math.max(...nEnt) },
    bursts: attempt.bursts,
  }
}

// ----------------------------------------------------------------- main
async function main() {
  const report = {
    criterion: 'C6 — prediction quality at 100 ms injected latency',
    method:
      'GATE — same-instant pairing: err = |post-reconcile client state at ack M − authoritative pos at ack M+1|; ' +
      'snap-back = along-track (auth_{M+1} − post_M) < −0.15 m. ' +
      'Diagnostic only — cross-instant pairing |P_M − snapshot(ack M)|, which is structurally one tick of ' +
      'motion (~0.375 m at sprint) and cannot be beaten by any client; the strict same-wall-time metric has ' +
      'a proven 0.356 m floor. See test/out/t6-c6-measurement-resolution.md.',
    commands: [
      'client/node_modules/.bin/tsx test/t6-prediction.mjs',
      'node test/lib/proxy.mjs 28092 127.0.0.1 18080   (spawned by the harness; 50 ms/direction)',
    ],
    thresholds: { errP95: 0.25, snapback: 0, rttInjectedMs: 100, rttObservedWindowMs: [90, 165] },
  }

  // Phase 0 — direct capture (transparency reference + route planning field).
  const direct = await directCapture()
  report.directTerrainSha = sha16(Buffer.from(direct.terrain))
  report.directHello = direct.hello
  const terrain = decodeWireTerrain(Buffer.from(direct.terrain))
  const routes = findRoutes(terrain, 8)
  report.routes = routes.map((r) => ({ az: r.az, flatLen: r.flatLen, slopeRun: r.slopeRun }))
  let script = null
  let chosenRoute = null
  for (const r of routes) {
    const s = buildScript(terrain, r)
    if (s) {
      script = s
      chosenRoute = r
      break
    }
  }
  if (!script) {
    report.verdict = 'FAIL'
    report.reason = 'no route produced the sprint/reversal/jump-onto-slope scenario on the live field'
    writeFileSync(OUT_RUN, JSON.stringify(report, null, 1))
    console.error(JSON.stringify(report, null, 1))
    process.exit(1)
  }
  report.route = { az: chosenRoute.az, flatLen: chosenRoute.flatLen, slopeRun: chosenRoute.slopeRun, jumpTick: script.jumpTick }
  report.scenario = { checks: script.checks, stats: script.stats }

  // Phase 1 — proxied run.
  const proxy = startProxy()
  await proxy.ready

  const nowMs = nowHrMs()
  const anchor1 = (Math.floor(nowMs / TICK_MS) + 2) * TICK_MS
  const a1 = await runAttempt(anchor1, script, chosenRoute)
  const ψMs = median(a1.snaps.map((s) => s.phaseNs / 1e6))
  const pEst1 = mod(anchorWallMod50ms(a1.anchorHrMs) + 2 * ONE_WAY_MS - ψMs, 50)
  report.attempts = [{ n: 1, anchorHrMs: a1.anchorHrMs, psiMs: ψMs, pEstMs: pEst1, summary: summarize(a1) }]

  let final = a1
  if (pEst1 < DANGER_EDGE_MS || pEst1 > 50 - DANGER_EDGE_MS) {
    // Phase 2 — re-anchor so arrivals land mid-period, re-run once.
    const S0mod = mod(ψMs - ONE_WAY_MS, 50) // server tick phase (ms, mod 50)
    const target = mod(ψMs - 2 * ONE_WAY_MS + 25, 50) // anchor mod 50 for p = 25 ms
    const tMod = mod(Number((BigInt(Math.round(target * 1000)) * 1000000n - HR_TO_WALL) / 1000000n), 50)
    const base = Math.ceil((nowHrMs() + 10) / TICK_MS) * TICK_MS
    const anchor2 = base + mod(tMod - mod(base, 50), 50)
    const a2 = await runAttempt(anchor2, script, chosenRoute)
    const ψ2 = median(a2.snaps.map((s) => s.phaseNs / 1e6))
    const pEst2 = mod(anchorWallMod50ms(a2.anchorHrMs) + 2 * ONE_WAY_MS - ψ2, 50)
    report.attempts.push({ n: 2, anchorHrMs: anchor2, psiMs: ψ2, pEstMs: pEst2, S0modMs: S0mod, summary: summarize(a2) })
    final = a2
  }

  // ---- verdict
  const s = summarize(final)
  report.summary = s
  report.proxiedTerrainSha = final.terrainSha
  report.terrainSameAsDirect = final.terrainSha === report.directTerrainSha
  const reasons = []
  if (s.err.n === 0) reasons.push('no same-instant pairs formed (harness produced no gate evidence)')
  else if (s.err.p95 === null || s.err.p95 >= 0.25) reasons.push(`err p95 ${s.err.p95?.toExponential(3)} m ≥ 0.25 m`)
  if (s.snapbackCount !== 0) reasons.push(`snap-backs ${s.snapbackCount} ≠ 0`)
  if (!report.terrainSameAsDirect) reasons.push('proxy not transparent (terrain sha differs)')
  // This checks that the proxy is IN THE PATH and injecting, not that the
  // end-to-end number is exactly 100 ms. The RTT is stamped when this process
  // decodes the pong, and this process is also stepping the sim on a 50 ms
  // timer -- so a pong that lands mid-tick waits for the loop to drain and the
  // figure reads ~one tick high. Measured 2026-08-23: proxy in isolation
  // 101.6 ms p50 (exactly the injected 100 ms), same server, same host; the
  // same run through this harness reads ~151 ms. The surplus is this
  // process's own scheduling, the same class as the t7 one-way tail
  // (QA-STATUS #11), and it does not touch the gate metric -- that is computed
  // from ack pairing, not from wall clocks.
  //
  // So: floor catches "proxy missing" (an uninjected path reads ~1 ms);
  // ceiling catches a genuinely wrong DELAY_MS. One tick of slack in between.
  if (s.rttMs.p50 === null || s.rttMs.p50 < 90 || s.rttMs.p50 > 165)
    reasons.push(
      `RTT p50 ${s.rttMs.p50?.toFixed(1)} ms outside 90-165 ms — latency injection is not ~100 ms/RTT ` +
      `(expected ~100 ms + up to one 50 ms tick of harness scheduling)`,
    )
  if (final.bursts.some((b) => b.tick >= LEG_START.reversal - 6 && b.tick <= script.jumpTick + 6))
    reasons.push('timer burst near an input change (phase contaminated)')
  report.verdict = reasons.length ? 'FAIL' : 'PASS'
  if (reasons.length) report.reasons = reasons

  // ---- evidence
  writeFileSync(OUT_PROXY, proxy.getOut())
  writeFileSync(
    OUT_SNAPS,
    final.snaps.map((x) => JSON.stringify(x)).join('\n') + '\n',
  )
  report.evidence = { run: OUT_RUN, snapshots: OUT_SNAPS, proxyLog: OUT_PROXY }
  writeFileSync(OUT_RUN, JSON.stringify(report, null, 1))
  proxy.child.kill('SIGTERM')
  console.log(JSON.stringify({ verdict: report.verdict, summary: s, reasons: report.reasons ?? null, evidence: report.evidence }, null, 1))
  process.exit(report.verdict === 'PASS' ? 0 : 1)

}

function anchorWallMod50ms(anchorHrMs) {
  return Number(mod((BigInt(Math.round(anchorHrMs * 1000)) * 1000n + HR_TO_WALL) % 50000000n, 50000000n)) / 1e6
}


main().catch((e) => {
  console.error('FATAL', e)
  process.exit(1)
})

// One live run through the proxy (script + route precomputed and validated).
async function runAttempt(anchorHrMs, script, route) {
  const ws = new WSClient(TARGET_HOST, PROXY_PORT, '/ws')
  const got = { hello: null, terrain: null }
  const snaps = []
  const rtts = []
  const bursts = []
  let myId = 0
  let pingSendNs = null
  let pendingPongTs = null
  let K = 0
  let lastInput = null
  const pred = new Map()
  let predictor = null

  ws.onMessage = (op, payload, recvNs) => {
    const g = payload.readUInt16LE(0)
    const p = payload.subarray(2)
    if (g === MSG.HELLO_ACK) got.hello = decodeHelloAck(p)
    else if (g === MSG.TERRAIN) got.terrain = p
    else if (g === MSG.PONG && pendingPongTs !== null) {
      if (p.readUInt32LE(0) === pendingPongTs) {
        rtts.push(Number(recvNs - pingSendNs) / 1e6)
        pendingPongTs = null
      }
    } else if (g === MSG.SNAPSHOT && predictor) {
      const snap = decodeSnapshot(p)
      const mine = snap.entities.find((e) => e.id === myId)
      const rec = { T: snap.tick, M: snap.ackSeq, K, nEnt: snap.entities.length, recvMs: nowHrMs() }
      const wallNs = recvNs + HR_TO_WALL
      rec.phaseNs = Number(mod(wallNs - BigInt(snap.tick) * 50000000n, 50000000n))
      if (!mine) {
        snaps.push(rec)
        return
      }
      const pm = pred.get(snap.ackSeq)
      if (pm) {
        rec.err = dist3(pm.pos, mine.pos)
        rec.pm = pm.pos
      }
      rec.auth = mine.pos.slice()
      const posBefore = [predictor.stateRef.pos.x, predictor.stateRef.pos.y, predictor.stateRef.pos.z]
      const trk = trackDirBound()
      const applied = predictor.reconcile({ id: mine.id, pos: mine.pos, quat: mine.quat, vel: mine.vel }, snap.ackSeq, nowHrMs())
      const after = predictor.stateRef
      rec.pre = posBefore
      rec.post = [after.pos.x, after.pos.y, after.pos.z]
      rec.trk = trk
      rec.reconD = (after.pos.x - posBefore[0]) * trk[0] + (after.pos.y - posBefore[1]) * trk[1] + (after.pos.z - posBefore[2]) * trk[2]
      rec.reconApplied = applied
      if (rec.reconD < -0.15) rec.snapback = true
      snaps.push(rec)
    }
  }

  function trackDirBound() {
    const st = predictor.stateRef
    const up = norm3([st.pos.x, st.pos.y, st.pos.z])
    const vel = [st.vel.x, st.vel.y, st.vel.z]
    const vt = [vel[0] - up[0] * dot3(vel, up), vel[1] - up[1] * dot3(vel, up), vel[2] - up[2] * dot3(vel, up)]
    if (Math.hypot(vt[0], vt[1], vt[2]) > 0.3) return norm3(vt)
    if (lastInput) {
      const f = [st.facing.x, st.facing.y, st.facing.z]
      const r = [up[1] * f[2] - up[2] * f[1], up[2] * f[0] - up[0] * f[2], up[0] * f[1] - up[1] * f[0]]
      const w = [
        f[0] * lastInput.moveY + r[0] * lastInput.moveX,
        f[1] * lastInput.moveY + r[1] * lastInput.moveX,
        f[2] * lastInput.moveY + r[2] * lastInput.moveX,
      ]
      if (Math.hypot(w[0], w[1], w[2]) > 0.1) return norm3(w)
    }
    return norm3([st.facing.x, st.facing.y, st.facing.z])
  }

  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'qa-t6')))
  for (let i = 0; i < 500 && (!got.hello || !got.terrain); i++) await sleep(10)
  if (!got.hello || !got.terrain) throw new Error('handshake/terrain timeout via proxy')
  if (got.hello.serverVer !== PROTOCOL_VERSION) throw new Error(`serverVer ${got.hello.serverVer}`)
  if (got.hello.tickHz !== 20) throw new Error(`tickHz ${got.hello.tickHz}`)
  if (got.hello.worldSeed !== 1337) throw new Error(`worldSeed ${got.hello.worldSeed}`)
  myId = got.hello.entityId
  const terrain = decodeWireTerrain(Buffer.from(got.terrain))
  const seed = spawnState(terrain)
  predictor = new Predictor()
  predictor.seed(seed, terrain, nowHrMs())

  let lastStepMs = null
  const tickSendMs = []
  let tickN = 0
  let next = anchorHrMs // phase-pinned first send (see main anchor computation)
  const timer = setInterval(() => {
    const now = nowHrMs()
    if (now < next) return
    let burst = 0
    while (now >= next) {
      doTick(tickN++)
      next += TICK_MS
      burst++
      if (now < next) break
    }
    if (lastStepMs !== null && now - lastStepMs < 20) bursts.push({ tick: tickN - burst, n: burst })
    lastStepMs = now
    if (tickN >= TOTAL_TICKS) clearInterval(timer)
  }, 5)

  function doTick(k) {
    tickSendMs.push(nowHrMs())
    const input = k < IDLE_TICKS ? { moveX: 0, moveY: 0, lookDir: spawnLook(), actionMask: 0 } : script.inputs[k]
    lastInput = input
    const st = predictor.predict(input, k, nowHrMs())
    pred.set(k, { pos: [st.pos.x, st.pos.y, st.pos.z], grounded: st.grounded })
    K = k + 1
    ws.sendBinary(
      frame(
        MSG.INPUT,
        encodeInput(input.moveX, input.moveY, [input.lookDir.x, input.lookDir.y, input.lookDir.z], input.actionMask, k),
      ),
    )
    if (k >= IDLE_TICKS && k % 40 === 0) {
      const ts = Date.now() & 0xffffffff
      pingSendNs = process.hrtime.bigint()
      pendingPongTs = ts
      const pb = Buffer.alloc(4)
      pb.writeUInt32LE(ts, 0)
      ws.sendBinary(frame(MSG.PING, pb))
    }
  }

  await new Promise((res) => {
    const iv = setInterval(() => {
      if (tickN >= TOTAL_TICKS) {
        clearInterval(iv)
        res()
      }
    }, 20)
    setTimeout(() => {
      clearInterval(iv)
      res()
    }, TOTAL_TICKS * TICK_MS + 20000)
  })
  clearInterval(timer)
  await sleep(600)
  ws.sendClose()

  return {
    myId,
    terrainSha: sha16(Buffer.from(got.terrain)),
    snaps,
    rtts,
    bursts,
    seedPos: [seed.pos.x, seed.pos.y, seed.pos.z],
    tickSendMs,
    anchorHrMs,
  }
}