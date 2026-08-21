#!/usr/bin/env node
/**
 * C10 STEP 1 — diagnosis of the zero-prefilter-pass 4-leg loop scan.
 *
 * Inputs (read-only):
 *   - test/out/t10-loop-scan.jsonl  (phase 'legs' rows: the 13,920-candidate
 *     2 m prefilter scan; phase 'survey' rows: the corrected 2 deg global
 *     terrain survey — the source the prefilter's steep-cell guard consumes)
 *   - test/out/world-seed1337.json  (captured world, u16 wire field)
 *
 * Outputs:
 *   - test/out/t10-diag-top20.jsonl  one row per rebuilt top candidate
 *     (incremental; re-run skips recorded keys — crash safety)
 *   - test/out/t10-diagnosis.json    assembled report:
 *       a) prefilter summary (gate margins at 49.5 / 50.0 / 50.5)
 *       b) top-20 candidates by fewest gate violations with per-leg
 *          maxSlope + max dr at 2 m AND 0.25 m (which leg fails, by how much)
 *       c) survey semantics check (which dataset the prefilter guard uses)
 *       d) far-pole reachability: per-azimuth meridian maxSlope over the
 *          far band (lat -88..-44 = colat 46..130 from +Y), per-10-deg
 *          sector minima, fully-walkable azimuth windows at each gate, and
 *          an 8-neighbour DP over the survey grid (path from colat ~46 to
 *          colat 130 staying within cells of slope <= gate) per starting az
 *
 * Gates: 49.5 (predecessor margin) and 50.0 (GDD hard limit, spec-legit).
 *
 *   node test/t10/diagnose.mjs run        # rebuild top 20 (incremental)
 *   node test/t10/diagnose.mjs finalize   # write test/out/t10-diagnosis.json
 */
import { readFileSync, appendFileSync, writeFileSync, existsSync } from 'node:fs'
import { loadField, sampleRadius, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)
const EPS = (2 * Math.PI) / 180
const slopeAt = (d) => slopeDeg(F, d, EPS)
const DEG = 180 / Math.PI
const RAD = (d) => (d * Math.PI) / 180

const SPAWN = [0, 1, 0]
const SCAN = 'test/out/t10-loop-scan.jsonl'
const TOP20OUT = 'test/out/t10-diag-top20.jsonl'
const OUT = 'test/out/t10-diagnosis.json'
const GATES = [49.5, 50.0]

// ---------- arc + buildLoop (copied from loop-design.mjs — same code, no
// import: that file runs its main logic at top level) ----------
function arc(p0, q0, stepM) {
  const c = Math.max(-1, Math.min(1, p0[0] * q0[0] + p0[1] * q0[1] + p0[2] * q0[2]))
  const theta = Math.acos(c)
  const n = norm3([p0[1] * q0[2] - p0[2] * q0[1], p0[2] * q0[0] - p0[0] * q0[2], p0[0] * q0[1] - p0[1] * q0[0]])
  let t0 = [n[1] * p0[2] - n[2] * p0[1], n[2] * p0[0] - n[0] * p0[2], n[0] * p0[1] - n[1] * p0[0]]
  if (t0[0] * q0[0] + t0[1] * q0[1] + t0[2] * q0[2] < 0) {
    n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]
    t0 = [-t0[0], -t0[1], -t0[2]]
  }
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
  let tru = 0
  for (let k = 1; k < s.length; k++) {
    const a = s[k - 1]
    const b = s[k]
    tru += Math.hypot(b.dir[0] * b.r - a.dir[0] * a.r, b.dir[1] * b.r - a.dir[1] * a.r, b.dir[2] * b.r - a.dir[2] * a.r)
  }
  return { samples: s, tStart: t0, tEnd, n, thetaDeg: +(theta * DEG).toFixed(3), flat: +flat.toFixed(3), tru: +tru.toFixed(3), from: p0 }
}

const eqPt = (az) => [Math.cos(RAD(az)), 0, -Math.sin(RAD(az))]
const farPt = (phiDeg, azDeg) => {
  const p = RAD(phiDeg)
  const a = RAD(azDeg)
  return [Math.sin(p) * Math.cos(a), -Math.cos(p), -Math.sin(p) * Math.sin(a)]
}

