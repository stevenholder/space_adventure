/**
 * Quaternion helpers — plain math, shared by the wire boundary (entity
 * orientation) and the renderer. No Three.js dependency so net/ and
 * tools/ can use them too.
 *
 * Convention: Hamilton quaternions, (x, y, z, w) in the wire order
 * (PROTOCOL entity `quat[4]` is f32 x|y|z|w).
 * Wire convention (GDD "Snapshot encoding"): the snapshot `quat` takes the
 * entity's local frame (+X right, +Y up, +Z forward) to
 * (right = up x facing, up, facing). The art models are authored -Z forward
 * (art/README node contract), so the scene layer composes `QUAT_FLIP_Y`
 * when applying a wire quat to a model root.
 */
import type { Vec3 } from '../sim/index.js'
import { vec } from '../sim/index.js'

export interface Quat {
  x: number
  y: number
  z: number
  w: number
}

export const QUAT_ID: Quat = { x: 0, y: 0, z: 0, w: 1 }

/**
 * 180-degree rotation about the local Y axis: the bridge between the GDD
 * wire frame (+Z forward) and the authored art frame (-Z forward).
 */
export const QUAT_FLIP_Y: Quat = { x: 0, y: 1, z: 0, w: 0 }

/** Normalize a quaternion in place. */
export function quatNorm(q: Quat): Quat {
  const l = Math.hypot(q.x, q.y, q.z, q.w)
  if (l < 1e-12) {
    q.x = 0
    q.y = 0
    q.z = 0
    q.w = 1
    return q
  }
  const inv = 1 / l
  q.x *= inv
  q.y *= inv
  q.z *= inv
  q.w *= inv
  return q
}

export function quatMul(a: Quat, b: Quat): Quat {
  return {
    x: a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
    y: a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
    z: a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
    w: a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
  }
}

export function quatFromAxisAngle(axis: Vec3, angle: number): Quat {
  const a = vec.norm(axis)
  const s = Math.sin(angle / 2)
  return { x: a.x * s, y: a.y * s, z: a.z * s, w: Math.cos(angle / 2) }
}

/**
 * Quaternion whose -Z axis points along `forward` and whose +Y axis points
 * along `up` (up must be roughly perpendicular to forward). This is the
 * "look rotation" used to build a body orientation from two vectors.
 */
export function quatFromForwardUp(forward: Vec3, up: Vec3): Quat {
  const f = vec.norm(forward)
  const u = vec.norm(vec.sub(up, vec.scale(f, vec.dot(up, f))))
  const r = vec.cross(f, u)
  // Rotation matrix R (columns): R·X=r, R·Y=u, R·(−Z)=f.
  return mat3ToQuat(r.x, u.x, -f.x, r.y, u.y, -f.y, r.z, u.z, -f.z)
}

/**
 * Quaternion whose +X/+Y/+Z axes point along (up x forward, up, forward) —
 * the GDD snapshot frame. This is the wire quat a server (or the mock
 * authority) stamps on an entity; compose with `QUAT_FLIP_Y` before
 * applying it to an authored (-Z forward) model root.
 */
export function quatFromBasis(up: Vec3, forward: Vec3): Quat {
  const f = vec.norm(forward)
  const u = vec.norm(vec.sub(up, vec.scale(f, vec.dot(up, f))))
  const r = vec.cross(u, f)
  return mat3ToQuat(r.x, u.x, f.x, r.y, u.y, f.y, r.z, u.z, f.z)
}

/** Shepperd's matrix-to-quaternion for a proper row-major 3x3. */
function mat3ToQuat(
  m00: number,
  m01: number,
  m02: number,
  m10: number,
  m11: number,
  m12: number,
  m20: number,
  m21: number,
  m22: number,
): Quat {
  const trace = m00 + m11 + m22
  let x: number
  let y: number
  let z: number
  let w: number
  if (trace > 0) {
    const s = Math.sqrt(trace + 1) * 2
    w = 0.25 * s
    x = (m21 - m12) / s
    y = (m02 - m20) / s
    z = (m10 - m01) / s
  } else if (m00 > m11 && m00 > m22) {
    const s = Math.sqrt(1 + m00 - m11 - m22) * 2
    w = (m21 - m12) / s
    x = 0.25 * s
    y = (m01 + m10) / s
    z = (m02 + m20) / s
  } else if (m11 > m22) {
    const s = Math.sqrt(1 + m11 - m00 - m22) * 2
    w = (m02 - m20) / s
    x = (m01 + m10) / s
    y = 0.25 * s
    z = (m12 + m21) / s
  } else {
    const s = Math.sqrt(1 + m22 - m00 - m11) * 2
    w = (m10 - m01) / s
    x = (m02 + m20) / s
    y = (m12 + m21) / s
    z = 0.25 * s
  }
  return quatNorm({ x, y, z, w })
}

/** Rotate a vector by a unit quaternion. */
export function quatRotate(q: Quat, v: Vec3): Vec3 {
  const qv: Vec3 = { x: q.x, y: q.y, z: q.z }
  const t = vec.scale(vec.cross(qv, v), 2)
  return vec.add(vec.add(v, vec.scale(t, q.w)), vec.cross(qv, t))
}

/** slerp with a linear fallback near colinear quaternions. */
export function quatSlerp(a: Quat, b: Quat, t: number): Quat {
  let d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w
  let bb = b
  if (d < 0) {
    d = -d
    bb = { x: -b.x, y: -b.y, z: -b.z, w: -b.w }
  }
  if (d > 0.9995) {
    return quatNorm({
      x: a.x + (bb.x - a.x) * t,
      y: a.y + (bb.y - a.y) * t,
      z: a.z + (bb.z - a.z) * t,
      w: a.w + (bb.w - a.w) * t,
    })
  }
  const theta = Math.acos(Math.min(1, d))
  const sa = Math.sin(theta)
  const wa = Math.sin((1 - t) * theta) / sa
  const wb = Math.sin(t * theta) / sa
  return {
    x: a.x * wa + bb.x * wb,
    y: a.y * wa + bb.y * wb,
    z: a.z * wa + bb.z * wb,
    w: a.w * wa + bb.w * wb,
  }
}
