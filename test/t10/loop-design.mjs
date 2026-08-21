#!/usr/bin/env node
/**
 * C10 STEP 1 — closed-loop lap designer (the multi-leg fallback).
 *
 * A single tilted great circle is INFEASIBLE on this terrain (test/out/
 * t10-alpha-scan.jsonl: 36 six-face coarse candidates, best maxSlope 54.5
 * deg on the Great Crater flanks; dense 0.25-deg escalation: all 24
 * six-face full-eval candidates 53-73 deg; the walkable six-face pockets
 * are 991-996 m flat > the 989.1 m ceiling). This tool designs the
 * fallback: a closed loop of <= 4 great-circle quarter-legs (each <= 90
 * deg of its circle) plus SHORT GEODESIC JOINTS (documented):
 *
 *   Leg 1: meridian A1 (90 deg arc)  spawn (0,1,0) -> Q1 equator az A1
 *   J1   : equatorial geodesic arc   Q1 (az A1) -> Q2 (az A2)   [short]
 *   Leg 2: great circle (<= 90 deg)  Q2 -> P3 (far side, colat phi3
 *                                    from the far pole, az B3)
 *   Leg 3: great circle (<= 90 deg)  P3 -> Q4 equator az A5
 *   J3   : equatorial geodesic arc   Q4 (az A5) -> Q5 (az A6)   [short]
 *   Leg 4: meridian A6 (90 deg arc)  Q5 -> spawn
 *   J2   : ZERO-length turn joint at P3 (tangent turn leg2->leg3)
 *   J4   : ZERO-length joint at spawn (start == end)
 *
 * Faces crossed (designed): 2 (spawn cap) -> 0 (leg1) -> 5 (J1+leg2, the
 * r=138 low band az 86-108) -> 3 (far-pole cap via P3) -> 4 (leg3+J3, the
 * r=132.2 +Z basin az 226-278) -> 1 (J3+leg4) -> 2. Six crossings.
 *
 * Gates (GDD: max_slope 50 deg hard, max_step 0.3 m; QA margin):
 *   - maxSlope <= 49.5 deg on every leg AND joint (0.25 m sampling)
 *   - no radius step dr > 0.33 m (0.25 m sampling)
 *   - total flat length in [894.9, 989.1] m (942 +/- 5%)
 *   - 6 distinct faces
 *   - >= 5 deg clearance from the beacon flank (az 5, s 43-59) and the
 *     az 162.5 staircase (s 51.5-61.5); the Great Crater (az 340-20 far
 *     side, 77 deg peaks) and the far steep ring (az 80-120, colat 40-55)
 *     are gated directly by the 0.25 m slope check + a 2 deg survey-cell
 *     prefilter guard.
 *
 * Crashing-safety: candidates in chunks (~45 s), one JSONL row per
 * candidate appended to test/out/t10-loop-scan.jsonl (phase 'legs' =
 * 2 m prefilter, 'legs-full' = 0.25 m full eval). Re-invocation skips
 * recorded (A1,A2,A5,A6,phi3,B3) keys.
 *
 *   node test/t10/loop-design.mjs scan     # 2 m prefilter grid scan
 *   node test/t10/loop-design.mjs full     # 0.25 m eval of prefilter pass
 *   node test/t10/loop-design.mjs select   # write design + legs files
 *
 * Output: test/out/t10-loop-design.json (full geometry: legs, joints,
 * face crossings with arc positions, per-leg slope/wall/hazard stats,
 * relative turn at P3) + test/t10/loop-legs.json (compact per-segment
 * geometry for the sim/live script generators).
 */
