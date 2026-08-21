/**
 * C3 — server authority (ROADMAP criterion 3), driven by the REAL client
 * Predictor (QA-STATUS resume item 6).
 *
 *   Server is authoritative: state forced client-side (dev override) is
 *   corrected by a snapshot within one tick (50 ms + network).
 *
 * Method: the same loop as client/src/main.ts (tickStep + processSnapshot),
 * headless under tsx — DOM-free client code via the tsconfig.sim.json graph:
 *   - transport: raw-TCP WS (test/lib/ws.mjs) against the LIVE server
 *     (ws://127.0.0.1:3000/ws, the nginx same-origin path);
 *   - codec: the client's own wire codec (client/src/net/protocol.ts);
 *   - terrain + sim: client/src/sim (decodeTerrain -> Terrain, spawnState);
 *   - prediction/replay: client/src/net/predictor.ts (the unit under test).
 *
 *   1. handshake; seed the Predictor from the wire terrain (as onTerrain does);
 *   2. WALK: one input per local tick (sendInput + predictor.predict, 20 Hz)
 *      walking +X for 1.5 s;
 *   3. STOP: stand-still inputs until the server state is at rest
 *      (two consecutive snapshots with |vel| < 5e-3 m/s);
 *   4. CORRUPT: force the predicted state +4 m tangent to the surface
 *      (the ?corrupt dev override, CORRUPT_DIST = 4) by mutating
 *      predictor.stateRef;
 *   5. the next incoming snapshot must snap the corrected state to within
 *      <1e-3 m of the authoritative (snapshot) position in ONE reconcile
 *      step, within one tick (50 ms + network) of the corruption.
 *
 * Deterministic and isolated: single client, pinned world (seed 1337),
 * fixed commands; all timings from one process clock (ns).
 *
 *   cd client && node_modules/.bin/tsx ../test/t3-authority.mjs
 */
import { writeFileSync, mkdirSync } from 'node:fs'
import { WSClient } from './lib/ws.mjs'

// Real client code (READ-ONLY): wire codec, terrain/sim, Predictor.
import {
  MSG,
  PROTOCOL_VERSION,
  unframe,
  encodeHello,
  encodeInput,
  decodeHelloAck,
  decodeTerrain,
  decodeSnapshot,
} from '../client/src/net/protocol.js'
import {
  decodeTerrain as simDecodeTerrain,
  spawnState,
  TICK_DT,
  vec,
} from '../client/src/sim/index.js'
import { Predictor } from '../client/src/net/predictor.js'

const OUT = new URL('./out/', import.meta.url)
mkdirSync(OUT, { recursive: true })
const EVIDENCE = new URL('./out/t3-authority.json', import.meta.url)

const HOST = '127.0.0.1'
const PORT = 3000 // A: nginx /ws path
const NAME = 'qa-t3'
const CORRUPT_DIST = 4 // m — matches main.ts ?corrupt override
const SNAP_EPS = 1e-3 // m — criterion 3 snap tolerance
const WALK_MS = 1500
const REST_VEL = 5e-3 // m/s — "at rest on the server" threshold
const ONE_TICK_NET_MS = 100 // one server tick (50 ms) + network budget
const CORRUPT_WATCHDOG_MS = 1000

const ns = () => process.hrtime.bigint()
const ms = (a, b) => Number(b - a) / 1e6
const t0 = ns()
const tMs = (atNs) => Number(ms(t0, atNs).toFixed(3))
const log = (line) => console.log(`[t3 ${new Date().toISOString()}] ${line}`)
const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z)
const len3 = (p) => Math.hypot(p.x, p.y, p.z)

// ------------------------------------------------------------- state
const ws = new WSClient(HOST, PORT)
const predictor = new Predictor()
let myId = -1
let simReady = false
let seq = 0
let phase = 'boot'
let command = { moveX: 0, moveY: 1, lookDir: [1, 0, 0], actionMask: 0 } // +X walk (moveY=forward, spawn facing +X)
let tickTimer = null
let corruptWatchdog = null
let startedAtMs = null

const inputsSent = [] // { seq, cmd, atMs }
const snaps = [] // { tick, ackSeq, ok, pos, vel, atMs }
let lastRestSnap = null
let restCount = 0
let correctedSnap = null
let finished = false
let pass = false

// forced-state record (filled by doCorrupt)
const forced = { applied: false, atMs: null, tick: null, offsetM: CORRUPT_DIST, dir: [0, 0, 0], preDistM: 0, pos: [0, 0, 0] }

