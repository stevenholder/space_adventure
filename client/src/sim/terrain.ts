/**
 * Cube-sphere terrain sampling.
 *
 * GDD "Terrain sampling" is binding on both ends — face order, axis
 * assignment and seams must match the Go server exactly, or players fall
 * through the ground at specific edges.
 *
 * Face order (PROTOCOL terrain message): +X, −X, +Y, −Y, +Z, −Z.
 *
 * Per-face (u, v) convention — pinned by GDD "Terrain sampling" and the
 * PROTOCOL terrain layout: the two non-normal axes in world order, each
 * divided by the magnitude of the face's own component:
 *   +X/−X → (dy, dz),  +Y/−Y → (dx, dz),  +Z/−Z → (dx, dy)
 */
import type { Terrain, Vec3 } from './types.js'
import { RULES, clamp, lerp, vec } from './types.js'

/** Face order: +X, −X, +Y, −Y, +Z, −Z (PROTOCOL). */
export const FACE_COUNT = 6

/**
 * Pick the face for a direction: the face whose signed component along its
 * normal is largest. Ties resolve to the lower face index, which matches a
 * "strict >" scan in face order on both ends.
 */
export function pickFace(d: Vec3): number {
  const score = [d.x, -d.x, d.y, -d.y, d.z, -d.z]
  let face = 0
  for (let i = 1; i < FACE_COUNT; i++) {
    if (score[i] > score[face]) face = i
  }
  return face
}

/** Face coordinates (u, v) in [-1, 1] for a direction (see header note). */
export function faceUV(face: number, d: Vec3): { u: number; v: number } {
  let m: number
  let u: number
  let v: number
  switch (face) {
    case 0: // +X
    case 1: // −X
      m = Math.abs(d.x)
      u = d.y
      v = d.z
      break
    case 2: // +Y
    case 3: // −Y
      m = Math.abs(d.y)
      u = d.x
      v = d.z
      break
    case 4: // +Z
    case 5: // −Z
    default:
      m = Math.abs(d.z)
      u = d.x
      v = d.y
      break
  }
  return { u: u / m, v: v / m }
}

/**
 * Bilinear radius sample at a direction (GDD steps 1–3).
 * Shared edges are duplicated in both adjacent faces and hold identical
 * values (server guarantee), so sampling is continuous across seams.
 */
export function sampleRadius(t: Terrain, d: Vec3): number {
  const face = pickFace(d)
  const { u, v } = faceUV(face, d)
  const g = t.faceGrid
  const gx = clamp(((u + 1) * 0.5 * (g - 1)), 0, g - 1)
  const gy = clamp(((v + 1) * 0.5 * (g - 1)), 0, g - 1)
  const x0 = Math.floor(gx)
  const y0 = Math.floor(gy)
  const x1 = Math.min(x0 + 1, g - 1)
  const y1 = Math.min(y0 + 1, g - 1)
  const fx = gx - x0
  const fy = gy - y0
  const base = face * g * g
  const r00 = t.radii[base + y0 * g + x0]
  const r10 = t.radii[base + y0 * g + x1]
  const r01 = t.radii[base + y1 * g + x0]
  const r11 = t.radii[base + y1 * g + x1]
  return lerp(lerp(r00, r10, fx), lerp(r01, r11, fx), fy)
}

/** Decode a wire `terrain` message (u16 radii) into f64 (GDD: f64 internally). */
export function decodeTerrain(
  faceGrid: number,
  radiusMin: number,
  radiusMax: number,
  raw: Uint16Array,
): Terrain {
  const n = FACE_COUNT * faceGrid * faceGrid
  if (raw.length < n) {
    throw new Error(`terrain: ${raw.length} radii, expected ${n}`)
  }
  const radii = new Float64Array(n)
  const span = radiusMax - radiusMin
  for (let i = 0; i < n; i++) {
    radii[i] = radiusMin + (raw[i] / 65535) * span
  }
  return { faceGrid, radiusMin, radiusMax, radii }
}

/**
 * Unit direction of grid node (face, i, j). The render mesh and any
 * generator MUST place vertices here so the mesh sits exactly on the
 * collision surface (bilinear returns the exact lattice value at a node).
 */
export function latticeDir(face: number, i: number, j: number, g: number): Vec3 {
  const u = -1 + (2 * i) / (g - 1)
  const v = -1 + (2 * j) / (g - 1)
  let d: Vec3
  switch (face) {
    case 0: // +X
      d = { x: 1, y: u, z: v }
      break
    case 1: // −X
      d = { x: -1, y: u, z: v }
      break
    case 2: // +Y
      d = { x: u, y: 1, z: v }
      break
    case 3: // −Y
      d = { x: u, y: -1, z: v }
      break
    case 4: // +Z
      d = { x: u, y: v, z: 1 }
      break
    case 5: // −Z
    default:
      d = { x: u, y: v, z: -1 }
      break
  }
  return vec.norm(d)
}

