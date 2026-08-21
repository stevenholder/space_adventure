#!/usr/bin/env node
/**
 * C5/C10 field mapper v2 — QA planning tool (reads the captured wire field,
 * never touches product code). Faster than v1: no 3-D BFS; corridor checks
 * are done on the exact geodesic segments the prerun will walk.
 *
 *   node test/t5/fieldmap.mjs [world.json] [out.json]
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, norm3 } from '../lib/field.mjs'

/**
 * Cube-face picker — matches server/internal/terrain/terrain.go FaceOf and
 * client/src/sim/terrain.ts pickFace (signed dominant component; magnitude
 * ties resolve +X,−X,+Y,−Y,+Z,−Z). test/lib/field.mjs faceOf had a
 * sign bug (absolute-magnitude only → never returned faces 1/3/5); fixed
 * 2026-08-19 with Main's approval after verification against both product
 * implementations. This local copy is identical to the fixed lib version
 * and keeps the mapper self-documenting.
 */
function faceOf(d) {
  const score = [d[0], -d[0], d[1], -d[1], d[2], -d[2]]
  let face = 0
  for (let f = 1; f < 6; f++) if (score[f] > score[face]) face = f
  return face
}
const worldPath = process.argv[2] ?? 'test/out/world-seed1337.json'
const outPath = process.argv[3] ?? 'test/out/t5-fieldmap.json'
const world = JSON.parse(readFileSync(worldPath, 'utf8'))
const F = loadField(world)
const SPAWN = [0, 1, 0]
const ARC = 0.5
/** Great-circle walker from spawn at azimuth az; per-node {s, r, slope}.
 *  Closes the circuit: stops only after the full 2π angle is covered. */
function walkCircle(azDeg, arc = ARC) {
  const rad = (azDeg * Math.PI) / 180
  const t = [Math.cos(rad), 0, -Math.sin(rad)]
  const out = []
  let dir = norm3(SPAWN)
  let s = 0
  let theta = 0
  for (let i = 0; i < 2600 && s < 1150; i++) {
    const r = sampleRadius(F, dir)
    out.push({ s: +s.toFixed(3), r: +r.toFixed(4), slope: +slopeDeg(F, dir).toFixed(2), face: faceOf(dir) })
    s += arc
    theta += arc / out[out.length - 1].r
    const d = arc / out[out.length - 1].r
    const c = Math.cos(d), sn = Math.sin(d)
    dir = norm3([dir[0] * c + t[0] * sn, dir[1] * c + t[1] * sn, dir[2] * c + t[2] * sn])
    if (theta >= 2 * Math.PI - 0.004) break
  }
  return out
}

function steepRanges(prof, thresh) {
  const ranges = []
  let from = null
  for (const p of prof) {
    if (p.slope > thresh) {
      if (from === null) from = p.s
    } else if (from !== null) {
      ranges.push([from, p.s])
      from = null
    }
  }
  if (from !== null) ranges.push([from, prof[prof.length - 1].s])
  return ranges
}

const azims = []
for (let a = 0; a < 360; a++) azims.push(a)

const circles = {}
for (const a of azims) {
  const prof = walkCircle(a)
  circles[a] = {
    maxSlope: Math.max(...prof.map((p) => p.slope)),
    steep: steepRanges(prof, 50),
    steep48: steepRanges(prof, 48),
    faces: [...new Set(prof.map((p) => p.face))],
    crossings: (() => {
      const cx = []
      for (let i = 1; i < prof.length; i++) if (prof[i].face !== prof[i - 1].face) cx.push([prof[i].s, prof[i - 1].face, prof[i].face])
      return cx
    })(),
    len: prof[prof.length - 1].s,
    slopeQ: prof.map((p) => Math.round(p.slope * 100)),
    rQ: prof.map((p) => Math.round(p.r * 1000)),
  }
}
console.error(`circles: ${azims.length} az x ${circles[0].len.toFixed(0)} m`)

// lap candidates: full-circuit max slope
const lapAz = azims
  .map((a) => ({ az: a, maxSlope: circles[a].maxSlope, steep: circles[a].steep.length }))
  .sort((x, y) => x.maxSlope - y.maxSlope)
console.error(`cleanest laps (az, maxSlope, #steep>50 ranges): ${JSON.stringify(lapAz.slice(0, 12))}`)

