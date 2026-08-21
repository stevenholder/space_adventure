#!/usr/bin/env node
/**
 * C10 STEP 2 re-tune - scarp-aware candidate prefilter for the far legs.
 *
 * The locked loop (test/out/t10-lap-waypoints.json) is:
 *   leg1: SPAWN -> eq(az 30)        [meridian 90 deg]
 *   j1  : eq(az 30) -> eq(az Q2)    [equatorial geodesic]
 *   leg2: eq(az Q2) -> P3 (farPt(colat, az)) [great circle]
 *   leg3: P3 -> eq(az 225.5)        [great circle]
 *   j3  : eq(az 225.5) -> eq(az 220)[equatorial geodesic]
 *   leg4: eq(az 220) -> SPAWN       [meridian 90 deg]
 * (Q4 fixed at 225.5 - the +Z face sliver crossing; Q5/leg4 at 220.)
 *
 * The 0.25 m wall gate (dr > 0.3) of the original scan missed both kink
 * scars (features < 0.25 m). This prefilter samples at 0.1 m and gates:
 *   - maxSlope <= 49.5 deg (GDD 50 hard limit, margin)
 *   - max step-UP per 0.1 m sample step <= 0.28 m (step-up must clear;
 *     wall-safe for the glued walker)
 *   - max step-DOWN per 0.1 m sample step <= 0.28 m (a steeper drop
 *     launches a ballistic flight - the glue band cannot hold off an edge)
 *   - per great-circle leg span <= 90.5 deg
 *   - total >= 942 m (ROADMAP #10: "at least one full circuit")
 *   - 6 distinct faces
 *
 *   node test/t10/scarp-scan.mjs
 */
