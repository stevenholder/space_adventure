/**
 * Character rig: named-node extraction from `char.player` (art/README node
 * contract: eye/head/torso/arm.l/arm.r/leg.l/arm.r — segmented, NOT rigged)
 * plus a procedural walk cycle (GDD: rotate the limb nodes about their own
 * pivots with a sine driven by horizontal speed; arms counter-swing; ease
 * to rest when stopped). No skeletal animation, no .glb clips.
 *
 * The same rig drives the local and remote bodies alike. For the local
 * player only, `head` is hidden — the camera sits at `eye`, a sibling of
 * `head`, so hiding the head never hides the camera.
 *
 * Placeholder: a flat-shaded box rig with the same node names and pivots,
 * so switching to the GLB (or losing it) never changes rig behavior.
 */
import * as THREE from 'three'
import { RULES } from '../sim/index.js'

export interface CharRig {
  /** Model root (feet at the local origin, -Z forward, +Y up). */
  root: THREE.Object3D
  head: THREE.Object3D | null
  eye: THREE.Object3D | null
  armL: THREE.Object3D | null
  armR: THREE.Object3D | null
  legL: THREE.Object3D | null
  legR: THREE.Object3D | null
  kind: 'model' | 'placeholder'
  /**
   * One animation step. `speed` = horizontal m/s, `dt` seconds.
   * Mutates limb node rotations; idempotent to call every frame.
   */
  animate(dt: number, speed: number): void
}

const WALK_FREQ = 1.9 // rad/s of phase per m/s (≈ 2.7 steps/s at walk speed)
const LEG_AMP = 0.55 // rad
const ARM_AMP = 0.4 // rad — counter-swing
const EASE_RATE = 6 // 1/s — how fast the swing eases to/from rest

interface WalkState {
  phase: number
  swing: number // 0 = rest, 1 = full swing
}

function makeAnimate(st: WalkState): (dt: number, speed: number) => number {
  return (dt: number, speed: number) => {
    const target = Math.min(1, speed / (RULES.walkSpeed * 0.6))
    st.swing += (target - st.swing) * Math.min(1, dt * EASE_RATE)
    st.phase += dt * WALK_FREQ * speed * st.swing
    const s = Math.sin(st.phase) * st.swing
    return s
  }
}

function applyCycle(
  rig: CharRig,
  s: number,
): void {
  // Legs: left/right out of phase. Arms: counter-swing (left arm with
  // right leg). Rotations about the limb pivots (shoulder/hip), +X swing.
  if (rig.legL) rig.legL.rotation.x = s * LEG_AMP
  if (rig.legR) rig.legR.rotation.x = -s * LEG_AMP
  if (rig.armL) rig.armL.rotation.x = -s * ARM_AMP
  if (rig.armR) rig.armR.rotation.x = s * ARM_AMP
}

/** Build the placeholder box rig (same node names/pivots as the GLB). */
function buildPlaceholder(): CharRig {
  const suit = new THREE.MeshStandardMaterial({ color: 0xd97a2b, roughness: 1, flatShading: true })
  const dark = new THREE.MeshStandardMaterial({ color: 0x2a2926, roughness: 1, flatShading: true })
  const pack = new THREE.MeshStandardMaterial({ color: 0x2f8f8f, roughness: 1, flatShading: true })
  const visor = new THREE.MeshStandardMaterial({ color: 0x0e3438, roughness: 0.6 })

  const root = new THREE.Group()
  const legL = new THREE.Group()
  legL.position.set(-0.15, 0.85, 0)
  legL.name = 'leg.l'
  legL.add(new THREE.Mesh(new THREE.BoxGeometry(0.16, 0.8, 0.18), dark).translateY(-0.42))
  legL.add(new THREE.Mesh(new THREE.BoxGeometry(0.18, 0.12, 0.3), dark).translateY(-0.06).translateZ(-0.05))
  const legR = legL.clone()
  legR.position.x = 0.15
  legR.name = 'leg.r'

  const torso = new THREE.Group()
  torso.position.set(0, 1.0, 0)
  torso.name = 'torso'
  torso.add(new THREE.Mesh(new THREE.BoxGeometry(0.45, 0.72, 0.3), suit).translateY(0.36))
  torso.add(new THREE.Mesh(new THREE.BoxGeometry(0.36, 0.4, 0.18), pack).translateY(0.34).translateZ(0.22))

  const head = new THREE.Group()
  head.position.set(0, 0.66, 0)
  head.name = 'head'
  head.add(new THREE.Mesh(new THREE.BoxGeometry(0.28, 0.3, 0.28), suit).translateY(0.12))
  head.add(new THREE.Mesh(new THREE.BoxGeometry(0.2, 0.1, 0.04), visor).translateY(0.14).translateZ(-0.14))
  torso.add(head)

  const eye = new THREE.Object3D()
  eye.position.set(0, 0.7, 0) // 1.0 + 0.7 = 1.7 m = GDD eye_height
  eye.name = 'eye'
  torso.add(eye)

  const armL = new THREE.Group()
  armL.position.set(-0.3, 0.7, 0)
  armL.name = 'arm.l'
  armL.add(new THREE.Mesh(new THREE.BoxGeometry(0.13, 0.58, 0.15), suit).translateY(-0.29))
  const armR = armL.clone()
  armR.position.x = 0.3
  armR.name = 'arm.r'
  torso.add(armL, armR)
  root.add(legL, legR, torso)

  const st: WalkState = { phase: 0, swing: 0 }
  const step = makeAnimate(st)
  const rig: CharRig = {
    root,
    head,
    eye,
    armL,
    armR,
    legL,
    legR,
    kind: 'placeholder',
    animate(dt: number, speed: number) {
      applyCycle(rig, step(dt, speed))
    },
  }
  return rig
}

/** Extract a rig from a loaded `char.player` gltf scene. */
function buildFromGltf(scene: THREE.Object3D): CharRig {
  const nodes: Record<string, THREE.Object3D> = {}
  scene.traverse((o) => {
    if (o.name && !nodes[o.name]) nodes[o.name] = o
  })
  const rig: CharRig = {
    root: scene,
    head: nodes['head'] ?? null,
    eye: nodes['eye'] ?? null,
    armL: nodes['arm.l'] ?? null,
    armR: nodes['arm.r'] ?? null,
    legL: nodes['leg.l'] ?? null,
    legR: nodes['leg.r'] ?? null,
    kind: 'model',
    animate() {},
  }
  const st: WalkState = { phase: 0, swing: 0 }
  const step = makeAnimate(st)
  rig.animate = (dt: number, speed: number) => {
    applyCycle(rig, step(dt, speed))
  }
  return rig
}

export function makePlaceholderRig(): CharRig {
  return buildPlaceholder()
}

export function makeRigFromGltf(scene: THREE.Object3D): CharRig {
  return buildFromGltf(scene)
}

/** True when the rig actually has the limb nodes it animates. */
export function rigIsComplete(rig: CharRig): boolean {
  return Boolean(rig.armL && rig.armR && rig.legL && rig.legR)
}
