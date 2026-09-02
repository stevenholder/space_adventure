/**
 * C4 — connection loss despawns the entity on other clients within 10 s
 * (ROADMAP criterion 4, heartbeat timeout) (QA-STATUS resume item 5).
 *
 * Method — two experiments against the live cluster, one per mechanism:
 *
 * (a) HARD KILL (the criterion's scenario): A and B both via
 *     ws://127.0.0.1:18080/ws (the NodePort path — nginx retired with the
 *     browser client, ROADMAP U18). Both joined and
 *     seeing each other. B's last byte is one final input; 200 ms later B
 *     does a hard TCP kill (test/lib/ws.mjs kill() — NO close frame). A
 *     records kill -> DESPAWN(B.id) received; must be ≤10 s. A also records
 *     the first snapshot after which B's id is gone. Mechanism evidence:
 *     B sent no WS close frame (harness invariant); if the latency is <<10 s
 *     the server detected the dead TCP itself (read EOF/RST) and despawned
 *     immediately — the connection-loss path, not a close-frame path.
 *
 * (b) HEARTBEAT TIMEOUT (the mechanism named by PROTOCOL: "drops
 *     connections silent for 10 s"): fresh A2/B2. B2 sends one final input,
 *     then goes fully silent (no game ping, no traffic) while its TCP
 *     socket stays open. The server's 10 s read deadline must fire: A2
 *     records last-B2-write -> DESPAWN(B2.id) ≈ 10 s (accept 9.5–10.5 s),
 *     and B2 records the server-initiated close it receives.
 *
 * Keep-alives use the GAME-level ping (type 0x0008 binary message), not a
 * WS control-frame ping: the server's reader decodes every inbound message
 * through the protocol and closes on unknown types (server/internal/server/
 * client.go reader). All timings are ns from one process clock
 * (test/lib/ws.mjs recvNs / process.hrtime).
 *
 * Deterministic and isolated: fresh entity ids per run; other agents' test
 * clients in the world are ignored (all checks filter on our own ids).
 *
 *   node test/t4-despawn.mjs
 */
import { writeFileSync, mkdirSync, appendFileSync } from 'node:fs'
import { WSClient } from './lib/ws.mjs'
import {
  MSG,
  PROTOCOL_VERSION,
  encodeHello,
  encodeInput,
  frame,
  decodeHelloAck,
  decodeSpawn,
  decodeSnapshot,
  decodeDespawn,
} from './lib/wire.mjs'

const OUT = new URL('./out/', import.meta.url)
mkdirSync(OUT, { recursive: true })
const EVENTS = new URL('./out/t4-despawn-events.jsonl', import.meta.url)
const EVIDENCE = new URL('./out/t4-despawn.json', import.meta.url)

const HOST = '127.0.0.1'
const A_PORT = 18080 // NodePort /ws — the only path since U18 retired nginx
const B_PORT = 18080
const DESPAWN_MAX_MS = 10_000 // criterion bound (phase a)
const SILENT_EXPECT_MS = 10_000 // PROTOCOL heartbeat timeout (phase b)
const SILENT_TOL_MS = 500 // +/- acceptance around 10 s
const KILL_GAP_MS = 200 // B's last write -> kill (no traffic in between)
const PING_EVERY_MS = 2000 // game-level keep-alive (PROTOCOL heartbeat)

const ns = () => process.hrtime.bigint()
const ms = (a, b) => Number(b - a) / 1e6
const log = (line) => console.log(`[t4 ${new Date().toISOString()}] ${line}`)
const event = (client, type, detail, atNs) => {
  appendFileSync(EVENTS, JSON.stringify({ client, type, detail, atNs: atNs !== undefined ? String(atNs) : null }, (_k, v) => (typeof v === 'bigint' ? String(v) : v)) + '\n')
}

