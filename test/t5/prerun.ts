/**
 * C5 pre-run v2: build the conformance route, run it through the CLIENT sim
 * (identical to tools/dump.ts: same step, same terrain, same look threading),
 * and emit (a) per-leg telemetry with assert checks so every terrain class
 * is provably exercised, (b) the JSONL input scripts for BOTH dump entry
 * points from the SAME input stream (parity by construction):
 *
 *   script-go.jsonl — {"state": {...}} header + {"input": {...}} lines
 *                     (server/cmd/server/dump.go format)
 *   script-ts.jsonl — bare state header + flat input lines
 *                     (client/tools/dump.ts format)
 *
 *   npx tsx test/t5/prerun.ts [--legs test/t5/legs.json]
 *
 * Legs v2 (docs/ROADMAP C5 coverage: level, walkable slope, slide, aim at
 * beacon base, max_step ledge, 1-tick jump, face 2→0 seam):
 *   { "label", "kind": "circle" | "toPoint" | "hold" | "pause" }
 *   circle : follow the spawn-centered great circle of azimuth `circleAz`
 *            (heading re-aligned to the circle tangent at the leg start);
 *            look aims at the SURFACE point 2 m ahead on the circle
 *            (corrected aim — v1 aimed the radial point, which is why the
 *            body held facing and walked a straight chord).
 *            Ends at `dist` (arc metres) or on stall (40 consecutive ticks
 *            with < 0.02 m movement) or at `maxTicks`.
 *   toPoint: steer at the fixed world direction `target` (the body follows
 *            the great circle from its position to the target point —
 *            looking along a fixed direction makes facing track the
 *            great-circle tangent, incl. the facing-hold when the look
 *            goes near-vertical). Ends when < 0.9 m from the target.
 *   hold   : fixed `ticks` pushing forward; look = `look` (fixed dir).
 *   pause  : fixed `ticks` of no input.
 *   `jumpDelayTicks` + `jumpTicks`: the JUMP bit is set on the leg ticks
 *            [delay, delay+jumpTicks) — a 1-tick jump = {delay: N, jumpTicks: 1}.
 *   `assert`: ["maxSlope<10", "slideTicks>=5", "airTicks>0", "stepUps>=1",
 *             "stalled", "faces:2->0"] — evaluated after the run.
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
const RAD = (d: number) => (d * Math.PI) / 180
const STALL_TICKS = 40 // consecutive < 0.02 m ticks that end a walk leg
const STALL_MOVE = 0.02
const MAX_TICKS_DEFAULT = 2500

const world = JSON.parse(readFileSync('test/out/world-seed1337.json', 'utf8'))
const terrain: Terrain = decodeTerrain(
  world.face_grid,
  world.radius_min,
  world.radius_max,
  new Uint16Array(world.radii),
)

// Corrected face picker (signed dominant axis, ties → earlier face) —
// matches server FaceOf / client pickFace and test/lib/field.mjs.
const faceOf = (d: Vec3): number => {
  const score = [d.x, -d.x, d.y, -d.y, d.z, -d.z]
  let f = 0
  for (let i = 1; i < 6; i++) if (score[i] > score[f]) f = i
  return f
}
const fmt = (p: Vec3): string => `[${p.x.toFixed(4)},${p.y.toFixed(4)},${p.z.toFixed(4)}]`
function pointAt(azDeg: number, s: number): Vec3 {
  const a = RAD(azDeg)
  let alpha = s / 152
  for (let k = 0; k < 8; k++) {
    const p: Vec3 = { x: Math.sin(alpha) * Math.cos(a), y: Math.cos(alpha), z: -Math.sin(alpha) * Math.sin(a) }
    alpha = s / sampleRadius(terrain, p)
  }
  return vec.norm({ x: Math.sin(alpha) * Math.cos(a), y: Math.cos(alpha), z: -Math.sin(alpha) * Math.sin(a) })
}

/** Tangent at pos of the spawn-centered great circle of azimuth az. */
function circleTangent(azDeg: number, pos: Vec3): Vec3 {
  const a = RAD(azDeg)
  const t: Vec3 = { x: Math.cos(a), y: 0, z: -Math.sin(a) }
  const p = vec.norm(pos)
  return vec.norm(vec.sub(t, vec.scale(p, vec.dot(t, p))))
}

interface Leg {
  label: string
  kind: 'circle' | 'toPoint' | 'hold' | 'pause'
  circleAz?: number
  dist?: number
  target?: [number, number, number]
  ticks?: number
  back?: boolean
  look?: [number, number, number]
  jumpDelayTicks?: number
  jumpTicks?: number
  maxTicks?: number
  assert?: string[]
}

const legArg = process.argv.includes('--legs')
  ? process.argv[process.argv.indexOf('--legs') + 1]
  : 'test/t5/legs.json'
const legs: Leg[] = JSON.parse(readFileSync(legArg, 'utf8'))

