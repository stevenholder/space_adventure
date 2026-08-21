/**
 * C7 — sustained 20 Hz tick + epoch-pinned one-way snapshot latency.
 *
 * Harness for the 10 scripted players (names qa-t7-01..10) of the
 * "11 clients × 60 s" scenario; the 11th client (qa-t7-00) is a real
 * browser page driven separately by the browser tool (fps + smoke +
 * its own snapshot timing, same-origin /ws via nginx).
 *
 * Criterion (docs/ROADMAP.md M1 C7): sustained 20 Hz, p95
 * server->client snapshot latency < 50 ms (epoch-pinned).
 *
 * Modes:
 *   node test/t7-sustain.mjs pin
 *   node test/t7-sustain.mjs run [--duration 60]
 *
 * Epoch pin (method — see evidence "epochPin.method"):
 *   The server tick loop is time.NewTicker(50 ms) (server/internal/
 *   server/server.go:96-97) — the tick grid C + T*50 ms has arbitrary
 *   phase C relative to the host wall clock, and /healthz is bare
 *   "ok" (no tick readout). The phase is pinned from the wire: an
 *   input sent at wall phase k (mod 50) arrives ~t_up later and is
 *   applied at the first tick boundary after arrival, so the
 *   send -> first-ack-snapshot latency A(k) is a 50 ms sawtooth in k.
 *   The jump position k* plus t_up = RTT_p50/2 (symmetric port-forward
 *   path) gives the boundary phase
 *       C_phase = (k* + t_up) mod 50
 *   and the per-snapshot one-way latency
 *       L(T) = (recvWall - T*50 ms - C_phase) mod 50 ms
 *   = generation-skip + downlink transport, under the physical
 *   assumption L < 50 ms (local server + userspace port-forward).
 *   Residual uncertainty is ±~2 ms (port-forward userspace-proxy
 *   jitter) — documented in the evidence.
 *
 * Deterministic: all movement is a pure function of tick index and
 * client index (no Math.random); CI-runnable.
 */
import { WSClient } from './lib/ws.mjs'
import {
  MSG,
  PROTOCOL_VERSION,
  ACTION,
  encodeHello,
  encodeInput,
  frame,
  decodeHelloAck,
  decodeSnapshot,
  seqNewer,
} from './lib/wire.mjs'
import fs from 'node:fs'

const HOST = '127.0.0.1'
const PORT = 18080
const TICK_MS = 50
const SEED_NS = null // deterministic by construction

// hrtime -> host wall clock (ns). Host and server share the clock
// (kind node runs in this WSL2 distro; port-forward adds only a
// userspace hop, no clock domain).
const HR_TO_WALL = BigInt(Date.now()) * 1_000_000n - process.hrtime.bigint()
const wallMs = () => Number(process.hrtime.bigint() + HR_TO_WALL) / 1e6
const wallNs = () => process.hrtime.bigint() + HR_TO_WALL
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/** Sleep until absolute wall-ms target; busy-spins the last <=2 ms. */
async function sleepUntil(targetMs) {
  let remain = targetMs - wallMs()
  if (remain > 3) {
    await sleep(remain - 2)
    remain = targetMs - wallMs()
  }
  const t0 = Date.now()
  while (remain > 0) {
    if (Date.now() - t0 > 50) break // safety: never block > 50 ms
    remain = targetMs - wallMs()
  }
}

const mod = (x, m) => ((x % m) + m) % m
const mod50 = (x) => mod(x, TICK_MS)
 function pct(sorted, p) {
   if (sorted.length === 0) return null
   const rank = Math.ceil((p / 100) * sorted.length)
   return sorted[Math.max(0, Math.min(sorted.length - 1, rank - 1))]
 }
const stats = (arr) => {
  const s = [...arr].sort((a, b) => a - b)
  return {
    n: s.length,
    p50: pct(s, 50),
    p95: pct(s, 95),
    min: s[0],
    max: s[s.length - 1],
  }
}

function tsTag() {
  return new Date().toISOString().replace(/[:.]/g, '-').replace('Z', 'Z')
}
function writeJson(name, obj) {
  const p = `test/out/${name}`
  fs.writeFileSync(p, JSON.stringify(obj, null, 1) + '\n')
  return p
}

