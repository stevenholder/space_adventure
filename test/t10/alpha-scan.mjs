#!/usr/bin/env node
/**
 * C10 lap-design scan — chunked alpha/beta search for the tilted great
 * circle that closes the lap.
 *
 * A great circle through the spawn pole crosses only 4 of the 6 cube faces;
 * a generic tilted circle (plane through the origin, normal off all three
 * axes) crosses all six. Lap = access meridian (flat spawn disc) + one full
 * circuit of the tilted circle + the same meridian back.
 *
 * Walkability constraints (from test/out/t5-fieldmap.json +
 * test/out/t5-ledges.json, the corrected-aim survey):
 *   - slope <= 48 deg everywhere on the circle AND on the access meridian
 *     (GDD max_slope is 50; 48 keeps margin for the finite-difference
 *     normal's ~1.5-cell noise)
 *   - no radius step-up > 0.33 m along the circle (a walking body's
 *     max_step is 0.3 m; 0.33 catches the cliff/wall class)
 *   - the beacon flank (az 5 deg, s 44-58 m, slopes 53-70 deg on fieldmap
 *     circles 4-6) and the az 162.5 staircase (az 159.5-164.5, s 52-61 m,
 *     slopes 53-60 deg) must not be climbed: the circle must stay >= 5 deg
 *     (12.5 m at r=150) from the hazard zones.
 *   - total lap length 942 m +/- 5% (894.9 - 989.1 m).
 *
 * Crashing-safety: every phase evaluates candidates in small chunks
 * (<= ~200 candidates, << 90 s at ~12 ms/candidate) and appends one JSONL
 * row per candidate to test/out/t10-alpha-scan.jsonl. A crashed run can be
 * re-invoked; already-recorded (phase,beta,alpha) rows are skipped.
 *
 *   node test/t10/alpha-scan.mjs coarse
 *   node test/t10/alpha-scan.mjs fine
 *   node test/t10/alpha-scan.mjs select
 *
 * `select` writes test/out/t10-lap-design.json (full geometry, face
 * crossing list with arc-length positions, hazard distances) and
 * test/t10/lap-legs.json (per-leg relative-turn + distance, the format
 * route-design.mjs / scripted clients consume).
 */
import { readFileSync, appendFileSync, writeFileSync, existsSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180 // normal_eps 2 deg
const slopeAt = (d) => slopeDeg(F, d, EPS)
const SPAWN = [0, 1, 0]
const R0 = sampleRadius(F, SPAWN)
const DEG = 180 / Math.PI
const OUT = 'test/out/t10-alpha-scan.jsonl'

// ---------------------------------------------------------------- hazards
// Meridian direction at azimuth az (deg, x=cos, z=-sin convention) and arc
// distance s from spawn (flat-disc approximation, valid < 60 m).
function meridianDir(azDeg, sM) {
  const th = sM / R0
  const a = (azDeg * Math.PI) / 180
  return norm3([Math.sin(th) * Math.cos(a), Math.cos(th), -Math.sin(th) * Math.sin(a)])
}

// Walkability gate: GDD max_slope is 50 deg (slopeOK = angle <= 50; sprint
// climbs a 50 deg slope at 0.29 m/step < max_step). QA gate 49.5 keeps a
// half-degree margin for the finite-difference normal's ~1.5-cell noise.
const SLOPE_GATE = 49.5

// Hazard zones (fieldmap circles 4-6 and 160-164 steep48 intervals, plus
// the az 5 beacon step-up at s~46.7 and az 162.5 staircase step at s~51).
const HAZARDS = []
for (let az = 3.5; az <= 6.5; az += 0.5) for (let s = 43; s <= 59; s += 2) HAZARDS.push({ az, s, kind: 'beacon', dir: meridianDir(az, s) })
for (let az = 159.5; az <= 164.5; az += 0.5) for (let s = 51.5; s <= 61.5; s += 1.5) HAZARDS.push({ az, s, kind: 'staircase', dir: meridianDir(az, s) })
const HAZ_MIN_DEG = 5

function hazardDistance(samples) {
  let best = Infinity
  let bestPt = null
  for (const p of samples) {
    const d = p.dir
    for (const h of HAZARDS) {
      const dot = d[0] * h.dir[0] + d[1] * h.dir[1] + d[2] * h.dir[2]
      const ang = (Math.acos(Math.max(-1, Math.min(1, dot))) * DEG)
      if (ang < best) {
        best = ang
        bestPt = { kind: h.kind, az: h.az, s: h.s }
      }
    }
  }
  return { deg: +best.toFixed(2), point: bestPt }
}

// ---------------------------------------------------------------- geometry
function walkCircle(u0, t0, distM, stepM = 0.25) {
  const n = norm3([u0[1] * t0[2] - u0[2] * t0[1], u0[2] * t0[0] - u0[0] * t0[2], u0[0] * t0[1] - u0[1] * t0[0]])
  const s = []
  let dir = norm3(u0)
  let arc = 0
  let prevR = sampleRadius(F, dir)
  s.push({ s: 0, r: prevR, slope: slopeAt(dir), face: faceOf(dir), dr: 0, dir: dir.map((x) => +x.toFixed(6)) })
  while (arc < distM - 1e-9) {
    const r = sampleRadius(F, dir)
    const ds = Math.min(stepM, distM - arc)
    const d = ds / r
    const t = [n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]]
    const c = Math.cos(d)
    const sn = Math.sin(d)
    const next = norm3([dir[0] * c + t[0] * sn, dir[1] * c + t[1] * sn, dir[2] * c + t[2] * sn])
    arc += ds
    const rN = sampleRadius(F, next)
    s.push({ s: +arc.toFixed(3), r: rN, slope: slopeAt(next), face: faceOf(next), dr: rN - prevR, dir: next.map((x) => +x.toFixed(6)) })
    prevR = rN
    dir = next
  }
  const tEnd = [n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]]
  return { samples: s, tEnd: norm3(tEnd) }
}

