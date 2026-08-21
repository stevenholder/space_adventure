/**
 * Slope-aware glue-band probe (GDD resolve, "was GROUND or SLIDE" branch).
 *
 * Throwaway verification tool for the C10 round-trip finding: the old flat
 * glue band `h <= ground_snap` (0.15 m) broke ground contact on descents
 * steeper than ~42° at walk speed — the per-tick downhill drop
 * |vel|·dt·sinθ = 4.5·0.05·sinθ exceeds 0.15 m, so the body micro-flew
 * ballistically each tick. The fix widens the band by that drop.
 *
 * Headless under Node via tsx — same conventions as tools/smoke.ts, terrain
 * from the committed seed-1337 capture (test/out/world-seed1337.json,
 * read-only, loaded like tools/dump.ts).
 *
 *   1. Scans the radius field for the steepest node with 45° ≤ slope ≤
 *      max_slope (walkable, steep enough that the old band flew) and places
 *      the body there with zero velocity.
 *   2. Walks downhill (steepest-descent facing, walk speed, no jump/sprint)
 *      for 200 fixed ticks (10 s, ~45 m).
 *   3. Asserts:
 *        A. 0 AIR ticks while the local slope ≤ max_slope — the C10 failure
 *           was 1-tick flights on exactly these ticks;
 *        B. h within 1e-6 m of the surface on every walkable tick, and
 *           never below the surface on any tick (GDD invariants);
 *        C. the walk covered slope ≥ 45° for ≥ 10 ticks — else the probe is
 *           vacuous (it would pass even without the fix);
 *        D. at least one tick had per-tick drop |vel|·dt·sinθ > ground_snap
 *           — the ticks where the OLD band would have flown;
 *        E. flat-ground jump unchanged: tick-1 rise ≈ jump_speed·dt =
 *           0.225 m, the body leaves the band (grounded = false) and is not
 *           re-glued during the arc (GDD jump invariant).
 *
 *   npx tsx tools/glue-probe.ts [--face F --i I --j J]   (pin the start node)
 */
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import {
  ACTION,
  RULES,
  TICK_DT,
  decodeTerrain,
  latticeDir,
  sampleRadius,
  slopeAngle,
  slopeOK,
  spawnLook,
  spawnState,
  step,
  vec,
  type Input,
  type State,
  type Terrain,
  type Vec3,
} from '../src/sim/index.js'

const DEG = Math.PI / 180
const WALK_TICKS = 200
const H_EPS = 1e-6 // m — glued ticks sit on the surface to float rounding
const WORLD = fileURLToPath(new URL('../../test/out/world-seed1337.json', import.meta.url))

function fail(msg: string): never {
  console.error(`glue-probe: ${msg}`)
  process.exit(1)
}

function arg(name: string): string | undefined {
  const i = process.argv.indexOf(name)
  return i >= 0 ? process.argv[i + 1] : undefined
}

// --- terrain (same load as tools/dump.ts) ----------------------------------
const field = JSON.parse(readFileSync(WORLD, 'utf8')) as {
  face_grid: number
  radius_min: number
  radius_max: number
  radii: number[]
}
const terrain: Terrain = decodeTerrain(
  field.face_grid,
  field.radius_min,
  field.radius_max,
  new Uint16Array(field.radii),
)

// --- start: walkable node with slope ≥ 45° on smooth terrain ---------------
// Capped at max_slope so tick 1 is GROUND mode (a >50° start would slide,
// which is not what the probe walks). The body must stay in the
// walk-speed regime (GROUND mode, v ≤ walk_speed) — that is the C10
// failure regime — so the run must also stay ≤ max_slope and smooth
// (no cell kinks / scarps, where a local drop can legitimately exceed
// the smoothed band: the GDD "ran off an edge" AIR case).
const SMOOTH_TOL = 0.15 // max |tan θ_small − tan θ_smoothed| mismatch

/** Tangential gradient of the radius field at `up` (m per m along the
 *  tangent plane) — finite difference with the same deterministic basis
 *  as surfaceNormal; its magnitude is the local (small-scale) slope. */
