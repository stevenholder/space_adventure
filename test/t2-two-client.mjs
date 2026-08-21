/**
 * C2 — two clients see each other, on terrain (ROADMAP criterion 2).
 *
 *   Two browser clients connect to the same server; each sees the other's
 *   character within 1 s of spawn, standing on the terrain rather than
 *   floating or sunk in it.
 *
 * Method (QA-STATUS resume item 4):
 *   A connects via ws://127.0.0.1:3000/ws (nginx same-origin path).
 *   B connects 500 ms later via ws://127.0.0.1:18080/ws (direct WS) so both
 *   wire paths are exercised. All measurements use the ns timestamps from
 *   test/lib/ws.mjs (one test process, one clock).
 *
 * Checks:
 *   (a) B's initial spawn burst (before its first snapshot) contains A's
 *       spawn — OR — A receives B's spawn ≤1 s after B connects;
 *   (b) each client sees the other's character within 1 s of spawn
 *       (spawn message AND first snapshot row with the other's id);
 *   (c) both spawn positions stand ON the terrain: |len(pos) −
 *       sampleRadius(world, pos)| ≤ 0.05 m against the captured live field
 *       test/out/world-seed1337.json (test/lib/field.mjs).
 *   Field sanity: live terrain payloads (both paths) sha256_16-match the
 *   captured file and hello_ack.world_seed == 1337.
 *
 * Deterministic and isolated: fresh entity ids per run, no shared mutable
 * state, pinned world (server-side seed 1337, captured field).
 *
 *   node test/t2-two-client.mjs
 */
import { readFileSync, writeFileSync, mkdirSync, appendFileSync } from 'node:fs'
import crypto from 'node:crypto'
import { WSClient } from './lib/ws.mjs'
import {
  MSG,
  PROTOCOL_VERSION,
  encodeHello,
  frame,
  decodeHelloAck,
  decodeSpawn,
  decodeSnapshot,
} from './lib/wire.mjs'
import { loadField, sampleRadius } from './lib/field.mjs'

const OUT = new URL('./out/', import.meta.url)
mkdirSync(OUT, { recursive: true })
const EVENTS = new URL('./out/t2-two-client-events.jsonl', import.meta.url)
const EVIDENCE = new URL('./out/t2-two-client.json', import.meta.url)

const HOST = '127.0.0.1'
const A_PORT = 3000 // nginx /ws path
const B_PORT = 18080 // direct WS path
const B_DELAY_MS = 500
const SEE_MS = 1000 // "within 1 s"
const ON_SURFACE_EPS = 0.05 // m

const ns = () => process.hrtime.bigint()
const ms = (a, b) => Number(b - a) / 1e6
const sha16 = (buf) => crypto.createHash('sha256').update(buf).digest('hex').slice(0, 16)
const len3 = (v) => Math.hypot(v[0], v[1], v[2])
const log = (line) => console.log(`[t2 ${new Date().toISOString()}] ${line}`)
const event = (client, type, detail, atNs) => {
  appendFileSync(EVENTS, JSON.stringify({ client, type, detail, atNs: String(atNs) }) + '\n')
}

// ------------------------------------------------------------- world field
const worldJson = JSON.parse(readFileSync(new URL('./out/world-seed1337.json', import.meta.url), 'utf8'))
const field = loadField(worldJson)

// ------------------------------------------------------------- recorder
function makeRecorder(name) {
  return {
    name,
    helloAck: null,
    terrainPayload: null,
    terrainSha: null,
    burst: [], // spawn rows received before the first snapshot
    spawns: new Map(), // entityId -> { atNs, name }
    snapshots: [], // { atNs, tick, ids:Set }
    firstSnapWith: new Map(), // entityId -> { atNs, pos, vel } (first snapshot row seen)
    firstSnapshotAtNs: null,
    pongAt: [],
    closeInfo: null,
  }
}

