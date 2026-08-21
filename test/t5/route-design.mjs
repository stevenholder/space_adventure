#!/usr/bin/env node
/**
 * C5/C10 route designer — computes great-circle path terrain from the
 * captured u16 field (test/lib/field.mjs, READ-ONLY) and emits legs for
 * prerun.ts.
 *
 *  (1) C5 conformance route: level -> aim (beacon-base dir) -> slide
 *      (beacon flank) -> ledge (az 162.5 staircase, stepUps) -> jump ->
 *      seam (face 2->0 crossing).
 *  (2) C10 lap: closed loop of six 90° great-circle legs (four meridian
 *      quadrants + two equator quadrants) crossing all six cube faces.
 *      A single great circle through the spawn pole crosses only 4 faces,
 *      so the lap is a loop of held-direction legs (each leg holds one
 *      direction; turns happen between legs).
 *
 *   node test/t5/route-design.mjs [--az <meridian az>]
 *
 * Output: test/t5/legs.json (v2) + test/out/t5-route-design.json (full
 * per-leg terrain reports).
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180 // normal_eps 2 deg, same as client sim
const slopeAt = (d) => slopeDeg(F, d, EPS)

const SPAWN = [0, 1, 0]
const R0 = sampleRadius(F, SPAWN)
const DEG = 180 / Math.PI

// ---------------------------------------------------------------- geometry
// Great-circle walk from unit direction u with unit tangent t (t ⊥ u),
// arc length measured with the local terrain radius. The circle lies in
// the plane with normal n = u × t; the tangent at any point is n × dir.
function walkCircle(u0, t0, distM, stepM = 0.25) {
  const n = norm3([u0[1] * t0[2] - u0[2] * t0[1], u0[2] * t0[0] - u0[0] * t0[2], u0[0] * t0[1] - u0[1] * t0[0]])
  const s = []
  let dir = norm3(u0)
  let arc = 0
  let prevR = sampleRadius(F, dir)
  s.push({ s: 0, r: +prevR.toFixed(4), slope: +slopeAt(dir).toFixed(2), face: faceOf(dir), dr: 0, dir: dir.map((x) => +x.toFixed(6)) })
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
    s.push({ s: +arc.toFixed(3), r: +rN.toFixed(4), slope: +slopeAt(next).toFixed(2), face: faceOf(next), dr: +(rN - prevR).toFixed(4), dir: next.map((x) => +x.toFixed(6)) })
    prevR = rN
    dir = next
  }
  const tEnd = [n[1] * dir[2] - n[2] * dir[1], n[2] * dir[0] - n[0] * dir[2], n[0] * dir[1] - n[1] * dir[0]]
  return { samples: s, tEnd: norm3(tEnd) }
}

// Terrain verdict for a leg's samples.
function report(samples) {
  let minSlope = Infinity
  let maxSlope = -Infinity
  let maxDr = 0
  let steep48 = 0
  let steep50 = 0
  let wall = 0 // dr spikes a walking body (0.225 m/tick) could not step
  const faces = []
  for (const p of samples) {
    minSlope = Math.min(minSlope, p.slope)
    maxSlope = Math.max(maxSlope, p.slope)
    maxDr = Math.max(maxDr, Math.abs(p.dr))
    if (p.slope > 48) steep48 += p.s
    if (p.slope > 50) steep50 += p.s
    if (p.dr > 0.33) wall += 1
    if (faces[faces.length - 1] !== p.face) faces.push(p.face)
  }
  const walkable = maxSlope <= 50 && wall === 0
  return { minSlope: +minSlope.toFixed(2), maxSlope: +maxSlope.toFixed(2), maxDr: +maxDr.toFixed(4), steep48m: +steep48.toFixed(1), steep50m: +steep50.toFixed(1), wallSteps: wall, faces, walkable, endDir: samples[samples.length - 1].dir }
}

// Great-circle tangent at unit position p toward unit target q (plane span(p,q)).
function tangentTo(p, q) {
  const c = p[0] * q[0] + p[1] * q[1] + p[2] * q[2]
  const t = norm3([q[0] - p[0] * c, q[1] - p[1] * c, q[2] - p[2] * c])
  return t
}


// Signed rotation (deg) taking tPrev to tNext about the local up at p —
// exactly the angle prerun's rotAbout applies. Well-defined for poleward
// tangents (unlike an azimuth difference).
function relTurn(p, tPrev, tNext) {
  const up = norm3(p)
  const d = tPrev[0] * tNext[0] + tPrev[1] * tNext[1] + tPrev[2] * tNext[2]
  const c = [tPrev[1] * tNext[2] - tPrev[2] * tNext[1], tPrev[2] * tNext[0] - tPrev[0] * tNext[2], tPrev[0] * tNext[1] - tPrev[1] * tNext[0]]
  const s = c[0] * up[0] + c[1] * up[1] + c[2] * up[2]
  return +(Math.atan2(s, d) * DEG).toFixed(3)
}

// ---------------------------------------------------------------- C10 lap
// A great circle through the spawn pole crosses only 4 of the 6 cube faces
// (its plane contains the pole axis). A generic tilted circle — plane
// through the origin whose normal is off all three axes — crosses all six.
// Lap route: short access meridian spawn -> circle nearest point (on the
// flat spawn disc), ONE full circuit holding a single direction, same
// meridian back. Closes at the spawn.
function meridianArc(n, from, theta) {
  let d = norm3(from)
  let arc = 0
  for (let k = 0; k < 360; k++) {
    const r = sampleRadius(F, d)
    const step = theta / 360
    const dd = step / r
    const tt = [n[1] * d[2] - n[2] * d[1], n[2] * d[0] - n[0] * d[2], n[0] * d[1] - n[1] * d[0]]
    const nd = norm3([d[0] * Math.cos(dd) + tt[0] * Math.sin(dd), d[1] * Math.cos(dd) + tt[1] * Math.sin(dd), d[2] * Math.cos(dd) + tt[2] * Math.sin(dd)])
    arc += 0.5 * (r + sampleRadius(F, nd)) * step
    d = nd
  }
  return arc
}

function designCircleLap(betaDeg, alphaDeg) {
  const b = (betaDeg * Math.PI) / 180
  const a = (alphaDeg * Math.PI) / 180
  const n = norm3([Math.sin(b) * Math.cos(a), Math.cos(b), Math.sin(b) * Math.sin(a)])
  // Circle's nearest point to the N pole: the in-plane projection of Y.
  const yn = n[1]
  const dStar = norm3([-n[0] * yn, 1 - n[1] * yn, -n[2] * yn])
  const tAccess = tangentTo(SPAWN, dStar)
  const thetaStar = Math.acos(Math.max(-1, Math.min(1, dStar[1])))
  const nAcc = norm3([tAccess[1] * SPAWN[2] - tAccess[2] * SPAWN[1], tAccess[2] * SPAWN[0] - tAccess[0] * SPAWN[2], tAccess[0] * SPAWN[1] - tAccess[1] * SPAWN[0]])
  const accessArc = meridianArc(nAcc, SPAWN, thetaStar)
  const tLap = norm3([n[1] * dStar[2] - n[2] * dStar[1], n[2] * dStar[0] - n[0] * dStar[2], n[0] * dStar[1] - n[1] * dStar[0]])
  const per = meridianArc(n, dStar, 2 * Math.PI)
  const w = walkCircle(dStar, tLap, per, 0.25)
  const rep = report(w.samples)
  return { beta: betaDeg, alpha: alphaDeg, n, dStar, tAccess, tLap, accessArc: +accessArc.toFixed(2), per: +per.toFixed(2), rep }
}

// Scan tilt (beta: normal's angle from +Y) and rotation (alpha) for a
// fully walkable circle crossing all six faces, with its spawn-side point
// on the flat disc (accessArc <= 45 m).
let lap = null
for (let beta = 75; beta <= 85; beta += 5) {
  for (let alpha = 0; alpha < 360; alpha += 5) {
    const c = designCircleLap(beta, alpha)
    if (c.rep.faces.length === 6 && c.rep.walkable && c.rep.maxSlope <= 48 && c.accessArc <= 45) {
      if (!lap || c.rep.maxSlope < lap.rep.maxSlope) lap = c
    }
  }
}
if (!lap) {
  console.error('C10: no walkable six-face circle found in the scan grid')
  process.exit(1)
}
const tReturn = norm3([-lap.tAccess[0], -lap.tAccess[1], -lap.tAccess[2]])
const lapAccessAz = +((Math.atan2(-lap.tAccess[2], lap.tAccess[0]) * DEG + 360) % 360).toFixed(3)
const lapCircleAz = relTurn(lap.dStar, lap.tAccess, lap.tLap)
const lapReturnAz = relTurn(lap.dStar, lap.tLap, tReturn)
writeFileSync('test/t10/lap-legs.json', JSON.stringify([
  { label: 'lapAccess', az: lapAccessAz, dist: lap.accessArc },
  { label: 'lapCircle', az: lapCircleAz, dist: lap.per },
  { label: 'lapReturn', az: lapReturnAz, dist: lap.accessArc },
], null, 1) + '\n')
console.error(`lap: beta=${lap.beta} alpha=${lap.alpha} per=${lap.per} m access=${lap.accessArc} m maxSlope=${lap.rep.maxSlope} faces=[${lap.rep.faces}] walkable=${lap.rep.walkable}`)
console.error(`  access az=${lapAccessAz} (spawn->entry ${lap.dStar.map((x) => x.toFixed(4))}), circle turn=${lapCircleAz}, return turn=${lapReturnAz}`)

// ---------------------------------------------------------------- C5 route
// Fixed points of interest (from ledge-scan events + beacon geometry).
const AIM_DIR = norm3([0.322743, 0.946065, -0.028236]) // beacon base (task-specified)
const LEDGE_DIR = norm3([-0.309533, 0.945867, -0.097595]) // az 162.5 staircase top step (0.2951 m)

// Leg 0 end: 20 m along meridian 0 (flat disc).
const w0 = walkCircle(SPAWN, norm3([1, 0, 0]), 20, 0.25)
const p0 = w0.samples[w0.samples.length - 1].dir
const t0 = w0.tEnd
const rep0 = report(w0.samples)

// Leg 1 (aim): steer the look at AIM_DIR; the body walks toward the beacon
// base. Geometric approximation: great circle from p0 toward AIM_DIR's
// surface point, ending ~1 m short of the 0.2899 m step-up (s≈46.97 on
// meridian 5). dist = chord distance.
const aimTarget = norm3(AIM_DIR)
const t1 = tangentTo(p0, aimTarget)
const w1 = walkCircle(p0, t1, 999, 0.25)
// find where this path comes closest to the step-up zone (slope rise 44-49 deg at s~47 on mer 5): end the aim leg where the path slope first exceeds 30 deg (rim base onset), i.e. keep it on approach ground.
let aimDist = null
for (const p of w1.samples) {
  if (p.slope > 30) {
    aimDist = p.s
    break
  }
}
if (aimDist === null) aimDist = 30
const w1r = walkCircle(p0, t1, aimDist, 0.25)
const p1 = w1r.samples[w1r.samples.length - 1].dir
const t1end = w1r.tEnd
const rep1 = report(w1r.samples)

// Leg 2 (slide): keep steering at AIM_DIR into the flank; body wall-stops
// on the 57-70 deg face and slides back (SLIDE mode). 8 m of walking +
// 60-tick pause for the slide.
const t2 = tangentTo(p1, aimTarget)
const w2 = walkCircle(p1, t2, 8, 0.25)
const p2 = w2.samples[w2.samples.length - 1].dir
const t2end = w2.tEnd
const rep2 = report(w2.samples)

// Leg 3 (ledge): great circle from p2 through LEDGE_DIR; the body steps up
// the staircase (stepUps >= 1) and the leg ends 1 m past the top step.
const t3 = tangentTo(p2, LEDGE_DIR)
const w3 = walkCircle(p2, t3, 999, 0.25)
let ledgeDist = null
{
  // distance along the path to LEDGE_DIR
  let d = norm3(p2)
  let tt = norm3(t3)
  let acc = 0
  for (let k = 0; k < 2400; k++) {
    const r = sampleRadius(F, d)
    const step = 1 / 2400
    const dd = step / r
    const cc = Math.cos(dd)
    const ss = Math.sin(dd)
    const nd = norm3([d[0] * cc + tt[0] * ss, d[1] * cc + tt[1] * ss, d[2] * cc + tt[2] * ss])
    const c = d[0] * LEDGE_DIR[0] + d[1] * LEDGE_DIR[1] + d[2] * LEDGE_DIR[2]
    const cn = nd[0] * LEDGE_DIR[0] + nd[1] * LEDGE_DIR[1] + nd[2] * LEDGE_DIR[2]
    if (cn > c) {
      ledgeDist = acc + step * 0.5
      break
    }
    acc += step
    const proj = nd[0] * d[0] + nd[1] * d[1] + nd[2] * d[2]
    tt = norm3([tt[0] - d[0] * proj, tt[1] - d[1] * proj, tt[2] - d[2] * proj])
    d = nd
  }
  if (ledgeDist === null) ledgeDist = 90
}
ledgeDist += 1.5
const w3r = walkCircle(p2, t3, ledgeDist, 0.25)
const p3 = w3r.samples[w3r.samples.length - 1].dir
const t3end = w3r.tEnd
const rep3 = report(w3r.samples)

// Leg 4 (jump): 1-tick jump leg; the body hops off the staircase top (over
// the wall step), airborne ~19 ticks, lands on the 55 deg face and settles
// on the walkable ground past it. 9 m of walking covers apex + landing.
const t4 = t3end
const w4 = walkCircle(p3, t4, 9, 0.25)
const p4 = w4.samples[w4.samples.length - 1].dir
const t4end = w4.tEnd
const rep4 = report(w4.samples)

// Leg 5 (seam): great circle from p4 crossing the face 2->0 boundary
// (x = y > 0, x >= |z|) and ending >= 8 m past it on face 0. Candidate
// boundary targets: sweep alpha over the boundary curve.
function boundaryDir(alphaDeg) {
  const a = alphaDeg / DEG
  return norm3([Math.cos(a) / Math.SQRT2, Math.cos(a) / Math.SQRT2, -Math.sin(a)])
}
let seam = null
for (let alpha = 8; alpha <= 36; alpha += 2) {
  const S = boundaryDir(alpha)
  const t5 = tangentTo(p4, S)
  const w5 = walkCircle(p4, t5, 999, 0.5)
  // find the crossing of x=y on this path
  let crossS = null
  for (let i = 1; i < w5.samples.length; i++) {
    const a = w5.samples[i - 1]
    const b = w5.samples[i]
    const fa = a.dir[0] - a.dir[1]
    const fb = b.dir[0] - b.dir[1]
    if (fa < 0 && fb >= 0) {
      crossS = b.s
      break
    }
  }
  if (crossS === null) continue
  const endS = crossS + 10
  const w5r = walkCircle(p4, t5, endS, 0.5)
  const rep5 = report(w5r.samples)
  const endFace = w5r.samples[w5r.samples.length - 1].face
  if (endFace !== 0) continue
  // path must be walkable up to (not including) the crossing zone margin
  const score = rep5.maxSlope - 0.01 * endS
  if (!seam || score < seam.score) seam = { alpha, t: t5, endS: +endS.toFixed(2), rep5, score }
}
let legs5 = null
let p5end = null
let t5end = null
if (seam) {
  const w5 = walkCircle(p4, seam.t, seam.endS, 0.5)
  legs5 = { report: report(w5.samples), dist: seam.endS, alpha: seam.alpha, crossingM: null }
  p5end = w5.samples[w5.samples.length - 1].dir
  t5end = w5.tEnd
  for (let i = 1; i < w5.samples.length; i++) {
    const a = w5.samples[i - 1]
    const b = w5.samples[i]
    if (a.dir[0] - a.dir[1] < 0 && b.dir[0] - b.dir[1] >= 0) {
      legs5.crossingM = +b.s.toFixed(2)
      break
    }
  }
} else {
  console.error('SEAM: no walkable 2->0 crossing found from p4')
}

// Relative turns between consecutive legs (the look steering is what
// prerun applies: each leg rotates the carried heading by relAz).
function turns() {
  const seq = [
    { p: SPAWN, t: norm3([1, 0, 0]), az: 0 },
    { p: p0, t: t1, rel: relTurn(p0, t0, t1) },
    { p: p1, t: t2, rel: relTurn(p1, t1end, t2) },
    { p: p2, t: t3, rel: relTurn(p2, t2end, t3) },
    { p: p3, t: t4, rel: relTurn(p3, t3end, t4) },
  ]
  if (seam) seq.push({ p: p4, t: seam.t, rel: relTurn(p4, t4end, seam.t) })
  return seq
}
const T = turns()

console.error('C5 route:')
console.error(`  level: 20 m az0 maxSlope=${rep0.maxSlope} walkable=${rep0.walkable}`)
console.error(`  aim:   ${aimDist.toFixed(1)} m steer@${AIM_DIR.map((x) => x.toFixed(4))} maxSlope=${rep1.maxSlope} walkable=${rep1.walkable}`)
console.error(`  slide: 8 m into flank + 60-tick pause; flank maxSlope=${rep2.maxSlope}`)
console.error(`  ledge: ${ledgeDist.toFixed(1)} m to staircase, maxSlope=${rep3.maxSlope} walkable=${rep3.walkable} faces=[${rep3.faces}]`)
console.error(`  jump:  9 m 1-tick jump; maxSlope=${rep4.maxSlope}`)
if (seam) console.error(`  seam:  ${seam.endS.toFixed(1)} m (alpha=${seam.alpha} deg), crossing at ${legs5.crossingM} m, maxSlope=${seam.rep5.maxSlope}, faces=[${seam.rep5.faces}]`)
console.error(`  turns: ${T.map((x) => x.az ?? x.rel).join(' -> ')}`)

// ---------------------------------------------------------------- emit
const legs = [
  { label: 'level', az: T[0].az, dist: 20 },
  { label: 'aim', az: T[1].rel, dist: +aimDist.toFixed(2), aimDir: AIM_DIR.map((x) => +x.toFixed(6)) },
  { label: 'slide', az: T[2].rel, dist: 8, pauseAfter: 60 },
  { label: 'ledge', az: T[3].rel, dist: +ledgeDist.toFixed(2) },
  { label: 'jump', az: T[4].rel, dist: 9, jumpTicks: 1 },
]
if (seam) legs.push({ label: 'seam', az: T[5].rel, dist: +seam.endS.toFixed(2) })

writeFileSync('test/t5/legs.json', JSON.stringify(legs, null, 1) + '\n')
const out = {
  world: 'test/out/world-seed1337.json',
  spawn: { r: R0, face: faceOf(SPAWN) },
  aim: { dir: AIM_DIR, note: 'task-specified beacon-base steering dir; beacon flank 57-70 deg at 43-58 m az 0-7.5' },
  ledge: { dir: LEDGE_DIR, stepM: 0.2951, slopeDeg: 39.4, az: 162.5, sM: 51.03, note: 'sharpest walkable max_step step (scan top event); staircase cluster 0.275-0.295 m' },
  c5: {
    turns: T.map((x) => x.az ?? x.rel),
    legs: [
      { label: 'level', rep: rep0 },
      { label: 'aim', rep: rep1, dist: aimDist },
      { label: 'slide', rep: rep2 },
      { label: 'ledge', rep: rep3, dist: ledgeDist },
      { label: 'jump', rep: rep4 },
      seam ? { label: 'seam', rep: seam.rep5, dist: seam.endS, alpha: seam.alpha, crossingM: legs5?.crossingM } : null,
    ],
  },
  c10: lap,
}
writeFileSync('test/out/t5-route-design.json', JSON.stringify(out, null, 1))
console.error('wrote test/t5/legs.json + test/out/t5-route-design.json')