/**
 * Build a terrain by evaluating `fill` at every lattice node. Used by the
 * headless tools and the mock world; the server owns real terrain
 * generation, this helper only exercises the shared grid layout.
 */
export function makeTerrain(
  faceGrid: number,
  radiusMin: number,
  radiusMax: number,
  fill: (d: Vec3) => number,
): Terrain {
  const g = faceGrid
  const radii = new Float64Array(FACE_COUNT * g * g)
  for (let f = 0; f < FACE_COUNT; f++) {
    for (let j = 0; j < g; j++) {
      for (let i = 0; i < g; i++) {
        const d = latticeDir(f, i, j, g)
        radii[f * g * g + j * g + i] = clamp(fill(d), radiusMin, radiusMax)
      }
    }
  }
  return { faceGrid, radiusMin, radiusMax, radii }
}

// ---------------------------------------------------------------------------
// Surface normal / slope — GDD "Terrain sampling" step 5, verbatim, so both
// ends get the same normal (hence the same slopeOK → grounded) from the
// same field.
// ---------------------------------------------------------------------------

/** GDD rule table: normal_eps = 2° — ~5 m arc at the surface, ~1.5 cells. */
const NORMAL_EPS = (RULES.normalEpsDeg * Math.PI) / 180

/** Outward surface normal at a direction (not radial on sloped ground). */
export function surfaceNormal(t: Terrain, d: Vec3): Vec3 {
  const up = vec.norm(d)
  // Deterministic, frame-free tangent basis (GDD, verbatim): +X projected
  // onto the tangent plane unless up is nearly ±X, then +Y.
  const k: Vec3 = Math.abs(up.x) <= 0.9 ? { x: 1, y: 0, z: 0 } : { x: 0, y: 1, z: 0 }
  const e1 = vec.norm(vec.sub(k, vec.scale(up, vec.dot(k, up))))
  const e2 = vec.cross(up, e1)
  const p0 = vec.scale(up, sampleRadius(t, up))
  const d1 = vec.norm(vec.add(up, vec.scale(e1, NORMAL_EPS)))
  const d2 = vec.norm(vec.add(up, vec.scale(e2, NORMAL_EPS)))
  const p1 = vec.scale(d1, sampleRadius(t, d1))
  const p2 = vec.scale(d2, sampleRadius(t, d2))
  const n = vec.cross(vec.sub(p1, p0), vec.sub(p2, p0))
  return vec.norm(vec.dot(n, up) < 0 ? vec.scale(n, -1) : n)
}

/** Angle (rad) between the surface normal and local up at `d`. */
export function slopeAngle(t: Terrain, d: Vec3): number {
  const n = surfaceNormal(t, d)
  const up = vec.norm(d)
  const c = clamp(vec.dot(n, up), -1, 1)
  return Math.acos(c)
}

/** GDD: walkable only while the slope angle is ≤ max_slope. */
export function slopeOK(t: Terrain, d: Vec3): boolean {
  return slopeAngle(t, d) <= RULES.maxSlopeRad
}

/**
 * Discrete Laplacian of the radius field (m/rad²) at `d`.
 * Positive → locally a basin (crater floor); negative → a ridge crest.
 * Used only for client-side prop scatter bias, never for collision.
 */
const CURV_STEP = 0.02 // rad — sub-cell, for the scatter bias

export function curvature(t: Terrain, d: Vec3): number {
  const up = vec.norm(d)
  const k: Vec3 = Math.abs(up.x) <= 0.9 ? { x: 1, y: 0, z: 0 } : { x: 0, y: 1, z: 0 }
  const e1 = vec.norm(vec.sub(k, vec.scale(up, vec.dot(k, up))))
  const e2 = vec.cross(up, e1)
  const r0 = sampleRadius(t, up)
  const d1 = vec.norm(vec.add(up, vec.scale(e1, CURV_STEP)))
  const d2 = vec.norm(vec.add(up, vec.scale(e2, CURV_STEP)))
  const d3 = vec.norm(vec.sub(up, vec.scale(e1, CURV_STEP)))
  const d4 = vec.norm(vec.sub(up, vec.scale(e2, CURV_STEP)))
  const sum =
    sampleRadius(t, d1) + sampleRadius(t, d2) + sampleRadius(t, d3) + sampleRadius(t, d4)
  return (sum - 4 * r0) / (CURV_STEP * CURV_STEP)
}
