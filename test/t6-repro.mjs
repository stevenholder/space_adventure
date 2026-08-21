/**
 * t6 offline repro — structural cause of the flat 1-tick deferred-match error.
 *
 * No network. Simulates the exact event timing of test/t6-prediction.mjs:
 *   - client sends input k at 50k + alpha (5 ms timer grid + jitter),
 *     predictor.predict(k) at the same instant (P_k frozen);
 *   - proxy: d_in one-way delay client→server, d_out server→client;
 *   - server: fixed 50 ms boundaries; at each boundary applies the LATEST
 *     received input (zero input before the first arrival — faithful to
 *     server/internal/server/server.go tick() + client.go reader), then
 *     snapshot (S_k, ack=k) is sent and arrives d_out later;
 *   - client: on snapshot(ack=M) arrival, Predictor.reconcile(S_M, M)
 *     (the real client/src/net/predictor.ts, real client/src/sim step).
 * Both chains use the real client sim on a FLAT synthetic terrain (the
 * sprint segment; grounded/resolve active, no slope).
 *
 * Prints: P_k vs S_k (which S_j P_k equals) for the sprint window, plus a
 * per-event trace of the client chain (predict k / reconcile snap+replay)
 * to locate exactly where the chain ends up one tick behind S_k.
 */
import { spawnState, step, spawnLook } from '../client/src/sim/index.js'
import { Predictor } from '../client/src/net/predictor.js'

// ------------------------------------------------- config
const DT = 0.05
const BOUNDARY_MS = 50
const D_IN = 50 // proxy one-way
const D_OUT = 50
const ALPHA_SWEEP = [5, 25, 45, 49.9]
const TIMER_GRID = 5
const N_IDLE = 40
const N_SPRINT = 80
const TOTAL = N_IDLE + N_SPRINT

const R = 6371000 + 100
const terrain = {
  faceGrid: 65,
  radiusMin: R,
  radiusMax: R,
  radii: new Uint16Array(6 * 65 * 65),
}

const SPRINT_IN = (look) => ({ moveX: 0, moveY: 1, lookDir: look, actionMask: 0x0001 })
const IDLE_IN = (look) => ({ moveX: 0, moveY: 0, lookDir: look, actionMask: 0 })
const ZERO_IN = (look) => ({ moveX: 0, moveY: 0, lookDir: look, actionMask: 0 })
const look = spawnLook()
const inpFor = (k) => (k < N_IDLE ? IDLE_IN(look) : SPRINT_IN(look))

// ------------------------------------------------- server model
class ServerModel {
  constructor() {
    this.state = spawnState(terrain)
    this.latestK = -1 // -1 = no input received (zero input)
    this.snapshots = []
  }
  receiveInput(k) {
    this.latestK = k
  }
  boundary(tMs) {
    const inp = this.latestK >= 0 ? inpFor(this.latestK) : ZERO_IN(look)
    this.state = step(this.state, inp, terrain, DT, look)
    if (this.latestK >= 0) this.snapshots.push({ k: this.latestK, sendMs: tMs + 0.5 })
  }
  chainUpTo(k) {
    let s = spawnState(terrain)
    for (let i = 0; i <= k; i++) s = step(s, inpFor(i), terrain, DT, look)
    return s
  }
}

const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z)
const P = (s) => ({ x: s.pos.x, y: s.pos.y, z: s.pos.z })

// closest j (within [-1, +2] around k) whose S_j matches pos
function matchJ(pos, k, S) {
  let best = -1
  let bd = Infinity
  for (let d = -1; d <= 2; d++) {
    const j = k + d
    if (j < 0 || j >= S.length) continue
    const dd = dist(pos, S[j].pos)
    if (dd < bd) {
      bd = dd
      best = j
    }
  }
  return { j: best, d: +bd.toFixed(5) }
}