import { readFileSync, existsSync, appendFileSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180
const DEG = 180 / Math.PI
const RAD = (d) => (d * Math.PI) / 180
const SPAWN = [0, 1, 0]
const OUT = process.argv[2] === 'fine' ? 'test/out/t10-scarp-scan-fine.jsonl' : 'test/out/t10-scarp-scan.jsonl'

const eqPt = (az) => norm3([Math.cos(RAD(az)), 0, -Math.sin(RAD(az))])
const farPt = (phi, az) =>
  norm3([Math.sin(RAD(phi)) * Math.cos(RAD(az)), -Math.cos(RAD(phi)), -Math.sin(RAD(phi)) * Math.sin(RAD(az))])

const STEP_M = 0.1
const STEPUP_GATE = 0.5
const STEPDOWN_GATE = 0.30
const SLOPE_GATE = 49.5
const QUARTER_MAX = 90.5
const TOTAL_LO = 942

// grid over the free parameters (A1 meridian, Q2 az, P3 colat/az, Q5 meridian)
// FINE grid around cand03 (a1=30 q2=50 p3c=28 p3az=198.5 q5=215) - the
// best structural candidate: full lap, closed, 6 faces, no stall, 2
// residual step-down flights (highland rim + north rim band).
const FINE = process.argv[2] === 'fine'
const A1AZ = FINE ? [30, 35] : [30, 35]
const Q2AZ = FINE ? [45, 50, 55] : [50, 55, 60]
const P3COLAT = FINE ? [28] : [28, 34]
const P3AZ = FINE ? [188.5, 193.5, 198.5, 203.5, 208.5] : [138.5, 168.5, 198.5]
const Q5AZ = FINE ? [210, 215, 220] : [215, 220, 225]

function buildArcs(a1az, q2az, p3colat, p3az, q5az) {
  const p3 = farPt(p3colat, p3az)
  return [
    { label: 'leg1', p0: SPAWN, p1: eqPt(a1az) },
    { label: 'j1', p0: eqPt(a1az), p1: eqPt(q2az) },
    { label: 'leg2', p0: eqPt(q2az), p1: p3 },
    { label: 'leg3', p0: p3, p1: eqPt(225.5) },
    { label: 'j3', p0: eqPt(225.5), p1: eqPt(q5az) },
    { label: 'leg4', p0: eqPt(q5az), p1: SPAWN },
  ]
}

function evaluateArcs(arcs) {
  let total = 0
  let maxSlope = 0
  let maxStepUp = 0
  let maxStepDown = 0
  let maxSpan = 0
  const faces = new Set()
  const worst = { slope: null, stepUp: null, stepDown: null }
  for (const sg of arcs) {
    const c = Math.max(-1, Math.min(1, sg.p0[0] * sg.p1[0] + sg.p0[1] * sg.p1[1] + sg.p0[2] * sg.p1[2]))
    const theta = Math.acos(c)
    maxSpan = Math.max(maxSpan, theta * DEG)
    // Rodrigues axis: norm(p0 x p1); rotating p0 by +theta about it lands on p1.
    const n = norm3([
      sg.p0[1] * sg.p1[2] - sg.p0[2] * sg.p1[1],
      sg.p0[2] * sg.p1[0] - sg.p0[0] * sg.p1[2],
      sg.p0[0] * sg.p1[1] - sg.p0[1] * sg.p1[0],
    ])
    const rotate = (p, a) => {
      const cs = Math.cos(a)
      const ss = Math.sin(a)
      const d = n[0] * p[0] + n[1] * p[1] + n[2] * p[2]
      return norm3([
        p[0] * cs + (n[1] * p[2] - n[2] * p[1]) * ss + n[0] * d * (1 - cs),
        p[1] * cs + (n[2] * p[0] - n[0] * p[2]) * ss + n[1] * d * (1 - cs),
        p[2] * cs + (n[0] * p[1] - n[1] * p[0]) * ss + n[2] * d * (1 - cs),
      ])
    }
    const estLen = theta * 150
    const N = Math.max(2, Math.round(estLen / STEP_M))
    const rs = []
    let prevR = null
    for (let k = 0; k <= N; k++) {
      const dir = k === 0 ? norm3(sg.p0) : k === N ? norm3(sg.p1) : rotate(sg.p0, (theta * k) / N)
      const r = sampleRadius(F, dir)
      const sl = slopeDeg(F, dir, EPS)
      faces.add(faceOf(dir))
      if (sl > maxSlope) {
        maxSlope = sl
        worst.slope = { seg: sg.label, frac: +(k / N).toFixed(3), slope: +sl.toFixed(2), dir: dir.map((x) => +x.toFixed(4)) }
      }
      if (k > 0) total += 0.5 * (prevR + r) * (theta / N)
      prevR = r
      rs.push(r)
    }
    // step clearance over the sim's 0.3 m step (3 x 0.1 m samples)
    for (let k = 3; k < rs.length; k++) {
      const dr3 = rs[k] - rs[k - 3]
      if (dr3 > maxStepUp) {
        maxStepUp = dr3
        worst.stepUp = { seg: sg.label, frac: +(k / rs.length).toFixed(3), dr: +dr3.toFixed(3) }
      }
      if (-dr3 > maxStepDown) {
        maxStepDown = -dr3
        worst.stepDown = { seg: sg.label, frac: +(k / rs.length).toFixed(3), dr: +dr3.toFixed(3) }
      }
    }
  }
  return { total, maxSlope, maxStepUp, maxStepDown, maxSpan, faces: [...faces].sort((a, b) => a - b), worst }
}

// resume-safe: skip already-scanned (q2az, p3colat, p3az) combos
const done = new Set()
if (existsSync(OUT)) {
  for (const line of readFileSync(OUT, 'utf8').split('\n')) {
    if (!line) continue
    try {
      const o = JSON.parse(line)
      if (o.phase === 'scarp' && o.a1az !== undefined) done.add(`${o.a1az}|${o.q2az}|${o.p3colat}|${o.p3az}|${o.q5az}`)
    } catch {
      // ignore
    }
  }
}

const rows = []
for (const a1az of A1AZ)
  for (const q2az of Q2AZ)
    for (const p3colat of P3COLAT)
      for (const p3az of P3AZ)
        for (const q5az of Q5AZ) {
          const key = `${a1az}|${q2az}|${p3colat}|${p3az}|${q5az}`
          if (done.has(key)) continue
          const ev = evaluateArcs(buildArcs(a1az, q2az, p3colat, p3az, q5az))
      const pass =
        ev.maxSlope <= SLOPE_GATE &&
        ev.maxStepUp <= STEPUP_GATE &&
        ev.maxStepDown <= STEPDOWN_GATE &&
        ev.maxSpan <= QUARTER_MAX &&
        ev.total >= TOTAL_LO &&
        ev.faces.length === 6
          const row = {
            phase: 'scarp',
            a1az,
            q2az,
            p3colat,
            p3az,
            q5az,
        total: +ev.total.toFixed(2),
        maxSlope: +ev.maxSlope.toFixed(2),
        maxStepUp: +ev.maxStepUp.toFixed(3),
        maxStepDown: +ev.maxStepDown.toFixed(3),
        maxSpan: +ev.maxSpan.toFixed(2),
        faces: ev.faces,
        pass,
        worst: ev.worst,
      }
      rows.push(row)
      appendFileSync(OUT, JSON.stringify(row) + '\n')
    }

rows.sort((a, b) => Number(b.pass) - Number(a.pass) || a.maxSlope - b.maxSlope)
console.error(
  `scarp-scan: ${rows.length} candidates, ${rows.filter((r) => r.pass).length} pass -> ${OUT}`,
)
for (const r of rows.slice(0, 10)) {
  console.error(
    `  a1=${r.a1az} q2=${r.q2az} p3c=${r.p3colat} p3az=${r.p3az} q5=${r.q5az} total=${r.total} maxSlope=${r.maxSlope} stepUp=${r.maxStepUp} stepDown=${r.maxStepDown} span=${r.maxSpan} pass=${r.pass}`,
  )
  if (!r.pass) {
    console.error(
      `    worst: slope@${JSON.stringify(r.worst.slope)} stepUp@${JSON.stringify(r.worst.stepUp)} stepDown@${JSON.stringify(r.worst.stepDown)}`,
    )
  }
}