function buildLoop(A1, A2, A5, A6, phi3, B3, stepM) {
  const segs = [
    { label: 'leg1', a: arc(SPAWN, eqPt(A1), stepM) },
    { label: 'j1', a: arc(eqPt(A1), eqPt(A2), stepM) },
    { label: 'leg2', a: arc(eqPt(A2), farPt(phi3, B3), stepM) },
    { label: 'leg3', a: arc(farPt(phi3, B3), eqPt(A5), stepM) },
    { label: 'j3', a: arc(eqPt(A5), eqPt(A6), stepM) },
    { label: 'leg4', a: arc(eqPt(A6), SPAWN, stepM) },
  ]
  let maxSlope = -Infinity
  let totalFlat = 0
  let totalTru = 0
  for (const sg of segs) {
    totalFlat += sg.a.flat
    totalTru += sg.a.tru
    for (const p of sg.a.samples) maxSlope = Math.max(maxSlope, p.slope)
  }
  return { segs, totalFlat: +totalFlat.toFixed(2), totalTru: +totalTru.toFixed(2), maxSlope: +maxSlope.toFixed(2) }
}

function legStats(a, stepM) {
  let mx = -Infinity
  let maxDr = -Infinity
  const faces = []
  for (const p of a.samples) {
    mx = Math.max(mx, p.slope)
    if (p.dr > maxDr) maxDr = p.dr
    if (faces[faces.length - 1] !== p.face) faces.push(p.face)
  }
  return { label: a.label || '', thetaDeg: a.thetaDeg, flat: a.flat, tru: a.tru, maxSlope: +mx.toFixed(2), maxDrM: +maxDr.toFixed(3), faces }
}

// ---------- rows ----------
const rows = []
for (const l of readFileSync(SCAN, 'utf8').split('\n')) {
  if (!l.trim()) continue
  const r = JSON.parse(l)
  if (r.phase === 'legs' && r.pre) rows.push(r)
}
const survey = []
for (const l of readFileSync(SCAN, 'utf8').split('\n')) {
  if (!l.trim()) continue
  const r = JSON.parse(l)
  if (r.phase === 'survey') survey.push(r)
}

// ---------- (a) prefilter summary: margins at both gates ----------
const summary = {
  candidates: rows.length,
  uniqueKeys: new Set(rows.map((r) => `${r.A2}|${r.A5}|${r.A6}|${r.phi3}|${r.B3}`)).size,
  pass: rows.filter((r) => r.pass).length,
  sixFaces: rows.filter((r) => r.distinct.length === 6).length,
  lenWindow880_1005: rows.filter((r) => r.total >= 880 && r.total <= 1005).length,
  maxSlope: {
    min: Math.min(...rows.map((r) => r.maxSlope)),
    max: Math.max(...rows.map((r) => r.maxSlope)),
    'le49.5': rows.filter((r) => r.maxSlope <= 49.5).length,
    'le50.0': rows.filter((r) => r.maxSlope <= 50.0).length,
    'le50.5': rows.filter((r) => r.maxSlope <= 50.5).length,
  },
  steepTouchGT0: rows.filter((r) => r.steepTouch > 0).length,
  'maxQuarterOver90.5': rows.filter((r) => r.maxQuarter > 90.5).length,
  totalM: {
    min: Math.min(...rows.map((r) => r.total)),
    max: Math.max(...rows.map((r) => r.total)),
  },
}

// ---------- (b) top 20 by fewest gate violations ----------
// Violation set = the gates the predecessor's prefilter applied at 2 m
// (maxSlope <= 50.5 there; 49.5/50.0 margins reported separately).
function violations(r) {
  const v = []
  if (r.distinct.length !== 6) v.push('faces')
  if (!(r.total >= 880 && r.total <= 1005)) v.push('length')
  if (r.maxSlope > 50.5) v.push('slope50.5')
  if (r.maxQuarter > 90.5) v.push('quarter')
  if (r.steepTouch > 0) v.push('steepTouch')
  return v
}
const ranked = rows
  .map((r) => ({ r, v: violations(r) }))
  .sort(
    (a, b) =>
      a.v.length - b.v.length ||
      a.r.maxSlope - b.r.maxSlope ||
      Math.abs(a.r.total - 942) - Math.abs(b.r.total - 942)
  )
const top20 = ranked.slice(0, 20)

const doneKeys = new Set()
if (existsSync(TOP20OUT)) {
  for (const l of readFileSync(TOP20OUT, 'utf8').split('\n')) {
    if (!l.trim()) continue
    const r = JSON.parse(l)
    doneKeys.add(`${r.A2}:${r.A5}:${r.A6}:${r.phi3}:${r.B3}`)
  }
}