// Great-circle tangent at unit position p toward unit target q.
function tangentTo(p, q) {
  const c = p[0] * q[0] + p[1] * q[1] + p[2] * q[2]
  return norm3([q[0] - p[0] * c, q[1] - p[1] * c, q[2] - p[2] * c])
}

// Path lengths over a great-circle arc (normal n, from `from`, angle theta):
//   flat  = ∫ r dθ (arc on the radial surface — the GDD "942 m" measure,
//           2π·r̄ on a flat 150 m sphere);
//   true  = Σ|P_{k+1}−P_k|, the 3D distance a body actually walks. The
//           terrain is rough at the 3.3 m grid scale (RMS radial wobble
//           ~0.27 m per 2.2 m), so true > flat by 12-25% on full laps —
//           a property of every full lap on this world (the flat +Y equator
//           alone walks 1177.7 m true vs 978.8 m flat).
function pathLengths(n, from, theta) {
  const N = 2880
  const dd = theta / N
  let dir = norm3(from)
  let r = sampleRadius(F, dir)
  let prev = [dir[0] * r, dir[1] * r, dir[2] * r]
  let flat = 0
  let tru = 0
  for (let k = 0; k < N; k++) {
    const tt = [n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]]
    const c = Math.cos(dd)
    const sn = Math.sin(dd)
    const nd = norm3([dir[0] * c + tt[0] * sn, dir[1] * c + tt[1] * sn, dir[2] * c + tt[2] * sn])
    const rN = sampleRadius(F, nd)
    const next = [nd[0] * rN, nd[1] * rN, nd[2] * rN]
    flat += 0.5 * (r + rN) * dd
    tru += Math.hypot(next[0] - prev[0], next[1] - prev[1], next[2] - prev[2])
    dir = nd
    r = rN
    prev = next
  }
  return { flat, tru }
}

