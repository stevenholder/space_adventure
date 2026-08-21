#!/usr/bin/env node
/**
 * C10 STEP 5 - read the latest bs-probe sample and compute the great-circle
 * arc distance s (m) from spawn (0,150,0) for each entity. Prints JSON:
 *   { s: {id: s}, stop: bool, stopId }
 * stop = some entity other than the probe (self) and any excluded id has
 * reached stopS.
 *
 *   node test/t10/bs-pos.mjs <stopS> [excludeId]
 */
import { readFileSync } from 'node:fs'

const stopS = Number(process.argv[2] || 24)
const exclude = process.argv[3] ? Number(process.argv[3]) : null
const probeId = JSON.parse(readFileSync('test/out/t10-bs-probe-hello.json', 'utf8')).self
const line = JSON.parse(readFileSync('test/out/t10-bs-probe.jsonl', 'utf8').trim().split('\n').pop())
const s = {}
let stop = false
let stopId = null
for (const [id, e] of Object.entries(line.ents)) {
  const p = e.pos
  const r = Math.hypot(p[0], p[1], p[2])
  const c = Math.max(-1, Math.min(1, p[1] / r))
  s[id] = +(r * Math.acos(c)).toFixed(2)
  if (Number(id) !== probeId && Number(id) !== exclude && s[id] >= stopS) {
    stop = true
    stopId = Number(id)
  }
}
console.log(JSON.stringify({ s, stop, stopId, tick: line.tick }))