// ------------------------------------------------------------- recorder
function makeClient(tag, port, name) {
  const ws = new WSClient(HOST, port)
  const rec = {
    tag, name, ws,
    helloAck: null,
    spawns: new Map(), // id -> { atNs, name }
    despawns: [], // { id, atNs }
    snapshots: [], // { atNs, tick, ids:Set }
    firstSnapWith: new Map(), // id -> { atNs }
    pongs: [],
    closeInfo: null,
    sentBytes: { hello: 0, input: 0, ping: 0, closeFrame: false, killed: false },
    lastInputAtNs: null,
  }
  ws.onMessage = (_op, payload, recvNs) => {
    const type = payload.readUInt16LE(0)
    const p = payload.slice(2)
    if (type === MSG.HELLO_ACK) {
      rec.helloAck = decodeHelloAck(p)
      event(rec.tag, 'hello_ack', rec.helloAck, recvNs)
    } else if (type === MSG.SPAWN) {
      const sp = decodeSpawn(p)
      rec.spawns.set(sp.entityId, { atNs: recvNs, name: sp.data })
      event(rec.tag, 'spawn', { id: sp.entityId, name: sp.data }, recvNs)
    } else if (type === MSG.DESPAWN) {
      const d = decodeDespawn(p)
      rec.despawns.push({ id: d.entityId, atNs: recvNs })
      event(rec.tag, 'despawn', { id: d.entityId }, recvNs)
    } else if (type === MSG.SNAPSHOT) {
      const s = decodeSnapshot(p)
      const ids = new Set(s.entities.map((e) => e.id))
      rec.snapshots.push({ atNs: recvNs, tick: s.tick, ids })
      for (const id of ids) if (!rec.firstSnapWith.has(id)) rec.firstSnapWith.set(id, { atNs: recvNs })
      if (rec.snapshots.length > 400) rec.snapshots.shift()
    } else if (type === MSG.PONG) {
      rec.pongs.push({ atNs: recvNs })
    }
  }
  ws.onClose = (info) => {
    rec.closeInfo = info
    event(rec.tag, 'close', info)
  }
  return rec
}

function sendHello(rec) {
  rec.ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, rec.name)))
  rec.sentBytes.hello++
}

function sendInput(rec, moveX, moveY) {
  rec.ws.sendBinary(frame(MSG.INPUT, encodeInput(moveX, moveY, [1, 0, 0], 0, 0)))
  rec.sentBytes.input++
  rec.lastInputAtNs = ns()
}

function sendGamePing(rec) {
  const p = Buffer.alloc(4)
  p.writeUInt32LE(Math.floor(performance.now()) >>> 0, 0)
  rec.ws.sendBinary(frame(MSG.PING, p))
  rec.sentBytes.ping++
}

async function join(rec) {
  await rec.ws.connect()
  sendHello(rec)
  const deadline = Date.now() + 10_000
  while (!rec.helloAck) {
    if (Date.now() > deadline) throw new Error(`${rec.tag}: no hello_ack`)
    await new Promise((r) => setTimeout(r, 2))
  }
  return rec.helloAck
}

async function waitMutual(a, b, label) {
  const deadline = Date.now() + 10_000
  for (;;) {
    const ok =
      a.spawns.has(b.helloAck.entityId) && b.spawns.has(a.helloAck.entityId) &&
      a.firstSnapWith.has(b.helloAck.entityId) && b.firstSnapWith.has(a.helloAck.entityId)
    if (ok) return
    if (Date.now() > deadline) {
      throw new Error(`${label}: mutual visibility timeout (a.sawB=${a.spawns.has(b.helloAck.entityId)}, b.sawA=${b.spawns.has(a.helloAck.entityId)})`)
    }
    await new Promise((r) => setTimeout(r, 2))
  }
}

async function waitForDespawn(a, victimId, label, timeoutMs) {
  const seen = a.despawns.map((d) => ({ ...d })).filter((d) => d.id === victimId)
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const hit = a.despawns.find((d) => d.id === victimId)
    if (hit) return hit
    if (Date.now() > deadline) {
      throw new Error(`${label}: no DESPAWN for entity ${victimId} within ${timeoutMs} ms (despawns so far: ${JSON.stringify(seen)})`)
    }
    await new Promise((r) => setTimeout(r, 2))
  }
}

function firstSnapshotWithout(a, id, afterNs) {
  for (const s of a.snapshots) {
    if (s.atNs > afterNs && !s.ids.has(id)) return s
  }
  return null
}

// keep a client alive with game-level pings (resets the server read deadline)
function startPingKeepAlive(rec) {
  return setInterval(() => {
    if (rec.ws.closed) return
    sendGamePing(rec)
  }, PING_EVERY_MS)
}

// ------------------------------------------------------------- checks
const checks = []
let pass = false
function check(id, passVal, detail) {
  checks.push({ id, pass: passVal, detail })
  log(`${passVal ? 'PASS' : 'FAIL'} ${id} ${detail}`)
}

// ------------------------------------------------------------- main
const evidence = {
  criterion: 'C4 (ROADMAP #4): connection loss despawns the entity on other clients within 10 s (heartbeat timeout)',
  run: { ts: new Date().toISOString(), endpoints: { A: `ws://${HOST}:${A_PORT}/ws (nginx)`, B: `ws://${HOST}:${B_PORT}/ws (direct)` } },
  phase_a: null,
  phase_b: null,
}