// GDD spawn (identical to sim.SpawnState / spawnState).
const up0: Vec3 = { x: 0, y: 1, z: 0 }
let state: State = {
  pos: vec.scale(up0, sampleRadius(terrain, up0)),
  vel: vec.zero(),
  facing: { x: 1, y: 0, z: 0 },
  grounded: true,
}
let lastLook: Vec3 = vec.copy(state.facing)

const goLines: string[] = [
  JSON.stringify({
    state: {
      pos: [state.pos.x, state.pos.y, state.pos.z],
      vel: [state.vel.x, state.vel.y, state.vel.z],
      grounded: state.grounded,
      facing: [state.facing.x, state.facing.y, state.facing.z],
    },
  }),
]
const tsLines: string[] = [
  JSON.stringify({
    pos: [state.pos.x, state.pos.y, state.pos.z],
    vel: [state.vel.x, state.vel.y, state.vel.z],
    grounded: state.grounded,
    facing: [state.facing.x, state.facing.y, state.facing.z],
  }),
]

interface Tel {
  label: string
  kind: string
  ticks: number
  minSlope: number
  maxSlope: number
  slideTicks: number
  airTicks: number
  stepUps: number
  faces: number[]
  startDir: string
  endDir: string
  stalled: boolean
  joinTurnDeg: number
  endTargetDist: number
}

const telemetry: Tel[] = []
let heading: Vec3 = { x: 1, y: 0, z: 0 }

function emitInput(input: Input): void {
  const look = [input.lookDir.x, input.lookDir.y, input.lookDir.z]
  goLines.push(
    JSON.stringify({
      input: { move_x: input.moveX, move_y: input.moveY, look, action_mask: input.actionMask },
    }),
  )
  tsLines.push(
    JSON.stringify({
      move_x: input.moveX,
      move_y: input.moveY,
      look_dir: look,
      action_mask: input.actionMask,
    }),
  )
}

/** One sim tick with look threading (mirrors both dump tools exactly). */
function simTick(input: Input, tel: Tel): number {
  const prevPos = vec.copy(state.pos)
  state = step(state, input, terrain, TICK_DT, lastLook)
  lastLook = sanitizeLook(input, lastLook)
  emitInput(input)
  tel.ticks++
  const moved = vec.len(vec.sub(state.pos, prevPos))
  const upNew = vec.norm(state.pos)
  const r = sampleRadius(terrain, upNew)
  const h = vec.len(state.pos) - r
  const sl = DEG(slopeAngle(terrain, upNew))
  tel.minSlope = Math.min(tel.minSlope, sl)
  tel.maxSlope = Math.max(tel.maxSlope, sl)
  if (!state.grounded) {
    if (h > 0.15) tel.airTicks++
    else tel.slideTicks++
  }
  const f = faceOf(state.pos)
  if (tel.faces[tel.faces.length - 1] !== f) tel.faces.push(f)
  const onSurf = Math.abs(h) < 1e-6
  if (onSurf && vec.len(state.pos) - vec.len(prevPos) >= 0.15) tel.stepUps++
  return moved
}

