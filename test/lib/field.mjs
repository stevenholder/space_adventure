/**
 * Terrain field sampling — independent QA implementation of the GDD
 * "Terrain sampling" section (binding on both ends) and PROTOCOL "Terrain
 * sampling (pinned)". Same formulas, written from the spec.
 */

/**
 * @typedef {Object} Field
 * @property {number} faceGrid
 * @property {number} radiusMin
 * @property {number} radiusMax
 * @property {Uint16Array} radii  // wire order: face·grid² + row·grid + col
 */

// Face order: +X, −X, +Y, −Y, +Z, −Z
const FACE_AXIS = [0, 0, 1, 1, 2, 2]
const FACE_SIGN = [1, -1, 1, -1, 1, -1]

/** Face index for a normalized direction (largest-magnitude component; ties → earlier face). */
export function faceOf(d) {
  const ax = [Math.abs(d[0]), Math.abs(d[1]), Math.abs(d[2])]
  let face = 0
  for (let f = 1; f < 6; f++) {
    // later face wins only on STRICTLY larger magnitude (tie keeps earlier)
    if (ax[FACE_AXIS[f]] > ax[FACE_AXIS[face]]) face = f
  }
  return face
}

/** (u, v) in [−1, 1] for a normalized direction, per-face axis assignment. */
export function faceUV(d, face) {
  const a = FACE_AXIS[face]
  const s = FACE_SIGN[face]
  const dom = d[a] * s // dominant magnitude with sign
  const other = [0, 1, 2].filter((i) => i !== a)
  const u = d[other[0]] / dom
  const v = d[other[1]] / dom
  return [u, v]
}

/** Bilinear radius sample (GDD step 3). d need not be normalized. */
export function sampleRadius(f, d) {
  const n = Math.hypot(d[0], d[1], d[2])
  const dir = [d[0] / n, d[1] / n, d[2] / n]
  const face = faceOf(dir)
  const [u, v] = faceUV(dir, face)
  const g = f.faceGrid
  const colF = ((u + 1) / 2) * (g - 1)
  const rowF = ((v + 1) / 2) * (g - 1)
  const c0 = Math.min(g - 2, Math.floor(colF))
  const r0 = Math.min(g - 2, Math.floor(rowF))
  const tc = colF - c0
  const tr = rowF - r0
  const base = face * g * g
  const r = (r, c) => f.radii[base + r * g + c]
  const a = r(r0, c0) * (1 - tc) + r(r0, c0 + 1) * tc
  const b = r(r0 + 1, c0) * (1 - tc) + r(r0 + 1, c0 + 1) * tc
  // decode: radius_min + code/65535·(radius_max − radius_min)
  const dec = (code) => f.radiusMin + (code / 65535) * (f.radiusMax - f.radiusMin)
  return dec(a * (1 - tr) + b * tr)
}

/** Surface normal via the GDD finite-difference formula (normal_eps = 2° = 0.0349066 rad, per rule table). */
export function surfaceNormal(f, up, normalEps = (2 * Math.PI) / 180) {
  const u = [up[0], up[1], up[2]]
  // k ← (1,0,0) if |dot(up,(1,0,0))| ≤ 0.9 else (0,1,0)
  const k = Math.abs(u[0]) <= 0.9 ? [1, 0, 0] : [0, 1, 0]
  const du = u[0] * k[0] + u[1] * k[1] + u[2] * k[2]
  let e1 = [k[0] - u[0] * du, k[1] - u[1] * du, k[2] - u[2] * du]
  const n1 = Math.hypot(...e1)
  e1 = [e1[0] / n1, e1[1] / n1, e1[2] / n1]
  const e2 = [
    u[1] * e1[2] - u[2] * e1[1],
    u[2] * e1[0] - u[0] * e1[2],
    u[0] * e1[1] - u[1] * e1[0],
  ]
  const P0 = [u[0] * sampleRadius(f, u), u[1] * sampleRadius(f, u), u[2] * sampleRadius(f, u)]
  const d1 = norm3([u[0] + e1[0] * normalEps, u[1] + e1[1] * normalEps, u[2] + e1[2] * normalEps])
  const d2 = norm3([u[0] + e2[0] * normalEps, u[1] + e2[1] * normalEps, u[2] + e2[2] * normalEps])
  const r1 = sampleRadius(f, d1)
  const r2 = sampleRadius(f, d2)
  const P1 = [d1[0] * r1, d1[1] * r1, d1[2] * r1]
  const P2 = [d2[0] * r2, d2[1] * r2, d2[2] * r2]
  const v1 = [P1[0] - P0[0], P1[1] - P0[1], P1[2] - P0[2]]
  const v2 = [P2[0] - P0[0], P2[1] - P0[1], P2[2] - P0[2]]
  let n = [
    v1[1] * v2[2] - v1[2] * v2[1],
    v1[2] * v2[0] - v1[0] * v2[2],
    v1[0] * v2[1] - v1[1] * v2[0],
  ]
  if (n[0] * u[0] + n[1] * u[1] + n[2] * u[2] < 0) n = [-n[0], -n[1], -n[2]]
  return norm3(n)
}

/** Slope in degrees: angle between surface normal and local up. */
export function slopeDeg(f, up, normalEps = 0.0333) {
  const n = surfaceNormal(f, up, normalEps)
  const d = Math.max(-1, Math.min(1, n[0] * up[0] + n[1] * up[1] + n[2] * up[2]))
  return (Math.acos(d) * 180) / Math.PI
}

export function norm3(v) {
  const n = Math.hypot(v[0], v[1], v[2])
  return n < 1e-12 ? [0, 0, 0] : [v[0] / n, v[1] / n, v[2] / n]
}

/**
 * Load a world file as emitted by capture-terrain.mjs:
 * { face_grid, radius_min, radius_max, radii: number[] (u16 wire order) }
 */
export function loadField(json) {
  return {
    faceGrid: json.face_grid,
    radiusMin: json.radius_min,
    radiusMax: json.radius_max,
    radii: Uint16Array.from(json.radii),
  }
}