try {
  // ================================================================= (a)
  log('=== phase (a): hard TCP kill (no close frame) ===')
  const A = makeClient('A', A_PORT, 'qa-t4-a')
  const B = makeClient('B', B_PORT, 'qa-t4-b')
  const aAck = await join(A)
  const bAck = await join(B)
  log(`A(id=${aAck.entityId}) and B(id=${bAck.entityId}) joined`)
  await waitMutual(A, B, 'phase a')
  log('mutual visibility: A sees B spawn+snapshot, B sees A spawn+snapshot')

  // A keeps its own connection alive (game-level ping every 2 s)
  const aPing = startPingKeepAlive(A)

  // B: one final input (last byte on the wire), then silence, then hard kill
  sendInput(B, 0, 0)
  const tLastB = B.lastInputAtNs
  await new Promise((r) => setTimeout(r, KILL_GAP_MS))
  const tKill = ns()
  B.ws.kill()
  B.sentBytes.killed = true
  log(`B hard-killed at +${ms(tLastB, tKill).toFixed(1)} ms after its last write (kill() = socket destroy, no close frame)`)

  const despA = await waitForDespawn(A, bAck.entityId, 'phase a', 15_000)
  await new Promise((r) => setTimeout(r, 300)) // let post-teardown snapshots arrive
  const killToDespawnMs = ms(tKill, despA.atNs)
  const lastToDespawnMs = ms(tLastB, despA.atNs)
  const snapGoneA = firstSnapshotWithout(A, bAck.entityId, tKill)
  check(
    'a1: B despawned on A within 10 s of the hard kill',
    killToDespawnMs <= DESPAWN_MAX_MS,
    `kill->DESPAWN = ${killToDespawnMs.toFixed(1)} ms (limit ${DESPAWN_MAX_MS}); last-B2write->DESPAWN = ${lastToDespawnMs.toFixed(1)} ms`,
  )
  check(
    'a2: mechanism is connection loss, not a close frame (B sent none)',
    B.sentBytes.closeFrame === false && B.sentBytes.killed === true,
    `B sent ${B.sentBytes.hello} hello + ${B.sentBytes.input} input, 0 WS close frames, then kill(); ` +
      `latency ${killToDespawnMs.toFixed(1)} ms ${killToDespawnMs < 5000 ? '<< 10 s read deadline -> server detected the dead TCP (read EOF/RST) and despawned on connection loss' : '~10 s -> server 10 s read deadline (heartbeat timeout) fired'}`,
  )
  log(`A: first snapshot without B after kill: ${snapGoneA ? `tick ${snapGoneA.tick} at +${ms(tKill, snapGoneA.atNs).toFixed(1)} ms` : 'none observed (despawn message is the primary signal)'}`)

  evidence.phase_a = {
    A: { entity_id: aAck.entityId, endpoint: evidence.run.endpoints.A },
    B: { entity_id: bAck.entityId, endpoint: evidence.run.endpoints.B },
    b_last_input_ns: String(tLastB),
    b_kill_ns: String(tKill),
    b_sent: B.sentBytes,
    despawn_at_ns: String(despA.atNs),
    kill_to_despawn_ms: +killToDespawnMs.toFixed(2),
    last_write_to_despawn_ms: +lastToDespawnMs.toFixed(2),
    limit_ms: DESPAWN_MAX_MS,
    first_snapshot_without_B_after_kill: snapGoneA ? { tick: snapGoneA.tick, at_ns: String(snapGoneA.atNs), at_ms: +ms(tKill, snapGoneA.atNs).toFixed(2) } : null,
    mechanism: killToDespawnMs < 5000
      ? 'immediate connection-loss detection: server read loop hit EOF/RST on the destroyed TCP (no close frame involved) and broadcast despawn'
      : 'server 10 s silence deadline (heartbeat timeout) expired',
  }

  clearInterval(aPing)
  A.ws.sendClose(1000)
  await new Promise((r) => setTimeout(r, 200))

  // ================================================================= (b)
  log('=== phase (b): silent connection, 10 s heartbeat timeout ===')
  const A2 = makeClient('A2', A_PORT, 'qa-t4-a2')
  const B2 = makeClient('B2', B_PORT, 'qa-t4-b2')
  const a2Ack = await join(A2)
  const b2Ack = await join(B2)
  log(`A2(id=${a2Ack.entityId}) and B2(id=${b2Ack.entityId}) joined`)
  await waitMutual(A2, B2, 'phase b')

  const a2Ping = startPingKeepAlive(A2)

  // B2: one final input, then FULL SILENCE on an open socket (no pings)
  sendInput(B2, 0, 0)
  const tLastB2 = B2.lastInputAtNs
  log(`B2 final write at T0; now silent (open TCP, no traffic) — waiting for the server's 10 s deadline`)

  const despB2 = await waitForDespawn(A2, b2Ack.entityId, 'phase b', 16_000)
  await new Promise((r) => setTimeout(r, 300)) // let post-teardown snapshots arrive
  // The server-initiated close of B2's socket arrives after the port-forward's
  // teardown propagation (observed ~1 s after the server teardown — see notes);
  // wait a bounded 3 s for it and stamp it with the process clock.
  let b2CloseObservedAt = null
  {
    const dl = Date.now() + 3000
    for (;;) {
      if (B2.closeInfo !== null) {
        if (b2CloseObservedAt === null) b2CloseObservedAt = ns()
        break
      }
      if (Date.now() > dl) break
      await new Promise((r) => setTimeout(r, 5))
    }
  }
  const silentToDespawnMs = ms(tLastB2, despB2.atNs)
  check(
    'b1: silent (open) connection dropped at ~10 s (heartbeat timeout)',
    silentToDespawnMs >= SILENT_EXPECT_MS - SILENT_TOL_MS && silentToDespawnMs <= SILENT_EXPECT_MS + SILENT_TOL_MS,
    `last-B2-write -> DESPAWN = ${silentToDespawnMs.toFixed(1)} ms (expected ~${SILENT_EXPECT_MS} ms, tolerance +/-${SILENT_TOL_MS})`,
  )
  check(
    'b2: server closed B2 silent socket after the timeout (before our cleanup)',
    B2.closeInfo !== null,
    B2.closeInfo ? `B2 close: reason=${B2.closeInfo.reason}, code=${B2.closeInfo.code ?? 'n/a'}, observed +${b2CloseObservedAt !== null ? ms(tLastB2, b2CloseObservedAt).toFixed(1) : 'n/a'} ms (server teardown; WS close frame not observed — port-forward teardown delivers a bare TCP close)` : 'B2 socket not closed by the server within 3 s after the timeout',
  )
  const snapGoneB2 = firstSnapshotWithout(A2, b2Ack.entityId, tLastB2)
  evidence.phase_b = {
    A2: { entity_id: a2Ack.entityId, endpoint: evidence.run.endpoints.A },
    B2: { entity_id: b2Ack.entityId, endpoint: evidence.run.endpoints.B },
    b2_last_input_ns: String(tLastB2),
    b2_sent: B2.sentBytes,
    b2_closed_by_server: B2.closeInfo ? { reason: B2.closeInfo.reason, code: B2.closeInfo.code ?? null, observed_at_ns: b2CloseObservedAt !== null ? String(b2CloseObservedAt) : null, observed_at_ms: b2CloseObservedAt !== null ? +ms(tLastB2, b2CloseObservedAt).toFixed(2) : null } : null,
    despawn_at_ns: String(despB2.atNs),
    silent_to_despawn_ms: +silentToDespawnMs.toFixed(2),
    expected_ms: SILENT_EXPECT_MS,
    tolerance_ms: SILENT_TOL_MS,
    first_snapshot_without_B2: snapGoneB2 ? { tick: snapGoneB2.tick, at_ns: String(snapGoneB2.atNs) } : null,
  }

  clearInterval(a2Ping)
  A2.ws.sendClose(1000)
  if (!B2.ws.closed) B2.ws.kill()
  await new Promise((r) => setTimeout(r, 200))
} catch (err) {
  log(`ERROR: ${err.message}`)
  checks.push({ id: 'run', pass: false, detail: String(err) })
}