const checks = []
function check(id, passVal, detail) {
  checks.push({ id, pass: passVal, detail })
  log(`${passVal ? 'PASS' : 'FAIL'} ${id} ${detail}`)
}

// ------------------------------------------------------------- input
function sendInput(cmd) {
  const s = seq
  const buf = Buffer.from(encodeInput(cmd.moveX, cmd.moveY, [cmd.lookDir[0], cmd.lookDir[1], cmd.lookDir[2]], cmd.actionMask, s))
  ws.sendBinary(buf)
  seq = (seq + 1) & 0xffff
  return s
}

// Mirror of main.ts tickStep: one input per local 50 ms tick — the same wire
// seq goes to the server and into the predictor's replay buffer.
function startTickLoop() {
  startedAtMs = performance.now()
  tickTimer = setInterval(() => {
    const nowMs = performance.now()
    const s = sendInput(command)
    if (s >= 0) {
      predictor.predict(command, s, nowMs)
      inputsSent.push({ seq: s, cmd: { ...command }, atMs: tMs(ns()) })
      if (inputsSent.length > 400) inputsSent.shift()
    }
  }, TICK_DT * 1000)
}

// ------------------------------------------------------------- corruption
// The ?corrupt dev override (main.ts): the local predicted position is
// forced 4 m off the server truth. Applied headless here by mutating the
// real Predictor's stateRef (its internal State).
function doCorrupt(snapEntry, recvNs) {
  const ref = predictor.stateRef
  if (!ref) throw new Error('no predicted state to corrupt')
  const authPos = snapEntry.pos
  const up = vec.norm(authPos)
  let tang = vec.sub(ref.facing, vec.scale(up, vec.dot(ref.facing, up)))
  if (vec.len(tang) < 0.1) {
    // degenerate (facing ~ parallel to up): project world +X instead
    tang = vec.sub({ x: 1, y: 0, z: 0 }, vec.scale(up, up.x))
  }
  tang = vec.norm(tang)
  ref.pos.x += CORRUPT_DIST * tang.x
  ref.pos.y += CORRUPT_DIST * tang.y
  ref.pos.z += CORRUPT_DIST * tang.z
  phase = 'await_correction'
  forced.applied = true
  forced.atMs = tMs(recvNs)
  forced.tick = snapEntry.tick
  forced.dir = [tang.x, tang.y, tang.z]
  forced.preDistM = dist(ref.pos, authPos)
  forced.pos = [ref.pos.x, ref.pos.y, ref.pos.z]
  log(`CORRUPT at tick ${snapEntry.tick} (+${tMs(recvNs)} ms): predicted pos forced +${CORRUPT_DIST} m tangent ` +
    `dir=(${tang.x.toFixed(4)},${tang.y.toFixed(4)},${tang.z.toFixed(4)}) auth=(${authPos.x.toFixed(4)},${authPos.y.toFixed(4)},${authPos.z.toFixed(4)}) ` +
    `-> forced=(${ref.pos.x.toFixed(4)},${ref.pos.y.toFixed(4)},${ref.pos.z.toFixed(4)}) preDist=${forced.preDistM.toFixed(4)} m`)
  // watchdog: the correction must arrive within 1 s of the corruption
  clearTimeout(corruptWatchdog)
  corruptWatchdog = setTimeout(() => {
    if (finished) return
    log(`FAIL: no effective reconcile within ${CORRUPT_WATCHDOG_MS} ms of the corruption (last snapshots: ${JSON.stringify(snaps.slice(-3))})`)
    finish()
  }, CORRUPT_WATCHDOG_MS)
}