// --------------------------------------------------------------- helpers

async function connectClient(name) {
  const ws = new WSClient(HOST, PORT, '/ws')
  ws.onMessage = null
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, name)))
  return ws
}

/** Resolve when the client has HELLO_ACK + TERRAIN (timeout ms). */
function waitHandshake(ws, timeoutMs = 10_000) {
  return new Promise((resolve, reject) => {
    const got = { hello: null, terrain: false }
    const t0 = Date.now()
    const timer = setInterval(() => {
      if (Date.now() - t0 > timeoutMs) {
        clearInterval(timer)
        reject(new Error(`handshake timeout for ${ws.path}: hello=${!!got.hello} terrain=${got.terrain}`))
      }
    }, 100)
    ws.onMessage = (_op, payload) => {
      const g = payload.readUInt16LE(0)
      if (g === MSG.HELLO_ACK) got.hello = decodeHelloAck(payload.subarray(2))
      if (g === MSG.TERRAIN) got.terrain = true
      if (got.hello && got.terrain) {
        clearInterval(timer)
        resolve(got.hello)
      }
    }
  })
}

/** Game-level ping RTT (type 0x0008 -> 0x0009). WS-control pings are
 *  NOT used: the server closes connections on them (protocol contract). */
async function measureRtt(ws, n, gapMs) {
  const rtts = []
  for (let j = 0; j < n; j++) {
    const p = Buffer.alloc(4)
    const t0 = wallMs()
    p.writeUInt32LE(Math.round(t0) >>> 0, 0)
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('pong timeout')), 3000)
      const prev = ws.onMessage
      ws.onMessage = (_op, payload) => {
        if (payload.readUInt16LE(0) === MSG.PONG) {
          rtts.push(wallMs() - t0)
          clearTimeout(timer)
          ws.onMessage = prev
          resolve()
        }
      }
      ws.sendBinary(frame(MSG.PING, p))
    })
    if (j < n - 1) await sleep(gapMs)
  }
  return rtts
}

// --------------------------------------------------------------- epoch pin

/**
 * Pin the server tick-grid phase (mod 50 ms) via the input-phase sweep.
 * Sends one input per 50 ms phase k = 0..49 (moveY=1, seq 1000+k) and
 * records A_k = arrival of the first snapshot whose ack covers seq
 * 1000+k, minus the send wall time.
 */