// Slope verdict along a meridian leg (access/return walkability).
function meridianSamples(n, from, theta, stepM = 0.25) {
  let d = norm3(from)
  const s = []
  let arc = 0
  const r0 = sampleRadius(F, d)
  s.push({ s: 0, slope: slopeAt(d), face: faceOf(d) })
  while (arc < theta * r0 - 1e-9) {
    const r = sampleRadius(F, d)
    const ds = Math.min(stepM, theta * r0 - arc)
    const dd = ds / r
    const tt = [n[1] * d[2] - n[2] * d[1], n[2] * d[0] - n[0] * d[2], n[0] * d[1] - n[1] * d[0]]
    const nd = norm3([d[0] * Math.cos(dd) + tt[0] * Math.sin(dd), d[1] * Math.cos(dd) + tt[1] * Math.sin(dd), d[2] * Math.cos(dd) + tt[2] * Math.sin(dd)])
    arc += ds
    s.push({ s: +arc.toFixed(3), slope: slopeAt(nd), face: faceOf(nd) })
    d = nd
  }
  let maxSlope = -Infinity
  let minSlope = Infinity
  const faces = []
  for (const p of s) {
    maxSlope = Math.max(maxSlope, p.slope)
    minSlope = Math.min(minSlope, p.slope)
    if (faces[faces.length - 1] !== p.face) faces.push(p.face)
  }
  return { maxSlope: +maxSlope.toFixed(2), minSlope: +minSlope.toFixed(2), faces }
}

function relTurn(p, tPrev, tNext) {
  const up = norm3(p)
  const d = tPrev[0] * tNext[0] + tPrev[1] * tNext[1] + tPrev[2] * tNext[2]
  const c = [tPrev[1] * tNext[2] - tPrev[2] * tNext[1], tPrev[2] * tNext[0] - tPrev[0] * tNext[2], tPrev[0] * tNext[1] - tPrev[1] * tNext[0]]
  const s = c[0] * up[0] + c[1] * up[1] + c[2] * up[2]
  return +(Math.atan2(s, d) * DEG).toFixed(3)
}

// ---------------------------------------------------------------- evaluate
function evalCandidate(phase, beta, alpha) {
  const b = (beta * Math.PI) / 180
  const a = (alpha * Math.PI) / 180
  const n = norm3([Math.sin(b) * Math.cos(a), Math.cos(b), Math.sin(b) * Math.sin(a)])
  // Circle's nearest point to the +Y pole: in-plane projection of Y.
  const yn = n[1]
  const dStar = norm3([-n[0] * yn, 1 - n[1] * yn, -n[2] * yn])
  const tAccess = tangentTo(SPAWN, dStar)
  const thetaStar = Math.acos(Math.max(-1, Math.min(1, dStar[1])))
  const nAcc = norm3([tAccess[1] * SPAWN[2] - tAccess[2] * SPAWN[1], tAccess[2] * SPAWN[0] - tAccess[0] * SPAWN[2], tAccess[0] * SPAWN[1] - tAccess[1] * SPAWN[0]])
  const acc = pathLengths(nAcc, SPAWN, thetaStar)
  const accessArc = acc.flat
  const accessRep = meridianSamples(nAcc, SPAWN, thetaStar)
  const tLap = norm3([n[1] * dStar[2] - n[2] * dStar[1], n[2] * dStar[0] - n[0] * dStar[2], n[0] * dStar[1] - n[1] * dStar[0]])
  const lap = pathLengths(n, dStar, 2 * Math.PI)
  const per = lap.flat
  const perTrue = lap.tru
  const w = walkCircle(dStar, tLap, per, 0.25)
  let maxSlope = -Infinity
  let minSlope = Infinity
  let wall = 0
  const runs = []
  const distinct = new Set()
  for (const p of w.samples) {
    maxSlope = Math.max(maxSlope, p.slope)
    minSlope = Math.min(minSlope, p.slope)
    if (p.dr > 0.33) wall += 1
    if (runs[runs.length - 1] !== p.face) runs.push(p.face)
    distinct.add(p.face)
  }
  const haz = hazardDistance(w.samples)
  const total = 2 * accessArc + per
  const totalTrue = 2 * accessArc + perTrue
  const ok =
    distinct.size === 6 &&
    maxSlope <= SLOPE_GATE &&
    wall === 0 &&
    accessRep.maxSlope <= SLOPE_GATE &&
    haz.deg >= HAZ_MIN_DEG &&
    total >= 894.9 &&
    total <= 989.1
  return {
    phase,
    beta,
    alpha,
    ok,
    distinct: [...distinct].sort((x, y) => x - y),
    runs,
    maxSlope: +maxSlope.toFixed(2),
    minSlope: +minSlope.toFixed(2),
    wallSteps: wall,
    accessArc: +accessArc.toFixed(2),
    accessMaxSlope: accessRep.maxSlope,
    per: +per.toFixed(2),
    perTrue: +perTrue.toFixed(2),
    total: +total.toFixed(2),
    totalTrue: +totalTrue.toFixed(2),
    hazDeg: haz.deg,
    hazPoint: haz.point,
    n: n.map((x) => +x.toFixed(6)),
    dStar: dStar.map((x) => +x.toFixed(6)),
  }
}
// ---------------------------------------------------------------- phases
const arg = process.argv[2]
const doneKeys = new Set()
if (existsSync(OUT)) {
  for (const l of readFileSync(OUT, 'utf8').split('\n')) {
    if (!l.trim()) continue
    const r = JSON.parse(l)
    if (typeof r.beta === 'number') doneKeys.add(`${r.phase}:${r.beta}:${r.alpha}`)
  }
}

