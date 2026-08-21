#!/usr/bin/env node
/**
 * C10 STEP 5 - scripted WS walker: the "remote player" for the browser
 * near-horizon check. Connects to the real server (WS 127.0.0.1:18080 /ws),
 * walks the geodesic from spawn to Q (test/out/t10-bs-pair.json: 45.9 m,
 * max slope 20.1 deg at Q) at walk speed 4.5 m/s, and HOLDS at Q
 * (closed-loop stop at d(Q) < 0.6 m on the server-authoritative pos) so a
 * browser client at P (az315, s=27, flat) can photograph it 23 m away -
 * at its horizon (23.7 m), standing upright on the 20.1 deg local normal.
 *
 * Walks driven at 20 Hz on the snapshot cadence (same pattern as
 * live-lap.mjs). Per-sample telemetry appended to
 * test/out/t10-bs-walker.jsonl; final summary in
 * test/out/t10-bs-walker-final.json.
 *
 *   node test/t10/bs-walker.mjs <holdS>
 */
import { WSClient } from '../lib/ws.mjs'
import { MSG, PROTOCOL_VERSION, encodeHello, encodeInput, frame, decodeHelloAck, decodeTerrain, decodeSnapshot, norm3, len3 } from '../lib/wire.mjs'
import { appendFileSync, writeFileSync, existsSync, unlinkSync, readFileSync } from 'node:fs'
import { createHash } from 'node:crypto'

const HOLD_S = Number(process.argv[2] || 20)
const STOP_D = 0.6 // stop when within 0.6 m of Q (closed loop on server pos)
// pair placement (test/out/t10-bs-pair.json): camera P on az315 s=27 (flat),
// remote Q at 20.1 deg slope, 23 m near-horizon ahead of the camera (+X view)
const PAIR = JSON.parse(readFileSync('test/out/t10-bs-pair.json', 'utf8'))
const TARGET = PAIR.Q
const OUT = 'test/out/t10-bs-walker.jsonl'
if (existsSync(OUT)) unlinkSync(OUT)

const ws = new WSClient('127.0.0.1', 18080, '/ws')
let helloAck = null
let terrainSha16 = null
let myId = null
let seq = 0
let lastSnap = null
let lastPos = null
const t0 = Date.now()
let walkEndTick = null
const tele = []

function sOf(p) {
  const r = len3(p)
  const c = Math.max(-1, Math.min(1, p[1] / r))
  return r * Math.acos(c)
}

ws.onMessage = (type, payload) => {
  if (type !== 0x2 || payload.length < 2) return
  const head = payload.readUInt16LE(0)
  const body = payload.subarray(2)
  if (head === MSG.HELLO_ACK) {
    helloAck = decodeHelloAck(body)
    myId = helloAck.entityId
    walkEndTick = null
  } else if (head === MSG.TERRAIN) {
    decodeTerrain(body)
    terrainSha16 = createHash('sha256').update(Buffer.from(body)).digest('hex').slice(0, 16)
  } else if (head === MSG.SNAPSHOT && myId !== null) {
    const s = decodeSnapshot(body)
    const me = s.entities.find((e) => e.id === myId)
    if (!me) return
    lastSnap = s
    if (walkEndTick === null) walkEndTick = s.tick + 30 * 20 // 30 s safety cap
    const dQ = len3([me.pos[0] - TARGET[0], me.pos[1] - TARGET[1], me.pos[2] - TARGET[2]])
    const walking = s.tick < walkEndTick && dQ > STOP_D
    if (!lastPos) lastPos = me.pos
    const dv = len3([me.pos[0] - lastPos[0], me.pos[1] - lastPos[1], me.pos[2] - lastPos[2]])
    lastPos = me.pos
    tele.push({ t_ms: Date.now() - t0, tick: s.tick, walking, dQ: +dQ.toFixed(2), s: +sOf(me.pos).toFixed(2), pos: me.pos, quat: me.quat, dv: +dv.toFixed(4) })
  }
}
// ---------------------------------------------------------------- drive
// 20 Hz input driver (same pattern as live-lap.mjs): reads the newest
// server-authoritative snapshot and sends INPUT on the snapshot cadence.
function driveTick() {
  if (!lastSnap || myId === null) return
  const s = lastSnap
  const me = s.entities.find((e) => e.id === myId)
  if (!me) return
  if (walkEndTick === null) walkEndTick = s.tick + 30 * 20
  const dQ = len3([me.pos[0] - TARGET[0], me.pos[1] - TARGET[1], me.pos[2] - TARGET[2]])
  const walking = s.tick < walkEndTick && dQ > STOP_D
  const look = norm3([TARGET[0] - me.pos[0], TARGET[1] - me.pos[1], TARGET[2] - me.pos[2]])
  ws.sendBinary(frame(MSG.INPUT, encodeInput(0, walking ? 1 : 0, look, 0, (seq++) % 65536)))
}
let driveTimer = null


async function main() {
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'QA-C10-bs-walker')))
  const deadline = Date.now() + 10000
  while ((!helloAck || !terrainSha16) && Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 20))
  }
  if (!helloAck || !terrainSha16) throw new Error('timeout waiting hello_ack/terrain')
  driveTimer = setInterval(driveTick, 50)
  await new Promise((r) => setTimeout(r, (15 + HOLD_S) * 1000 + 500))
  for (let i = 0; i < tele.length; i += 10) appendFileSync(OUT, JSON.stringify(tele[i]) + '\n')
  const last = tele[tele.length - 1]
  writeFileSync('test/out/t10-bs-walker-final.json', JSON.stringify({
    entity: myId,
    terrainSha16,
    ticks: tele.length,
    walkMode: 'closed-loop: walk until within ' + STOP_D + ' m of Q, 30 s safety cap',
    holdS: HOLD_S,
    final: { s: last.s, pos: last.pos, quat: last.quat },
    maxDv: Math.max(...tele.map((x) => x.dv)),
  }, null, 1) + '\n')
  console.error(`walker: done, ${tele.length} ticks, final s=${last.s} m pos=${JSON.stringify(last.pos)} quat=${JSON.stringify(last.quat)}`)
  clearInterval(driveTimer)
  ws.sendClose(1000)
  setTimeout(() => ws.kill(), 300).unref()
  process.exit(0)
}
main().catch((e) => {
  console.error('walker error:', e.message)
  process.exit(1)
})