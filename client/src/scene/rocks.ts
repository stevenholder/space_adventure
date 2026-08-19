/**
 * Rock scatter (GDD "Rocks"): ~400 rocks, 0.3–1.5 m, three variants
 * (`prop.rock.a/b/c`), none on slopes above `rock_slope_max` (35°),
 * denser in crater floors and along ridges.
 *
 * Client-side decoration only — M1 rocks have no collision. Every client
 * runs this exact code with the server's `world_seed`, so all clients
 * agree without the server ever talking about props (PROTOCOL: world_seed
 * seeds client-side decoration only).
 *
 * Density bias uses the sim's discrete Laplacian `curvature`:
 * positive (radius concave-down → locally LOWER than neighbors) = basin /
 * crater floor; negative = ridge crest.
 */
import * as THREE from 'three'
import type { Terrain } from '../sim/index.js'
import { curvature, sampleRadius, slopeAngle, vec } from '../sim/index.js'
import { mulberry32 } from '../sim/rng.js'

/** GDD: rocks skip slopes above 35°. */
const ROCK_SLOPE_MAX = (35 * Math.PI) / 180
const TARGET_COUNT = 400
const MIN_SIZE = 0.3
const MAX_SIZE = 1.5
/** Curvature (discrete Laplacian, m/rad²) thresholds for the density bias. */
const BASIN_T = 20
const RIDGE_T = -20
/** Accept probability by terrain class — flat is sparse, features are dense. */
const P_FLAT = 0.1
const P_FEATURE = 0.9
const MAX_TRIES = TARGET_COUNT * 60

export interface RockPlacement {
  pos: [number, number, number]
  quat: [number, number, number, number]
  scale: [number, number, number]
  variant: number
}

/**
 * Deterministic scatter. Pure in (terrain, seed) — the same inputs give the
 * same placements on every client. Returns placements only; geometry is
 * applied by the caller (procedural fallback first, GLB when art arrives).
 */
export function scatterRocks(t: Terrain, seed: number): RockPlacement[] {
  const rng = mulberry32((seed ^ 0x5eed) >>> 0)
  const out: RockPlacement[] = []
  let tries = 0
  while (out.length < TARGET_COUNT && tries++ < MAX_TRIES) {
    // Uniform random direction on the sphere.
    const z = rng() * 2 - 1
    const th = rng() * Math.PI * 2
    const r = Math.sqrt(1 - z * z)
    const d = vec.norm({ x: r * Math.cos(th), y: z, z: r * Math.sin(th) })
    // GDD: skip slopes above rock_slope_max.
    if (slopeAngle(t, d) > ROCK_SLOPE_MAX) continue
    // Denser in crater floors (basin) and along ridges, sparse elsewhere.
    const c = curvature(t, d)
    const p = c > BASIN_T || c < RIDGE_T ? P_FEATURE : P_FLAT
    if (rng() > p) continue

    const size = MIN_SIZE + rng() * (MAX_SIZE - MIN_SIZE)
    const surf = sampleRadius(t, d)
    const pos: [number, number, number] = [
      d.x * (surf + size * 0.05), // sink slightly — seated, not floating
      d.y * (surf + size * 0.05),
      d.z * (surf + size * 0.05),
    ]
    // Radial orientation: local +Y along the surface normal (≈ radial),
    // random spin about it. Kept under rock_size so walking through a rock
    // is a brush, not a wall.
    const qy = new THREE.Quaternion().setFromUnitVectors(
      new THREE.Vector3(0, 1, 0),
      new THREE.Vector3(d.x, d.y, d.z),
    )
    const spin = new THREE.Quaternion().setFromAxisAngle(
      new THREE.Vector3(d.x, d.y, d.z),
      rng() * Math.PI * 2,
    )
    const q = spin.multiply(qy)
    out.push({
      pos,
      quat: [q.x, q.y, q.z, q.w],
      scale: [
        size * (0.8 + rng() * 0.4),
        size * (0.55 + rng() * 0.5),
        size * (0.8 + rng() * 0.4),
      ],
      variant: Math.floor(rng() * 3),
    })
  }
  return out
}


/**
 * Build the InstancedMesh group from placements. `geoms` must have one
 * entry per variant (0..2); the caller supplies procedural fallbacks or
 * the GLB geometry from `prop.rock.a/b/c`.
 */
export function buildRockGroup(
  placements: RockPlacement[],
  geoms: THREE.BufferGeometry[],
): THREE.Group {
  const group = new THREE.Group()
  const counts = [0, 0, 0]
  for (const p of placements) counts[p.variant]++
  const mats = [0x7d7f85, 0x8a8275, 0x6f7480].map(
    (c) =>
      new THREE.MeshStandardMaterial({
        color: c,
        roughness: 1,
        metalness: 0,
        flatShading: true,
      }),
  )
  const m4 = new THREE.Matrix4()
  const q = new THREE.Quaternion()
  const v3 = new THREE.Vector3()
  const s3 = new THREE.Vector3()
  for (let v = 0; v < 3; v++) {
    if (counts[v] === 0) continue
    const mesh = new THREE.InstancedMesh(geoms[v], mats[v], counts[v])
    let k = 0
    for (const p of placements) {
      if (p.variant !== v) continue
      q.set(p.quat[0], p.quat[1], p.quat[2], p.quat[3])
      v3.set(p.pos[0], p.pos[1], p.pos[2])
      s3.set(p.scale[0], p.scale[1], p.scale[2])
      m4.compose(v3, q, s3)
      mesh.setMatrixAt(k++, m4)
    }
    mesh.instanceMatrix.needsUpdate = true
    group.add(mesh)
  }
  return group
}