function runChunked(cands, phase, chunkSize) {
  const fresh = cands.filter(([b, a]) => !doneKeys.has(`${phase}:${b}:${a}`))
  console.error(`${phase}: ${cands.length} candidates, ${fresh.length} fresh (${cands.length - fresh.length} cached)`)
  const t0 = process.hrtime.bigint()
  let best = null
  for (let i = 0; i < fresh.length; i += chunkSize) {
    const chunk = fresh.slice(i, i + chunkSize)
    const lines = []
    for (const [b, a] of chunk) {
      const r = evalCandidate(phase, b, a)
      if (r.ok && (!best || Math.abs(r.total - 942) < Math.abs(best.total - 942))) best = r
      lines.push(JSON.stringify(r))
    }
    appendFileSync(OUT, lines.join('\n') + '\n')
    const el = Number(process.hrtime.bigint() - t0) / 1e6
    console.error(`  chunk ${i / chunkSize + 1}: ${chunk.length} candidates in ${el.toFixed(0)} ms total; best ok so far: ${best ? `beta=${best.beta} alpha=${best.alpha} total=${best.total} maxSlope=${best.maxSlope}` : 'none'}`)
  }
  const el = Number(process.hrtime.bigint() - t0) / 1e6
  console.error(`${phase} done in ${(el / 1000).toFixed(1)} s; best ok: ${best ? `beta=${best.beta} alpha=${best.alpha} total=${best.total} m maxSlope=${best.maxSlope} haz=${best.hazDeg} deg` : 'NONE'}`)
  return best
}

