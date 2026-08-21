/**
 * C5 ledge scanner v2 — finds the sharpest WALKABLE step-up the terrain
 * offers, two ways:
 *
 *  (a) cell scan: max radius difference between adjacent field cells,
 *      restricted to walkable cells (finite-difference slope ≤ 48° at
 *      both) — an upper bound on what a walking body can step up per
 *      bilinear-spread cell, and where.
 *
 *  (b) sim scan: walk the REAL client sim (same step() as the dumps) along
 *      144 great circles out of spawn to 140 m and report single-tick
 *      radius jumps (Δr) that spike above the local walk baseline — the
 *      step-up branch at work (GDD max_step = 0.3 m; a "max_step ledge"
 *      is the sharpest instance the body can actually reach on foot).
 *      Proper stall detection: net progress = max angular distance from
 *      spawn so far; stalled if it stagnates (oscillation at the 50°
 *      contour adds path length without net progress).
 *
 *   npx tsx test/t5/ledge-scan.ts [world.json] [out.json]
 */
import { readFileSync, writeFileSync } from 'node:fs'
import {
  TICK_DT,
  decodeTerrain,
  sampleRadius,
  slopeAngle,
  sanitizeLook,
  step,
  vec,
  type Input,
  type State,
  type Terrain,
  type Vec3,
} from '../../client/src/sim/index.js'

const DEG = (r: number) => (r * 180) / Math.PI

const worldPath = process.argv[2] ?? 'test/out/world-seed1337.json'
const outPath = process.argv[3] ?? 'test/out/t5-ledges.json'
const world = JSON.parse(readFileSync(worldPath, 'utf8'))
const terrain: Terrain = decodeTerrain(world.face_grid, world.radius_min, world.radius_max, new Uint16Array(world.radii))

// ---------------------------------------------------------------- (a) cell scan
// Cell direction for (face, row, col): per-face axis assignment
//   +X/−X: u=y, v=z | +Y/−Y: u=x, v=z | +Z/−Z: u=x, v=y
function cellDir(face: number, row: number, col: number, g: number): Vec3 {
  const u = -1 + (2 * col) / (g - 1)
  const v = -1 + (2 * row) / (g - 1)
  switch (face) {
    case 0: return vec.norm({ x: 1, y: u, z: v })
    case 1: return vec.norm({ x: -1, y: u, z: v })
    case 2: return vec.norm({ x: u, y: 1, z: v })
    case 3: return vec.norm({ x: u, y: -1, z: v })
    case 4: return vec.norm({ x: u, y: v, z: 1 })
    default: return vec.norm({ x: u, y: v, z: -1 })
  }
}
const g = terrain.faceGrid
const slopeAt = (d: Vec3) => DEG(slopeAngle(terrain, d))
const cellSlope: number[][][] = []
const cellDirF: Vec3[][][] = []
const cellR: number[][][] = []
for (let f = 0; f < 6; f++) {
  cellSlope[f] = []; cellDirF[f] = []; cellR[f] = []
  for (let r = 0; r < g; r++) {
    cellSlope[f][r] = []; cellDirF[f][r] = []; cellR[f][r] = []
    for (let c = 0; c < g; c++) {
      const d = cellDir(f, r, c, g)
      cellDirF[f][r][c] = d
      cellR[f][r][c] = sampleRadius(terrain, d)
      cellSlope[f][r][c] = slopeAt(d)
    }
  }
}
interface CellStep {
  face: number
  row: number
  col: number
  dr: number
  dir: [number, number, number]
  slopeA: number
  slopeB: number
  az: number
  s: number
}
const cellSteps: CellStep[] = []
const azOf = (d: Vec3) => {
  const t = vec.norm(vec.sub(d, vec.scale({ x: 0, y: 1, z: 0 }, d.y)))
  const az = ((Math.atan2(-t.z, t.x) * 180) / Math.PI + 360) % 360
  const s = (Math.acos(Math.max(-1, Math.min(1, d.y))) * 150)
  return { az, s }
}
for (let f = 0; f < 6; f++) {
  for (let r = 0; r < g; r++) {
    for (let c = 0; c < g; c++) {
      const nb: [number, number][] = [[f, r, c + 1], [f, r + 1, c]]
      for (const [f2, r2, c2] of nb) {
        if (f2 === f && (c2 >= g || r2 >= g)) continue
        const a = cellSlope[f][r][c]
        const b = cellSlope[f2][r2][c2]
        if (a > 48 || b > 48) continue
        const dr = cellR[f2][r2][c2] - cellR[f][r][c]
        if (Math.abs(dr) >= 0.25) {
          const d = cellDirF[f][r][c]
          const { az, s } = azOf(d)
          cellSteps.push({
            face: f,
            row: r,
            col: c,
            dr: +Math.abs(dr).toFixed(4),
            dir: [d.x, d.y, d.z].map((x) => +x.toFixed(6)),
            slopeA: +a.toFixed(2),
            slopeB: +b.toFixed(2),
            az: +az.toFixed(2),
            s: +s.toFixed(2),
          })
        }
      }
    }
  }
}
cellSteps.sort((x, y) => y.dr - x.dr)
console.error(`cell scan: ${cellSteps.length} walkable adjacent-cell steps ≥ 0.25 m; top:`, cellSteps.slice(0, 8).map((c2) => `face${c2.face}(${c2.row},${c2.col}) dr=${c2.dr} az=${c2.az} s=${c2.s} slopes ${c2.slopeA}/${c2.slopeB}`).join(' | '))

