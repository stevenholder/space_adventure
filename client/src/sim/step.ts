/**
 * Fixed-step on-foot movement — the GDD "Integrator", verbatim. The server
 * tick and the client prediction run this exact order; replay
 * reconciliation only converges when both ends step identically.
 *
 *  1. Sanitize input (move clamped to the unit disk; look falls back to
 *     the last applied look when non-finite or degenerate)
 *  2. Frame and facing — clamped look, tangent projection, facing_hold
 *  3. Mode from carried state: GROUND | SLIDE | AIR
 *  4. Acceleration per mode (no gravity in GROUND — the ground's normal
 *     force cancels it; SLIDE gets gravity along the downslope tangent;
 *     AIR gets radial gravity + air control + terminal clamp)
 *  5. Jump — level-triggered, GROUND only (holding re-jumps each landing)
 *  6. Integrate (semi-implicit Euler: the velocity from 4–5 moves the body)
 *  7. resolve — the single writer of grounded (step-up / wall bisection /
 *     ground_snap glue / slopeOK)
 *
 * No world-edge clamp (a sphere has no edge), no atan2 anywhere, no
 * variable dt. `step` is pure and total: it copies its state, never reads
 * a clock. `dt` is TICK_DT = 50 ms on both ends.
 */
import type { Input, State, Terrain, Vec3 } from './types.js'
import { ACTION, RULES, SPAWN_DIR, TICK_DT, clamp, vec } from './types.js'
import { sampleRadius, slopeOK, surfaceNormal } from './terrain.js'

const DEG = Math.PI / 180
const EPS = RULES.epsDegen
const SIN_LOOK_CLAMP = Math.sin(RULES.lookClampDeg * DEG)
const COS_LOOK_CLAMP = Math.cos(RULES.lookClampDeg * DEG)
type Mode = 'GROUND' | 'SLIDE' | 'AIR'

// ---------------------------------------------------------------------------
// Input sanitization (GDD "Input")
// ---------------------------------------------------------------------------

/**
 * GDD: each component clamped to [-1, 1], then the pair to unit length —
 * diagonal movement is never 1.41× faster. Non-finite → 0.
 */
export function sanitizeMove(input: Input): { x: number; y: number } {
  let x = input.moveX
  let y = input.moveY
  if (!Number.isFinite(x)) x = 0
  if (!Number.isFinite(y)) y = 0
  x = x < -1 ? -1 : x > 1 ? 1 : x
  y = y < -1 ? -1 : y > 1 ? 1 : y
  const l = Math.hypot(x, y)
  if (l > 1) return { x: x / l, y: y / l }
  return { x, y }
}

/**
 * GDD: non-finite or shorter than eps_degen → the last applied input's
 * look; otherwise normalized.
 */
export function sanitizeLook(input: Input, prevLook: Vec3): Vec3 {
  const l = input.lookDir
  if (!Number.isFinite(l.x) || !Number.isFinite(l.y) || !Number.isFinite(l.z)) {
    return vec.copy(prevLook)
  }
  if (vec.len(l) < EPS) return vec.copy(prevLook)
  return vec.norm(l)
}

/**
 * GDD "Spawn": the tangent projection of world +X at spawn (fallback +Z
 * when +X is degenerate). The spawn plain is flat, so this is independent
 * of the terrain field.
 */
export function spawnLook(): Vec3 {
  const up = vec.norm(SPAWN_DIR)
  const x = tangentOf({ x: 1, y: 0, z: 0 }, up)
  if (x) return x
  return tangentOf({ x: 0, y: 0, z: 1 }, up) ?? vec.copy(SPAWN_DIR)
}

function tangentOf(w: Vec3, up: Vec3): Vec3 | null {
  const t = vec.sub(w, vec.scale(up, vec.dot(w, up)))
  return vec.len(t) >= EPS ? vec.norm(t) : null
}

// ---------------------------------------------------------------------------
// The per-tick step (GDD "Integrator")
// ---------------------------------------------------------------------------

/**
 * GDD clampLook: push the look to at least look_clamp from ±up,
 * preserving azimuth. When the look sits exactly on a pole the tangent is
 * zero — the fallback is the previous facing (GDD, verbatim).
 */
export function clampLook(look: Vec3, up: Vec3, prevFacing: Vec3): Vec3 {
  const l = vec.norm(look)
  const c = vec.dot(l, up)
  if (c >= COS_LOOK_CLAMP) return pushOffPole(l, up, c, 1, prevFacing)
  if (c <= -COS_LOOK_CLAMP) return pushOffPole(l, up, c, -1, prevFacing)
  return l
}