import { readFileSync, appendFileSync, writeFileSync, existsSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180 // normal_eps 2 deg (GDD rule table)
const slopeAt = (d) => slopeDeg(F, d, EPS)

const SPAWN = [0, 1, 0]
const DEG = 180 / Math.PI
const RAD = (d) => (d * Math.PI) / 180
const SLOPE_GATE = 49.5
const WALL_DR = 0.33
const HAZ_MIN_DEG = 5
const LEN_LO = 894.9
const LEN_HI = 989.1
const QUARTER_MAX = 90.5 // deg; a leg must stay a quarter of its circle
const OUT = 'test/out/t10-loop-scan.jsonl'

// ---------------------------------------------------------------- hazards
// Same beacon/staircase hazard points as test/t10/alpha-scan.mjs.
const R0 = sampleRadius(F, SPAWN)
function meridianDir(azDeg, sM) {
  const th = sM / R0
  const a = RAD(azDeg)
  return norm3([Math.sin(th) * Math.cos(a), Math.cos(th), -Math.sin(th) * Math.sin(a)])
}
const HAZARDS = []
for (let az = 3.5; az <= 6.5; az += 0.5) for (let s = 43; s <= 59; s += 2) HAZARDS.push({ az, s, dir: meridianDir(az, s) })
for (let az = 159.5; az <= 164.5; az += 0.5) for (let s = 51.5; s <= 61.5; s += 1.5) HAZARDS.push({ az, s, dir: meridianDir(az, s) })

// Great Crater / far-ring guard: 2 deg survey cells with slope > 49.5 in
// the far hemisphere (from the phase 'survey' rows of this same file).
const steepCells = new Set()
const latToIdx = (lat) => Math.round((lat + 90) / 2)
const lonToIdx = (lon) => ((Math.round(lon / 2) % 180) + 180) % 180
for (const l of readFileSync(OUT, 'utf8').split('\n')) {
  if (!l.trim()) continue
  const r = JSON.parse(l)
  if (r.phase === 'survey' && r.slope > 49.5 && r.lat < -15) {
    steepCells.add(`${latToIdx(r.lat)},${lonToIdx(r.lon)}`)
  }
}
if (steepCells.size === 0) {
  console.error('ERROR: no survey rows found — run `node test/t10/loop-survey.mjs` first')
  process.exit(1)
}
// Angular-distance guard: sample within ~4 deg of a steep cell.
function nearFarSteep(d) {
  const lat = Math.asin(Math.max(-1, Math.min(1, d[1]))) * DEG
  const lon = Math.atan2(-d[2], d[0]) * DEG
  const li = latToIdx(lat)
  const lo = lonToIdx(lon)
  for (let di = -1; di <= 1; di++) for (let dj = -1; dj <= 1; dj++) {
    if (steepCells.has(`${li + di},${(lo + dj + 180) % 180}`)) return true
  }
  return false
}

// ---------------------------------------------------------------- arcs
// Great-circle arc from unit p0 to unit q0 (angle < 180 deg), walked at
// flat (arc-length) step `stepM`. Returns per-node slope/face/dr plus the
// flat and true (3D chord-sum) lengths and both end tangents.
function arc(p0, q0, stepM) {
  const c = Math.max(-1, Math.min(1, p0[0] * q0[0] + p0[1] * q0[1] + p0[2] * q0[2]))
  const theta = Math.acos(c)
  const n = norm3([p0[1] * q0[2] - p0[2] * q0[1], p0[2] * q0[0] - p0[0] * q0[2], p0[0] * q0[1] - p0[1] * q0[0]])
  // t0 must point toward q0 (cross(n,p0) = norm(q0 - c*p0) by construction).
  let t0 = [n[1] * p0[2] - n[2] * p0[1], n[2] * p0[0] - n[0] * p0[2], n[0] * p0[1] - n[1] * p0[0]]
  if (t0[0] * q0[0] + t0[1] * q0[1] + t0[2] * q0[2] < 0) {
    n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]
    t0 = [-t0[0], -t0[1], -t0[2]]
  }
  // Total flat length: 720-step trapezoid over the angle.
  let flat = 0
  {
    const N = 720
    const dd = theta / N
    let dir = norm3(p0)
    let r = sampleRadius(F, dir)
    for (let k = 0; k < N; k++) {
      const t = [n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]]
      const nd = norm3([dir[0] * Math.cos(dd) + t[0] * Math.sin(dd), dir[1] * Math.cos(dd) + t[1] * Math.sin(dd), dir[2] * Math.cos(dd) + t[2] * Math.sin(dd)])
      const rN = sampleRadius(F, nd)
      flat += 0.5 * (r + rN) * dd
      dir = nd
      r = rN
    }
  }
  // Walk at flat-length steps.
  const s = []
  let dir = norm3(p0)
  let arcLen = 0
  let prevR = sampleRadius(F, dir)
  s.push({ s: 0, r: prevR, slope: slopeAt(dir), face: faceOf(dir), dr: 0, dir: dir })
  while (arcLen < flat - 1e-9) {
    const r = sampleRadius(F, dir)
    const ds = Math.min(stepM, flat - arcLen)
    const d = ds / r
    const t = [n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]]
    const nd = norm3([dir[0] * Math.cos(d) + t[0] * Math.sin(d), dir[1] * Math.cos(d) + t[1] * Math.sin(d), dir[2] * Math.cos(d) + t[2] * Math.sin(d)])
    arcLen += ds
    const rN = sampleRadius(F, nd)
    s.push({ s: +arcLen.toFixed(3), r: rN, slope: slopeAt(nd), face: faceOf(nd), dr: rN - prevR, dir: nd })
    prevR = rN
    dir = nd
  }
  const tEnd = norm3([n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]])
  // True 3D length.
  let tru = 0
  for (let k = 1; k < s.length; k++) {
    const a = s[k - 1]
    const b = s[k]
    tru += Math.hypot(b.dir[0] * b.r - a.dir[0] * a.r, b.dir[1] * b.r - a.dir[1] * a.r, b.dir[2] * b.r - a.dir[2] * a.r)
  }
  return { samples: s, tStart: t0, tEnd, n, thetaDeg: +(theta * DEG).toFixed(3), flat: +flat.toFixed(3), tru: +tru.toFixed(3), from: p0 }
}

