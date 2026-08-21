#!/usr/bin/env node
/**
 * C10 STEP 2/3 - waypoint generator for the closed-loop lap.
 *
 * Loop family (6 great-circle arcs):
 *   leg1: SPAWN -> eq(az A1)         [meridian, 90 deg]
 *   j1  : eq(az A1) -> eq(az Q2)     [equatorial geodesic]
 *   leg2: eq(az Q2) -> P3 (colat, az) [great circle]
 *   leg3: P3 -> eq(az 225.5)         [great circle; Q4 fixed: +Z sliver]
 *   j3  : eq(az 225.5) -> eq(az Q5)  [equatorial geodesic]
 *   leg4: eq(az Q5) -> SPAWN         [meridian, 90 deg]
 *
 *   node test/t10/lap-waypoints.mjs <A1> <Q2> <P3colat> <P3az> <Q5> [outPath]
 *
 * Emits: per-waypoint unit dir, surface radius, cumulative flat s, face;
 * plus face-run crossing positions (for the sim-lap crossing assertion).
 * One point every ~0.5 deg of arc angle (~0.8-1.4 m), plus the endpoints.
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { loadField, sampleRadius, faceOf, norm3 } from '../lib/field.mjs'

const args = process.argv.slice(2)
const A1 = Number(args[0] || 30)
const Q2 = Number(args[1] || 55)
const P3COLAT = Number(args[2] || 28)
const P3AZ = Number(args[3] || 198.5)
const Q5 = Number(args[4] || 225)
const OUT = args[5] || 'test/out/t10-lap-waypoints.json'
const Q4 = 225.5

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const DEG = 180 / Math.PI
const RAD = (d) => (d * Math.PI) / 180

const SPAWN = [0, 1, 0]
const eqPt = (az) => norm3([Math.cos(RAD(az)), 0, -Math.sin(RAD(az))])
const farPt = (phi, az) =>
  norm3([Math.sin(RAD(phi)) * Math.cos(RAD(az)), -Math.cos(RAD(phi)), -Math.sin(RAD(phi)) * Math.sin(RAD(az))])

const P3 = farPt(P3COLAT, P3AZ)
const SEGS = [
  { label: 'leg1', p0: SPAWN, p1: eqPt(A1) },
  { label: 'j1', p0: eqPt(A1), p1: eqPt(Q2) },
  { label: 'leg2', p0: eqPt(Q2), p1: P3 },
  { label: 'leg3', p0: P3, p1: eqPt(Q4) },
  { label: 'j3', p0: eqPt(Q4), p1: eqPt(Q5) },
  { label: 'leg4', p0: eqPt(Q5), p1: SPAWN },
]

const wps = []
let s = 0
for (const sg of SEGS) {
  const c = Math.max(-1, Math.min(1, sg.p0[0] * sg.p1[0] + sg.p0[1] * sg.p1[1] + sg.p0[2] * sg.p1[2]))
  const theta = Math.acos(c)
  const n = norm3([
    sg.p0[1] * sg.p1[2] - sg.p0[2] * sg.p1[1],
    sg.p0[2] * sg.p1[0] - sg.p0[0] * sg.p1[2],
    sg.p0[0] * sg.p1[1] - sg.p0[1] * sg.p1[0],
  ])
  const N = Math.max(2, Math.round(theta / RAD(0.5)))
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
  let rPrev = sampleRadius(F, sg.p0)
  for (let k = 1; k <= N; k++) {
    const dir = k === N ? norm3(sg.p1) : rotate(sg.p0, (theta * k) / N)
    const r = sampleRadius(F, dir)
    s += 0.5 * (rPrev + r) * (theta / N)
    rPrev = r
    wps.push({
      seg: sg.label,
      s: +s.toFixed(3),
      r: +r.toFixed(4),
      face: faceOf(dir),
      dir: dir.map((x) => +x.toFixed(7)),
    })
  }
}
// face-run crossings (midpoint s of each face change)
const crossings = []
for (let i = 1; i < wps.length; i++) {
  if (wps[i].face !== wps[i - 1].face) {
    crossings.push({ s: +(((wps[i].s + wps[i - 1].s) / 2).toFixed(2)), from: wps[i - 1].face, to: wps[i].face })
  }
}
const facesVisited = [...new Set(wps.map((w) => w.face))].sort((a, b) => a - b)
const out = {
  params: { A1, Q2, P3colat: P3COLAT, P3az: P3AZ, Q4, Q5 },
  total: +s.toFixed(3),
  lastS: s,
  n: wps.length,
  facesVisited,
  crossings,
  wps,
}
writeFileSync(OUT, JSON.stringify(out))
console.error(`wrote ${OUT}: ${wps.length} waypoints, total s=${s.toFixed(2)} m, faces=${facesVisited}, ${crossings.length} crossings: ` + crossings.map((x) => `${x.from}>${x.to}@${x.s}`).join(' '))