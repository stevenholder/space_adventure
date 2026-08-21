/**
 * Terrain mesh — the render view of the server's six-array radius field.
 *
 * Both the mesh and the collision sampler live on the same cube-sphere
 * lattice (sim `latticeDir`, no v-flip), so the mesh sits EXACTLY on the
 * collision surface: a bilinear radius sample returns the stored lattice
 * value at a node, and the mesh vertices are those same nodes.
 *
 * Normals are the sim's own `surfaceNormal` (GDD formula) — the shading
 * agrees with the slope the collision code sees. Vertex colors ramp with
 * radius; no fog (art/README: there is no distance on a 150 m world).
 */
import * as THREE from 'three'
import type { Terrain } from '../sim/index.js'
import { latticeDir, surfaceNormal } from '../sim/index.js'
import { hash3 } from '../sim/rng.js'

/** Low → high stops: dusty green lowland → tan rock → grey → ice. */
const STOPS: [number, number, number][] = [
  [0.28, 0.38, 0.24],
  [0.42, 0.47, 0.31],
  [0.55, 0.52, 0.42],
  [0.62, 0.62, 0.64],
  [0.91, 0.93, 0.96],
]

function rampColor(t01: number, out: [number, number, number]): void {
  const x = Math.min(1, Math.max(0, t01)) * (STOPS.length - 1)
  const i = Math.min(STOPS.length - 2, Math.floor(x))
  const k = x - i
  const a = STOPS[i]
  const b = STOPS[i + 1]
  out[0] = a[0] + (b[0] - a[0]) * k
  out[1] = a[1] + (b[1] - a[1]) * k
  out[2] = a[2] + (b[2] - a[2]) * k
}

