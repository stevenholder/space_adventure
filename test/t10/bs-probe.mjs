#!/usr/bin/env node
/**
 * C10 STEP 5 - passive WS position probe for the browser near-horizon
 * check. Joins the world (HELLO), verifies terrain sha16, then samples the
 * snapshot stream and appends one JSON line every 500 ms to
 * test/out/t10-bs-probe.jsonl:
 *   { t_ms, tick, ents: { id: { pos, quat } } }
 * The probe itself joins as a stationary player at spawn (third entity);
 * its id is the one whose pos never leaves [~0,150,~0].
 *
 *   node test/t10/bs-probe.mjs <durationS>
 */
import { createHash } from 'node:crypto'
import { WSClient } from '../lib/ws.mjs'
import { appendFileSync, writeFileSync, existsSync, unlinkSync, readFileSync } from 'node:fs'
import { MSG, PROTOCOL_VERSION, encodeHello, frame, decodeHelloAck, decodeTerrain, decodeSnapshot } from '../lib/wire.mjs'

const DURATION_S = Number(process.argv[2] || 45)
const OUT = 'test/out/t10-bs-probe.jsonl'
if (existsSync(OUT)) unlinkSync(OUT)

const ws = new WSClient('127.0.0.1', 18080, '/ws')
let helloAck = null
let terrainSha16 = null
let last = null // latest decoded snapshot
let myId = null

const t0 = Date.now()
ws.onMessage = (type, payload) => {
  if (type !== 0x2 || payload.length < 2) return
  const head = payload.readUInt16LE(0)
  const body = payload.subarray(2)
  if (head === MSG.HELLO_ACK) {
    helloAck = decodeHelloAck(body)
    myId = helloAck.entityId
  } else if (head === MSG.TERRAIN) {
    decodeTerrain(body)
    terrainSha16 = createHash('sha256').update(Buffer.from(body)).digest('hex').slice(0, 16)
  } else if (head === MSG.SNAPSHOT) {
    last = decodeSnapshot(body)
  }
}

const sample = setInterval(() => {
  if (!last) return
  const ents = {}
  for (const e of last.entities) ents[e.id] = { pos: e.pos, quat: e.quat }
  appendFileSync(OUT, JSON.stringify({ t_ms: Date.now() - t0, tick: last.tick, self: myId, ents }) + '\n')
}, 500)

async function main() {
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'QA-C10-bs-probe')))
  const deadline = Date.now() + 10000
  while ((!helloAck || !terrainSha16) && Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 20))
  }
  if (!helloAck || !terrainSha16) throw new Error('timeout waiting hello_ack/terrain')
  writeFileSync('test/out/t10-bs-probe-hello.json', JSON.stringify({ helloAck, terrainSha16, self: myId }, null, 1) + '\n')
  console.error(`probe: joined as entity ${myId}, terrain sha16=${terrainSha16} (expect c80269c44a757a8f)`)
  await new Promise((r) => setTimeout(r, DURATION_S * 1000))
  const lines = readFileSync(OUT, 'utf8').trim().split('\n')
  clearInterval(sample)
  const lastLine = JSON.parse(lines[lines.length - 1])
  const summary = {}
  for (const [id, e] of Object.entries(lastLine.ents)) {
    summary[id] = { pos: e.pos, quat: e.quat }
  }
  console.error(`probe: done, ${lines.length} samples, final entities: ${JSON.stringify(summary)}`)
  ws.destroy?.()
  process.exit(0)
}
main().catch((e) => {
  console.error('probe error:', e.message)
  process.exit(1)
})