// ------------------------------------------------- one run
function run(alphaMs) {
  const server = new ServerModel()
  const predictor = new Predictor()
  predictor.seed(spawnState(terrain), terrain, 0)

  const events = []
  for (let k = 0; k < TOTAL; k++) {
    const target = k * BOUNDARY_MS + alphaMs
    const tSend = Math.ceil(target / TIMER_GRID) * TIMER_GRID
    events.push({ t: tSend, type: 'send', k })
    events.push({ t: tSend + D_IN, type: 'recv', k })
  }
  const tEnd = TOTAL * BOUNDARY_MS + 300
  for (let t = BOUNDARY_MS; t < tEnd; t += BOUNDARY_MS) events.push({ t, type: 'boundary' })
  events.sort((a, b) => a.t - b.t || (a.type < b.type ? -1 : 1))

  // First pass: server side (arrivals + boundaries) → snapshot sends.
  const snapSends = []
  for (const e of events) {
    if (e.type === 'recv') server.receiveInput(e.k)
    else if (e.type === 'boundary') {
      const n0 = server.snapshots.length
      server.boundary(e.t)
      if (server.snapshots.length > n0) snapSends.push(server.snapshots[server.snapshots.length - 1])
    }
  }
  for (const s of snapSends) events.push({ t: s.sendMs + D_OUT, type: 'snap', k: s.k })
  events.sort((a, b) => a.t - b.t || (a.type < b.type ? -1 : 1))

  const predAt = new Map()
  const trace = []
  const recvLog = []
  let nextSend = 0
  for (const e of events) {
    if (e.type === 'send' && e.k === nextSend) {
      nextSend++
      const st = predictor.predict(inpFor(e.k), e.k, e.t)
      predAt.set(e.k, P(st))
      trace.push({ kind: `predict ${e.k}`, t: +e.t.toFixed(1) })
    } else if (e.type === 'snap') {
      const snap = snapSends.find((s) => s.k === e.k)
      const Sref = server.chainUpTo(e.k)
      const ent = { id: 1, pos: [Sref.pos.x, Sref.pos.y, Sref.pos.z], quat: [0, 0, 0, 1], vel: [Sref.vel.x, Sref.vel.y, Sref.vel.z] }
      const prePos = P(predictor.stateRef)
      const buffered = [...predictor['inputs'].keys()].filter((x) => x > e.k).sort((a, b) => a - b)
      const applied = predictor.reconcile(ent, e.k, e.t)
      const postPos = P(predictor.stateRef)
      recvLog.push({
        k: e.k,
        t: +e.t.toFixed(1),
        pre: +dist(prePos, Sref.pos).toFixed(5),
        postD: +dist(postPos, Sref.pos).toFixed(5),
        buffered,
        applied,
      })
      trace.push({ kind: `reconcile ack=${e.k} replay=[${buffered}]`, t: +e.t.toFixed(1) })
    }
  }

  const S = []
  for (let k = 0; k < TOTAL; k++) S[k] = server.chainUpTo(k)

  const rows = []
  for (let k = N_IDLE; k < N_IDLE + 40; k++) {
    const m = matchJ(predAt.get(k), k, S)
    rows.push({ k, m })
  }
  const cruise = rows.slice(15)
  const counts = { behind: 0, aligned: 0, ahead: 0, other: 0 }
  for (const r of cruise) {
    const d = r.m.j - r.k
    if (d === -1) counts.behind++
    else if (d === 0) counts.aligned++
    else if (d === 1) counts.ahead++
    else counts.other++
  }
  return { alphaMs, rows, counts, recvLog, trace, S, predAt }
}

// ------------------------------------------------- main
for (const alpha of ALPHA_SWEEP) {
  const r = run(alpha)
  console.log(`\n=== alpha=${alpha} ms ===`)
  console.log(`  P_k closest S_j: j=k-1: ${r.counts.behind} | j=k: ${r.counts.aligned} | j=k+1: ${r.counts.ahead} | other: ${r.counts.other} (of ${r.rows.length - 15} cruise)`)
  const bad = r.rows.filter((x) => x.m.j !== x.k)
  console.log('  first mismatch rows (k, matched S_j, dist):')
  for (const x of bad.slice(0, 6)) console.log(`  k=${x.k}  P_k==S_${x.m.j}  d=${x.m.d}`)
  // event trace around the first mismatch
  const k0 = bad.length ? bad[0].k : 41
  const m = matchJ(r.predAt.get(k0), k0, r.S)
  const t = r.trace.findIndex((x) => x.kind.includes(String(k0)))
  console.log(`  trace around k=${k0} (P_${k0}==S_${m ? m.j : '?'}):`)
  const start = Math.max(0, r.trace.findIndex((x) => x.kind.includes(String(k0 - 2))))
  for (const x of r.trace.slice(start, start + 10)) console.log(`    t=${x.t}  ${x.kind}`)
  const rl = r.recvLog.filter((x) => x.k >= k0 - 3 && x.k <= k0 + 1)
  console.log('  recv log (ack, t, preDist, postDist, replaySet, applied):')
  for (const x of rl) console.log(`    k=${x.k} t=${x.t} pre=${x.pre} post=${x.postD} replay=[${x.buffered}] applied=${x.applied}`)
}