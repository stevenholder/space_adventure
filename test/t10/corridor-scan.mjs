#!/usr/bin/env node
/**
 * C10 STEP 1 — spawn-side + far-side meridian corridor scan (corrected
 * field, 0.5 m resolution — 4x finer than the 2 deg survey).
 *
 * For every azimuth 0..359: walk the meridian from the +Y (spawn) pole
 * through the equator to the far (-Y) pole at 0.5 m flat steps and record
 * per-hemisphere maxSlope, maxDr, radius range, and the colat spans where
 * slope exceeds each gate (49.5 / 50.0). Also the equator point slope.
 *
 * Answers: which azimuths give a walkable leg1 (spawn->equator) / leg4
 * (equator->spawn) and which far-side meridians are walkable — the
 * first-order map for any 6-face loop, independent of the 4-leg grid's
 * A1=40 fixation (which the corrected survey has invalidated).
 *
 * Crash safety: chunks of 36 azimuths, one JSON line per azimuth appended
 * to test/out/t10-corridors.jsonl; re-run skips recorded azimuths.
 *
 *   node test/t10/corridor-scan.mjs
 */
import { readFileSync, appendFileSync, existsSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180
const DEG = 180 / Math.PI
const RAD = (d) => (d * Math.PI) / 180
const OUT = 'test/out/t10-corridors.jsonl'
const GATES = [49.5, 50.0]
const STEP = 0.5 // m

const done = new Set()
if (existsSync(OUT)) {
  for (const l of readFileSync(OUT, 'utf8').split('\n')) {
    if (!l.trim()) continue
    const r = JSON.parse(l)
    if (r.phase === 'corridor') done.add(r.az)
  }
}

// Meridian at fixed az: dir(colat) = (sin colat cos az, cos colat, -sin colat sin az)
// colat from +Y pole (0 at spawn, 180 at far pole).
function scanAz(az) {
  const a = RAD(az)
  const dirAt = (colatDeg) => {
    const c = RAD(colatDeg)
    return [Math.sin(c) * Math.cos(a), Math.cos(c), -Math.sin(c) * Math.sin(a)]
  }
  // Walk the meridian at flat 0.5 m steps. The meridian is analytic
  // (dirAt below), so advance colat by dd = ds / r-mean per step — no
  // tangent reconstruction (a d x (d+eps) cross product gives the
  // azimuthal direction, not the meridian tangent).
  const pts = []
  let colat = 0
  let r = sampleRadius(F, dirAt(0))
  pts.push({ colat, r, slope: slopeDeg(F, dirAt(0), EPS) })
  let s = 0
  while (colat < 180 - 1e-9) {
    const dd0 = STEP / r // radians
    const nc = Math.min(180, colat + dd0 * DEG)
    const rN = sampleRadius(F, dirAt(nc))
    const dd = STEP / (0.5 * (r + rN))
    const c2 = Math.min(180, colat + dd * DEG)
    const d2 = dirAt(c2)
    s += 0.5 * (r + rN) * dd
    const slope = slopeDeg(F, d2, EPS)
    pts.push({ colat: c2, r: rN, slope })
    r = rN
    colat = c2
  }
  // Per-hemisphere stats.
  const half = (lo, hi) => {
    const ps = pts.filter((p) => p.colat >= lo && p.colat <= hi)
    let mx = -Infinity
    let maxDr = 0
    let rmin = Infinity
    let rmax = -Infinity
    for (let i = 0; i < ps.length; i++) {
      mx = Math.max(mx, ps[i].slope)
      rmin = Math.min(rmin, ps[i].r)
      rmax = Math.max(rmax, ps[i].r)
      if (i > 0) maxDr = Math.max(maxDr, Math.abs(ps[i].r - ps[i - 1].r))
    }
    const spans = {}
    for (const g of GATES) {
      const out = []
      let start = null
      let smx = 0
      for (const p of ps) {
        if (p.slope > g) {
          if (start === null) {
            start = p.colat
            smx = p.slope
          } else smx = Math.max(smx, p.slope)
        } else if (start !== null) {
          out.push({ colat: `${start.toFixed(0)}-${p.colat.toFixed(0)}`, max: +smx.toFixed(2) })
          start = null
        }
      }
      if (start !== null) out.push({ colat: `${start.toFixed(0)}-180`, max: +smx.toFixed(2) })
      spans[g] = out
    }
    return { maxSlope: +mx.toFixed(2), maxDrM: +maxDr.toFixed(3), rMin: +rmin.toFixed(2), rMax: +rmax.toFixed(2), overGate: spans }
  }
  const eq = pts.reduce((best, p) => (Math.abs(p.colat - 90) < Math.abs(best.colat - 90) ? p : best))
  return { phase: 'corridor', az, spawn: half(0, 90), far: half(90, 180), eqSlope: +eq.slope.toFixed(2), eqR: +eq.r.toFixed(2), arcLen: +s.toFixed(1) }
}

let n = 0
for (let chunk = 0; chunk < 10; chunk++) {
  const from = chunk * 36
  const to = Math.min(360, from + 36)
  const t0 = process.hrtime.bigint()
  const lines = []
  for (let az = from; az < to; az++) {
    if (done.has(az)) continue
    lines.push(JSON.stringify(scanAz(az)))
    n++
  }
  if (lines.length) appendFileSync(OUT, lines.join('\n') + '\n')
  const el = Number(process.hrtime.bigint() - t0) / 1e6
  console.error(`chunk ${chunk + 1}: az ${from}..${to} (${lines.length} fresh, ${(el / 1000).toFixed(1)} s)`)
}
console.error(`corridor scan done: ${n} fresh azimuths -> ${OUT}`)