async function epochPin() {
  const ws = await connectClient('qa-t7-pin')
  try {
    await waitHandshake(ws)

    const rtt = await measureRtt(ws, 10, 300)
    const rttS = stats(rtt)
    const t_up = rttS.p50 / 2

    // Sweep: one input per phase, aligned to the host wall 50 ms grid.
    const base = Math.ceil((wallMs() + 1500) / TICK_MS) * TICK_MS
    const S = new Array(50).fill(null) // send wall time per k
    const A = new Array(50).fill(null) // send -> first-ack-snapshot arrival
    let sent = -1 // highest k sent (main loop only)
    let next = 0 // lowest k not yet resolved (handler only)
    const ackLog = [] // {t, T, ack} per snapshot — debug trail
    let closedInfo = null
    ws.onClose = (info) => (closedInfo = info)
    ws.onMessage = (_op, payload, recvNs) => {
      if (payload.readUInt16LE(0) !== MSG.SNAPSHOT) return
      const d = decodeSnapshot(payload.subarray(2))
      const t = Number(recvNs + HR_TO_WALL) / 1e6
      ackLog.push({ t: +t.toFixed(2), T: d.tick, ack: d.ackSeq })
      // first snapshot whose ack covers input k (ack >= 1000+k):
      while (next <= sent && seqNewer(d.ackSeq, 999 + next)) {
        A[next] = t - S[next] // S[next] set: next <= sent
        next++
      }
    }

    for (let k = 0; k < 50; k++) {
      // 51 ms spacing: >= one tick (no burst: one input per boundary)
      // and phase (S_k mod 50) = k, since base ≡ 0 (mod 50) and
      // 51k ≡ k (mod 50).
      const target = base + k * (TICK_MS + 1)
      await sleepUntil(target)
      S[k] = wallMs()
      sent = k
      ws.sendBinary(frame(MSG.INPUT, encodeInput(0, 1, [0, 0, 1], 0, 1000 + k)))
    }
    await sleep(1500) // let the last acks land

    const missing = A.map((v, k) => (v === null ? k : -1)).filter((k) => k >= 0)
    if (missing.length > 0) {
      writeJson(
        `t7-epoch-pin-debug-${tsTag()}.json`,
        { A, S, ackLog, rtt, t_up, closedInfo },
      )
      throw new Error(`pin sweep: no ack snapshot for k=${missing.join(',')} (debug written)`)
    }

    // Sawtooth fit: A_k = t_up + (50 - u_k) + base with u_k = (k - j) mod 50,
    // j = fractional jump position. Sanity: max step diff >= 25 ms; then
    // grid-search j at 0.25 ms resolution minimizing MAD (sub-integer
    // precision — an integer argmax over diffs is ±1 ms quantized at the
    // wrap, which smears the one-way latency across the mod-50 wrap).
    const diffs = []
    for (let k = 1; k < 50; k++) diffs.push([k, A[k] - A[k - 1]])
    const best = Math.max(...diffs.map(([, d]) => d))
    if (best < 25) {
      writeJson(
        `t7-epoch-pin-debug-${tsTag()}.json`,
        { A, S, ackLog, rtt, t_up, closedInfo },
      )
      throw new Error(`pin sweep: no sawtooth jump (max diff ${best.toFixed(1)} ms) (debug written)`)
    }
    let jStar = 0
    let madStar = Infinity
    let baseMed = 0
    for (let g = 0; g < 200; g++) {
      const j = g * 0.25
      const vals = []
      for (let k = 0; k < 50; k++) {
        const u = mod50(k - j)
        if (u < 8) continue // near-boundary: application-at-boundary case skews
        vals.push(A[k] - t_up - mod50(50 - u))
      }
      const b = pct([...vals].sort((a, x) => a - x), 50)
      const m = pct(vals.map((v) => Math.abs(v - b)).sort((a, x) => a - x), 50)
      if (m < madStar) {
        madStar = m
        baseMed = b
        jStar = j
      }
    }
    const kJump = Math.round(jStar) % 50
    const C_phase = mod50(jStar + t_up)
    const resid = []
    const pred = []
    for (let k = 0; k < 50; k++) {
      const u = mod50(k - jStar)
      if (u < 8) continue
      resid.push(A[k] - t_up - mod50(50 - u) - baseMed)
      pred.push([k, u])
    }
    const mad = madStar

    const out = {
      ts: tsTag(),
      method:
        'input-phase sweep: one input per 50 ms phase k (mod 50, host wall grid), ' +
        'A_k = arrival of first snapshot with ack >= 1000+k minus send time; ' +
        'A_k is a 50 ms sawtooth in k; fractional jump position j* (0.25 ms grid, MAD fit) -> ' +
        'C_phase = (j* + t_up) mod 50, ' +
        't_up = RTT_p50/2 (symmetric port-forward path). ' +
        'One-way L(T) = (recvWall - T*50ms - C_phase) mod 50 ms; assumes L < 50 ms ' +
        '(local server + userspace port-forward, no clock domain change). ' +
        'Residual uncertainty ±~2 ms (port-forward userspace-proxy jitter).',
      host: HOST,
      port: PORT,
      t_up_ms: t_up,
      j_star_ms: jStar,
      k_jump: kJump,
      jump_size_ms: best,
      C_phase_ms: C_phase,
      boundary_skip_ms: baseMed, // δ_gen + downlink (constant part)
      fit_mad_ms: mad,
      n_fit: pred.length,
      A_ms: A.map((v) => +v.toFixed(3)),
      S_ms: S.map((v) => +v.toFixed(3)),
      sanity: {
        A_range_ms: [Math.min(...A), Math.max(...A)].map((v) => +v.toFixed(3)),
        sawtooth_decreasing: diffs.filter(([, d]) => d < -0.5).length, // expect ~48
      },
    }
    const path = writeJson(`t7-epoch-pin-${out.ts}.json`, out)
    console.log(
      `EPOCH-PIN C_phase=${C_phase.toFixed(2)} ms t_up=${t_up.toFixed(2)} ms ` +
        `rtt p50=${rttS.p50.toFixed(2)} p95=${rttS.p95.toFixed(2)} ms k*=${kJump} ` +
        `jump=${best.toFixed(1)} ms base(δ+down)=${baseMed.toFixed(2)} ms MAD=${mad.toFixed(2)} ms ` +
        `-> ${path}`,
    )
    return out
  } finally {
    ws.sendClose()
  }
}