// ---------------------------------------------------------------- (b) sim scan
const up0: Vec3 = { x: 0, y: 1, z: 0 }
const state0: State = {
  pos: vec.scale(up0, sampleRadius(terrain, up0)),
  vel: vec.zero(),
  facing: { x: 1, y: 0, z: 0 },
  grounded: true,
}

interface Event {
  az: number
  tick: number
  s: number
  netS: number
  dir: [number, number, number]
  dr: number
  slopeHere: number
  baseline: number
}
interface Stall {
  az: number
  netS: number
  slope: number
}
interface AzResult {
  az: number
  netS: number
  stalled: boolean
  maxSlope: number
  topDr: { s: number; dr: number; slope: number }[]
}

const events: Event[] = []
const stalls: Stall[] = []
const azResults: AzResult[] = []
const S_MAX = 140

for (let az = 0; az < 360; az += 2.5) {
  const rad = (az * Math.PI) / 180
  const t0: Vec3 = { x: Math.cos(rad), y: 0, z: -Math.sin(rad) }
  let state: State = { pos: vec.copy(state0.pos), vel: vec.copy(state0.vel), facing: vec.copy(state0.facing), grounded: state0.grounded }
  let lastLook: Vec3 = vec.copy(state.facing)
  let t: Vec3 = vec.copy(t0)
  let s = 0
  let netS = 0
  let tick = 0
  let stalled = false
  let maxSlope = 0
  const drHist: number[] = []
  const topDr: { s: number; dr: number; slope: number }[] = []
  let lastNet = 0
  let lastNetTick = 0
  while (tick < 1200) {
    const upNow = vec.norm(state.pos)
    const dAim = Math.min(2, Math.max(0.1, S_MAX - s)) / sampleRadius(terrain, upNow)
    const dT = vec.norm(vec.add(vec.scale(upNow, Math.cos(dAim)), vec.scale(t, Math.sin(dAim))))
    const ahead = vec.norm(vec.sub(vec.scale(dT, sampleRadius(terrain, dT)), state.pos))
    const input: Input = { moveX: 0, moveY: 1, lookDir: ahead, actionMask: 0 }
    const prevPos = vec.copy(state.pos)
    state = step(state, input, terrain, TICK_DT, lastLook)
    lastLook = sanitizeLook(input, lastLook)
    tick++
    const moved = vec.len(vec.sub(state.pos, prevPos))
    s += moved
    const upNew = vec.norm(state.pos)
    netS = Math.max(netS, (Math.acos(Math.max(-1, Math.min(1, upNew.y))) * vec.len(state.pos)))
    t = vec.norm(vec.sub(t, vec.scale(upNew, vec.dot(t, upNew))))
    const sl = DEG(slopeAngle(terrain, upNew))
    maxSlope = Math.max(maxSlope, sl)
    if (netS > lastNet + 0.5) {
      lastNet = netS
      lastNetTick = tick
    }
    if (s > S_MAX + 4) break
    if (tick - lastNetTick > 300 && netS < S_MAX) {
      stalled = true
      break
    }
    if (!state.grounded) continue
    const dr = vec.len(state.pos) - vec.len(prevPos)
    if (dr >= 0.04) {
      const win = drHist.slice(-20)
      const baseline = win.length ? [...win].sort((a, b) => a - b)[Math.floor(win.length / 2)] : 0
      if (dr >= 0.07 && dr >= 2.2 * baseline + 0.015) {
        events.push({
          az,
          tick,
          s: +s.toFixed(2),
          netS: +netS.toFixed(2),
          dir: [upNew.x, upNew.y, upNew.z].map((x) => +x.toFixed(6)),
          dr: +dr.toFixed(4),
          slopeHere: +sl.toFixed(2),
          baseline: +baseline.toFixed(4),
        })
      }
      topDr.push({ s: +s.toFixed(1), dr: +dr.toFixed(4), slope: +sl.toFixed(1) })
      if (topDr.length > 12) topDr.shift()
    }
    drHist.push(dr)
  }
  azResults.push({ az, netS: +netS.toFixed(1), stalled, maxSlope: +maxSlope.toFixed(2), topDr: topDr.slice(-6) })
  if (stalled) {
    const up = vec.norm(state.pos)
    stalls.push({ az, netS: +netS.toFixed(1), slope: +DEG(slopeAngle(terrain, up)).toFixed(2) })
  }
}

events.sort((a, b) => b.dr - a.dr)
const out = { world: worldPath, cellSteps: cellSteps.slice(0, 40), events: events.slice(0, 80), stalls, azResults }
writeFileSync(outPath, JSON.stringify(out, null, 1))
console.error(`sim scan: ${azResults.length} az, stalled: ${stalls.length}, spike events: ${events.length}`)
console.error('top 20 spike events:')
for (const e of events.slice(0, 20)) console.error(`  az=${e.az} s=${e.s} netS=${e.netS} dR=${e.dr} slope=${e.slopeHere}° base=${e.baseline} dir=[${e.dir.join(',')}]`)
console.error('stalls (az, netS, slope):')
for (const st of stalls) console.error(`  az=${st.az} netS=${st.netS} slope=${st.slope}°`)