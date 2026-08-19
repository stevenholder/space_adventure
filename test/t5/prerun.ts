/**
 * C5 pre-run: build the conformance route, run it through the CLIENT sim
 * (identical to tools/dump.ts: same step, same terrain, same look threading),
 * and emit (a) per-leg telemetry so we can verify all six terrains are
 * exercised, (b) the canonical JSONL input script (Go format) that both
 * dump entry points will run.
 *
 *   npx tsx test/t5/prerun.ts --legs test/t5/legs.json
 *
 * Legs: [{ "az": <deg, turn rel to previous heading>, "dist": <m>,
 *          "sprint"?: bool, "jumpTicks"?: int, "pauseAfter"?: ticks,
 *          "label"?: string }]
 * Leg 0's heading reference is +X at spawn (spawnLook). Each leg follows a
 * great circle; lookDir aims 2 m ahead along it, per tick.
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

const ACTION_SPRINT = 0x0001
const ACTION_JUMP = 0x0002
const DEG = (r: number) => (r * 180) / Math.PI

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const terrain: Terrain = decodeTerrain(
  world.face_grid,
  world.radius_min,
  world.radius_max,
  new Uint16Array(world.radii),
)

interface Leg {
  az: number
  dist: number
  sprint?: boolean
  jumpTicks?: number
  pauseAfter?: number
  label?: string
}

const legArg = process.argv.includes('--legs')
  ? process.argv[process.argv.indexOf('--legs') + 1]
  : 'test/t5/legs.json'
const legs: Leg[] = JSON.parse(readFileSync(legArg, 'utf8'))

const faceOf = (d: Vec3): number => {
  const ax = [Math.abs(d.x), Math.abs(d.y), Math.abs(d.z)]
  let a = 0
  for (let i = 1; i < 3; i++) if (ax[i] > ax[a]) a = i
  return a * 2 + ([d.x, d.y, d.z][a] >= 0 ? 0 : 1)
}
const fmt = (p: Vec3): string => `[${p.x.toFixed(4)},${p.y.toFixed(4)},${p.z.toFixed(4)}]`

// GDD spawn
const up0: Vec3 = { x: 0, y: 1, z: 0 }
let state: State = {
  pos: vec.scale(up0, sampleRadius(terrain, up0)),
  vel: vec.zero(),
  facing: { x: 1, y: 0, z: 0 },
  grounded: true,
}
let lastLook: Vec3 = vec.copy(state.facing)
const script: string[] = [
  JSON.stringify({
    pos: [state.pos.x, state.pos.y, state.pos.z],
    vel: [state.vel.x, state.vel.y, state.vel.z],
    grounded: state.grounded,
    facing: [state.facing.x, state.facing.y, state.facing.z],
  }),
]

function rotAbout(t: Vec3, up: Vec3, azDeg: number): Vec3 {
  const a = (azDeg * Math.PI) / 180
  const c = Math.cos(a)
  const s = Math.sin(a)
  return vec.norm(vec.add(vec.add(vec.scale(t, c), vec.scale(vec.cross(up, t), s)), vec.scale(up, vec.dot(up, t) * (1 - c))))
}

function emitInput(input: Input): void {
  script.push(
    JSON.stringify({
      input: {
        move_x: input.moveX,
        move_y: input.moveY,
        look: [input.lookDir.x, input.lookDir.y, input.lookDir.z],
        action_mask: input.actionMask,
      },
    }),
  )
}

interface Telemetry {
  label: string
  ticks: number
  minSlope: number
  maxSlope: number
  slideTicks: number
  airTicks: number
  stepUps: number
  faces: number[]
  startDir: string
  endDir: string
}

const telemetry: Telemetry[] = []
let heading: Vec3 = { x: 1, y: 0, z: 0 }

for (const leg of legs) {
  const upStart = vec.norm(state.pos)
  const t0 = rotAbout(heading, upStart, leg.az)
  const tel: Telemetry = {
    label: leg.label ?? `leg${telemetry.length}`,
    ticks: 0,
    minSlope: Infinity,
    maxSlope: -Infinity,
    slideTicks: 0,
    airTicks: 0,
    stepUps: 0,
    faces: [faceOf(state.pos)],
    startDir: fmt(state.pos),
    endDir: '',
  }
  let t = vec.copy(t0)
  let remaining = leg.dist
  let jumpsLeft = leg.jumpTicks ?? 0
  while (remaining > 1e-9) {
    const upNow = vec.norm(state.pos)
    const dAim = Math.min(2, remaining) / sampleRadius(terrain, upNow)
    const ahead = vec.norm(vec.add(vec.scale(upNow, Math.cos(dAim)), vec.scale(t, Math.sin(dAim))))
    const input: Input = {
      moveX: 0,
      moveY: 1,
      lookDir: ahead,
      actionMask: (jumpsLeft > 0 ? ACTION_JUMP : 0) | (leg.sprint ? ACTION_SPRINT : 0),
    }
    if (jumpsLeft > 0) jumpsLeft--
    const prevPos = vec.copy(state.pos)
    state = step(state, input, terrain, TICK_DT, lastLook)
    lastLook = sanitizeLook(input, lastLook)
    emitInput(input)
    tel.ticks++
    const moved = vec.len(vec.sub(state.pos, prevPos))
    remaining -= moved
    const upNew = vec.norm(state.pos)
    heading = vec.norm(vec.sub(t, vec.scale(upNew, vec.dot(t, upNew))))
    t = vec.copy(heading)
    const sl = DEG(slopeAngle(terrain, upNew))
    tel.minSlope = Math.min(tel.minSlope, sl)
    tel.maxSlope = Math.max(tel.maxSlope, sl)
    if (!state.grounded) {
      const h = vec.len(state.pos) - sampleRadius(terrain, upNew)
      if (h > 0.15) tel.airTicks++
      else tel.slideTicks++
    }
    const f = faceOf(state.pos)
    if (tel.faces[tel.faces.length - 1] !== f) tel.faces.push(f)
    const onSurf = Math.abs(vec.len(state.pos) - sampleRadius(terrain, upNew)) < 1e-6
    if (onSurf && vec.len(state.pos) - vec.len(prevPos) >= 0.15) tel.stepUps++
  }
  tel.endDir = fmt(state.pos)
  telemetry.push(tel)
  if (leg.pauseAfter) {
    for (let i = 0; i < leg.pauseAfter; i++) {
      const input: Input = { moveX: 0, moveY: 0, lookDir: vec.copy(heading), actionMask: 0 }
      state = step(state, input, terrain, TICK_DT, lastLook)
      lastLook = sanitizeLook(input, lastLook)
      emitInput(input)
    }
  }
}

console.error(`total ticks: ${script.length - 1}`)
console.error(JSON.stringify(telemetry, null, 1))
console.error(
  `final: pos=${fmt(state.pos)} vel=[${state.vel.x.toFixed(3)},${state.vel.y.toFixed(3)},${state.vel.z.toFixed(3)}] grounded=${state.grounded} r=${sampleRadius(terrain, vec.norm(state.pos)).toFixed(3)} |pos|=${vec.len(state.pos).toFixed(3)}`,
)

writeFileSync('test/t5/script-go.jsonl', script.join('\n') + '\n')
const tsLines = script.map((l, i) => {
  if (i === 0) return l
  const o = JSON.parse(l)
  return JSON.stringify({
    move_x: o.input.move_x,
    move_y: o.input.move_y,
    look_dir: o.input.look,
    action_mask: o.input.action_mask,
  })
})
writeFileSync('test/t5/script-ts.jsonl', tsLines.join('\n') + '\n')
console.error('wrote test/t5/script-go.jsonl + test/t5/script-ts.jsonl')