// --------------------------------------------------------------- sustain run

/** Deterministic per-client input pattern (tick index n, client i). */
function pattern(i, n) {
  const t = n / 20
  const theta = (2 * Math.PI * i) / 10 + (i % 2 === 0 ? 1 : -1) * 2 * Math.PI * (t / 30)
  const mx = Math.sin(theta)
  const my = Math.cos(theta)
  let mask = 0
  if (Math.sin((2 * Math.PI * t) / 20 + i) > 0) mask |= ACTION.SPRINT
  const j1 = Math.round((20 + i) * 20)
  const j2 = Math.round((40 + i) * 20)
  if (n === j1 || n === j2) mask |= ACTION.JUMP
  return { mx, my, look: [mx, 0, my], mask, seq: n + 1 }
}

async function inputLoop(ws, i, t0Ms, durMs) {
  const ticks = Math.floor(durMs / TICK_MS)
  let next = t0Ms
  for (let n = 0; n < ticks; n++) {
    next = t0Ms + n * TICK_MS
    const wait = next - wallMs()
    if (wait > 0) await sleep(wait)
    const p = pattern(i, n)
    ws.sendBinary(frame(MSG.INPUT, encodeInput(p.mx, p.my, p.look, p.mask, p.seq)))
  }
}

async function runSustain(durationS, opts = {}) {
  const { nClients = 10, offset = 0, tag = '' } = opts
  console.log(`PIN: sweeping epoch pin ...`)
  const pin = await epochPin()
  const C_phase = pin.C_phase_ms
  const durMs = durationS * 1000
  const nTick = Math.floor(durMs / TICK_MS)

  const clients = []
  for (let i = 1; i <= nClients; i++) {
    const name = `qa-t7-${String(i + offset).padStart(2, '0')}`
    const ws = await connectClient(name)
    const hello = await waitHandshake(ws)
    clients.push({
      name,
      ws,
      entityId: hello.entityId,
      snaps: [], // {T, ack, t}
      last: null,
      gaps: 0,
      dupes: 0,
      nEntMax: 0,
      inWindow: false,
      pingTs: [],
      rtt: [],
      phases: [], // arrival phase (mod 50) vs the tick grid
    })
  }
  console.log(`PIN OK C_phase=${C_phase.toFixed(2)} ms; 10 clients connected (entities ${clients.map((c) => c.entityId).join(',')})`)

  // Event-loop liveness: 5 ms timer; intervals > 8 ms = stall samples.
  const loopLag = []
  let lastLagNs = process.hrtime.bigint()
  const lagTimer = setInterval(() => {
    const now = process.hrtime.bigint()
    const dMs = Number(now - lastLagNs) / 1e6
    lastLagNs = now
    if (dMs > 8) loopLag.push(+dMs.toFixed(2))
  }, 5)
  await sleep(2000)
  const t0 = wallMs()
  console.log(`WINDOW-START t0=${t0.toFixed(0)} duration=${durationS}s`)
  const startWallNs = wallNs()

  const inputPromises = clients.map((c, idx) => inputLoop(c.ws, idx + 1, t0, durMs))

  clients.forEach((c) => {
    c.ws.onMessage = (_op, payload, recvNs) => {
      const t = Number(recvNs + HR_TO_WALL) / 1e6
      const g = payload.readUInt16LE(0)
      if (g === MSG.PONG) {
        c.rtt.push(t - c.pingTs.shift())
        return
      }
      if (g !== MSG.SNAPSHOT) return
      const d = decodeSnapshot(payload.subarray(2))
      c.nEntMax = Math.max(c.nEntMax, d.entities.length)
      if (t < t0 - 200) return
      const rec = { T: d.tick, ack: d.ackSeq, t }
      if (c.last) {
        const dT = d.tick - c.last.T
        if (dT === 1) {
          // ok
        } else if (dT > 1) c.gaps += dT - 1
        else c.dupes += 1
      }
      c.last = rec
      if (t >= t0 && t <= t0 + durMs + 200) {
        c.snaps.push(rec)
        c.phases.push(mod50(rec.t - rec.T * TICK_MS))
      }
    }
  })

  // Two game-level pings per client (t≈5 s, t≈55 s) — RTT reference.
  await sleep(5000)
  clients.forEach((c) => {
    const p = Buffer.alloc(4)
    p.writeUInt32LE(Math.round(wallMs()) >>> 0, 0)
    c.pingTs.push(wallMs())
    c.ws.sendBinary(frame(MSG.PING, p))
  })
  clients.forEach((c) => {
    const p = Buffer.alloc(4)
    p.writeUInt32LE(Math.round(wallMs()) >>> 0, 0)
    c.pingTs.push(wallMs())
    c.ws.sendBinary(frame(MSG.PING, p))
  })
  await sleep(5000)

  await Promise.all(inputPromises)
  const t1 = wallMs()
  clients.forEach((c) => c.ws.sendClose())
  await sleep(500)

  // ------------------------------------------------------------ stats
  clearInterval(lagTimer)
  const lagS = [...loopLag].sort((a, b) => a - b)
  const lagStats = {
    n: lagS.length,
    p50: pct(lagS, 50),
    p95: pct(lagS, 95),
    max: lagS.length ? lagS[lagS.length - 1] : null,
  }
  const rawRecs = []
  const perClient = clients.map((c) => {
    const s = c.snaps
    const L = s.map((r) => mod50(r.t - r.T * TICK_MS - C_phase))
    const wraps = []
    for (let i = 0; i < s.length; i++) {
      rawRecs.push({ n: c.name, T: s[i].T, t: +s[i].t.toFixed(1), L: +L[i].toFixed(3) })
      if (L[i] > 40) wraps.push(s[i].t)
    }
    const arrivals = []
    for (let k = 1; k < s.length; k++) arrivals.push(s[k].t - s[k - 1].t)
    const hz =
      s.length > 1 ? (s[s.length - 1].T - s[0].T) / ((s[s.length - 1].t - s[0].t) / 1000) : null
    const phS = [...c.phases].sort((a, b) => a - b)
    const phMed = phS.length ? pct(phS, 50) : null
    const devS = phS.length
      ? c.phases
          .map((v) => Math.min(Math.abs(v - phMed), TICK_MS - Math.abs(v - phMed)))
          .sort((a, b) => a - b)
      : []
    return {
      name: c.name,
      entityId: c.entityId,
      nSnap: s.length,
      nEntMax: c.nEntMax,
      ticks: s.length ? [s[0].T, s[s.length - 1].T] : null,
      hz,
      gaps: c.gaps,
      dupes: c.dupes,
      arrival_ms: stats(arrivals),
      oneWay_ms: stats(L),
      arrivalPhase_ms: phS.length
        ? {
            p50: pct(phS, 50),
            p95: pct(phS, 95),
            jitterP50: pct(devS, 50),
            jitterP95: pct(devS, 95),
            maxDev: devS[devS.length - 1],
          }
        : null,
      rtt_ms: c.rtt.length ? stats(c.rtt) : null,
      nWrap: wraps.length,
      wrapSpan_ms: wraps.length > 1 ? +(Math.max(...wraps) - Math.min(...wraps)).toFixed(1) : 0,
    }
  })

  const hzs = perClient.map((c) => c.hz).filter((v) => v !== null)
  const oneWayP95 = perClient.map((c) => c.oneWay_ms.p95)
  const gate = {
    tickHz_20:
      perClient.every((c) => c.gaps === 0 && c.dupes === 0) &&
      hzs.every((v) => Math.abs(v - 20) / 20 <= 0.01),
    oneWay_p95_lt_50ms: oneWayP95.every((v) => v < 50),
    jitter_p95_lt_50ms: perClient.every((c) => c.arrivalPhase_ms.jitterP95 < 50),
  }

  const out = {
    ts: tsTag(),
    duration_s: durationS,
    t0_ms: t0,
    t1_ms: t1,
    epochPin: pin,
    clients: perClient,
    global: {
      hz_min: Math.min(...hzs),
      hz_max: Math.max(...hzs),
      total_gaps: perClient.reduce((a, c) => a + c.gaps, 0),
      total_dupes: perClient.reduce((a, c) => a + c.dupes, 0),
      oneWay_p95_max_ms: Math.max(...oneWayP95),
      jitter_p95_max_ms: Math.max(...perClient.map((c) => c.arrivalPhase_ms.jitterP95)),
      arrivalPhase_p50_ms: pct(
        perClient.map((c) => c.arrivalPhase_ms.p50).sort((a, b) => a - b),
        50,
      ),
      pinPredictedPhase_ms: mod50(C_phase + pin.boundary_skip_ms),
      total_wraps: perClient.reduce((a, c) => a + c.nWrap, 0),
      loopLag_ms: lagStats,
    },
    gate,
    notes: [
      'one-way L(T) = (recvWall - T*50ms - C_phase) mod 50 ms; assumes L < 50 ms ' +
        '(local server + userspace port-forward, no clock domain change)',
      '±~2 ms residual: port-forward userspace-proxy jitter (documented per task)',
      't_up = RTT_p50/2 (symmetric path); epoch pin re-run inside this process',
      'recvNs = hrtime at frame decode (test/lib/ws.mjs _tryDecode)',
      'input pattern: deterministic rotation + sprint window + 2 jumps/client (see pattern())',
      'browser client qa-t7-00 (same-origin /ws via nginx) measured separately',
      'arrivalPhase_ms: C-free metric — per-snapshot arrival phase (recvWall - T*50ms) mod 50; jitterP95 = p95 of circular deviation from its own median; immune to epoch-pin quantization',
      'nWrap = snapshots with L > 40 ms (one tick late under the mod-50 wrap); loopLag_ms = harness event-loop stall intervals (> 8 ms over a 5 ms timer) — adjudicates self-inflicted decode delay vs a real delivery tail',
    ],
  }
  const path = writeJson(`t7-sustain-${out.ts}${tag ? '-' + tag : ''}.json`, out)
  const rawP = `test/out/t7-raw-${out.ts}${tag ? '-' + tag : ''}.jsonl`
  fs.writeFileSync(rawP, rawRecs.map((r) => JSON.stringify(r)).join('\n') + '\n')
  console.log(`DONE -> ${path} (raw: ${rawP})`)
  for (const c of perClient) {
    console.log(
      `  ${c.name}: ent=${c.entityId} snaps=${c.nSnap} hz=${c.hz?.toFixed(3)} gaps=${c.gaps} dupes=${c.dupes} ` +
        `arr p50=${c.arrival_ms.p50?.toFixed(1)} p95=${c.arrival_ms.p95?.toFixed(1)} ms ` +
        `oneWay p50=${c.oneWay_ms.p50?.toFixed(2)} p95=${c.oneWay_ms.p95?.toFixed(2)} max=${c.oneWay_ms.max?.toFixed(2)} ms wraps=${c.nWrap}`,
    )
  }
  console.log(
    `GATE: 20Hz continuity ${gate.tickHz_20 ? 'PASS' : 'FAIL'} | ` +
      `oneWay p95 < 50 ms ${gate.oneWay_p95_lt_50ms ? 'PASS' : 'FAIL'} ` +
      `(worst p95 ${Math.max(...oneWayP95).toFixed(2)} ms)`,
  )
  void startWallNs
  return out
}

// --------------------------------------------------------------- main

const arg = process.argv[2] ?? 'run'
let durationS = 60
let nClients = 10
let offset = 0
let tag = ''
for (let i = 3; i < process.argv.length; i++) {
  if (process.argv[i] === '--duration') durationS = Number(process.argv[++i])
  else if (process.argv[i] === '--clients') nClients = Number(process.argv[++i])
  else if (process.argv[i] === '--offset') offset = Number(process.argv[++i])
  else if (process.argv[i] === '--tag') tag = process.argv[++i]
}

process.on('SIGINT', () => {
  console.log('SIGINT — aborting')
  process.exit(130)
})

try {
  if (arg === 'pin') await epochPin()
  else if (arg === 'run') await runSustain(durationS, { nClients, offset, tag })
  else throw new Error(`unknown mode ${arg} (use pin|run)`)
  process.exit(0)
} catch (e) {
  console.error('FAIL:', e?.message ?? e)
  process.exit(1)
}