function wireRecorder(ws, rec) {
  ws.onMessage = (_op, payload, recvNs) => {
    const type = payload.readUInt16LE(0)
    const p = payload.slice(2)
    if (type === MSG.HELLO_ACK) {
      rec.helloAck = decodeHelloAck(p)
      event(rec.name, 'hello_ack', rec.helloAck, recvNs)
    } else if (type === MSG.TERRAIN) {
      rec.terrainPayload = p
      rec.terrainSha = sha16(p)
      event(rec.name, 'terrain', { bytes: p.length, sha256_16: rec.terrainSha }, recvNs)
    } else if (type === MSG.SPAWN) {
      const sp = decodeSpawn(p)
      const beforeFirstSnap = rec.firstSnapshotAtNs === null
      if (beforeFirstSnap) rec.burst.push({ id: sp.entityId, atNs: recvNs, name: sp.data })
      rec.spawns.set(sp.entityId, { atNs: recvNs, name: sp.data })
      event(rec.name, 'spawn', { id: sp.entityId, name: sp.data, inBurst: beforeFirstSnap }, recvNs)
    } else if (type === MSG.SNAPSHOT) {
      const snap = decodeSnapshot(p)
      const ids = new Set(snap.entities.map((e) => e.id))
      if (rec.firstSnapshotAtNs === null) rec.firstSnapshotAtNs = recvNs
      rec.snapshots.push({ atNs: recvNs, tick: snap.tick, ids })
      for (const e of snap.entities) {
        if (!rec.firstSnapWith.has(e.id)) {
          rec.firstSnapWith.set(e.id, { atNs: recvNs, pos: e.pos, vel: e.vel })
        }
      }
      event(rec.name, 'snapshot', { tick: snap.tick, count: snap.entities.length, ids: [...ids] }, recvNs)
    } else if (type === MSG.PONG) {
      rec.pongAt.push(recvNs)
      event(rec.name, 'pong', {}, recvNs)
    } else if (type === MSG.DESPAWN) {
      event(rec.name, 'despawn', { id: p.readUInt32LE(0) }, recvNs)
    } else {
      event(rec.name, `type_${type.toString(16)}`, {}, recvNs)
    }
  }
  ws.onClose = (info) => {
    rec.closeInfo = info
  }
}

// ------------------------------------------------------------- connect
async function join(port, name) {
  const ws = new WSClient(HOST, port)
  const rec = makeRecorder(name)
  wireRecorder(ws, rec)
  const t0 = ns()
  await ws.connect()
  const tOpen = ns()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, name)))
  const tHello = ns()
  // wait for the full handshake: hello_ack + terrain + own spawn + first snapshot
  const deadline = Date.now() + 10_000
  for (;;) {
    const ownId = rec.helloAck?.entityId ?? -1
    const haveAll =
      rec.helloAck && rec.terrainPayload && rec.spawns.has(ownId) && rec.firstSnapshotAtNs !== null
    if (haveAll) break
    if (Date.now() > deadline) {
      throw new Error(
        `${name}: handshake incomplete: helloAck=${!!rec.helloAck} terrain=${!!rec.terrainPayload} ` +
          `ownSpawn=${rec.spawns.has(ownId)} firstSnap=${rec.firstSnapshotAtNs !== null}`,
      )
    }
    await new Promise((r) => setTimeout(r, 2))
  }
  return { ws, rec, t0, tOpen, tHello }
}

// ------------------------------------------------------------- main
const checks = []
let evidence = null
function check(id, pass, detail) {
  checks.push({ id, pass, detail })
  log(`${pass ? 'PASS' : 'FAIL'} ${id} ${detail}`)
}