// ---------------------------------------------------------------- candidate
const eqPt = (az) => [Math.cos(RAD(az)), 0, -Math.sin(RAD(az))]
const farPt = (phiDeg, azDeg) => {
  const p = RAD(phiDeg)
  const a = RAD(azDeg)
  return [Math.sin(p) * Math.cos(a), -Math.cos(p), -Math.sin(p) * Math.sin(a)]
}

function hazDeg(samples) {
  let best = Infinity
  for (const p of samples) {
    const d = p.dir
    for (const h of HAZARDS) {
      const dot = d[0] * h.dir[0] + d[1] * h.dir[1] + d[2] * h.dir[2]
      const ang = Math.acos(Math.max(-1, Math.min(1, dot))) * DEG
      if (ang < best) best = ang
    }
  }
  return +best.toFixed(2)
}

// Assemble the loop; `stepM` = 2 (prefilter) or 0.25 (full).
function buildLoop(A1, A2, A5, A6, phi3, B3, stepM) {
  const Q1 = eqPt(A1)
  const Q2 = eqPt(A2)
  const Q4 = eqPt(A5)
  const Q5 = eqPt(A6)
  const P3 = farPt(phi3, B3)
  const segs = [
    { label: 'leg1', a: arc(SPAWN, Q1, stepM) },
    { label: 'j1', a: arc(Q1, Q2, stepM) },
    { label: 'leg2', a: arc(Q2, P3, stepM) },
    { label: 'leg3', a: arc(P3, Q4, stepM) },
    { label: 'j3', a: arc(Q4, Q5, stepM) },
    { label: 'leg4', a: arc(Q5, SPAWN, stepM) },
  ]
  let maxSlope = -Infinity
  let minSlope = Infinity
  let wall = 0
  let totalFlat = 0
  let totalTru = 0
  let haz = Infinity
  let steepTouch = 0
  const faceSeq = []
  const crossings = []
  let cum = 0
  for (const sg of segs) {
    totalFlat += sg.a.flat
    totalTru += sg.a.tru
    haz = Math.min(haz, hazDeg(sg.a.samples))
    for (const p of sg.a.samples) {
      maxSlope = Math.max(maxSlope, p.slope)
      minSlope = Math.min(minSlope, p.slope)
      if (p.dr > WALL_DR) wall += 1
      if (stepM >= 1 && p.dir[1] < -0.3 && nearFarSteep(p.dir)) steepTouch += 1
      if (faceSeq.length === 0) {
        faceSeq.push(p.face)
      } else if (faceSeq[faceSeq.length - 1] !== p.face) {
        faceSeq.push(p.face)
        crossings.push({ s: +(cum + p.s).toFixed(2), from: faceSeq[faceSeq.length - 2], to: p.face, seg: sg.label })
      }
    }
    cum += sg.a.flat
  }
  const distinct = [...new Set(faceSeq)].sort((x, y) => x - y)
  // Relative turn at P3 (zero-length joint J2): signed angle between the
  // leg2 end tangent and the leg3 start tangent about the local up at P3.
  const t3up = norm3(P3)
  const t2e = segs[2].a.tEnd
  const t3s = segs[3].a.tStart
  const cx = t2e[1] * t3s[2] - t2e[2] * t3s[1]
  const cy = t2e[2] * t3s[0] - t2e[0] * t3s[2]
  const cz = t2e[0] * t3s[1] - t2e[1] * t3s[0]
  const relTurnDeg = +(Math.acos(Math.max(-1, Math.min(1, t2e[0] * t3s[0] + t2e[1] * t3s[1] + t2e[2] * t3s[2])) * DEG * (t3up[0] * cx + t3up[1] * cy + t3up[2] * cz >= 0 ? 1 : -1))).toFixed(2)
  return {
    segs,
    totalFlat: +totalFlat.toFixed(2),
    totalTru: +totalTru.toFixed(2),
    maxSlope: +maxSlope.toFixed(2),
    minSlope: +minSlope.toFixed(2),
    wall,
    hazDeg: haz,
    steepTouch,
    distinct,
    crossings,
    relTurnDeg,
    P3: P3.map((x) => +x.toFixed(6)),
    Q1, Q2, Q4, Q5,
    maxQuarter: Math.max(...segs.map((s) => s.a.thetaDeg)),
  }
}