function tangentGradient(t: Terrain, up: Vec3): Vec3 {
  const k: Vec3 = Math.abs(up.x) <= 0.9 ? { x: 1, y: 0, z: 0 } : { x: 0, y: 1, z: 0 }
  const e1 = vec.norm(vec.sub(k, vec.scale(up, vec.dot(k, up))))
  const e2 = vec.cross(up, e1)
  const eps = 0.02
  const r0 = sampleRadius(t, up)
  const dr1 = (sampleRadius(t, vec.norm(vec.add(up, vec.scale(e1, eps)))) - r0) / (eps * r0)
  const dr2 = (sampleRadius(t, vec.norm(vec.add(up, vec.scale(e2, eps)))) - r0) / (eps * r0)
  return vec.add(vec.scale(e1, dr1), vec.scale(e2, dr2))
}

/** Steepest-descent tangent direction at `up` (`fallback` on flats). */
function downhillDir(t: Terrain, up: Vec3, fallback: Vec3): Vec3 {
  const grad = tangentGradient(t, up)
  return vec.len(grad) < 1e-6 ? vec.copy(fallback) : vec.norm(vec.scale(grad, -1))
}

function findStart(): { face: number; i: number; j: number; slopeDeg: number } {
  const g = RULES.faceGrid
  let best: { face: number; i: number; j: number; slopeDeg: number; score: number } | null = null
  for (let face = 0; face < 6; face++)
    for (let i = 0; i < g; i++)
      for (let j = 0; j < g; j++) {
        const d = latticeDir(face, i, j, g)
        const a = slopeAngle(terrain, d) / DEG
        if (a < 45 || a > RULES.maxSlopeDeg) continue
        const smooth = (p: Vec3): boolean => {
          const ang = slopeAngle(terrain, p)
          return Math.abs(vec.len(tangentGradient(terrain, p)) - Math.tan(ang)) <= SMOOTH_TOL
        }
        if (!smooth(d)) continue
        // Re-steered lookahead over the whole 45 m walk (20 ticks apart):
        // the first 4.5 m (the C10 regime the fix targets) must stay in
        // [45°, max_slope], the rest walkable (≤ max_slope — no slide
        // regime) and smooth throughout — else the run leaves the smooth
        // walk-speed regime the probe asserts on.
        let q = d
        let steepMin = a
        let pass = true
        for (let k = 1; k <= 20; k++) {
          const dl = downhillDir(terrain, q, spawnLook())
          q = vec.norm(vec.add(vec.scale(q, sampleRadius(terrain, q)), vec.scale(dl, 2.25)))
          const a2 = slopeAngle(terrain, q) / DEG
          if (a2 > RULES.maxSlopeDeg || !smooth(q)) {
            pass = false
            break
          }
          if (k <= 2 && a2 < 45) {
            pass = false
            break
          }
          if (k <= 2) steepMin = Math.min(steepMin, a2)
        }
        if (!pass) continue
        if (best === null || steepMin > best.score) best = { face, i, j, slopeDeg: a, score: steepMin }
      }
  if (best === null) fail('no smooth start with 45° ≤ slope ≤ max_slope and a smooth 45 m downhill run')
  return { face: best.face, i: best.i, j: best.j, slopeDeg: best.slopeDeg }
}

// --- A–D: the 200-tick downhill walk ----------------------------------------
let start: { face: number; i: number; j: number; slopeDeg: number }
const pinF = arg('--face')
const pinI = arg('--i')
const pinJ = arg('--j')
if (pinF !== undefined && pinI !== undefined && pinJ !== undefined) {
  const face = Number(pinF)
  const i = Number(pinI)
  const j = Number(pinJ)
  if (![face, i, j].every(Number.isInteger)) fail('--face/--i/--j must be integers')
  const d = latticeDir(face, i, j, RULES.faceGrid)
  start = { face, i, j, slopeDeg: slopeAngle(terrain, d) / DEG }
} else {
  start = findStart()
}

const up0 = latticeDir(start.face, start.i, start.j, RULES.faceGrid)
let facing = downhillDir(terrain, up0, spawnLook())
let state: State = {
  pos: vec.scale(up0, sampleRadius(terrain, up0)),
  vel: vec.zero(),
  facing,
  grounded: slopeOK(terrain, up0),
}

interface TickRec {
  tick: number
  h: number
  slopeDeg: number
  grounded: boolean
  /** per-tick downhill drop |vel|·dt·sinθ exceeded the OLD flat band */
  oldBandFly: boolean
}
const ticks: TickRec[] = []
for (let i = 1; i <= WALK_TICKS; i++) {
  const up = vec.norm(state.pos)
  facing = downhillDir(terrain, up, facing)
  const input: Input = { moveX: 0, moveY: 1, lookDir: facing, actionMask: 0 }
  state = step(state, input, terrain, TICK_DT)
  const upN = vec.norm(state.pos)
  const slope = slopeAngle(terrain, upN)
  ticks.push({
    tick: i,
    h: vec.len(state.pos) - sampleRadius(terrain, upN),
    slopeDeg: slope / DEG,
    grounded: state.grounded,
    oldBandFly: vec.len(state.vel) * TICK_DT * Math.sin(slope) > RULES.groundSnap,
  })
}