if (arg === 'coarse') {
  const cands = []
  for (let beta = 75; beta <= 85; beta += 5) for (let alpha = 0; alpha < 360; alpha += 5) cands.push([beta, alpha])
  runChunked(cands, 'coarse', 72)
} else if (arg === 'fine') {
  // Neighborhoods: top coarse candidates passing all constraints except
  // total length (total is the fine scan's job), deduped to distinct
  // alpha neighborhoods.
  const rows = []
  if (existsSync(OUT)) for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const coarse = rows.filter((r) => r.phase === 'coarse')
  const pass = coarse.filter((r) => r.distinct.length === 6 && r.maxSlope <= SLOPE_GATE && r.wallSteps === 0 && r.accessMaxSlope <= SLOPE_GATE && r.hazDeg >= HAZ_MIN_DEG)
  pass.sort((a, b) => Math.abs(a.total - 942) - Math.abs(b.total - 942) || a.maxSlope - b.maxSlope)
  const centers = []
  for (const r of pass) {
    if (!centers.some((c) => Math.min(Math.abs(c - r.alpha), 360 - Math.abs(c - r.alpha)) <= 5)) centers.push(r.alpha)
    if (centers.length >= 3) break
  }
  if (centers.length === 0) {
    console.error('fine: no coarse candidate passed the constraints — cannot refine')
    process.exit(1)
  }
  console.error(`fine neighborhoods (alpha centers): ${centers.join(', ')} (from ${pass.length} constraint-passing coarse rows)`)
  const cands = []
  for (const c0 of centers) {
    // beta window: total 942±5% needs access <= ~24 m -> 90-beta <= ~9.3 deg;
    // keep 78..86 so the total edge is bracketed.
    for (let beta = 78; beta <= 86; beta += 1) {
      for (let da = -5; da <= 5.001; da += 0.5) {
        const al = ((c0 + da) % 360 + 360) % 360
        cands.push([beta, +al.toFixed(1)])
      }
    }
  }
  runChunked(cands, 'fine', 150)
} else if (arg === 'dense') {
  // Dense fine scan — escalation after the 5-deg coarse grid produced no
  // six-face circle inside the slope/wall/hazard gates (best 54.5 deg,
  // Great Crater flanks). alpha 0.25 deg x beta 76-88, 2 m prefilter,
  // full 0.25 m evaluation only for prefilter survivors.
  function prefilter(beta, alpha) {
    const b = (beta * Math.PI) / 180
    const a = (alpha * Math.PI) / 180
    const n = norm3([Math.sin(b) * Math.cos(a), Math.cos(b), Math.sin(b) * Math.sin(a)])
    const yn = n[1]
    const dStar = norm3([-n[0] * yn, 1 - n[1] * yn, -n[2] * yn])
    const tAccess = tangentTo(SPAWN, dStar)
    const thetaStar = Math.acos(Math.max(-1, Math.min(1, dStar[1])))
    const nAcc = norm3([tAccess[1] * SPAWN[2] - tAccess[2] * SPAWN[1], tAccess[2] * SPAWN[0] - tAccess[0] * SPAWN[2], tAccess[0] * SPAWN[1] - tAccess[1] * SPAWN[0]])
    const acc = pathLengths(nAcc, SPAWN, thetaStar)
    const accessArc = acc.flat
    const lap = pathLengths(n, dStar, 2 * Math.PI)
    const per = lap.flat
    const total = 2 * accessArc + per
    if (total < 894.9 || total > 989.1) return { pass: false, reason: 'total', total: +total.toFixed(2) }
    const tLap = norm3([n[1] * dStar[2] - n[2] * dStar[1], n[2] * dStar[0] - n[0] * dStar[2], n[0] * dStar[1] - n[1] * dStar[0]])
    const w = walkCircle(dStar, tLap, per, 2) // 2 m prefilter step
    // No wall gate at 2 m resolution: a 48 deg slope moves dr ~2.2 m per 2 m
    // step, so the 0.33 wall threshold (valid only at 0.25 m steps) fires on
    // every slope. Walls are gated by the full 0.25 m evaluation instead;
    // wall2 is still reported for information.
    let maxSlope = -Infinity
    let wall = 0
    const runs = []
    const distinct = new Set()
    for (const p of w.samples) {
      maxSlope = Math.max(maxSlope, p.slope)
      if (p.dr > 0.33) wall += 1
      if (runs[runs.length - 1] !== p.face) runs.push(p.face)
      distinct.add(p.face)
    }
    const haz = hazardDistance(w.samples)
    const accRep = meridianSamples(nAcc, SPAWN, thetaStar, 2)
    const reasons = []
    if (distinct.size !== 6) reasons.push('faces')
    if (maxSlope > 50.5) reasons.push(`slope${maxSlope.toFixed(1)}`) // prefilter gate: 0.25 m full eval gates at SLOPE_GATE
    if (accRep.maxSlope > 50.5) reasons.push(`acc${accRep.maxSlope.toFixed(1)}`)
    if (haz.deg < HAZ_MIN_DEG) reasons.push(`haz${haz.deg}`)
    return {
      pass: reasons.length === 0,
      reason: reasons.join(','),
      total: +total.toFixed(2),
      maxSlope2: +maxSlope.toFixed(2),
      wall2: wall,
      distinct2: [...distinct].sort((x, y) => x - y),
      hazDeg2: haz.deg,
      access2: +accessArc.toFixed(2),
      accSlope2: accRep.maxSlope,
    }
  }
  const t0 = process.hrtime.bigint()
  const fulls = []
  for (let beta = 76; beta <= 88; beta += 1) {
    const cands = []
    for (let alpha = 0; alpha < 360; alpha += 0.25) cands.push([beta, +alpha.toFixed(2)])
    const fresh = cands.filter(([b, a]) => !doneKeys.has(`dense:${b}:${a}`))
    for (let i = 0; i < fresh.length; i += 480) {
      const chunk = fresh.slice(i, i + 480)
      const lines = []
      for (const [b, a] of chunk) {
        const pre = prefilter(b, a)
        if (pre.pass) {
          const r = evalCandidate('dense', b, a)
          fulls.push(r)
          lines.push(JSON.stringify(r))
        } else {
          lines.push(JSON.stringify({ phase: 'dense', pre: true, beta: b, alpha: a, ...pre }))
        }
      }
      appendFileSync(OUT, lines.join('\n') + '\n')
      const el = Number(process.hrtime.bigint() - t0) / 1e6
      console.error(`  dense beta=${beta} chunk ${Math.floor(i / 480) + 1}: ${chunk.length} cands, ${fulls.filter((r) => r.beta === beta).length} full-eval so far (${(el / 1000).toFixed(1)} s)`)
    }
  }
  const el = Number(process.hrtime.bigint() - t0) / 1e6
  const passCount = fulls.filter((r) => r.ok).length
  console.error(`dense done in ${(el / 1000).toFixed(1)} s; ${fulls.length} full-evaluated, ${passCount} pass all gates`)
} else if (arg === 'select') {
  const rows = []
  if (!existsSync(OUT)) {
    console.error('select: no scan file — run `coarse` and `fine` first')
    process.exit(1)
  }
  for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const okRows = rows.filter((r) => !r.pre && Array.isArray(r.distinct) && r.distinct.length === 6 && r.maxSlope <= SLOPE_GATE && r.wallSteps === 0 && r.accessMaxSlope <= SLOPE_GATE && r.hazDeg >= HAZ_MIN_DEG && r.total >= 894.9 && r.total <= 989.1)
  if (okRows.length === 0) {
    console.error('select: no candidate passed all constraints')
    process.exit(1)
  }
  okRows.sort((a, b) => Math.abs(a.total - 942) - Math.abs(b.total - 942) || a.maxSlope - b.maxSlope)
  const sel = okRows[0]
  console.error(`select: ${okRows.length} feasible candidates; choosing beta=${sel.beta} alpha=${sel.alpha} (total=${sel.total} m, maxSlope=${sel.maxSlope}, haz=${sel.hazDeg} deg)`)
  selectFinal(sel)
} else {
  console.error('usage: alpha-scan.mjs coarse|fine|select')
  process.exit(1)
}