// ---------------------------------------------------------------- scan grid
// A1 fixed at 40 (walkable q0 corridor, face 0 exit, 35 deg from the
// beacon). A2 in face 5, A5 in face 4 with A6 = A5 - {5.5, 6.5} in face 1,
// B3 constrained so both far legs stay <= QUARTER_MAX deg.
const A1 = 40
const A6OFFS = [5.5, 6.5]
const A5OFFS = [90, 120, 150, 170, 180] // A5 = A2 + off
const A2CAND = [46, 50, 54, 58, 62, 76, 80, 90, 96, 100, 106, 108, 110, 136, 140]
const PHI3 = [28, 30, 32, 34, 36, 38, 40, 42]

function gridCandidates() {
  const cands = []
  for (const A2 of A2CAND) {
    for (const off of A5OFFS) {
      const A5 = A2 + off
      if (A5 <= 225 || A5 >= 230.5) continue // J3 start face 4 / end face 1
      for (const A6o of A6OFFS) {
        const A6 = A5 - A6o
        if (A6 <= 135 || A6 >= 225) continue
        const lo = A5 - (QUARTER_MAX - 0.2)
        const hi = A2 + (QUARTER_MAX - 0.2)
        if (lo >= hi) continue
        for (let B3 = Math.ceil(lo * 2) / 2; B3 <= hi + 1e-9; B3 = +(B3 + 0.5).toFixed(1)) {
          for (const phi3 of PHI3) cands.push([A1, A2, A5, A6, phi3, +B3.toFixed(1)])
        }
      }
    }
  }
  return cands
}

const arg = process.argv[2]
const doneKeys = new Set()
if (existsSync(OUT)) {
  for (const l of readFileSync(OUT, 'utf8').split('\n')) {
    if (!l.trim()) continue
    const r = JSON.parse(l)
    if (r.phase === 'legs' || r.phase === 'legs-full') {
      doneKeys.add(`${r.phase}:${r.A1}:${r.A2}:${r.A5}:${r.A6}:${r.phi3}:${r.B3}`)
    }
  }
}

