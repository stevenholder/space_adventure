#!/usr/bin/env node
/**
 * C10 loop-design global terrain survey — chunked scan over a 2 deg
 * lat/lon grid of the whole world.
 *
 * Purpose (STEP 1 of the C10 multi-leg lap design):
 *   - locate the Great Crater flanks (slope > 49.5 regions) so the loop
 *     legs can be routed around them;
 *   - map the terrain radius r(dir) so the flat-length budget
 *     (total ~942 m +/-5% = 894.9..989.1 m) can be computed per arc;
 *   - confirm the walkable corridors the fieldmap suggested (spawn-side
 *     meridian azimuths 13-18 / 28-29 / 40 / 267-296; far-pole cap).
 *
 * Crashing-safety: 10 lat rows per chunk (~1800 samples, ~2 s), each
 * chunk appends one JSON line per sample to test/out/t10-loop-scan.jsonl
 * with phase 'survey'. Re-invocation skips already-recorded (lat,lon)
 * pairs.
 *
 *   node test/t10/loop-survey.mjs
 *
 * Convention (matches test/lib/field.mjs + the t5 fieldmap): azimuth 0
 * -> +X, increasing toward -Z: dir = (cos(lat)cos(az), sin(lat),
 * -cos(lat)sin(az)); lat = angle from the equator toward the +Y (spawn)
 * pole, so lat=+90 is the spawn pole and lat=-90 the far pole.
 */
import { readFileSync, appendFileSync, existsSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const OUT = 'test/out/t10-loop-scan.jsonl'
const STEP = 2 // deg
const EPS = (2 * Math.PI) / 180 // normal_eps, GDD rule table

const done = new Set()
if (existsSync(OUT)) {
  for (const l of readFileSync(OUT, 'utf8').split('\n')) {
    if (!l.trim()) continue
    const r = JSON.parse(l)
    if (r.phase === 'survey') done.add(`${r.lat}:${r.lon}`)
  }
}

// Face-center radii (exact, for the report) — the six signed axis
// directions, plus the true far-pole value.
const centers = [
  ['+X', [1, 0, 0]],
  ['-X', [-1, 0, 0]],
  ['+Y spawn', [0, 1, 0]],
  ['-Y far', [0, -1, 0]],
  ['+Z', [0, 0, 1]],
  ['-Z', [0, 0, -1]],
]
for (const [name, d] of centers) {
  console.error(`${name} r = ${sampleRadius(F, d).toFixed(3)} m, face ${faceOf(d)}`)
}

function surveyChunk(latFrom, latTo) {
  const lines = []
  for (let lat = latFrom; lat <= latTo; lat += STEP) {
    for (let lon = 0; lon < 360; lon += STEP) {
      const key = `${lat}:${lon}`
      if (done.has(key)) continue
      const a = (lon * Math.PI) / 180
      const b = (lat * Math.PI) / 180
      const d = [Math.cos(b) * Math.cos(a), Math.sin(b), -Math.cos(b) * Math.sin(a)]
      const r = sampleRadius(F, d)
      const slope = slopeDeg(F, d, EPS)
      lines.push(JSON.stringify({ phase: 'survey', lat, lon, r: +r.toFixed(3), slope: +slope.toFixed(2), face: faceOf(d) }))
    }
  }
  if (lines.length) appendFileSync(OUT, lines.join('\n') + '\n')
  return lines.length
}

const t0 = process.hrtime.bigint()
let n = 0
for (let i = 0; i * 10 < 89; i++) {
  // 89 lat values: -88..88 step 2; chunks of 10 values
  const lo = -88 + i * 10 * STEP
  const hi = -88 + (i + 1) * 10 * STEP - STEP
  n += surveyChunk(lo, Math.min(hi, 88))
  const el = Number(process.hrtime.bigint() - t0) / 1e6
  console.error(`chunk ${i + 1}: lat ${lo}..${Math.min(hi, 88)} (${n} samples, ${(el / 1000).toFixed(1)} s)`)
}
console.error(`survey done: ${n} fresh samples in ${((Number(process.hrtime.bigint() - t0) / 1e6) / 1000).toFixed(1)} s -> ${OUT}`)