// ------------------------------------------------------------- transport events
ws.onMessage = (_op, payload, recvNs) => {
  const { type, payload: p } = unframe(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength))
  if (type === MSG.hello_ack) {
    const ack = decodeHelloAck(p)
    myId = ack.entityId
    log(`hello_ack: server_ver=${ack.serverVer} tick_hz=${ack.tickHz} world_seed=${ack.worldSeed} entity_id=${myId} (+${tMs(recvNs)} ms)`)
  } else if (type === MSG.terrain) {
    const wire = decodeTerrain(p)
    const terrain = simDecodeTerrain(wire.faceGrid, wire.radiusMin, wire.radiusMax, wire.radii)
    const spawn = spawnState(terrain)
    predictor.seed(spawn, terrain, performance.now())
    simReady = true
    phase = 'walk'
    startTickLoop()
    log(`terrain + Predictor seed: face_grid=${wire.faceGrid} r=[${wire.radiusMin},${wire.radiusMax}] ` +
      `spawn pos=(${spawn.pos.x.toFixed(3)},${spawn.pos.y.toFixed(3)},${spawn.pos.z.toFixed(3)}) (+${tMs(recvNs)} ms)`)
  } else if (type === MSG.snapshot) {
    const snap = decodeSnapshot(p)
    const phaseAtEntry = phase
    const row = snap.entities.find((e) => e.id === myId)
    if (!row) return
    const nowMs = performance.now()
    const ok = predictor.reconcile(row, snap.ackSeq, nowMs)
    const pos = { x: row.pos[0], y: row.pos[1], z: row.pos[2] }
    const vel = { x: row.vel[0], y: row.vel[1], z: row.vel[2] }
    const entry = { tick: snap.tick, ackSeq: snap.ackSeq, ok, pos, vel, atMs: tMs(recvNs) }
    snaps.push(entry)
    if (snaps.length > 400) snaps.shift()

    if (phase === 'walk' && nowMs - startedAtMs >= WALK_MS) {
      phase = 'stop'
      command = { moveX: 0, moveY: 0, lookDir: [1, 0, 0], actionMask: 0 }
      log(`walk ${WALK_MS} ms done — pos=(${pos.x.toFixed(3)},${pos.y.toFixed(3)},${pos.z.toFixed(3)}) vel=${len3(vel).toFixed(3)} m/s, switching to stand (+${entry.atMs} ms)`)
    }

    if (phase === 'stop') {
      const resting = len3(vel) < REST_VEL
      if (resting) restCount++
      else restCount = 0
      if (restCount >= 2) {
        lastRestSnap = entry
        doCorrupt(entry, recvNs)
      }
    }

    // The first EFFECTIVE reconcile after the corruption is the correction.
    if (phaseAtEntry === 'await_correction' && ok) {
      const post = predictor.stateRef
      const snapDistM = dist(post.pos, entry.pos)
      correctedSnap = {
        tick: entry.tick,
        ackSeq: entry.ackSeq,
        atMs: entry.atMs,
        authoritativePos: entry.pos,
        correctedPos: { x: post.pos.x, y: post.pos.y, z: post.pos.z },
        vel: entry.vel,
        snapDistM,
        elapsedFromCorruptMs: tMs(recvNs) - forced.atMs,
        tickDelta: entry.tick - lastRestSnap.tick,
      }
      log(`CORRECTED at tick ${entry.tick}: corrected pos=(${post.pos.x.toFixed(5)},${post.pos.y.toFixed(5)},${post.pos.z.toFixed(5)}) ` +
        `authoritative=(${entry.pos.x.toFixed(5)},${entry.pos.y.toFixed(5)},${entry.pos.z.toFixed(5)}) ` +
        `snapDist=${snapDistM.toExponential(3)} m, ${correctedSnap.elapsedFromCorruptMs} ms after corruption (tick ${lastRestSnap.tick} -> ${entry.tick})`)
      finish()
    }
  } else if (type === MSG.pong) {
    // inputs every tick already reset the server's read deadline; pongs not needed
  }
}

ws.onClose = (info) => {
  if (!finished) log(`ws closed early: ${info.reason}`)
}

// ------------------------------------------------------------- finish
function finish() {
  if (finished) return
  finished = true
  clearInterval(tickTimer)
  clearTimeout(corruptWatchdog)
  const c = correctedSnap
  check(
    'c3a: forced +4 m tangent applied to the live predicted state',
    forced.applied && !!c && Math.abs(forced.preDistM - CORRUPT_DIST) < 0.05,
    `preDist=${forced.preDistM.toFixed(4)} m (target ${CORRUPT_DIST}.000), dir=(${forced.dir.map((d) => d.toFixed(4)).join(',')}) at tick ${forced.tick}`,
  )
  check(
    'c3b: corrected state snaps <1e-3 m from the authoritative position in one step',
    !!c && c.snapDistM < SNAP_EPS,
    c
      ? `snapDist=${c.snapDistM.toExponential(3)} m (limit ${SNAP_EPS}) in a single effective reconcile; pre-correction distance ${forced.preDistM.toFixed(3)} m`
      : 'no effective reconcile observed after the corruption',
  )
  check(
    'c3c: correction within one tick (50 ms + network) of the forced state',
    !!c && c.elapsedFromCorruptMs <= ONE_TICK_NET_MS,
    c
      ? `corrupt -> corrected in ${c.elapsedFromCorruptMs} ms (budget ${ONE_TICK_NET_MS} ms = 1 tick + network), tick delta ${c.tickDelta}`
      : 'no correction observed',
  )
  pass = checks.length > 0 && checks.every((x) => x.pass)
  log(`OVERALL: ${pass ? 'PASS' : 'FAIL'}`)
  writeEvidence()
  ws.sendClose(1000)
  setTimeout(() => process.exit(pass ? 0 : 1), 150)
}