const walkable = ticks.filter((t) => t.slopeDeg <= RULES.maxSlopeDeg)
const airborneWhileWalkable = walkable.filter((t) => !t.grounded)
const hBad = walkable.filter((t) => t.h > H_EPS)
const belowSurface = ticks.filter((t) => t.h < -1e-9)
const steepTicks = ticks.filter((t) => t.slopeDeg >= 45)
const maxSlope = Math.max(...ticks.map((t) => t.slopeDeg))
const minH = Math.min(...ticks.map((t) => t.h))
const maxH = Math.max(...ticks.map((t) => t.h))
const oldFlyCount = ticks.filter((t) => t.oldBandFly).length
const airborneTotal = ticks.filter((t) => !t.grounded)

const A = airborneWhileWalkable.length === 0
const B = hBad.length === 0 && belowSurface.length === 0
const C = steepTicks.length >= 10
const D = oldFlyCount >= 1

// --- E: flat-ground jump on the spawn disc (the terrain contract makes it
// --- flat; the GDD jump invariant says the band never re-glues a jump) -----
let js = spawnState(terrain)
const look = spawnLook()
const jumpIn: Input = { moveX: 0, moveY: 0, lookDir: look, actionMask: ACTION.JUMP }
js = step(js, jumpIn, terrain, TICK_DT)
const jumpGrounded = js.grounded
const jumpRise = vec.len(js.pos) - sampleRadius(terrain, vec.norm(js.pos))
const arc: { h: number; grounded: boolean }[] = []
for (let i = 2; i <= 6; i++) {
  js = step(js, { moveX: 0, moveY: 0, lookDir: look, actionMask: 0 }, terrain, TICK_DT)
  const u = vec.norm(js.pos)
  arc.push({ h: vec.len(js.pos) - sampleRadius(terrain, u), grounded: js.grounded })
}
const spawnSlopeDeg = slopeAngle(terrain, { x: 0, y: 1, z: 0 }) / DEG
const expectedRise = RULES.jumpSpeed * TICK_DT // 0.225 m
const E1 = Math.abs(jumpRise - expectedRise) < 0.01
const E2 = !jumpGrounded
const E3 = arc.every((a) => !a.grounded)

// --- report ------------------------------------------------------------------
const report = {
  start: { face: start.face, i: start.i, j: start.j, slopeDeg: +start.slopeDeg.toFixed(2) },
  walk: {
    ticks: WALK_TICKS,
    maxSlopeDeg: +maxSlope.toFixed(2),
    ticksSlopeGe45: steepTicks.length,
    oldBandFlyTicks: oldFlyCount,
    airborneWhileWalkable: airborneWhileWalkable.length,
    airborneTotal: airborneTotal.length,
    hMin: minH.toExponential(2),
    hMax: maxH.toExponential(2),
  },
  jump: {
    spawnSlopeDeg: +spawnSlopeDeg.toFixed(3),
    tick1Rise: +jumpRise.toFixed(4),
    expectedRise,
    leftBandOnTick1: E2,
    noReGlueTicks2to6: E3,
    arcH: arc.map((a) => +a.h.toFixed(4)),
  },
  assertions: {
    A_walkableTicksNeverAirborne: A,
    B_hGluedNeverBelowSurface: B,
    C_runCoveredSlopeGe45: C,
    D_oldBandWouldHaveFlown: D,
    E1_jumpTick1RiseIs0225m: E1,
    E2_jumpLeavesBand: E2,
    E3_jumpNotReGlued: E3,
  },
}
console.log(JSON.stringify(report, null, 2))

const pass = A && B && C && D && E1 && E2 && E3
if (!pass) {
  const firstAir = airborneWhileWalkable[0]
  console.error(
    `glue-probe FAIL: A=${A} B=${B} C=${C} D=${D} E1=${E1} E2=${E2} E3=${E3}` +
      (firstAir ? ` (first airborne walkable tick ${firstAir.tick}, slope ${firstAir.slopeDeg.toFixed(1)}°)` : ''),
  )
  process.exit(1)
}
console.log('glue-probe OK')