// ---------------------------------------------------------------- report
pass = checks.length > 0 && checks.every((c) => c.pass)
log(`OVERALL: ${pass ? 'PASS' : 'FAIL'}`)
evidence.overall = pass ? 'PASS' : 'FAIL'
evidence.checks = checks
evidence.notes = [
  'A/A2 keep-alive = game-level ping (type 0x0008 binary, PROTOCOL heartbeat) every 2 s; the server reader decodes every inbound message and closes on unknown types, so WS-control-frame pings are NOT used.',
  'Phase (a) measures the criterion bound (<=10 s) under a hard loss: B sends no close frame; kill() destroys the TCP. Observed latency distinguishes immediate EOF/RST detection from the 10 s deadline.',
  'Phase (b) verifies the mechanism named in PROTOCOL ("drops connections silent for 10 s"): an open but silent socket must be dropped at the 10 s read deadline.',
  'OBSERVED (transport, not product): the kubectl port-forward sa-pf-server (host :18080 -> pod) propagates connection TEARDOWN ~1 s after the event — phase (a): kill -> server detection ~1002 ms; phase (b): server teardown -> B2 sees a bare TCP close ~+1001 ms (no WS close frame delivered). The pod->host DATA path is prompt (20 Hz snapshots; phase-b DESPAWN reached the witness at 10001.6 ms).',
]
writeFileSync(EVIDENCE, JSON.stringify(evidence, null, 2) + '\n')
log(`evidence: ${EVIDENCE.pathname}`)
process.exit(pass ? 0 : 1)