// ------------------------------------------------------------- evidence
function writeEvidence() {
  const ev = {
    criterion: 'C3 (ROADMAP #3): server-authoritative correction within one tick',
    run: { ts: new Date().toISOString(), endpoint: `ws://${HOST}:${PORT}/ws (nginx)`, name: NAME, entity_id: myId },
    method:
      'real client Predictor (client/src/net/predictor.ts) vs live server; client wire codec (net/protocol.ts) + client sim terrain (sim/); raw-TCP transport (test/lib/ws.mjs); loop mirrors client/src/main.ts tickStep/processSnapshot',
    phases: {
      walk_ms: WALK_MS,
      rest_vel_threshold_mps: REST_VEL,
      last_rest_snapshot: lastRestSnap
        ? { tick: lastRestSnap.tick, ackSeq: lastRestSnap.ackSeq, pos: lastRestSnap.pos, vel: lastRestSnap.vel, at_ms: lastRestSnap.atMs, auth_pos_m: +len3(lastRestSnap.pos).toFixed(5) }
        : null,
    },
    corruption: forced.applied
      ? { at_ms: forced.atMs, tick: forced.tick, offset_m: forced.offsetM, dir: forced.dir.map((d) => +d.toFixed(5)), pre_dist_m: +forced.preDistM.toFixed(5), forced_pos: forced.pos.map((d) => +d.toFixed(5)) }
      : null,
    correction: correctedSnap
      ? {
          snapshot_tick: correctedSnap.tick,
          snapshot_ack_seq: correctedSnap.ackSeq,
          snapshot_at_ms: correctedSnap.atMs,
          authoritative_pos: correctedSnap.authoritativePos,
          corrected_pos: correctedSnap.correctedPos,
          snap_dist_m: +correctedSnap.snapDistM.toExponential(4),
          tolerance_m: SNAP_EPS,
          elapsed_from_corrupt_ms: correctedSnap.elapsedFromCorruptMs,
          one_tick_budget_ms: ONE_TICK_NET_MS,
          tick_delta: correctedSnap.tickDelta,
          single_step: true,
        }
      : null,
    recent_snapshots: snaps.slice(-24).map((s) => ({ tick: s.tick, ackSeq: s.ackSeq, ok: s.ok, at_ms: s.atMs, vel_mps: +len3(s.vel).toExponential(3), pos_m: +len3(s.pos).toFixed(4) })),
    inputs_sent: inputsSent.length,
  }
  ev.overall = pass ? 'PASS' : 'FAIL'
  ev.checks = checks
  writeFileSync(EVIDENCE, JSON.stringify(ev, null, 2) + '\n')
  log(`evidence: ${EVIDENCE.pathname}`)
}

// ------------------------------------------------------------- start
async function main() {
  await ws.connect()
  log(`connected ws://${HOST}:${PORT}/ws (+0.000 ms)`)
  ws.sendBinary(Buffer.from(encodeHello(PROTOCOL_VERSION, NAME)))
  const deadline = Date.now() + 10_000
  for (;;) {
    if (simReady) break
    if (Date.now() > deadline) throw new Error('timeout waiting for hello_ack + terrain')
    await new Promise((r) => setTimeout(r, 2))
  }
  // walk + stop + corruption + correction run via the tick loop and the
  // snapshot handler; finish() (or the watchdog) ends the process.
  await new Promise(() => {})
}

main().catch((err) => {
  log(`ERROR: ${err.message}`)
  clearInterval(tickTimer)
  clearTimeout(corruptWatchdog)
  checks.push({ id: 'run', pass: false, detail: String(err) })
  pass = false
  writeEvidence()
  ws.kill()
  process.exit(1)
})