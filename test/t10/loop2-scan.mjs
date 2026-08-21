#!/usr/bin/env node
/**
 * C10 STEP 1b/2 — corrected 6-face loop scan (post-diagnosis grid).
 *
 * The predecessor grid was structurally broken (test/out/t10-diagnosis.json):
 * A1 fixed at 40 — the spawn meridian az 40 is 54.3 deg on the corrected
 * field (t10-corridors.jsonl); the far-side landing window forced by the
 * short-j3 face logic (A5 = A6 + {5.5, 6.5}) excluded the one face-4
 * entry that keeps 6 faces. Corrected topology (face audit):
 *
 *   leg1: meridian A1 (6..30, face 0)        spawn -> equator   [2 -> 0]
 *   j1  : equatorial arc A1 -> A2 (crosses 45)                    [0 -> 5]
 *   leg2: great circle Q2(A2, 56..108) -> P3 (far cap, face 3)    [5 -> 3]
 *   leg3: great circle P3 -> Q4(A5, 225.5..231.5, face 4)         [3 -> 4]
 *   j3  : equatorial arc A5 -> A6 (crosses 225)                   [4 -> 1]
 *   leg4: meridian A6 (219..225, face 1)       equator -> spawn   [1 -> 2]
 *
 *   A6 = A5 - {5.5, 6.5}; P3 = farPt(phi3, B3), colat from +Y = 180-phi3.
 *   B3 in [A5 - 90.3, A2 + 90.3] keeps both far legs <= 90.5 deg.
 *   All windows from the 0.5 m corridor map (t10-corridors.jsonl):
 *   spawn-walkable 6-31 / 217-224; far-walkable 55-69 / 80-81 / 108.
 *   Face 4 enters at the leg3 tail + j3 (az 225-231.5, colat < 135).
 *
 * Two-stage (decoupled) prefilter at 2 m, then full 0.25 m buildLoop at
 * BOTH gates (49.5 and 50.0), gate-tagged rows appended to
 * test/out/t10-loop2-scan.jsonl:
 *   phase 'leg2'/'leg3'  : per-arc far-leg stats (2 m)
 *   phase 'merid'        : leg1/leg4 meridian stats (0.25 m exact)
 *   phase 'joint'        : j1/j3 equatorial arc stats (0.25 m exact)
 *   phase 'combine'      : prefilter passes (length window 880..1005,
 *                          maxSlope <= 50.5 per part, wall == 0, haz >= 5)
 *   phase 'full'         : 0.25 m buildLoop, ok495 + ok500 flags
 *   phase 'select'       : best candidate -> design + legs files
 *
 *   node test/t10/loop2-scan.mjs run      # leg2/leg3/merid/joint tables
 *   node test/t10/loop2-scan.mjs combine  # prefilter combinations
 *   node test/t10/loop2-scan.mjs full     # 0.25 m eval of combine passes
 *   node test/t10/loop2-scan.mjs select   # write test/out/t10-loop-design.json
 *
 * arc() copied verbatim from test/t10/loop-design.mjs (same flat-length
 * 720-step trapezoid + flat-step walk semantics).
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
const OUT = 'test/out/t10-loop2-scan.jsonl'
const WALL_DR = 0.33
const HAZ_MIN_DEG = 5
const LEN_LO = 894.9
const LEN_HI = 989.1
const LEN_PREF_LO = 860
const LEN_PREF_HI = 1080 // widened for diagnosis: the 6-face polar floor is ~1010+ m
const QUARTER_MAX = 90.5
const PRE_SLOPE = 50.5
const GATES = [49.5, 50.0]

// hazard points (beacon az 3.5-6.5 s 43-59; staircase az 159.5-164.5 s 51.5-61.5)
const R0 = sampleRadius(F, SPAWN)
function meridianDir(azDeg, sM) {
  const th = sM / R0
  const a = RAD(azDeg)
  return norm3([Math.sin(th) * Math.cos(a), Math.cos(th), -Math.sin(th) * Math.sin(a)])
}
const HAZARDS = []
for (let az = 3.5; az <= 6.5; az += 0.5) for (let s = 43; s <= 59; s += 2) HAZARDS.push({ dir: meridianDir(az, s) })
for (let az = 159.5; az <= 164.5; az += 0.5) for (let s = 51.5; s <= 61.5; s += 1.5) HAZARDS.push({ dir: meridianDir(az, s) })

function hazDeg(dirs) {
  let best = Infinity
  for (const d of dirs) {
    for (const h of HAZARDS) {
      const dot = Math.max(-1, Math.min(1, d[0] * h.dir[0] + d[1] * h.dir[1] + d[2] * h.dir[2]))
      const ang = Math.acos(dot) * DEG
      if (ang < best) best = ang
    }
  }
  return best
}

// ---------------------------------------------------------------- arcs
// (copied from loop-design.mjs)
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

function arcStats(a) {
  let mx = -Infinity
  let mn = Infinity
  let wall = 0
  const dirs = a.samples.map((p) => p.dir)
  for (const p of a.samples) {
    mx = Math.max(mx, p.slope)
    mn = Math.min(mn, p.slope)
    if (p.dr > WALL_DR) wall += 1
  }
  return {
    thetaDeg: a.thetaDeg,
    flat: a.flat,
    tru: a.tru,
    maxSlope: +mx.toFixed(2),
    minSlope: +mn.toFixed(2),
    wall,
    haz: +hazDeg(dirs).toFixed(2),
    tEnd: a.tEnd.map((x) => +x.toFixed(5)),
    tStart: a.tStart.map((x) => +x.toFixed(5)),
  }
}

const eqPt = (az) => [Math.cos(RAD(az)), 0, -Math.sin(RAD(az))]
const farPt = (phiDeg, azDeg) => {
  const p = RAD(phiDeg)
  const a = RAD(azDeg)
  return [Math.sin(p) * Math.cos(a), -Math.cos(p), -Math.sin(p) * Math.sin(a)]
}

// ---------------------------------------------------------------- grid
const A1s = [6, 10, 14, 18, 22, 26, 30]
const A2s = [55, 56, 57, 60, 64, 68, 80, 81, 108]
const A5s = []
for (let a5 = 225.5; a5 <= 231.5 + 1e-9; a5 = +(a5 + 0.5).toFixed(1)) A5s.push(a5)
const A6OFFS = [5.5, 6.5]
const PHI3 = [28, 30, 32, 34, 36, 38, 40, 42, 43, 44]
const B3_LO = 135 // global prefilter window (min A5 - 90.3 = 135.2)
const B3_HI = 198.3 // max A2 + 90.3

const done = new Set()
if (existsSync(OUT)) {
  for (const l of readFileSync(OUT, 'utf8').split('\n')) {
    if (!l.trim()) continue
    const r = JSON.parse(l)
    done.add(`${r.phase}:${r.k || ''}`)
  }
}

function arcRows(phase, keys, arcOfKey, chunk = 400) {
  const lines = []
  let n = 0
  for (const k of keys) {
    if (done.has(`${phase}:${k}`)) continue
    const st = arcStats(arcOfKey(k))
    lines.push(JSON.stringify({ phase, k: k, ...st }))
    n++
    if (lines.length % chunk === 0) {
      appendFileSync(OUT, lines.splice(0).join('\n') + '\n')
    }
  }
  if (lines.length) appendFileSync(OUT, lines.join('\n') + '\n')
  return n
}

const arg = process.argv[2]
if (arg === 'run') {
  const t0 = process.hrtime.bigint()
  // leg2: Q2(A2) -> P3(phi3, B3)
  {
    const keys = []
    const fns = new Map()
    for (const A2 of A2s) for (const phi3 of PHI3) for (let B3 = B3_LO; B3 <= B3_HI + 1e-9; B3 = +(B3 + 0.5).toFixed(1)) {
      const k = `2:${A2}:${phi3}:${B3}`
      keys.push(k)
      fns.set(k, () => arc(eqPt(A2), farPt(phi3, B3), 2))
    }
    const n = arcRows('leg2', keys, (k) => fns.get(k)())
    console.error(`leg2: ${n} arcs`)
  }
  // leg3: P3(phi3, B3) -> Q4(A5); B3 must also satisfy the leg2 quarter
  // window (B3 <= 198.3) and leg3's own: B3 >= A5 - 90.3.
  {
    const keys = []
    const fns = new Map()
    for (const A5 of A5s) {
      const lo = Math.ceil((A5 - 90.3) * 2) / 2 // 0.5 deg grid, aligned
      for (const phi3 of PHI3) for (let B3 = lo; B3 <= B3_HI + 1e-9; B3 = +(B3 + 0.5).toFixed(1)) {
        const k = `3:${A5}:${phi3}:${B3}`
        keys.push(k)
        fns.set(k, () => arc(farPt(phi3, B3), eqPt(A5), 2))
      }
    }
    const n = arcRows('leg3', keys, (k) => fns.get(k)())
    console.error(`leg3: ${n} arcs`)
  }
  // meridians: leg1 at A1, leg4 at A6 = A5 - off (0.25 m exact)
  {
    const azs = new Set([...A1s])
    for (const A5 of A5s) for (const off of A6OFFS) azs.add(+(A5 - off).toFixed(1))
    const keys = []
    const fns = new Map()
    for (const az of azs) {
      const k = `m:${az}`
      keys.push(k)
      fns.set(k, () => arc(SPAWN, eqPt(az), 0.25))
    }
    const n = arcRows('merid', keys, (k) => fns.get(k)())
    console.error(`merid: ${n} arcs`)
  }
  // joints: j1 (A1->A2), j3 (A5->A6) at 0.25 m
  {
    const keys = []
    const fns = new Map()
    for (const A1 of A1s) for (const A2 of A2s) {
      const k = `j1:${A1}:${A2}`
      keys.push(k)
      fns.set(k, () => arc(eqPt(A1), eqPt(A2), 0.25))
    }
    for (const A5 of A5s) for (const off of A6OFFS) {
      const A6 = +(A5 - off).toFixed(1)
      const k = `j3:${A5}:${A6}`
      keys.push(k)
      fns.set(k, () => arc(eqPt(A5), eqPt(A6), 0.25))
    }
    const n = arcRows('joint', keys, (k) => fns.get(k)())
    console.error(`joint: ${n} arcs`)
  }
  console.error(`run done in ${((Number(process.hrtime.bigint() - t0) / 1e6) / 1000).toFixed(1)} s`)
} else if (arg === 'combine') {
  const rows = []
  for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const leg2 = {}
  const leg3 = {}
  const merid = {}
  const joint = {}
  for (const r of rows) {
    if (r.phase === 'leg2') leg2[r.k] = r
    else if (r.phase === 'leg3') leg3[r.k] = r
    else if (r.phase === 'merid') merid[r.k] = r
    else if (r.phase === 'joint') joint[r.k] = r
  }
  // NOTE: no wall (dr) gate at 2 m prefilter — a 50-deg slope gives dr~2.4 m
  // per 2 m step, so the 0.25 m-resolution wall heuristic fails on every
  // real slope here. GDD walkability is max_slope <= 50 deg (max_step 0.3 m
  // is a per-tick dynamic number, not a spatial gate); wall is reported at
  // full 0.25 m resolution for the record only.
  const ok = (r, g = PRE_SLOPE) => r && r.maxSlope <= g && r.thetaDeg <= QUARTER_MAX && r.haz >= HAZ_MIN_DEG
  const combineDone = new Set(rows.filter((r) => r.phase === 'combine').map((r) => r.k))
  const lines = []
  let n = 0
  for (const A1 of A1s) {
    const L1 = merid[`m:${A1}`]
    if (!ok(L1)) {
      console.error(`combine: WARNING leg1 az=${A1} not ok (maxSlope=${L1 && L1.maxSlope})`)
      continue
    }
    for (const A2 of A2s) {
      const J1 = joint[`j1:${A1}:${A2}`]
      if (!ok(J1)) continue
      const l1j1 = L1.flat + J1.flat
      for (const A5 of A5s) for (const off of A6OFFS) {
        const A6 = +(A5 - off).toFixed(1)
        const J3 = joint[`j3:${A5}:${A6}`]
        const L4 = merid[`m:${A6}`]
        if (!ok(J3) || !ok(L4)) continue
        const base = l1j1 + J3.flat + L4.flat
        if (base > LEN_PREF_HI) continue
        for (const phi3 of PHI3) {
          const b3lo = Math.ceil(Math.max(B3_LO, A5 - (QUARTER_MAX - 0.2)) * 2) / 2
          const b3hi = Math.min(B3_HI, A2 + (QUARTER_MAX - 0.2))
          for (let B3 = b3lo; B3 <= b3hi + 1e-9; B3 = +(B3 + 0.5).toFixed(1)) {
            const k2 = `2:${A2}:${phi3}:${B3}`
            const k3 = `3:${A5}:${phi3}:${B3}`
            const L2 = leg2[k2]
            const L3 = leg3[k3]
            if (!ok(L2) || !ok(L3)) continue
            const total = base + L2.flat + L3.flat
            if (total < LEN_PREF_LO || total > LEN_PREF_HI) continue
            const k = `c:${A1}:${A2}:${A5}:${A6}:${phi3}:${B3}`
            if (combineDone.has(k)) continue
            const minHaz = Math.min(L1.haz, J1.haz, L2.haz, L3.haz, J3.haz, L4.haz)
            const maxSlope = Math.max(L1.maxSlope, J1.maxSlope, L2.maxSlope, L3.maxSlope, J3.maxSlope, L4.maxSlope)
            lines.push(JSON.stringify({ phase: 'combine', k, A1, A2, A5, A6, phi3, B3, total: +total.toFixed(2), maxSlope: +maxSlope.toFixed(2), haz: +minHaz.toFixed(2) }))
            n++
          }
        }
      }
    }
  }
  appendFileSync(OUT, lines.join('\n') + (lines.length ? '\n' : ''))
  console.error(`combine: ${n} prefilter passes (of ${A1s.length * A2s.length * A5s.length * A6OFFS.length * PHI3.length} core grid)`)
} else if (arg === 'full') {
  const rows = []
  for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const cands = rows.filter((r) => r.phase === 'combine')
const doneFull = new Set(rows.filter((r) => r.phase === 'full').map((r) => r.k))
const wantKeys = process.env.FULL_KEYS ? new Set(process.env.FULL_KEYS.split(',')) : null
const fresh = cands.filter((r) => !doneFull.has(r.k) && (!wantKeys || wantKeys.has(r.k)))
  console.error(`full: ${cands.length} combine passes, ${fresh.length} fresh`)
  // buildLoop at 0.25 m (same assembly as loop-design.mjs)
  function buildLoop(A1, A2, A5, A6, phi3, B3) {
    const P3 = farPt(phi3, B3)
    const segs = [
      { label: 'leg1', a: arc(SPAWN, eqPt(A1), 0.25) },
      { label: 'j1', a: arc(eqPt(A1), eqPt(A2), 0.25) },
      { label: 'leg2', a: arc(eqPt(A2), P3, 0.25) },
      { label: 'leg3', a: arc(P3, eqPt(A5), 0.25) },
      { label: 'j3', a: arc(eqPt(A5), eqPt(A6), 0.25) },
      { label: 'leg4', a: arc(eqPt(A6), SPAWN, 0.25) },
    ]
    let maxSlope = -Infinity
    let minSlope = Infinity
    let wall = 0
    let totalFlat = 0
    let totalTru = 0
    let haz = Infinity
    const faceSeq = []
    const crossings = []
    let cum = 0
    for (const sg of segs) {
      totalFlat += sg.a.flat
      totalTru += sg.a.tru
      // hazard: subsample to keep it cheap (every 8th node)
      for (let i = 0; i < sg.a.samples.length; i += 8) haz = Math.min(haz, (function (d) { let b = Infinity; for (const h of HAZARDS) { const dot = Math.max(-1, Math.min(1, d[0] * h.dir[0] + d[1] * h.dir[1] + d[2] * h.dir[2])); b = Math.min(b, Math.acos(dot) * DEG); } return b })(sg.a.samples[i].dir))
      for (const p of sg.a.samples) {
        maxSlope = Math.max(maxSlope, p.slope)
        minSlope = Math.min(minSlope, p.slope)
        if (p.dr > WALL_DR) wall += 1
        if (faceSeq.length === 0) faceSeq.push(p.face)
        else if (faceSeq[faceSeq.length - 1] !== p.face) {
          faceSeq.push(p.face)
          crossings.push({ s: +(cum + p.s).toFixed(2), from: faceSeq[faceSeq.length - 2], to: p.face, seg: sg.label })
        }
      }
      cum += sg.a.flat
    }
    const distinct = [...new Set(faceSeq)].sort((x, y) => x - y)
    const t2e = segs[2].a.tEnd
    const t3s = segs[3].a.tStart
    const t3up = norm3(P3)
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
      hazDeg: +haz.toFixed(2),
      distinct,
      crossings,
      relTurnDeg,
      P3: P3.map((x) => +x.toFixed(6)),
      maxQuarter: Math.max(...segs.map((s) => s.a.thetaDeg)),
    }
  }
  const t0 = process.hrtime.bigint()
  const lines = []
  for (let i = 0; i < fresh.length; i++) {
    const r = fresh[i]
    const L = buildLoop(r.A1, r.A2, r.A5, r.A6, r.phi3, r.B3)
    // ROADMAP C10 (reworded): no length band. Gates: 6 faces, slope <= gate,
    // no wall step at 0.25 m, hazard clearance, quarter-leg bound.
    const okG = (g) => L.distinct.length === 6 && L.maxSlope <= g && L.wall === 0 && L.hazDeg >= HAZ_MIN_DEG && L.maxQuarter <= QUARTER_MAX
    lines.push(JSON.stringify({
      phase: 'full', k: r.k, A1: r.A1, A2: r.A2, A5: r.A5, A6: r.A6, phi3: r.phi3, B3: r.B3,
      ok495: okG(GATES[0]), ok500: okG(GATES[1]),
      total: L.totalFlat, totalTru: L.totalTru, maxSlope: L.maxSlope, minSlope: L.minSlope,
      wall: L.wall, haz: L.hazDeg, maxQuarter: L.maxQuarter, distinct: L.distinct,
      relTurnP3: L.relTurnDeg, P3: L.P3, crossings: L.crossings,
      legs: L.segs.map((s) => {
        let mx = -Infinity
        for (const p of s.a.samples) mx = Math.max(mx, p.slope)
        return { label: s.label, flat: s.a.flat, tru: s.a.tru, thetaDeg: s.a.thetaDeg, maxSlope: +mx.toFixed(2) }
      }),
    }))
    if ((i + 1) % 50 === 0) {
      appendFileSync(OUT, lines.splice(0).join('\n') + '\n')
      const el = Number(process.hrtime.bigint() - t0) / 1e6
      console.error(`  full ${i + 1}/${fresh.length} in ${(el / 1000).toFixed(1)} s`)
    }
  }
  if (lines.length) appendFileSync(OUT, lines.join('\n') + '\n')
  console.error(`full done: ${fresh.length} evals`)
} else if (arg === 'select') {
  const rows = []
  for (const l of readFileSync(OUT, 'utf8').split('\n')) if (l.trim()) rows.push(JSON.parse(l))
  const okRows = rows.filter((r) => r.phase === 'full' && (r.ok495 || r.ok500))
  if (okRows.length === 0) {
    console.error('select: no candidate passed all gates')
    process.exit(1)
  }
  okRows.sort((a, b) => Math.abs(a.total - 942) - Math.abs(b.total - 942) || a.maxSlope - b.maxSlope)
  const sel = okRows[0]
  console.error(JSON.stringify(sel, null, 1))
  console.error(`select: ${okRows.length} feasible; best: ${sel.k} (total=${sel.total} m, maxSlope=${sel.maxSlope}, haz=${sel.haz} deg, turnP3=${sel.relTurnP3} deg, ok495=${sel.ok495} ok500=${sel.ok500})`)
} else {
  console.error('usage: loop2-scan.mjs run|combine|full|select')
  process.exit(1)
}