for (const leg of legs) {
  const tel: Tel = {
    label: leg.label,
    kind: leg.kind,
    ticks: 0,
    minSlope: Infinity,
    maxSlope: -Infinity,
    slideTicks: 0,
    airTicks: 0,
    stepUps: 0,
    faces: [faceOf(state.pos)],
    startDir: fmt(state.pos),
    endDir: '',
    stalled: false,
    joinTurnDeg: 0,
    endTargetDist: Infinity,
  }

  // Circle legs: re-align the heading to the circle tangent at the join.
  let t: Vec3
  if (leg.kind === 'circle' && leg.circleAz !== undefined) {
    t = circleTangent(leg.circleAz, state.pos)
    const upNow = vec.norm(state.pos)
    const crossUp = vec.dot(vec.cross(heading, t), upNow)
    tel.joinTurnDeg = DEG(Math.atan2(crossUp, vec.dot(heading, t)))
    t = vec.copy(t)
  } else {
    t = vec.copy(heading)
  }

  let remaining = leg.dist ?? Infinity
  let lowStreak = 0
  let maxTicks = leg.maxTicks ?? MAX_TICKS_DEFAULT
  const jumpDelay = leg.jumpDelayTicks ?? 0
  const jumpCount = leg.jumpTicks ?? 0
  let target: Vec3 | null = null
  if (leg.kind === 'toPoint' && leg.target) {
    target = { x: leg.target[0], y: leg.target[1], z: leg.target[2] }
  }

  const walk = leg.kind === 'circle' || leg.kind === 'toPoint'
  const doTicks = leg.kind === 'hold' || leg.kind === 'pause'
  let i = 0
  while (true) {
    if (walk) {
      if (remaining <= 1e-9 || i >= maxTicks) break
    } else if (i >= (leg.ticks ?? 0)) {
      break
    }

    const upNow = vec.norm(state.pos)
    // Re-tangentize the travel direction at the current position.
    const tNow = vec.norm(vec.sub(t, vec.scale(upNow, vec.dot(t, upNow))))
    let look: Vec3
    if (leg.kind === 'pause') {
      look = vec.copy(lastLook)
    } else if (leg.kind === 'toPoint' && target) {
      look = vec.copy(target)
    } else if (leg.kind === 'hold' && leg.look) {
      look = { x: leg.look[0], y: leg.look[1], z: leg.look[2] }
    } else {
      // circle + hold-without-look: aim at the SURFACE point 2 m ahead.
      const r = sampleRadius(terrain, upNow)
      const dAim = Math.min(2, walk ? remaining : 2) / r
      const dT = vec.norm(vec.add(vec.scale(upNow, Math.cos(dAim)), vec.scale(tNow, Math.sin(dAim))))
      look = vec.norm(vec.sub(vec.scale(dT, sampleRadius(terrain, dT)), state.pos))
    }
    const jumping = i >= jumpDelay && i < jumpDelay + jumpCount
    const input: Input = {
      moveX: 0,
      moveY: leg.kind === 'pause' ? 0 : leg.back ? -1 : 1,
      lookDir: look,
      actionMask: jumping ? ACTION_JUMP : 0,
    }
    const moved = simTick(input, tel)
    i++


    if (walk) {
      remaining -= moved
      const upNew = vec.norm(state.pos)
      t = vec.norm(vec.sub(t, vec.scale(upNew, vec.dot(t, upNew))))
      lowStreak = moved < STALL_MOVE ? lowStreak + 1 : 0
      if (lowStreak >= STALL_TICKS) {
        tel.stalled = true
        break
      }
      if (target) {
        const dAng = Math.acos(Math.max(-1, Math.min(1, vec.dot(vec.norm(state.pos), vec.norm(target)))))
        tel.endTargetDist = dAng * sampleRadius(terrain, upNow)
        if (tel.endTargetDist < 0.9) break
      }
    }
  }
  tel.endDir = fmt(state.pos)
  heading = vec.copy(t)
  telemetry.push(tel)
}

// ---------------------------------------------------------------- asserts
type AssertResult = { label: string; assert: string; ok: boolean; detail: string }
const assertResults: AssertResult[] = []
for (const tel of telemetry) {
  const leg = legs[telemetry.indexOf(tel)]
  for (const a of leg.assert ?? []) {
    const m = a.match(/^(maxSlope|minSlope|slideTicks|airTicks|stepUps|ticks|endTargetDist)(<=|>=|<|>|==)(-?[\d.]+)$/)
    let ok = false
    let detail = ''
    if (m) {
      const key = m[1] as keyof Tel
      const val = tel[key] as number
      const rhs = Number(m[3])
      ok =
        m[2] === '<'
          ? val < rhs
          : m[2] === '<='
            ? val <= rhs
            : m[2] === '>'
              ? val > rhs
              : m[2] === '>='
                ? val >= rhs
                : val === rhs
      detail = `${key}=${typeof val === 'number' && Number.isInteger(val) ? val : +val.toFixed(3)} ${m[2]} ${rhs}`
    } else if (a === 'stalled') {
      ok = tel.stalled
      detail = `stalled=${tel.stalled}`
    } else if (a.startsWith('faces:')) {
      const [from, to] = a.slice(6).split('->').map(Number)
      const idx = tel.faces.findIndex((f, k) => f === from && k + 1 < tel.faces.length && tel.faces[k + 1] === to)
      ok = idx >= 0
      detail = `faces=${tel.faces.join(',')}`
    } else {
      throw new Error(`unknown assert: ${a}`)
    }
    assertResults.push({ label: tel.label, assert: a, ok, detail })
  }
}

const totalTicks = goLines.length - 1
console.error(`total ticks: ${totalTicks}`)
console.error(JSON.stringify(telemetry, null, 1))
for (const r of assertResults) {
  console.error(`${r.ok ? 'ASSERT PASS' : 'ASSERT FAIL'} [${r.label}] ${r.assert}  (${r.detail})`)
}
const allOk = assertResults.every((r) => r.ok)
console.error(
  `final: pos=${fmt(state.pos)} vel=[${state.vel.x.toFixed(3)},${state.vel.y.toFixed(3)},${state.vel.z.toFixed(3)}] grounded=${state.grounded} r=${sampleRadius(terrain, vec.norm(state.pos)).toFixed(3)} |pos|=${vec.len(state.pos).toFixed(3)} face=${faceOf(state.pos)}`,
)

writeFileSync('test/t5/script-go.jsonl', goLines.join('\n') + '\n')
writeFileSync('test/t5/script-ts.jsonl', tsLines.join('\n') + '\n')
writeFileSync(
  'test/t5/prerun-telemetry.json',
  JSON.stringify({ totalTicks, telemetry, assertResults, ok: allOk }, null, 1),
)
console.error('wrote test/t5/script-go.jsonl + test/t5/script-ts.jsonl + prerun-telemetry.json')
if (!allOk) process.exit(1)