// ---------------------------------------------------------------- select
function selectFinal(sel) {
  // Re-derive the full geometry (single candidate, ~12 ms) so the emitted
  // design carries face crossing positions and leg turns.
  const b = (sel.beta * Math.PI) / 180
  const a = (sel.alpha * Math.PI) / 180
  const n = norm3([Math.sin(b) * Math.cos(a), Math.cos(b), Math.sin(b) * Math.sin(a)])
  const yn = n[1]
  const dStar = norm3([-n[0] * yn, 1 - n[1] * yn, -n[2] * yn])
  const tAccess = tangentTo(SPAWN, dStar)
  const thetaStar = Math.acos(Math.max(-1, Math.min(1, dStar[1])))
  const nAcc = norm3([tAccess[1] * SPAWN[2] - tAccess[2] * SPAWN[1], tAccess[2] * SPAWN[0] - tAccess[0] * SPAWN[2], tAccess[0] * SPAWN[1] - tAccess[1] * SPAWN[0]])
  const acc = pathLengths(nAcc, SPAWN, thetaStar)
  const accessArc = acc.flat
  const accessTrue = acc.tru
  const accessRep = meridianSamples(nAcc, SPAWN, thetaStar)
  const tLap = norm3([n[1] * dStar[2] - n[2] * dStar[1], n[2] * dStar[0] - n[0] * dStar[2], n[0] * dStar[1] - n[1] * dStar[0]])
  const lap = pathLengths(n, dStar, 2 * Math.PI)
  const per = lap.flat
  const perTrue = lap.tru
  const w = walkCircle(dStar, tLap, per, 0.25)
  let maxSlope = -Infinity
  let wall = 0
  const haz = hazardDistance(w.samples)
  const crossings = [] // full-lap arc positions of face changes
  const runs = []
  const distinct = new Set()
  for (const p of w.samples) {
    maxSlope = Math.max(maxSlope, p.slope)
    if (p.dr > 0.33) wall += 1
    distinct.add(p.face)
    if (runs.length === 0) {
      runs.push(p.face)
    } else if (runs[runs.length - 1] !== p.face) {
      runs.push(p.face)
      crossings.push({ s: +p.s.toFixed(2), from: runs[runs.length - 2], to: p.face })
    }
  }
  const tReturn = norm3([-tAccess[0], -tAccess[1], -tAccess[2]])
  const lapAccessAz = +((Math.atan2(-tAccess[2], tAccess[0]) * DEG + 360) % 360).toFixed(3)
  const lapCircleAz = relTurn(dStar, tAccess, tLap)
  const lapReturnAz = relTurn(dStar, tLap, tReturn)
  // Full-lap crossing list: access stays on the spawn face (verify), circle
  // crossings offset by the access arc.
  const accessFaces = new Set(accessRep.faces)
  if (accessFaces.size > 1) throw new Error('access meridian crosses faces — not on the flat disc')
  const fullCrossings = crossings.map((c) => ({ s: +(c.s + accessArc).toFixed(2), from: c.from, to: c.to }))
  const total = 2 * accessArc + per
  const totalTrue = 2 * accessTrue + perTrue
  const endDrift = Math.acos(Math.max(-1, Math.min(1, w.samples[w.samples.length - 1].dir[0] * dStar[0] + w.samples[w.samples.length - 1].dir[1] * dStar[1] + w.samples[w.samples.length - 1].dir[2] * dStar[2]))) * R0
  const design = {
    world: 'test/out/world-seed1337.json',
    phase: sel.phase,
    beta: sel.beta,
    alpha: sel.alpha,
    planeNormal: n.map((x) => +x.toFixed(6)),
    entryPoint: { dir: dStar.map((x) => +x.toFixed(6)), fromSpawnDeg: +(thetaStar * DEG).toFixed(3), fromSpawnM: +accessArc.toFixed(2) },
    legs: [
      { label: 'lapAccess', az: lapAccessAz, dist: +accessTrue.toFixed(2) },
      { label: 'lapCircle', az: lapCircleAz, dist: +perTrue.toFixed(2) },
      { label: 'lapReturn', az: lapReturnAz, dist: +accessTrue.toFixed(2) },
    ],
    accessArc: +accessArc.toFixed(2),
    accessTrue: +accessTrue.toFixed(2),
    per: +per.toFixed(2),
    perTrue: +perTrue.toFixed(2),
    total: +total.toFixed(2),
    totalTrue: +totalTrue.toFixed(2),
    circleCrossings: crossings,
    fullLapCrossings: fullCrossings,
    maxSlope: +maxSlope.toFixed(2),
    accessMaxSlope: accessRep.maxSlope,
    wallSteps: wall,
    hazDeg: haz.deg,
    hazPoint: haz.point,
    circleEndDriftM: +endDrift.toFixed(3),
    tLap: tLap.map((x) => +x.toFixed(6)),
    constraints: {
      slopeLimit: SLOPE_GATE,
      wallDrLimit: 0.33,
      hazMinDeg: HAZ_MIN_DEG,
      totalM: [894.9, 989.1],
      hazards: 'beacon az 3.5-6.5 s 43-59 (fieldmap circles 4-6); staircase az 159.5-164.5 s 51.5-61.5 (circles 160-164)',
    },
  }
  writeFileSync('test/out/t10-lap-design.json', JSON.stringify(design, null, 1) + '\n')
  writeFileSync('test/t10/lap-legs.json', JSON.stringify(design.legs, null, 1) + '\n')
  console.error('wrote test/out/t10-lap-design.json + test/t10/lap-legs.json')
  console.error(JSON.stringify(design, null, 1))
}