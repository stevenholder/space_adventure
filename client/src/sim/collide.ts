/**
 * Static collider push-out plus terrain re-seat (GDD "Static colliders" ->
 * "Integrator addition", steps 8 and 9). Ported line for line from
 * server/internal/sim/collide.go — this is the Go/TS mirror pair the
 * conformance test diffs, so a difference here is a player standing inside
 * a wall on one screen and outside it on the other. Wiring this into `step`
 * is done in step.ts.
 *
 * NO DOM: this file compiles under tsconfig.sim.json (lib ES2022 only).
 */
import type { Vec3 } from './types.js'
import { RULES, clamp, vec } from './types.js'

// Structurally identical to net/phase2.ts's `Collider` (the decoded wire
// shape). Declared locally so src/sim never imports src/net.
export interface Collider {
  kind: number
  center: [number, number, number]
  half: [number, number, number]
  quat: [number, number, number, number]
}

// protocol.go: ColliderBox = 0, ColliderSphere = 1.
const COLLIDER_SPHERE = 1

// Rule table constants for the player collision shape (GDD "Static
// colliders" rule table).
export const BODY_RADIUS = 0.35 // body_radius: collision sphere, m
export const BODY_SPHERE_H = 0.9 // body_sphere_h: chest height above the feet, m

// The distance below which a sphere-vs-collider closest point is treated as
// coincident with the sphere centre — the exit normal is then undefined by
// construction, not just numerically noisy.
const DEGEN_EPS = 1e-9

interface Nearest {
  hit: boolean
  n: Vec3
  depth: number
}

/**
 * resolveColliders applies GDD integrator steps 8 (static collider
 * push-out) and 9 (terrain re-seat), in that order, verbatim.
 */
export function resolveColliders(
  pos: Vec3,
  vel: Vec3,
  up: Vec3,
  grounded: boolean,
  cs: Collider[],
  radiusAt: (d: Vec3) => number,
): { pos: Vec3; vel: Vec3; grounded: boolean } {
  // No colliders: nothing to push out of, and step 9 exists ONLY to repair a
  // push-out that sank the feet below the terrain (GDD "Integrator
  // addition"). Running it anyway re-seats whenever |pos| sits an ulp under
  // the sampled radius, which re-rounds pos and perturbs the trajectory by
  // ~1e-13 per tick. Harmless against C5's 5% bar, but it silently voids the
  // byte-identical property that is the evidence the wire work never touched
  // the sim -- and a drift you cannot distinguish from noise is a drift you
  // stop noticing.
  if (cs.length === 0) {
    return { pos, vel, grounded }
  }

  let p = pos
  let v = vel
  const u = up
  let g = grounded
  const cosMaxSlope = Math.cos(RULES.maxSlopeRad)

  // Step 8 — static collider resolution, in the order received.
  let c = vec.add(p, vec.scale(u, BODY_SPHERE_H))
  for (let i = 0; i < cs.length; i++) {
    const { hit, n, depth } = nearest(c, BODY_RADIUS, cs[i], u)
    if (!hit) continue
    p = vec.add(p, vec.scale(n, depth))
    c = vec.add(p, vec.scale(u, BODY_SPHERE_H))
    const d = vec.dot(v, n)
    if (d < 0) v = vec.sub(v, vec.scale(n, d))
    if (vec.dot(n, u) >= cosMaxSlope) g = true
  }

  // Step 9 — re-seat on the terrain if step 8 pushed the feet under it.
  const dir = vec.norm(p)
  const r = radiusAt(dir)
  if (vec.len(p) < r) {
    p = vec.scale(dir, r)
    const d = vec.dot(v, u)
    if (d < 0) v = vec.sub(v, vec.scale(u, d))
  }

  return { pos: p, vel: v, grounded: g }
}

/**
 * nearest is the sphere-vs-collider closest-point test: sphere centre c,
 * radius, against collider col. Total (every input yields a defined, finite
 * result) and non-allocating. up feeds only the degenerate-case fallback
 * normal.
 */
function nearest(c: Vec3, radius: number, col: Collider, up: Vec3): Nearest {
  if (col.kind === COLLIDER_SPHERE) return nearestSphere(c, radius, col, up)
  // ColliderBox and anything unrecognised resolve as a box.
  return nearestBox(c, radius, col, up)
}

function nearestSphere(c: Vec3, radius: number, col: Collider, up: Vec3): Nearest {
  const center: Vec3 = { x: col.center[0], y: col.center[1], z: col.center[2] }
  const colRadius = col.half[0]
  const combined = radius + colRadius

  const diff = vec.sub(c, center)
  const dist = vec.len(diff)
  if (dist >= combined) return { hit: false, n: vec.zero(), depth: 0 }
  if (dist < DEGEN_EPS) return { hit: true, n: up, depth: combined }
  return { hit: true, n: vec.scale(diff, 1 / dist), depth: combined - dist }
}

function nearestBox(c: Vec3, radius: number, col: Collider, up: Vec3): Nearest {
  const center: Vec3 = { x: col.center[0], y: col.center[1], z: col.center[2] }
  const half: Vec3 = { x: col.half[0], y: col.half[1], z: col.half[2] }
  const q = col.quat
  const qConj: [number, number, number, number] = [-q[0], -q[1], -q[2], q[3]]

  const local = rotateQ(qConj, vec.sub(c, center))
  const clamped: Vec3 = {
    x: clamp(local.x, -half.x, half.x),
    y: clamp(local.y, -half.y, half.y),
    z: clamp(local.z, -half.z, half.z),
  }
  const closest = vec.add(rotateQ(q, clamped), center)

  const diff = vec.sub(c, closest)
  const dist = vec.len(diff)
  if (dist >= radius) return { hit: false, n: vec.zero(), depth: 0 }
  if (dist < DEGEN_EPS) {
    // Sphere centre coincides with its closest point in the box (e.g.
    // exactly at the box centre): no defined exit normal. Push out along up
    // by the full body radius rather than propagate a NaN.
    return { hit: true, n: up, depth: radius }
  }
  return { hit: true, n: vec.scale(diff, 1 / dist), depth: radius - dist }
}

/** Applies quaternion q (x, y, z, w) to vector v. */
function rotateQ(q: readonly [number, number, number, number], v: Vec3): Vec3 {
  const [x, y, z, w] = q
  // t = 2 * cross(qv, v)
  const tx = 2 * (y * v.z - z * v.y)
  const ty = 2 * (z * v.x - x * v.z)
  const tz = 2 * (x * v.y - y * v.x)
  return {
    x: v.x + w * tx + (y * tz - z * ty),
    y: v.y + w * ty + (z * tx - x * tz),
    z: v.z + w * tz + (x * ty - y * tx),
  }
}