if (process.argv[2] === 'run') {
  for (const { r, v } of top20) {
    const key = `${r.A2}:${r.A5}:${r.A6}:${r.phi3}:${r.B3}`
    if (doneKeys.has(key)) continue
    const L2 = buildLoop(r.A1, r.A2, r.A5, r.A6, r.phi3, r.B3, 2)
    const Lf = buildLoop(r.A1, r.A2, r.A5, r.A6, r.phi3, r.B3, 0.25)
    const legs2 = L2.segs.map((s) => legStats({ ...s.a, label: s.label }, 2))
    const legsF = Lf.segs.map((s) => legStats({ ...s.a, label: s.label }, 0.25))
    const line = JSON.stringify({
      key,
      A1: r.A1, A2: r.A2, A5: r.A5, A6: r.A6, phi3: r.phi3, B3: r.B3,
      violations: v,
      total2m: L2.totalFlat, totalFull: Lf.totalFlat, totalTru: Lf.totalTru,
      maxSlope2m: L2.maxSlope, maxSlopeFull: Lf.maxSlope,
      margin49_5: +(L2.maxSlope - 49.5).toFixed(2),
      margin50_0: +(L2.maxSlope - 50.0).toFixed(2),
      legs: legs2.map((l2, i) => ({ ...l2, ...legsF[i] })),
    })
    appendFileSync(TOP20OUT, line + '\n')
    console.error(`  rebuilt ${key} (viol=${v.length}, maxSlope2m=${L2.maxSlope}, maxSlopeFull=${Lf.maxSlope})`)
  }
  console.error(`run: top 20 rebuild done -> ${TOP20OUT}`)
} else if (process.argv[2] === 'finalize') {
  // ---------- (c) survey semantics ----------
  const latSet = new Set(survey.map((r) => r.lat))
  const lonSet = new Set(survey.map((r) => r.lon))
  const surveySemantics = {
    prefilterGuardSource: "phase 'survey' rows of t10-loop-scan.jsonl (corrected 2-deg global survey), NOT test/out/t5-fieldmap.json (loop-design.mjs:85-98)",
    surveyRows: survey.length,
    latValues: [...latSet].length,
    lonValues: [...lonSet].length,
    gridComplete: survey.length === latSet.size * lonSet.size,
    t5FieldmapDependence: 'none — loop-design.mjs reads only t10-loop-scan.jsonl + world-seed1337.json',
    note: 'survey = 2-deg point samples (corrected arc-length drift of t5-fieldmap); 2-deg cells ~4.7 m apart at r~135 m, so cell values under-estimate the true max between samples by up to ~1-2 deg (observed 2m->0.25m uplift in the leg scan).',
  }

  // ---------- (d) far-pole reachability from the survey ----------
  // lat = -90..90 (90 = spawn +Y pole, -90 far -Y pole); lon/az 0..358.
  // colat from +Y = 90 - lat. Far band: lat -88..-44 (colat 46..130).
  // Face -Y entry needs colat > 135 = lat < -45; use lat -88..-44 band +
  // lat -44..-2 mid band for the approach.
  const latIdx = {}
  for (const l of latSet) latIdx[l] = l
  const lats = [...latSet].sort((a, b) => a - b) // -88..88
  const lonVals = [...lonSet].sort((a, b) => a - b) // 0..358
  const cell = {}
  for (const r of survey) cell[`${r.lat},${r.lon}`] = r

  const bandMax = (latLo, latHi) => {
    // per-lon max slope over the lat band
    const out = {}
    for (const lon of lonVals) {
      let mx = -Infinity
      for (const lat of lats) if (lat >= latLo && lat <= latHi) {
        const c = cell[`${lat},${lon}`]
        if (c && c.slope > mx) mx = c.slope
      }
      out[lon] = mx
    }
    return out
  }

  const bands = {
    far: { latLo: -88, latHi: -44, desc: 'colat 46..130 from +Y (far-pole approach incl. -Y face entry)' },
    mid: { latLo: -44, latHi: 44, desc: 'equatorial ring (face bands +X/-X/+Z/-Z)' },
    spawn: { latLo: 44, latHi: 88, desc: 'spawn cap (+Y face)' },
  }

  const reachability = {}
  for (const [name, b] of Object.entries(bands)) {
    const perLon = bandMax(b.latLo, b.latHi)
    const vals = lonVals.map((lon) => perLon[lon])
    const minMax = Math.min(...vals)
    const perGate = {}
    for (const g of GATES) {
      const walk = vals.map((v) => v <= g)
      // longest contiguous fully-walkable az window (cyclic, 2-deg cells)
      let bestLen = 0
      let bestStart = -1
      const doubled = [...walk, ...walk]
      for (let i = 0; i < walk.length; i++) {
        let len = 0
        for (let j = i; j < i + walk.length && doubled[j]; j++) len++
        if (len > bestLen) {
          bestLen = len
          bestStart = lonVals[i]
        }
      }
      perGate[g] = {
        fullyWalkableAz: lonVals.filter((lon, i) => walk[i]).length,
        longestWindowDeg: bestLen * 2,
        longestWindowAz: bestLen >= 2 ? `${lonVals[bestStart]}..${(lonVals[bestStart] + bestLen * 2 - 2 + 360) % 360}` : null,
        minMaxSlope: minMax,
        marginToGate: +(minMax - g).toFixed(2),
      }
    }
    // per-10-deg sector minima of the per-az meridian max
    const sectors = []
    for (let s0 = 0; s0 < 360; s0 += 10) {
      let mx = -Infinity
      for (const lon of lonVals) if (lon >= s0 && lon < s0 + 10) mx = Math.max(mx, perLon[lon])
      sectors.push({ az: `${s0}-${s0 + 10}`, meridianMax: +mx.toFixed(2), ok50: mx <= 50.0, ok495: mx <= 49.5 })
    }
    reachability[name] = { ...b, perGate, sectors }
  }

  // DP over the survey grid per gate: from any cell of lat=-44 (colat 46)
  // at starting az, can you reach lat=-88 (colat 130) staying within cells
  // of slope <= gate? 8-neighbourhood, 2-deg steps.
  const dpReach = {}
  for (const g of GATES) {
    const ok = {}
    for (const r of survey) ok[`${r.lat},${r.lon}`] = r.slope <= g
    const idx = {}
    lats.forEach((lat, i) => lonVals.forEach((lon, j) => (idx[`${lat},${lon}`] = i * lonVals.length + j)))
    const H = lats.length
    const W = lonVals.length
    const reach = new Set()
    // start row: lat=-44 (colat 46, band top); goal row: lat=-88 (colat 130).
    const startRow = lats.indexOf(-44)
    const endRow = lats.indexOf(-88)
    const seen = new Array(H * W).fill(false)
    const queue = []
    for (let j = 0; j < W; j++) {
      const k = startRow * W + j
      if (ok[`${lats[startRow]},${lonVals[j]}`]) {
        seen[k] = true
        queue.push(k)
      }
    }
    let qi = 0
    while (qi < queue.length) {
      const k = queue[qi++]
      const i = Math.floor(k / W)
      const j = k % W
      if (i === endRow) reach.add(lonVals[j])
      for (let di = -1; di <= 1; di++) {
        for (let dj = -1; dj <= 1; dj++) {
          if (di === 0 && dj === 0) continue
          const ni = i + di
          const nj = (j + dj + W) % W
          if (ni < 0 || ni >= H) continue
          const nk = ni * W + nj
          if (seen[nk]) continue
          if (ok[`${lats[ni]},${lonVals[nj]}`]) {
            seen[nk] = true
            queue.push(nk)
          }
        }
      }
    }
    dpReach[g] = {
      startingAzCanReachFarPole: [...reach].sort((a, b) => a - b),
      count: reach.size,
      note: 'BFS on 2-deg survey cells, slope <= gate per cell; start row lat=-44 (colat 46), goal row lat=-88 (colat 130); optimistic by ~1-2 deg vs 0.25 m truth',
    }
  }

  const topRows = []
  if (existsSync(TOP20OUT)) {
    for (const l of readFileSync(TOP20OUT, 'utf8').split('\n')) if (l.trim()) topRows.push(JSON.parse(l))
  }
  topRows.sort((a, b) => top20.findIndex(({ r }) => `${r.A2}:${r.A5}:${r.A6}:${r.phi3}:${r.B3}` === a.key) - top20.findIndex(({ r }) => `${r.A2}:${r.A5}:${r.A6}:${r.phi3}:${r.B3}` === b.key))

  const diag = {
    world: 'test/out/world-seed1337.json',
    scanFile: SCAN,
    gates: GATES,
    prefilterSummary: summary,
    top20,
    surveySemantics,
    reachability,
    dpReach,
  }
  writeFileSync(OUT, JSON.stringify(diag, null, 1) + '\n')
  console.error(`finalize: wrote ${OUT} (top20 rows: ${topRows.length})`)
} else {
  console.error('usage: diagnose.mjs run|finalize')
  process.exit(1)
}