try {
  log(`world file: face_grid=${worldJson.face_grid} radius=[${worldJson.radius_min},${worldJson.radius_max}] ` +
    `sha256_16=${worldJson.meta.sha256_16} seed=${worldJson.meta.world_seed}`)

  // ---- A (nginx path) ----
  const A = await join(A_PORT, 'qa-t2-a')
  log(`A connected :${A_PORT} (nginx /ws) — handshake ${ms(A.t0, A.tHello).toFixed(1)} ms, ` +
    `entity_id=${A.rec.helloAck.entityId} world_seed=${A.rec.helloAck.worldSeed} tick_hz=${A.rec.helloAck.tickHz}`)

  // ---- B (direct path), +500 ms after A's open ----
  const tSchedule = ns()
  const delayLeft = B_DELAY_MS - ms(A.tOpen, tSchedule)
  if (delayLeft > 0) await new Promise((r) => setTimeout(r, delayLeft))
  const B = await join(B_PORT, 'qa-t2-b')
  log(`B connected :${B_PORT} (direct /ws) +${ms(A.tOpen, B.tOpen).toFixed(1)} ms after A — ` +
    `entity_id=${B.rec.helloAck.entityId} world_seed=${B.rec.helloAck.worldSeed} tick_hz=${B.rec.helloAck.tickHz}`)

  const aId = A.rec.helloAck.entityId
  const bId = B.rec.helloAck.entityId

  // ---- wait until both have seen the other (spawn msg + snapshot row) ----
  const deadline = Date.now() + 10_000
  for (;;) {
    const ok =
      A.rec.spawns.has(bId) && B.rec.spawns.has(aId) &&
      A.rec.firstSnapWith.has(bId) && B.rec.firstSnapWith.has(aId)
    if (ok) break
    if (Date.now() > deadline) {
      throw new Error(
        `mutual visibility timeout: A.sawB_spawn=${A.rec.spawns.has(bId)} B.sawA_spawn=${B.rec.spawns.has(aId)} ` +
          `A.sawB_snap=${A.rec.firstSnapWith.has(bId)} B.sawA_snap=${B.rec.firstSnapWith.has(aId)}`,
      )
    }
    await new Promise((r) => setTimeout(r, 2))
  }

  // settle: one more second of snapshots, then record
  await new Promise((r) => setTimeout(r, 1000))

  // ---------------------------------------------------------------- (a)
  const aInBurst = B.rec.burst.find((s) => s.id === aId)
  const bSpawnAtA = A.rec.spawns.get(bId)
  const aSpawnAtB = B.rec.spawns.get(aId)
  const burstOk = !!aInBurst && aInBurst.atNs < B.rec.firstSnapshotAtNs
  const aSeesBMs = bSpawnAtA ? ms(B.t0, bSpawnAtA.atNs) : null
  check(
    'a: B burst contains A spawn OR A gets B spawn <=1 s',
    burstOk || (aSeesBMs !== null && aSeesBMs <= SEE_MS),
    `burst_contains_A=${burstOk}${aInBurst ? ` (B recv ${ms(B.t0, aInBurst.atNs).toFixed(1)} ms after B connect)` : ''}; ` +
      `A recv B spawn ${aSeesBMs !== null ? aSeesBMs.toFixed(1) : 'n/a'} ms after B connect (limit 1000)`,
  )

  // ---------------------------------------------------------------- (b)
  const bSeesAMs = aSpawnAtB ? ms(B.t0, aSpawnAtB.atNs) : null
  const bSeesASnap = B.rec.firstSnapWith.get(aId)
  const bSeesASnapMs = bSeesASnap ? ms(B.t0, bSeesASnap.atNs) : null
  const aSeesBSnap = A.rec.firstSnapWith.get(bId)
  const aSeesBSnapMs = aSeesBSnap ? ms(B.t0, aSeesBSnap.atNs) : null
  const bSeesA =
    bSeesAMs !== null && bSeesAMs <= SEE_MS && bSeesASnapMs !== null && bSeesASnapMs <= SEE_MS
  const aSeesB =
    aSeesBMs !== null && aSeesBMs <= SEE_MS && aSeesBSnapMs !== null && aSeesBSnapMs <= SEE_MS
  check(
    'b: each client sees the other within 1 s',
    bSeesA && aSeesB,
    `B sees A: spawn ${bSeesAMs !== null ? bSeesAMs.toFixed(1) : 'n/a'} ms, first snapshot row ${bSeesASnapMs !== null ? bSeesASnapMs.toFixed(1) : 'n/a'} ms (limit 1000); ` +
      `A sees B: spawn ${aSeesBMs !== null ? aSeesBMs.toFixed(1) : 'n/a'} ms, first snapshot row ${aSeesBSnapMs !== null ? aSeesBSnapMs.toFixed(1) : 'n/a'} ms (limit 1000)`,
  )

  // ---------------------------------------------------------------- (c)
  const faceOfDir = (pos) => {
    const n = len3(pos)
    const d = [pos[0] / n, pos[1] / n, pos[2] / n]
    const ax = [Math.abs(d[0]), Math.abs(d[1]), Math.abs(d[2])]
    const axx = [0, 0, 1, 1, 2, 2]
    let f = 0
    for (let i = 1; i < 6; i++) if (ax[axx[i]] > ax[axx[f]]) f = i
    return f
  }
  const onSurface = (pos) => {
    const r = len3(pos)
    const expected = sampleRadius(field, pos)
    return { pos, r, expected, dev: Math.abs(r - expected), face: faceOfDir(pos), pass: Math.abs(r - expected) <= ON_SURFACE_EPS }
  }
  const aPos = A.rec.firstSnapWith.get(aId).pos
  const bPosAtA = A.rec.firstSnapWith.get(bId).pos
  const bPosAtB = B.rec.firstSnapWith.get(bId).pos
  const sA = onSurface(aPos)
  const sB1 = onSurface(bPosAtA)
  const sB2 = onSurface(bPosAtB)
  const onSurfPass = sA.pass && sB1.pass && sB2.pass
  check(
    'c: both spawn positions on terrain (|len - sampleRadius| <= 0.05 m)',
    onSurfPass,
    `A: r=${sA.r.toFixed(4)} vs field ${sA.expected.toFixed(4)} dev=${sA.dev.toExponential(2)} m (face ${sA.face}); ` +
      `B@A: r=${sB1.r.toFixed(4)} dev=${sB1.dev.toExponential(2)} m; B@B: r=${sB2.r.toFixed(4)} dev=${sB2.dev.toExponential(2)} m`,
  )

  // ---------------------------------------------------------------- field sanity
  const worldSha = worldJson.meta.sha256_16
  const fieldOk =
    A.rec.terrainSha === worldSha &&
    B.rec.terrainSha === worldSha &&
    A.rec.helloAck.worldSeed === 1337 &&
    B.rec.helloAck.worldSeed === 1337 &&
    A.rec.helloAck.tickHz === 20 &&
    B.rec.helloAck.tickHz === 20
  check(
    'field: live terrain == captured world-seed1337.json (both paths), seed 1337, 20 Hz',
    fieldOk,
    `A sha256_16=${A.rec.terrainSha} B sha256_16=${B.rec.terrainSha} world=${worldSha}; ` +
      `seeds ${A.rec.helloAck.worldSeed}/${B.rec.helloAck.worldSeed}; tick_hz ${A.rec.helloAck.tickHz}/${B.rec.helloAck.tickHz}`,
  )

  evidence = {
    criterion: 'C2 (ROADMAP #2): two clients see each other <=1 s of spawn, on terrain',
    run: { ts: new Date().toISOString(), world_sha256_16: worldJson.meta.sha256_16, world_seed: worldJson.meta.world_seed },
    clients: {
      A: { endpoint: `ws://127.0.0.1:${A_PORT}/ws (nginx)`, name: 'qa-t2-a', entity_id: aId, connect_start_ns: String(A.t0), open_ns: String(A.tOpen), hello_ns: String(A.tHello), terrain_sha256_16: A.rec.terrainSha },
      B: { endpoint: `ws://127.0.0.1:${B_PORT}/ws (direct)`, name: 'qa-t2-b', entity_id: bId, connect_start_ns: String(B.t0), open_ns: String(B.tOpen), hello_ns: String(B.tHello), connect_after_A_open_ms: Number(ms(A.tOpen, B.tOpen).toFixed(2)), terrain_sha256_16: B.rec.terrainSha },
    },
    timing_ms: {
      a_handshake: Number(ms(A.t0, A.tHello).toFixed(3)),
      b_handshake: Number(ms(B.t0, B.tHello).toFixed(3)),
      b_burst_A_spawn_after_b_connect: aInBurst ? Number(ms(B.t0, aInBurst.atNs).toFixed(2)) : null,
      a_recv_b_spawn_after_b_connect: aSeesBMs !== null ? Number(aSeesBMs.toFixed(2)) : null,
      b_recv_a_spawn_after_b_connect: bSeesAMs !== null ? Number(bSeesAMs.toFixed(2)) : null,
      b_first_snapshot_with_a_after_b_connect: bSeesASnapMs !== null ? Number(bSeesASnapMs.toFixed(2)) : null,
      a_first_snapshot_with_b_after_b_connect: aSeesBSnapMs !== null ? Number(aSeesBSnapMs.toFixed(2)) : null,
    },
    on_surface: {
      A: { pos: sA.pos, radius_m: +sA.r.toFixed(4), field_radius_m: +sA.expected.toFixed(4), deviation_m: +sA.dev.toExponential(3) },
      B_in_A_snapshot: { pos: sB1.pos, radius_m: +sB1.r.toFixed(4), field_radius_m: +sB1.expected.toFixed(4), deviation_m: +sB1.dev.toExponential(3) },
      B_own_snapshot: { pos: sB2.pos, radius_m: +sB2.r.toFixed(4), field_radius_m: +sB2.expected.toFixed(4), deviation_m: +sB2.dev.toExponential(3) },
      tolerance_m: ON_SURFACE_EPS,
    },
  }
} catch (err) {
  log(`ERROR: ${err.message}`)
  checks.push({ id: 'run', pass: false, detail: String(err) })
}

// ---------------------------------------------------------------- report
const pass = checks.length > 0 && checks.every((c) => c.pass)
log(`OVERALL: ${pass ? 'PASS' : 'FAIL'}`)
if (evidence) {
  evidence.overall = pass ? 'PASS' : 'FAIL'
  evidence.checks = checks
  writeFileSync(EVIDENCE, JSON.stringify(evidence, null, 2) + '\n')
  log(`evidence: ${EVIDENCE.pathname}`)
}
process.exit(pass ? 0 : 1)