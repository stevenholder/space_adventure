/**
 * Sky: a deep-space dome with a static starfield. No distance fog — on a
 * 150 m world there is no distance to fog out (art/README).
 *
 * The dome is centered on the ORIGIN (the planet center), not the camera:
 * the player walks on the planet surface, so an origin-centered sky keeps
 * stars fixed to the world as the player moves across the surface.
 */
import * as THREE from 'three'
import { mulberry32 } from '../sim/rng.js'

const DOME_RADIUS = 420 // well inside the 500 m far clip
const STAR_COUNT = 900
const STAR_RADIUS = 400

export function buildSky(): THREE.Group {
  const group = new THREE.Group()

  const dome = new THREE.Mesh(
    new THREE.SphereGeometry(DOME_RADIUS, 24, 16),
    new THREE.MeshBasicMaterial({ color: 0x04060d, side: THREE.BackSide, fog: false }),
  )
  group.add(dome)

  const rng = mulberry32(0xc0ffee)
  const pos = new Float32Array(STAR_COUNT * 3)
  for (let i = 0; i < STAR_COUNT; i++) {
    // Uniform on the sphere.
    const z = rng() * 2 - 1
    const th = rng() * Math.PI * 2
    const r = Math.sqrt(1 - z * z)
    pos[i * 3] = STAR_RADIUS * r * Math.cos(th)
    pos[i * 3 + 1] = STAR_RADIUS * z
    pos[i * 3 + 2] = STAR_RADIUS * r * Math.sin(th)
  }
  const geo = new THREE.BufferGeometry()
  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3))
  const stars = new THREE.Points(
    geo,
    new THREE.PointsMaterial({
      color: 0xdfe8ff,
      size: 1.5,
      sizeAttenuation: false,
      fog: false,
    }),
  )
  group.add(stars)
  return group
}