// ---------------------------------------------------------------- ledge scan (radius-only, 0.1 m)
const ledges = []
for (const a of azims) {
  const rad = (a * Math.PI) / 180
  const t = [Math.cos(rad), 0, -Math.sin(rad)]
  let dir = norm3(SPAWN)
  let s = 0
  const rs = [sampleRadius(F, dir)]
  for (let i = 1; i <= 1600; i++) {
    const d = 0.1 / rs[rs.length - 1]
    const c = Math.cos(d), sn = Math.sin(d)
    dir = norm3([dir[0] * c + t[0] * sn, dir[1] * c + t[1] * sn, dir[2] * c + t[2] * sn])
    s += 0.1
    rs.push(sampleRadius(F, dir))
  }
  // find maximal rising runs: cumulative rise >= 0.10 m over width <= 1.5 m
  for (let i = 1; i < rs.length; i++) {
    const dr = rs[i] - rs[i - 1]
    if (dr < 0.02) continue
    let j = i, rise = 0
    while (j + 1 < rs.length && j - i + 1 <= 15) {
      rise += rs[j + 1] - rs[j]
      // the run is monotone-ish: allow small dips of <0.02
      if (rs[j + 1] - rs[j] < -0.02) break
      j++
      if (rise >= 0.1) break
    }
    const width = j - i + 1
    if (rise >= 0.1 && width <= 15 && rise / width >= 0.06) {
      const s0 = +(i * 0.1).toFixed(2), s1 = +((j + 1) * 0.1).toFixed(2)
      const approach = Math.max(0, i - 10 >= 0 ? rs[i] - rs[i - 10] : 0) // 1 m before
      const top = j + 10 < rs.length ? rs[j + 10] - rs[j + 1] : 0 // 1 m after
      // local context slopes from the 2 m flanks
      const ctx = (k) => {
        // crude: rise over 1 m => tan ~ rise
        return (rs[k + 10] - rs[k]) / 1
      }
      ledges.push({
        az: a,
        s0,
        s1,
        rise: +rise.toFixed(4),
        width,
        grad: +(rise / width / 0.1).toFixed(3), // rise per 0.1 m
        approach1m: +approach.toFixed(4),
        top1m: +top.toFixed(4),
        rTop: +rs[j].toFixed(4),
        face: faceOf(dir),
      })
      i = j
    }
  }
}
// dedupe adjacent candidates, rank by rise
ledges.sort((x, y) => y.rise - x.rise)
const topLedges = ledges.slice(0, 30)
console.error(`ledges: ${ledges.length} raw, top rise ${topLedges[0]?.rise} m @ az ${topLedges[0]?.az} s ${topLedges[0]?.s0}`)

// ---------------------------------------------------------------- hexagonal great circles (planes ⊥ body diagonals)
// The only great circles that can cross all six cube faces (a great circle
// through spawn always contains the Y axis → at most 4 faces). Four body
// diagonals → four candidate circles; the terrain decides which (if any)
// is fully walkable (max slope < 50° over the full circuit).
function hexCircle(diag) {
  const dn = norm3(diag)
  // plane {p : p·dn = 0}; a point in it: p0 = (−dn[1], dn[0], 0)
  const p0 = norm3([-dn[1], dn[0], 0])
  // tangent at p0: dn × p0 (⊥ p0 and ⊥ dn → in-plane tangent)
  let b = norm3([
    dn[1] * p0[2] - dn[2] * p0[1],
    dn[2] * p0[0] - dn[0] * p0[2],
    dn[0] * p0[1] - dn[1] * p0[0],
  ])
  const prof = []
  let dir = p0.slice()
  let s = 0
  let theta = 0
  for (let i = 0; i < 2600 && s < 1150; i++) {
    const r = sampleRadius(F, dir)
    prof.push({ s: +s.toFixed(3), r: +r.toFixed(4), slope: +slopeDeg(F, dir).toFixed(2), face: faceOf(dir) })
    s += 0.5
    theta += 0.5 / prof[prof.length - 1].r
    const d = 0.5 / prof[prof.length - 1].r
    const c = Math.cos(d), sn = Math.sin(d)
    dir = norm3([dir[0] * c + b[0] * sn, dir[1] * c + b[1] * sn, dir[2] * c + b[2] * sn])
    if (theta >= 2 * Math.PI - 0.004) break
  }
  const minSlope = prof.reduce((m, p) => (p.slope < m.slope ? p : m), prof[0])
  return {
    diag: diag.map((x) => +x.toFixed(4)),
    start: p0.map((x) => +x.toFixed(6)),
    minSlopeAt: { s: minSlope.s, slope: minSlope.slope, face: minSlope.face },
    maxSlope: Math.max(...prof.map((p) => p.slope)),
    steep: steepRanges(prof, 50),
    steep48: steepRanges(prof, 48),
    faces: [...new Set(prof.map((p) => p.face))],
    crossings: (() => {
      const cx = []
      for (let i = 1; i < prof.length; i++) if (prof[i].face !== prof[i - 1].face) cx.push([prof[i].s, prof[i - 1].face, prof[i].face])
      return cx
    })(),
    len: prof[prof.length - 1].s,
    closed: theta >= 2 * Math.PI - 0.004,
    slopeQ: prof.map((p) => Math.round(p.slope * 100)),
    rQ: prof.map((p) => Math.round(p.r * 1000)),
  }
}
const hexes = [hexCircle([1, 1, 1]), hexCircle([1, 1, -1]), hexCircle([1, -1, 1]), hexCircle([-1, 1, 1])]
for (const h of hexes) console.error(`hex ${h.diag.join(',')}: len ${h.len.toFixed(1)} m closed=${h.closed} maxSlope ${h.maxSlope} faces ${h.faces.join(',')} steep>50: ${JSON.stringify(h.steep)} minSlope @${h.minSlopeAt.s}=${h.minSlopeAt.slope}`)