export function buildTerrain(t: Terrain): THREE.Mesh {
  const g = t.faceGrid
  const perFace = g * g
  const vcount = 6 * perFace
  const positions = new Float32Array(vcount * 3)
  const normals = new Float32Array(vcount * 3)
  const colors = new Float32Array(vcount * 3)
  const span = t.radiusMax - t.radiusMin || 1

  // Vertex pass: lattice node → world position, sim normal, ramp color.
  for (let f = 0; f < 6; f++) {
    for (let j = 0; j < g; j++) {
      for (let i = 0; i < g; i++) {
        const v = f * perFace + j * g + i
        const d = latticeDir(f, i, j, g)
        const r = t.radii[v]
        positions[v * 3] = d.x * r
        positions[v * 3 + 1] = d.y * r
        positions[v * 3 + 2] = d.z * r
        const n = surfaceNormal(t, d)
        normals[v * 3] = n.x
        normals[v * 3 + 1] = n.y
        const c: [number, number, number] = [0, 0, 0]
        rampColor((r - t.radiusMin) / span, c)
        // Deterministic per-node jitter so the low-poly surface does not
        // read as banded; ±4 %, same for every client.
        const jx = hash3(f, i, j) - 0.5
        colors[v * 3] = c[0] * (1 + 0.08 * jx)
        colors[v * 3 + 1] = c[1] * (1 + 0.08 * jx)
        colors[v * 3 + 2] = c[2] * (1 + 0.08 * jx)
      }
    }
  }

  // Seam weld: every cube edge is stored once per adjacent face, and the
  // two copies are computed independently (per-face f64 direction math,
  // per-face color jitter). A 1-ulp f32 difference between the copies of
  // a boundary node opens into a visible slit at grazing angles (QA
  // C7-F1). Copy the canonical face's exact f32 triples over the other
  // face's boundary row so both sides of every seam share bit-identical
  // vertices — no gap can open, and the mesh stays on the collision
  // surface (the canonical face's node IS the lattice node).
  //
  // Canonical face = the lower face index of each pair. Order matters: at
  // each of the 8 cube corners the Z face's node is written by both a
  // Y->Z and an X->Z copy; the X face (index 0/1) is the smallest index
  // in every corner trio, so the X->Z edges are welded last and the X
  // face wins the final write. X->Y edges run first, harmlessly.
  // Tuple: [faceA, fixA, valA, faceB, fixB, valB]; fix=0 -> i is fixed at
  // val (j varies, k <-> free axis), fix=1 -> j is fixed at val (i
  // varies). val 0 -> index 0, 1 -> g-1.
  const SEAMS: readonly (readonly [
    number, 0 | 1, 0 | 1, number, 0 | 1, 0 | 1,
  ])[] = [
    // X∩Y edges (k <-> z)
    [0, 0, 1, 2, 0, 1], // x=+1, y=+1
    [0, 0, 0, 3, 0, 1], // x=+1, y=−1
    [1, 0, 1, 2, 0, 0], // x=−1, y=+1
    [1, 0, 0, 3, 0, 0], // x=−1, y=−1
    // Y∩Z edges (k <-> x) — before the X∩Z edges, see note above
    [2, 1, 1, 4, 1, 1], // y=+1, z=+1
    [2, 1, 0, 5, 1, 1], // y=+1, z=−1
    [3, 1, 1, 4, 1, 0], // y=−1, z=+1
    [3, 1, 0, 5, 1, 0], // y=−1, z=−1
    // X∩Z edges (k <-> y); A fixes j, B fixes i
    [0, 1, 1, 4, 0, 1], // x=+1, z=+1
    [0, 1, 0, 5, 0, 1], // x=+1, z=−1
    [1, 1, 1, 4, 0, 0], // x=−1, z=+1
    [1, 1, 0, 5, 0, 0], // x=−1, z=−1
  ]
  for (const [fa, fixA, va, fb, fixB, vb] of SEAMS) {
    const ia = va ? g - 1 : 0
    const ib = vb ? g - 1 : 0
    for (let k = 0; k < g; k++) {
      const s3 = (fa * perFace + (fixA ? ia * g + k : k * g + ia)) * 3
      const t3 = (fb * perFace + (fixB ? ib * g + k : k * g + ib)) * 3
      positions[t3] = positions[s3]
      positions[t3 + 1] = positions[s3 + 1]
      positions[t3 + 2] = positions[s3 + 2]
      normals[t3] = normals[s3]
      normals[t3 + 1] = normals[s3 + 1]
      normals[t3 + 2] = normals[s3 + 2]
      colors[t3] = colors[s3]
      colors[t3 + 1] = colors[s3 + 1]
      colors[t3 + 2] = colors[s3 + 2]
    }
  }

  // Index pass: two triangles per cell. Winding is fixed by an outward
  // test (geometric normal must point away from the planet center) so it
  // is correct on every face regardless of per-face UV orientation.
  const cells = (g - 1) * (g - 1)
  const indices = new Uint32Array(6 * cells * 6)
  let p = 0
  for (let f = 0; f < 6; f++) {
    const base = f * perFace
    for (let j = 0; j < g - 1; j++) {
      for (let i = 0; i < g - 1; i++) {
        const a = base + j * g + i
        const b = a + 1
        const c = a + g
        const e = a + g + 1
        const tris = [a, b, e, e, c, a]
        for (let q = 0; q < 2; q++) {
          const i0 = tris[q * 3]
          const i1 = tris[q * 3 + 1]
          const i2 = tris[q * 3 + 2]
          // Geometric normal of (i0, i1, i2)
          const ax = positions[i0 * 3]
          const ay = positions[i0 * 3 + 1]
          const az = positions[i0 * 3 + 2]
          const ex = positions[i1 * 3] - ax
          const ey = positions[i1 * 3 + 1] - ay
          const ez = positions[i1 * 3 + 2] - az
          const fx = positions[i2 * 3] - ax
          const fy = positions[i2 * 3 + 1] - ay
          const fz = positions[i2 * 3 + 2] - az
          const nx = ey * fz - ez * fy
          const ny = ez * fx - ex * fz
          const nz = ex * fy - ey * fx
          // Outward = away from the planet center (the centroid is inside)
          const cx = (positions[i0 * 3] + positions[i1 * 3] + positions[i2 * 3]) / 3
          const cy = (positions[i0 * 3 + 1] + positions[i1 * 3 + 1] + positions[i2 * 3 + 1]) / 3
          const cz = (positions[i0 * 3 + 2] + positions[i1 * 3 + 2] + positions[i2 * 3 + 2]) / 3
          const flip = nx * cx + ny * cy + nz * cz < 0
          if (flip) {
            indices[p] = i0
            indices[p + 1] = i2
            indices[p + 2] = i1
          } else {
            indices[p] = i0
            indices[p + 1] = i1
            indices[p + 2] = i2
          }
          p += 3
        }
      }
    }
  }

  const geo = new THREE.BufferGeometry()
  geo.setAttribute('position', new THREE.BufferAttribute(positions, 3))
  geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3))
  geo.setAttribute('color', new THREE.BufferAttribute(colors, 3))
  geo.setIndex(new THREE.BufferAttribute(indices, 1))
  // Lambert, not Standard: per-vertex lighting. The PBR fragment shader
  // dominates fill cost on weak GPUs for a 49k-triangle planet, and
  // roughness-1 / metalness-0 PBR with no env map is visually identical
  // to plain diffuse.
  const mat = new THREE.MeshLambertMaterial({ vertexColors: true })
  const mesh = new THREE.Mesh(geo, mat)
  mesh.matrixAutoUpdate = false
  return mesh
}