function pushOffPole(l: Vec3, up: Vec3, c: number, sign: number, prevFacing: Vec3): Vec3 {
  let t = vec.sub(l, vec.scale(up, c))
  if (vec.len(t) < EPS) t = prevFacing
  return vec.add(vec.scale(up, sign * COS_LOOK_CLAMP), vec.scale(vec.norm(t), SIN_LOOK_CLAMP))
}

/** GDD approach: constant acceleration toward target, no overshoot. */
function approach(v: Vec3, target: Vec3, maxDelta: number): Vec3 {
  const d = vec.sub(target, v)
  const dl = vec.len(d)
  if (dl <= maxDelta) return target
  return vec.add(v, vec.scale(d, maxDelta / dl))
}

/**
 * One fixed step. `prev` is not mutated. `prevLook` is the last applied
 * input's look (GDD "Input" fallback) — defaults to the spawn look when
 * not supplied.
 */
export function step(
  prev: State,
  input: Input,
  terrain: Terrain,
  dt: number,
  prevLook?: Vec3,
): State {
  // 1. Sanitize input
  const mv = sanitizeMove(input)
  const sprint = (input.actionMask & ACTION.SPRINT) !== 0
  const jump = (input.actionMask & ACTION.JUMP) !== 0
  const look = sanitizeLook(input, prevLook ?? spawnLook())

  // 2. Frame and facing — a direct rule, not a dynamic: facing is updated
  //    from the clamped look every tick, held when the look is too
  //    near-vertical to define a stable azimuth.
  const up = vec.norm(prev.pos)
  const clamped = clampLook(look, up, prev.facing)
  const tang = vec.sub(clamped, vec.scale(up, vec.dot(clamped, up)))
  const facing =
    vec.len(tang) >= RULES.facingHold ? vec.norm(tang) : vec.copy(prev.facing)
  const right = vec.cross(up, facing)

  // 3. Mode (from state carried out of the previous step)
  const inContact = vec.len(prev.pos) - sampleRadius(terrain, up) <= RULES.groundSnap
  const mode: Mode = prev.grounded ? 'GROUND' : inContact ? 'SLIDE' : 'AIR'

  // 4. Acceleration — per mode. Binary target: stick magnitude does not
  //    scale speed.
  const speed = sprint ? RULES.sprintSpeed : RULES.walkSpeed
  const wishLen = Math.hypot(mv.x, mv.y)
  const target =
    wishLen > EPS
      ? vec.scale(vec.add(vec.scale(facing, mv.y), vec.scale(right, mv.x)), speed / wishLen)
      : vec.zero()
  let vel = prev.vel
  if (mode === 'GROUND') {
    if (wishLen > EPS) vel = approach(vel, target, RULES.accelGround * dt)
    else vel = vec.scale(vel, Math.exp(-RULES.frictionGround * dt))
  } else if (mode === 'SLIDE') {
    // Gravity along the downslope tangent: no walk accel, no friction.
    const n = surfaceNormal(terrain, up)
    const g = vec.scale(up, -RULES.gravity)
    vel = vec.add(vel, vec.scale(vec.sub(g, vec.scale(n, vec.dot(g, n))), dt))
  } else {
    const vr = vec.dot(vel, up)
    let vt = vec.sub(vel, vec.scale(up, vr))
    if (wishLen > EPS) vt = approach(vt, target, RULES.accelAir * dt)
    vel = vec.sub(vec.add(vec.scale(up, vr), vt), vec.scale(up, RULES.gravity * dt))
    const vrAfter = vec.dot(vel, up)
    if (vrAfter < -RULES.terminalSpeed) {
      vel = vec.sub(vel, vec.scale(up, vrAfter + RULES.terminalSpeed))
    }
  }

  // 5. Jump — level-triggered, once per grounded contact (bunny hop)
  if (mode === 'GROUND' && jump) vel = vec.add(vel, vec.scale(up, RULES.jumpSpeed))

  // 6. Integrate (semi-implicit Euler)
  const posOld = prev.pos
  const posNew = vec.add(posOld, vec.scale(vel, dt))

  // 7. Terrain resolution — the single writer of grounded
  const r = resolve(posOld, posNew, vel, mode, terrain)
  return { pos: r.pos, vel: r.vel, facing, grounded: r.grounded }
}

// ---------------------------------------------------------------------------
// Terrain resolution (GDD, verbatim)
// ---------------------------------------------------------------------------