// ---------------------------------------------------------------- geodesic segment checker
/** Direction at (az, s) on the az great circle; on-demand circle for non-integer az. */
const circleCache = { ...circles }
function dirAt(az, s) {
  if (!circleCache[az]) circleCache[az] = (() => {
    const prof = walkCircle(az)
    return { rQ: prof.map((p) => Math.round(p.r * 1000)) }
  })()
  const prof = circleCache[az]
  const n = Math.min(prof.rQ.length - 1, Math.round(s / ARC))
  const rad = (az * Math.PI) / 180
  const t = [Math.cos(rad), 0, -Math.sin(rad)]
  let dir = norm3(SPAWN)
  for (let i = 0; i < n; i++) {
    const r = prof.rQ[i] / 1000
    const d = ARC / r
    const c = Math.cos(d), sn = Math.sin(d)
    dir = norm3([dir[0] * c + t[0] * sn, dir[1] * c + t[1] * sn, dir[2] * c + t[2] * sn])
  }
  return dir
}

/** Walk the geodesic between two direction points; profile of slope every 2 m. */
function geodesic(p1, p2, label) {
  const a1 = norm3(p1), a2 = norm3(p2)
  const ang = Math.acos(Math.max(-1, Math.min(1, a1[0] * a2[0] + a1[1] * a2[1] + a1[2] * a2[2])))
  const s1 = Math.sin(ang)
  const out = { label, angleDeg: +(ang * 180 / Math.PI).toFixed(2) }
  if (s1 < 1e-9) return out
  // tangent at a1 toward a2
  let dir = a1.slice()
  const tang = norm3([a2[0] - a1[0] * (a1[0] * a2[0] + a1[1] * a2[1] + a1[2] * a2[2]),
    a2[1] - a1[1] * (a1[0] * a2[0] + a1[1] * a2[1] + a1[2] * a2[2]),
    a2[2] - a1[2] * (a1[0] * a2[0] + a1[1] * a2[1] + a1[2] * a2[2])])
  let s = 0
  let maxSlope = 0
  const samples = []
  for (let i = 0; i < 2000; i++) {
    const r = sampleRadius(F, dir)
    const sl = slopeDeg(F, dir)
    maxSlope = Math.max(maxSlope, sl)
    if (i % 4 === 0) samples.push({ s: +s.toFixed(1), slope: +sl.toFixed(2), r: +r.toFixed(3) })
    const cosTh = a1[0] * dir[0] + a1[1] * dir[1] + a1[2] * dir[2]
    if (Math.acos(Math.max(-1, Math.min(1, cosTh))) >= ang - 0.005) break
    const d = 0.5 / r
    const c = Math.cos(d), sn = Math.sin(d)
    dir = norm3([dir[0] * c + tang[0] * sn, dir[1] * c + tang[1] * sn, dir[2] * c + tang[2] * sn])
    s += 0.5
    if (s > ang * 200) break
  }
  out.s = +s.toFixed(1)
  out.maxSlope = +maxSlope.toFixed(2)
  out.samples = samples.filter((x) => x.s % 10 < 0.6)
  out.steep48 = samples.filter((x) => x.slope > 48).map((x) => x.s)
  return out
}

const segs = [
  geodesic([0, 1, 0], dirAt(15, 18), 'L1 level az15 s0-18'),
  geodesic(dirAt(15, 22), dirAt(15, 70), 'L3 slope az15 s22-70'),
  geodesic(dirAt(15, 70), dirAt(15, 125), 'L4 seam az15 s70-125'),
  geodesic(dirAt(15, 125), dirAt(15, 46), 'L5 return az15 s125-46'),
  geodesic(dirAt(15, 46), dirAt(77.5, 46), 'L6 highway az15-77.5 s~46 dip'),
  geodesic(dirAt(77.5, 46), dirAt(77.5, 56), 'L7 ledge climb az77.5 s46-56'),
  geodesic(dirAt(77.5, 56), dirAt(77.5, 60), 'L8 shoulder az77.5 s56-60'),
]
console.error('geodesics:')
for (const g of segs) console.error(`  ${g.label}: ${g.s ?? '?'} m maxSlope=${g.maxSlope ?? '?'} steep48@${(g.steep48 ?? []).join(',') || '-'}`)

const out = { world: worldPath, spawn: { r: sampleRadius(F, SPAWN), face: faceOf(SPAWN) }, circles, lapAz: lapAz.slice(0, 20), ledges: topLedges, hexes, geodesics: segs }
writeFileSync(outPath, JSON.stringify(out))
console.error(`wrote ${outPath}`)