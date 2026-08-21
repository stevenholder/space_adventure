/**
 * Differential walk probe: walks the real client sim from spawn at several
 * azimuths and logs progress every 40 ticks. Diagnoses why the full sim
 * scan reports identical stalls at every azimuth.
 *
 *   npx tsx test/t5/walk-probe.ts
 */
import { readFileSync } from 'node:fs'
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
const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const terrain: Terrain = decodeTerrain(
  world.face_grid,
  world.radius_min,
  world.radius_max,
  new Uint16Array(world.radii),
)

const up0: Vec3 = { x: 0, y: 1, z: 0 }
const state0: State = {
  pos: vec.scale(up0, sampleRadius(terrain, up0)),
  vel: vec.zero(),
  facing: { x: 1, y: 0, z: 0 },
  grounded: true,
}

for (const az of [0, 7.5, 90, 200]) {
  const rad = (az * Math.PI) / 180
  const t0: Vec3 = { x: Math.cos(rad), y: 0, z: -Math.sin(rad) }
  let state: State = {
    pos: vec.copy(state0.pos),
    vel: vec.copy(state0.vel),
    facing: vec.copy(state0.facing),
    grounded: state0.grounded,
  }
  let lastLook: Vec3 = vec.copy(state.facing)
  let t: Vec3 = vec.copy(t0)
  let s = 0
  let netS = 0
  let maxSlope = 0
  console.log(`\n=== az=${az} ===`)
  for (let tick = 1; tick <= 600; tick++) {
    const upNow = vec.norm(state.pos)
    const dAim = Math.min(2, Math.max(0.1, 140 - s)) / sampleRadius(terrain, upNow)
    const dT = vec.norm(vec.add(vec.scale(upNow, Math.cos(dAim)), vec.scale(t, Math.sin(dAim))))
    const ahead = vec.norm(vec.sub(vec.scale(dT, sampleRadius(terrain, dT)), state.pos))
    const input: Input = { moveX: 0, moveY: 1, lookDir: ahead, actionMask: 0 }
    const prevPos = vec.copy(state.pos)
    state = step(state, input, terrain, TICK_DT, lastLook)
    lastLook = sanitizeLook(input, lastLook)
    s += vec.len(vec.sub(state.pos, prevPos))
    const upNew = vec.norm(state.pos)
    netS = Math.max(netS, Math.acos(Math.max(-1, Math.min(1, upNew.y))) * vec.len(state.pos))
    t = vec.norm(vec.sub(t, vec.scale(upNew, vec.dot(t, upNew))))
    const sl = DEG(slopeAngle(terrain, upNew))
    maxSlope = Math.max(maxSlope, sl)
    if (tick % 40 === 0 || tick <= 3) {
      const h = vec.len(state.pos) - sampleRadius(terrain, upNew)
      console.log(
        `t=${tick} s=${s.toFixed(1)} net=${netS.toFixed(1)} sl=${sl.toFixed(1)} g=${state.grounded} h=${h.toFixed(3)} |p|=${vec.len(state.pos).toFixed(2)}`,
      )
    }
  }
  console.log(`end: net=${netS.toFixed(1)} maxSlope=${maxSlope.toFixed(1)}`)
}