/**
 * GDD wallSlide: p_old is on/above the surface, p_new below it by more
 * than max_step. Bisection finds the first contact; the fixed tolerance
 * runs on both ends, so results differ only by float rounding.
 */
function wallSlide(pOld: Vec3, pNew: Vec3, terrain: Terrain): Vec3 {
  const d = vec.sub(pNew, pOld)
  let lo = 0
  let hi = 1
  const f = (t: number): number => {
    const p = vec.add(pOld, vec.scale(d, t))
    return vec.len(p) - sampleRadius(terrain, vec.norm(p))
  }
  while (hi - lo > RULES.wallTol) {
    const mid = (lo + hi) / 2
    if (f(mid) >= 0) lo = mid
    else hi = mid
  }
  return vec.add(pOld, vec.scale(d, lo))
}

/**
 * GDD resolve — the only position correction in M1. The radial zeroing is
 * the velocity re-projection, done at the new position so walking across a
 * curved surface cannot accumulate radial drift.
 */
function resolve(
  pOld: Vec3,
  pNew: Vec3,
  vel: Vec3,
  mode: Mode,
  terrain: Terrain,
): { pos: Vec3; vel: Vec3; grounded: boolean } {
  let up = vec.norm(pNew)
  const h = vec.len(pNew) - sampleRadius(terrain, up)
  if (h < 0) {
    // Feet below the surface: step-up, landing, or the normal curvature fit.
    let pos: Vec3
    if (-h <= RULES.maxStep) {
      pos = vec.scale(up, sampleRadius(terrain, up))
    } else {
      // Wall: the step is too high to climb — stop at first contact.
      pos = wallSlide(pOld, pNew, terrain)
      up = vec.norm(pos)
    }
    return {
      pos,
      vel: vec.sub(vel, vec.scale(up, vec.dot(vel, up))),
      grounded: slopeOK(terrain, up),
    }
  }
  if (mode === 'AIR') {
    // Still above the surface.
    return { pos: pNew, vel, grounded: false }
  }
  // GDD resolve, "was GROUND or SLIDE" — slope-aware glue band: θ_contact
  // is the same slope measure as slopeOK (same surfaceNormal/normal_eps);
  // the band widens by the per-step downhill drop |vel|·dt·sin θ, so steep
  // walkable slopes stay glued at walk/sprint speed. |vel| is the
  // tangential speed — contact velocity is radial-zeroed every step
  // (tangent-velocity invariant), and a jump tick's radial rise must not
  // widen the band, or the GDD jump margin (jump_speed·dt − ground_snap)
  // stops holding exactly. Flat ground (θ ≈ 0) gives exactly groundSnap —
  // old flat/cliff behavior unchanged.
  const theta = Math.acos(clamp(vec.dot(surfaceNormal(terrain, up), up), -1, 1))
  const tangent = vec.sub(vel, vec.scale(up, vec.dot(vel, up)))
  const slopeDrop = vec.len(tangent) * TICK_DT * Math.max(0, Math.sin(theta))
  if (h <= RULES.groundSnap + slopeDrop) {
    // Still in the glue band — feet stay glued downhill.
    return {
      pos: vec.scale(up, sampleRadius(terrain, up)),
      vel: vec.sub(vel, vec.scale(up, vec.dot(vel, up))),
      grounded: slopeOK(terrain, up),
    }
  }
  // Ran off an edge, or jumped — airborne.
  return { pos: pNew, vel, grounded: false }
}

// ---------------------------------------------------------------------------
// Spawn / wire inference
// ---------------------------------------------------------------------------

/**
 * GDD "Spawn": on the surface along spawn_dir, standing, zero velocity;
 * look = tangent projection of +X (fallback +Z); facing = the spawn look's
 * tangent; grounded = slopeOK (true on the flat spawn disc).
 */
export function spawnState(terrain: Terrain): State {
  const up = vec.norm(SPAWN_DIR)
  return {
    pos: vec.scale(up, sampleRadius(terrain, up)),
    vel: vec.zero(),
    facing: spawnLook(),
    grounded: slopeOK(terrain, up),
  }
}

/**
 * Infer grounded from a wire entity — a snapshot carries no grounded flag.
 * The exact GDD contact rule: in the glue band AND the slope is walkable.
 * Used only at the reconciliation snap; the next step's resolve re-derives
 * it, so a 1-tick mode mismatch self-corrects.
 */
export function inferGrounded(pos: Vec3, terrain: Terrain): boolean {
  const up = vec.norm(pos)
  return (
    vec.len(pos) - sampleRadius(terrain, up) <= RULES.groundSnap && slopeOK(terrain, up)
  )
}
