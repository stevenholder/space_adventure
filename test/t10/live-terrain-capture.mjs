#!/usr/bin/env node
/**
 * C10 - capture the CURRENT live terrain from the server (without touching
 * the shared world-seed1337.json capture) and diff it against the captured
 * file, cell by cell.
 *
 *   node test/t10/live-terrain-capture.mjs [host] [port]
 *
 * Emits test/out/t10-live-world.json (+ meta) and prints the diff summary.
 */
import { readFileSync, writeFileSync } from 'node:fs'
import crypto from 'node:crypto'
import { WSClient } from '../lib/ws.mjs'
import { MSG, PROTOCOL_VERSION, encodeHello, frame, decodeHelloAck, decodeTerrain } from '../lib/wire.mjs'

const host = process.argv[2] || '127.0.0.1'
const port = Number(process.argv[3] || 18080)
const ws = new WSClient(host, port)
const got = {}
ws.onMessage = (_op, payload) => {
  const type = payload.readUInt16LE(0)
  const p = payload.subarray(2)
  if (type === MSG.HELLO_ACK) got.helloAck = decodeHelloAck(p)
  else if (type === MSG.TERRAIN) got.terrain = p
}
ws.onClose = () => {
  console.error('closed early')
  process.exit(2)
}
const deadline = setTimeout(() => {
  console.error('timeout')
  process.exit(2)
}, 10_000)
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'qa-c10-capture')))
while (!got.terrain) await new Promise((r) => setTimeout(r, 5))
clearTimeout(deadline)

const t = decodeTerrain(got.terrain)
const sha = crypto.createHash('sha256').update(got.terrain).digest('hex').slice(0, 16)
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
    captured_at: new Date().toISOString(),
  },
}
writeFileSync('test/out/t10-live-world.json', JSON.stringify(world))
console.error(`live terrain: seed=${got.helloAck.worldSeed} faceGrid=${t.faceGrid} sha16=${sha} -> test/out/t10-live-world.json`)

// diff against the captured (pre-retune) file
try {
  const old = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
  if (old.face_grid !== t.faceGrid) {
    console.error(`face_grid differs: old ${old.face_grid} vs live ${t.faceGrid}`)
  } else {
    let diff = 0
    let maxD = 0
    let minR = Infinity
    let maxR = -Infinity
    const cells = []
    const g = t.faceGrid
    for (let i = 0; i < old.radii.length; i++) {
      const d = t.radii[i] - old.radii[i]
      if (d !== 0) {
        diff++
        const ad = Math.abs(d)
        if (ad > maxD) maxD = ad
        if (cells.length < 5000) cells.push({ i, face: Math.floor(i / (g * g)), old: old.radii[i], live: t.radii[i] })
      }
    }
    for (const r of t.radii) {
      const m = world.radius_min + (r / 65535) * (world.radius_max - world.radius_min)
      if (m < minR) minR = m
      if (m > maxR) maxR = m
    }
    const faces = new Set(cells.map((c) => c.face))
    console.error(
      JSON.stringify(
        {
          diffCells: diff,
          totalCells: old.radii.length,
          maxDeltaU16: maxD,
          liveRadiusRange: [minR.toFixed(2), maxR.toFixed(2)],
          facesTouched: [...faces].sort((a, b) => a - b),
          sampleCells: cells.slice(0, 12),
        },
        null,
        1,
      ),
    )
  }
} catch (e) {
  console.error('no old capture to diff:', e.message)
}
ws.kill()