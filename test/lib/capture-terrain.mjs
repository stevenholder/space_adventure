/**
 * Connect to the live server, handshake, capture the terrain wire message.
 * Emits:
 *   test/out/world-seed<seed>.json — { face_grid, radius_min, radius_max, radii }
 *   test/out/terrain-raw-<sha256>.bin — raw terrain payload bytes
 * Prints hello_ack values and payload size.
 */
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import crypto from 'node:crypto'
import { WSClient } from './ws.mjs'
import { MSG, PROTOCOL_VERSION, encodeHello, frame, decodeHelloAck, decodeTerrain } from './wire.mjs'

const [host = '127.0.0.1', port = '18080'] = process.argv.slice(2)
const name = process.argv.length > 4 ? process.argv[4] : 'qa-capture'

const ws = new WSClient(host, Number(port))
const got = {}

ws.onMessage = (_op, payload) => {
  const type = payload.readUInt16LE(0)
  const p = payload.slice(2)
  if (type === MSG.HELLO_ACK) got.helloAck = decodeHelloAck(p)
  else if (type === MSG.TERRAIN) got.terrain = p
}

ws.onClose = (info) => {
  console.error('closed early:', info.reason)
  process.exit(2)
}

const deadline = setTimeout(() => {
  console.error('timeout waiting for terrain')
  process.exit(2)
}, 10_000)

await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, name)))

while (!got.terrain) await new Promise((r) => setTimeout(r, 5))
clearTimeout(deadline)

const t = decodeTerrain(got.terrain)
const sha = crypto.createHash('sha256').update(got.terrain).digest('hex').slice(0, 16)
const outDir = new URL('../out/', import.meta.url)
mkdirSync(outDir, { recursive: true })

const world = {
  face_grid: t.faceGrid,
  radius_min: t.radiusMin,
  radius_max: t.radiusMax,
  radii: Array.from(t.radii),
  meta: {
    captured_from: `ws://${host}:${port}/ws`,
    world_seed: got.helloAck.worldSeed,
    tick_hz: got.helloAck.tickHz,
    server_ver: got.helloAck.serverVer,
    bytes: t.bytes,
    sha256_16: sha,
  },
}
const worldPath = new URL(`../out/world-seed${got.helloAck.worldSeed}.json`, import.meta.url)
writeFileSync(worldPath, JSON.stringify(world))
writeFileSync(new URL(`../out/terrain-raw-${sha}.bin`, import.meta.url), got.terrain)
console.log(JSON.stringify({
  hello_ack: got.helloAck,
  terrain: { face_grid: t.faceGrid, radius_min: t.radiusMin, radius_max: t.radiusMax, bytes: t.bytes, sha256_16: sha },
  world_file: worldPath.pathname,
}))
ws.kill()