if (arg === 'scan') {
  const cands = gridCandidates()
  const fresh = cands.filter(([a1, a2, a5, a6, p3, b3]) => !doneKeys.has(`legs:${a1}:${a2}:${a5}:${a6}:${p3}:${b3}`))
  console.error(`scan: ${cands.length} candidates, ${fresh.length} fresh`)
  const t0 = process.hrtime.bigint()
  let best = null
  for (let i = 0; i < fresh.length; i += 900) {
    const chunk = fresh.slice(i, i + 900)
    const lines = []
    for (const [a1, a2, a5, a6, phi3, B3] of chunk) {
      const L = buildLoop(a1, a2, a5, a6, phi3, B3, 2)
      const pass = L.distinct.length === 6 && L.totalFlat >= 880 && L.totalFlat <= 1005 && L.maxSlope <= 50.5 && L.maxQuarter <= QUARTER_MAX && L.steepTouch === 0
      lines.push(JSON.stringify({ phase: 'legs', pre: true, A1: a1, A2: a2, A5: a5, A6: a6, phi3, B3, pass, total: L.totalFlat, maxSlope: L.maxSlope, maxQuarter: L.maxQuarter, haz: L.hazDeg, steepTouch: L.steepTouch, distinct: L.distinct, relTurnP3: L.relTurnDeg }))
      if (pass && (!best || Math.abs(L.totalFlat - 942) < Math.abs(best.totalFlat - 942))) best = L
    }
    appendFileSync(OUT, lines.join('\n') + '\n')
    const el = Number(process.hrtime.bigint() - t0) / 1e6
    console.error(`  chunk ${Math.floor(i / 900) + 1}: ${chunk.length} cands in ${(el / 1000).toFixed(1)} s; best pass: ${best ? `total=${best.totalFlat} maxSlope=${best.maxSlope}` : 'none'}`)
  }
} else if (arg === 'full') {
  const rows = []
  for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const passRows = rows.filter((r) => r.phase === 'legs' && r.pre && r.pass)
  const fresh = passRows.filter((r) => !doneKeys.has(`legs-full:${r.A1}:${r.A2}:${r.A5}:${r.A6}:${r.phi3}:${r.B3}`))
  console.error(`full: ${passRows.length} prefilter pass, ${fresh.length} fresh`)
  const t0 = process.hrtime.bigint()
  let best = null
  for (let i = 0; i < fresh.length; i += 60) {
    const chunk = fresh.slice(i, i + 60)
    const lines = []
    for (const r of chunk) {
      const L = buildLoop(r.A1, r.A2, r.A5, r.A6, r.phi3, r.B3, 0.25)
      const ok = L.distinct.length === 6 && L.totalFlat >= LEN_LO && L.totalFlat <= LEN_HI && L.maxSlope <= SLOPE_GATE && L.wall === 0 && L.hazDeg >= HAZ_MIN_DEG && L.maxQuarter <= QUARTER_MAX
      lines.push(JSON.stringify({ phase: 'legs-full', A1: r.A1, A2: r.A2, A5: r.A5, A6: r.A6, phi3: r.phi3, B3: r.B3, ok, total: L.totalFlat, totalTru: L.totalTru, maxSlope: L.maxSlope, minSlope: L.minSlope, wall: L.wall, haz: L.hazDeg, maxQuarter: L.maxQuarter, distinct: L.distinct, relTurnP3: L.relTurnDeg, P3: L.P3, crossings: L.crossings, legs: L.segs.map((s) => ({ label: s.label, n: s.a.n.map((x) => +x.toFixed(6)), from: s.a.from.map((x) => +x.toFixed(6)), thetaDeg: s.a.thetaDeg, flat: s.a.flat, tru: s.a.tru })) }))
      if (ok && (!best || Math.abs(L.totalFlat - 942) < Math.abs(best.totalFlat - 942))) best = { ...r, total: L.totalFlat }
    }
    appendFileSync(OUT, lines.join('\n') + '\n')
    const el = Number(process.hrtime.bigint() - t0) / 1e6
    console.error(`  chunk ${Math.floor(i / 60) + 1}: ${chunk.length} cands in ${(el / 1000).toFixed(1)} s; best ok: ${best ? `total=${best.total}` : 'none'}`)
  }
} else if (arg === 'select') {
  const rows = []
  for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const okRows = rows.filter((r) => r.phase === 'legs-full' && r.ok)
  if (okRows.length === 0) {
    console.error('select: no candidate passed all gates')
    process.exit(1)
  }
  okRows.sort((a, b) => Math.abs(a.total - 942) - Math.abs(b.total - 942) || a.maxSlope - b.maxSlope)
  const sel = okRows[0]
  console.error(`select: ${okRows.length} feasible; choosing A1=${sel.A1} A2=${sel.A2} A5=${sel.A5} A6=${sel.A6} phi3=${sel.phi3} B3=${sel.B3} (total=${sel.total} m, maxSlope=${sel.maxSlope}, haz=${sel.haz} deg, turnP3=${sel.relTurnP3} deg)`)
  const L = buildLoop(sel.A1, sel.A2, sel.A5, sel.A6, sel.phi3, sel.B3, 0.25)
  // Per-segment detailed stats for the report.
  const segReport = L.segs.map((s) => {
    let mx = -Infinity
    let mn = Infinity
    let w = 0
    const faces = []
    for (const p of s.a.samples) {
      mx = Math.max(mx, p.slope)
      mn = Math.min(mn, p.slope)
      if (p.dr > WALL_DR) w += 1
      if (faces[faces.length - 1] !== p.face) faces.push(p.face)
    }
    return { label: s.label, thetaDeg: s.a.thetaDeg, flat: s.a.flat, tru: s.a.tru, maxSlope: +mx.toFixed(2), minSlope: +mn.toFixed(2), wallSteps: w, faces, n: s.a.n.map((x) => +x.toFixed(6)), from: s.a.from.map((x) => +x.toFixed(6)) }
  })
  const design = {
    world: 'test/out/world-seed1337.json',
    start: SPAWN,
    startUp: SPAWN,
    farPole: [0, -1, 0],
    P3: L.P3,
    params: { A1: sel.A1, A2: sel.A2, A5: sel.A5, A6: sel.A6, phi3: sel.phi3, B3: sel.B3 },
    segments: segReport,
    joints: [
      { id: 'J1', kind: 'equatorial geodesic arc', from: 'leg1 end', to: 'leg2 start', az: `${sel.A1} -> ${sel.A2}`, flat: L.segs[1].a.flat },
      { id: 'J2', kind: 'zero-length turn joint', at: 'P3 (far side)', turnDeg: L.relTurnDeg },
      { id: 'J3', kind: 'equatorial geodesic arc', from: 'leg3 end', to: 'leg4 start', az: `${sel.A5} -> ${sel.A6}`, flat: L.segs[4].a.flat },
      { id: 'J4', kind: 'zero-length joint (start == end)', at: 'spawn' },
    ],
    faceCrossings: L.crossings,
    totalFlat: L.totalFlat,
    totalTru: L.totalTru,
    maxSlope: L.maxSlope,
    wallSteps: L.wall,
    hazDeg: L.hazDeg,
    maxQuarterDeg: L.maxQuarter,
    constraints: {
      slopeGate: SLOPE_GATE,
      wallDrGate: WALL_DR,
      hazMinDeg: HAZ_MIN_DEG,
      totalM: [LEN_LO, LEN_HI],
      quarterMaxDeg: QUARTER_MAX,
      hazards: 'beacon az 3.5-6.5 s 43-59; staircase az 159.5-164.5 s 51.5-61.5; Great Crater az 340-20 far side (77 deg peaks, survey); far steep ring az 80-120 colat 40-55 (survey)',
    },
  }
  writeFileSync('test/out/t10-loop-design.json', JSON.stringify(design, null, 1) + '\n')
  writeFileSync('test/t10/loop-legs.json', JSON.stringify(design.segments.map((s) => ({ label: s.label, n: s.n, from: s.from, thetaDeg: s.thetaDeg, flat: s.flat })), null, 1) + '\n')
  console.error('wrote test/out/t10-loop-design.json + test/t10/loop-legs.json')
  console.error(JSON.stringify({ totalFlat: L.totalFlat, totalTru: L.totalTru, maxSlope: L.maxSlope, hazDeg: L.hazDeg, wall: L.wall, maxQuarter: L.maxQuarter, crossings: L.crossings, relTurnP3: L.relTurnDeg }, null, 1))
} else {
  console.error('usage: loop-design.mjs scan|full|select')
  process.exit(1)
}