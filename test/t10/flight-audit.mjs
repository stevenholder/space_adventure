#!/usr/bin/env node
/**
 * C10 STEP 3 - flight audit for the post-fix verdict.
 *
 * Decision rule (Main): flights are allowed ONLY as GDD AIR cases -
 * kink scars / lips > max_step / rim drops verified from the FIELD
 * (feature < ~0.25 m scale, local max-min over ~+/-10 m). Zero
 * tolerance for flights on smooth <= 50 deg slopes.
 *
 * Checks per candidate (sim result + waypoint file):
 *   1. HARD: min h >= 0 every tick (no fall-through)
 *   2. NO SEAM HITCH: no flight within +/-2 ticks of any face crossing
 *      or leg junction tick window
 *   3. per flight: ticks, s, az/lat, maxH, hang time, and field
 *      evidence at the flight start: slopeDeg, local max-min radius
 *      over +/-10 m along the path, max 0.3 m window drop in +/-5 m
 *      (the scarp signature), feature classification
 *
 *   node test/t10/flight-audit.mjs <simResult> <wpsFile> [label]
 */
import { readFileSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const [simPath, wpsPath, label = 'cand'] = process.argv.slice(2)
const res = JSON.parse(readFileSync(simPath, 'utf8'))
const wpFile = JSON.parse(readFileSync(wpsPath, 'utf8'))
const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180
const DEG = 180 / Math.PI
const RAD = (d) => (d * Math.PI) / 180

const t = res.telemetry
const azOf = (x, y, z) => {
  const a = Math.atan2(-z, x) * DEG
  return (a + 360) % 360
}
const latOf = (x, y, z) => {
  const r = Math.hypot(x, y, z)
  return Math.asin(Math.max(-1, Math.min(1, y / r))) * DEG
}

// 1. HARD: fall-through
let minH = Infinity
for (const p of t) if (p.h < minH) minH = p.h
const noFallThrough = minH >= 0

// 2. flights
const flights = []
let cur = null
for (const p of t) {
  if (p.h > 0.05) {
    if (!cur) cur = { from: p.tick, to: p.tick, sFrom: p.s, sTo: p.s, maxH: 0, minH: 0, x: p.x, y: p.y, z: p.z }
    cur.to = p.tick
    cur.sTo = p.s
    cur.maxH = Math.max(cur.maxH, p.h)
    cur.minH = Math.min(cur.minH, p.h)
  } else if (cur) {
    flights.push(cur)
    cur = null
  }
}
if (cur) flights.push(cur)

// 3. crossing + junction tick windows
const crossings = res.crossings.map((c) => ({ kind: 'crossing', ...c }))
// junctions: leg boundaries from the waypoint file (first wp of each seg)
const junctions = []
{
  let prevSeg = null
  for (const w of wpFile.wps) {
    if (w.seg !== prevSeg) {
      if (prevSeg !== null) junctions.push({ kind: 'junction', label: `${prevSeg}->${w.seg}`, s: w.s })
      prevSeg = w.seg
    }
  }
}
const sToTick = (s) => {
  // telemetry s is piecewise-constant per advance; find the first tick with sProg >= s
  for (let i = 0; i < t.length; i++) if (t[i].s >= s) return i + 1 // tick numbers are 1-based
  return t.length
}
const windows = [
  ...crossings.map((c) => ({ ...c, tick: sToTick(c.s) })),
  ...junctions.map((j) => ({ ...j, tick: sToTick(j.s) })),
]
const HITCH = 2
const hitches = []
for (const f of flights) {
  for (const w of windows) {
    if (f.from - HITCH <= w.tick && w.tick <= f.to + HITCH) hitches.push({ flight: `${f.from}-${f.to}`, window: `${w.kind}@${w.tick} (s=${w.s})` })
  }
}

// 4. field evidence per flight
const wpByS = wpFile.wps
const wpAt = (s) => {
  // nearest waypoint by s (binary-ish linear; n ~ 800)
  let best = wpByS[0]
  let bd = Infinity
  for (const w of wpByS) {
    const d = Math.abs(w.s - s)
    if (d < bd) {
      bd = d
      best = w
    }
  }
  return best
}
const audited = flights.map((f) => {
  const s0 = (f.sFrom + f.sTo) / 2
  const w = wpAt(s0)
  const dir = norm3([w.dir[0], w.dir[1], w.dir[2]])
  const slope = slopeDeg(F, dir, EPS)
  const face = faceOf(dir)
  // along-path profile: +/-10 m at 0.2 m resolution; need a local tangent frame
  // build orthonormal: t = tangent (dir rotated 90 deg toward next wp), b = up x t
  const up = norm3([w.dir[0], w.dir[1], w.dir[2]])
  const next = wpAt(s0 + 10)
  const t1 = norm3([
    next.dir[0] * w.r - w.dir[0] * next.r,
    next.dir[1] * w.r - w.dir[1] * next.r,
    next.dir[2] * w.r - w.dir[2] * next.r,
  ])
  const t2 = norm3([t1[1] * up[2] - t1[2] * up[1], t1[2] * up[0] - t1[0] * up[2], t1[0] * up[1] - t1[1] * up[0]])
  const at = (d) => {
    // point on the sphere ~d m along the tangent from the flight site
    const ang = d / (w.r || 150)
    const cs = Math.cos(ang)
    const ss = Math.sin(ang)
    return norm3([up[0] * cs + t2[0] * ss, up[1] * cs + t2[1] * ss, up[2] * cs + t2[2] * ss])
  }
  const profile = []
  for (let d = -10; d <= 10.01; d += 0.2) {
    const dd = at(d)
    profile.push({ d, r: sampleRadius(F, dd) })
  }
  let maxWindowDrop = 0
  let maxWindowRise = 0
  for (let i = 0; i + 3 < profile.length; i += 3) {
    const dr = profile[i + 3].r - profile[i].r // 0.6 m window
    if (-dr > maxWindowDrop) maxWindowDrop = -dr
    if (dr > maxWindowRise) maxWindowRise = dr
  }
  // 0.3 m windows
  let maxDrop03 = 0
  let maxRise03 = 0
  for (let i = 0; i + 1 < profile.length; i++) {
    const dr = profile[i + 1].r - profile[i].r // 0.2 m step
    if (-dr > maxDrop03) maxDrop03 = -dr
    if (dr > maxRise03) maxRise03 = dr
  }
  const rs = profile.map((p) => p.r)
  const localMax = Math.max(...rs)
  const localMin = Math.min(...rs)
  // classify
  const feature =
    maxDrop03 > 0.12
      ? 'scarp/rim-drop (0.2 m step drop > 0.12 m)'
      : maxRise03 > 0.12
        ? 'kink lip (0.2 m step rise > 0.12 m)'
        : slope <= 50
          ? 'SMOOTH SLOPE - ESCALATE'
          : 'steep smooth (verify)'
  return {
    ticks: [f.from, f.to],
    hangS: +(((f.to - f.from + 1) * 0.05).toFixed(2)),
    s: +s0.toFixed(2),
    az: +azOf(f.x, f.y, f.z).toFixed(2),
    lat: +latOf(f.x, f.y, f.z).toFixed(2),
    face,
    maxH: +f.maxH.toFixed(3),
    minH: +f.minH.toFixed(3),
    slopeDeg: +slope.toFixed(2),
    localMaxMin10m: +(localMax - localMin).toFixed(2),
    maxDrop02m: +maxDrop03.toFixed(3),
    maxRise02m: +maxRise03.toFixed(3),
    maxDrop06m: +maxWindowDrop.toFixed(3),
    feature,
  }
})

const report = {
  label,
  params: wpFile.params,
  hard: {
    noFallThrough,
    minH: +minH.toFixed(4),
    endpointDelta: res.endpointDelta,
    pathLen: res.pathLen,
    faces: res.faces,
    crossings: res.crossings.length,
    stalled: res.assertResults.find((a) => a.label === 'stall')?.ok === false,
  },
  seamHitches: hitches,
  noSeamHitch: hitches.length === 0,
  flights: audited,
  flightCount: flights.length,
  totalHangS: +audited.reduce((a, f) => a + f.hangS, 0).toFixed(2),
  maxFlightH: +Math.max(0, ...audited.map((f) => f.maxH)).toFixed(3),
  verdict:
    noFallThrough &&
    hitches.length === 0 &&
    audited.every((f) => f.feature !== 'SMOOTH SLOPE - ESCALATE' && f.slopeDeg <= 50.0)
      ? 'QUALIFYING (GDD AIR cases only)'
      : 'REVIEW NEEDED',
}
console.log(JSON.stringify(report, null, 1))