/**
 * Pure simulation core — movement + terrain sampling.
 *
 * NO DOM, NO Three.js, NO browser globals. This module compiles under
 * tsconfig.sim.json with lib ES2022 only, so reaching for `window`,
 * `document` or `performance` here is a build error. `dt` and input are
 * always passed in, never read from a clock.
 *
 * The rule table is docs/GDD.md "M1 on-foot movement" (binding). The
 * integrator order is GDD "Integrator" (binding on both implementations —
 * replay reconciliation only converges when Go and TS step identically).
 */

import type { Collider } from './collide.js'

export interface Vec3 {
  x: number
  y: number
  z: number
}

/** One `input` command (PROTOCOL 0x0003). Current command state, latest wins. */
export interface Input {
  /** wish direction in the body tangent frame, each in [-1, 1] */
  moveX: number
  moveY: number
  /** absolute world-space unit direction the eyes point along (client-authoritative) */
  lookDir: Vec3
  /** 0x0001 sprint, 0x0002 jump (PROTOCOL action_mask) */
  actionMask: number
  /**
   * Static colliders for this tick's GDD steps 8-9 (ResolveColliders).
   * Optional so existing callers that omit it keep taking the no-op path —
   * mirrors Go's Input.Colliders, which is not part of the wire payload.
   */
  colliders?: Collider[]
}

/** Simulated body state. Mutated by `step` only through returned copies. */
export interface State {
  /** world-space Cartesian, planet centre at origin */
  pos: Vec3
  vel: Vec3
  /** body facing: unit vector, tangent to the surface */
  facing: Vec3
  grounded: boolean
}

/** Decoded `terrain` message (PROTOCOL 0x000A). f64 internally (GDD). */
export interface Terrain {
  faceGrid: number
  radiusMin: number
  radiusMax: number
  /** radii[face·G² + row·G + col], metres from planet centre */
  radii: Float64Array
}

export const ACTION = {
  SPRINT: 0x0001,
  JUMP: 0x0002,
} as const

/** GDD "M1 on-foot movement" rule table — every value named in the GDD. */
export const RULES = {
  tickHz: 20, // Hz, server AND client-prediction tick; dt = 0.05 s fixed
  walkSpeed: 4.5, // m/s
  sprintSpeed: 7.5, // m/s
  accelGround: 50, // m/s² — 0→walk in ~0.09 s
  frictionGround: 8, // 1/s, v *= exp(−friction·dt) with no move input
  accelAir: 8, // m/s² — air control steers, momentum dominates
  gravity: 9.8, // m/s² toward planet centre (SLIDE + AIR only; GROUND is supported)
  jumpSpeed: 4.5, // m/s
  terminalSpeed: 60, // m/s downward radial-speed clamp
  maxStep: 0.3, // m walked up per step without jumping
  maxSlopeDeg: 50, // steeper → slide
  maxSlopeRad: (50 * Math.PI) / 180,
  groundSnap: 0.15, // m glue band
  lookClampDeg: 1, // look_dir stays this far from ±up
  facingHold: 0.1, // below this tangent length the look is too near-vertical: facing holds
  normalEpsDeg: 2, // finite-difference offset for surfaceNormal (~5 m arc, ~1.5 cells)
  wallTol: 1e-3, // m bisection tolerance for wall contact
  epsDegen: 1e-6, // numeric-degeneracy threshold
  // Client-only render values (GDD rule table):
  eyeHeight: 1.7, // m camera offset above the foot position
  nearClip: 0.05, // m first-person near plane
  farClip: 500, // m first-person far plane
  // Declared for M2 (GDD) — M1 terrain collision uses the foot point:
  capsuleRadius: 0.4, // m
  capsuleHeight: 1.8, // m
  // World:
  planetRadius: 150, // m nominal surface radius
  radiusMin: 124, // m wire-encoding floor
  radiusMax: 190, // m wire-encoding ceiling
  faceGrid: 65, // samples per cube face
} as const

/** Fixed sim step — the server tick. Never a variable frame delta (GDD). */
export const TICK_DT = 1 / RULES.tickHz

/** GDD spawn: on the surface along (0,1,0), standing, zero velocity. */
export const SPAWN_DIR: Vec3 = { x: 0, y: 1, z: 0 }

// ---------------------------------------------------------------------------
// Vector helpers (pure, allocation-light enough for a 20 Hz sim)
// ---------------------------------------------------------------------------

export const vec = {
  zero(): Vec3 {
    return { x: 0, y: 0, z: 0 }
  },
  copy(a: Vec3): Vec3 {
    return { x: a.x, y: a.y, z: a.z }
  },
  set(a: Vec3, x: number, y: number, z: number): Vec3 {
    a.x = x
    a.y = y
    a.z = z
    return a
  },
  add(a: Vec3, b: Vec3): Vec3 {
    return { x: a.x + b.x, y: a.y + b.y, z: a.z + b.z }
  },
  sub(a: Vec3, b: Vec3): Vec3 {
    return { x: a.x - b.x, y: a.y - b.y, z: a.z - b.z }
  },
  scale(a: Vec3, s: number): Vec3 {
    return { x: a.x * s, y: a.y * s, z: a.z * s }
  },
  dot(a: Vec3, b: Vec3): number {
    return a.x * b.x + a.y * b.y + a.z * b.z
  },
  cross(a: Vec3, b: Vec3): Vec3 {
    return {
      x: a.y * b.z - a.z * b.y,
      y: a.z * b.x - a.x * b.z,
      z: a.x * b.y - a.y * b.x,
    }
  },
  len(a: Vec3): number {
    return Math.hypot(a.x, a.y, a.z)
  },
  norm(a: Vec3): Vec3 {
    const l = Math.hypot(a.x, a.y, a.z)
    if (l < 1e-12) return { x: 0, y: 0, z: 0 }
    return { x: a.x / l, y: a.y / l, z: a.z / l }
  },
  lerp(a: Vec3, b: Vec3, t: number): Vec3 {
    return {
      x: a.x + (b.x - a.x) * t,
      y: a.y + (b.y - a.y) * t,
      z: a.z + (b.z - a.z) * t,
    }
  },
}

export function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v
}

export function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t
}
