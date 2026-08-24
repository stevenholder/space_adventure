/**
 * Equipped weapon: a model parented to the character's `arm.r` node.
 *
 * Asset resolution reuses `AssetLib` exactly as `character.ts` and
 * `main.ts` already do (art/manifest.json, `/art/<file>`, cached by id) —
 * there is exactly one asset-loading path in this client, and this module
 * does not invent a second one. A `.glb` that is not in the manifest (or
 * fails to load) is normal, not an error: the caller falls back to a
 * flat-shaded placeholder box (art/README placeholder rule).
 *
 * The model carries two named nodes by convention (matching the low-poly
 * `arm.r` geometry in character.ts: a box spanning local Y 0 to -0.58 from
 * the shoulder pivot, -Z forward): `grip`, aligned to the hand so the model
 * sits in the fist rather than floating at the shoulder, and `muzzle`,
 * where the flash and (later) the tracer originate.
 *
 * First-person body (GDD): the local player already sees this model,
 * because it is parented into the same rig that renders for the local
 * body with only `head` hidden — there is no separate viewmodel, no second
 * camera, no FOV hack, so this file adds none. Fire feedback (recoil) is
 * applied to the weapon's own root rotation ONLY, never to the camera —
 * ARCHITECTURE: look is changed only by the player's mouse, a corrected
 * view is motion sickness.
 */
import * as THREE from 'three'
import type { AssetLib } from './assets.js'

/** Local-space hand anchor within `arm.r`, at the wrist end of the forearm
 *  box (see character.ts's placeholder geometry). Used when the model has
 *  no `grip` node to align against. */
const HAND_ANCHOR = new THREE.Vector3(0, -0.55, 0.02)

const RECOIL_KICK = 0.35 // rad, applied to the model's local pitch on fire()
const RECOIL_EASE = 18 // 1/s, exponential decay back to rest
const FLASH_SECONDS = 0.05 // muzzle flash visible duration

export interface WeaponHandle {
  /** Model root, parented under the character's `arm.r`. */
  root: THREE.Object3D
  kind: 'model' | 'placeholder'
  /** Trigger a recoil kick + muzzle flash. Call once per shot. */
  fire(): void
  /** Per-frame ease of the recoil/flash state. Call every render frame. */
  update(dt: number): void
}

/** First descendant (or self) named `name`, or null. */
function findNamed(root: THREE.Object3D, name: string): THREE.Object3D | null {
  if (root.name === name) return root
  let found: THREE.Object3D | null = null
  root.traverse((o) => {
    if (found === null && o.name === name) found = o
  })
  return found
}

/** Flat-shaded placeholder weapon: a barrel + stock, with grip/muzzle
 *  nodes at the same conceptual spots a real `.glb` would carry. */
function buildPlaceholder(): THREE.Object3D {
  const body = new THREE.MeshLambertMaterial({ color: 0x33363a, flatShading: true })
  const accent = new THREE.MeshLambertMaterial({ color: 0x8a8f94, flatShading: true })

  const root = new THREE.Group()
  root.name = 'weapon'

  const barrel = new THREE.Mesh(new THREE.BoxGeometry(0.06, 0.06, 0.5), body)
  barrel.position.set(0, 0, -0.28)
  root.add(barrel)

  const stock = new THREE.Mesh(new THREE.BoxGeometry(0.08, 0.12, 0.2), accent)
  stock.position.set(0, -0.02, 0.08)
  root.add(stock)

  const grip = new THREE.Object3D()
  grip.name = 'grip'
  grip.position.set(0, -0.04, 0.02)
  root.add(grip)

  const muzzle = new THREE.Object3D()
  muzzle.name = 'muzzle'
  muzzle.position.set(0, 0, -0.53)
  root.add(muzzle)

  return root
}

/** A small additive sprite parented to `muzzle`, hidden until fire(). */
function buildFlash(muzzle: THREE.Object3D): THREE.Sprite {
  const mat = new THREE.SpriteMaterial({
    color: 0xfff2b0,
    transparent: true,
    opacity: 0,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  })
  const sprite = new THREE.Sprite(mat)
  sprite.scale.setScalar(0.18)
  sprite.visible = false
  muzzle.add(sprite)
  return sprite
}

/**
 * Load `assetId` through the shared `AssetLib` and parent the result (or a
 * placeholder box, if the `.glb` is absent) to `armR`, aligning the
 * model's `grip` node to the hand.
 */
export async function attachWeapon(
  lib: AssetLib,
  armR: THREE.Object3D,
  assetId: string,
): Promise<WeaponHandle> {
  const loaded = await lib.loadGltf(assetId)
  // Each equipped body needs its own node hierarchy (a THREE object has a
  // single parent) — clone, same as character.ts does for `char.player`.
  const root = loaded ? loaded.clone(true) : buildPlaceholder()
  const kind: WeaponHandle['kind'] = loaded ? 'model' : 'placeholder'
  root.name = 'weapon'

  armR.add(root)
  const grip = findNamed(root, 'grip')
  if (grip) {
    // Shift the root so the grip node lands on the hand anchor; the model
    // shares arm.r's -Z-forward/+Y-up convention, so no rotation is needed.
    root.position.copy(HAND_ANCHOR).sub(grip.position)
  } else {
    root.position.copy(HAND_ANCHOR)
  }

  const muzzle = findNamed(root, 'muzzle')
  const flash = muzzle ? buildFlash(muzzle) : null

  const baseRotX = root.rotation.x
  let recoil = 0
  let flashLeft = 0

  const handle: WeaponHandle = {
    root,
    kind,
    fire() {
      recoil = RECOIL_KICK
      flashLeft = FLASH_SECONDS
    },
    update(dt: number) {
      if (recoil > 1e-4) {
        recoil *= Math.exp(-RECOIL_EASE * dt)
      } else {
        recoil = 0
      }
      root.rotation.x = baseRotX - recoil

      if (flash) {
        flashLeft = Math.max(0, flashLeft - dt)
        flash.visible = flashLeft > 0
        flash.material.opacity = flashLeft / FLASH_SECONDS
      }
    },
  }
  return handle
}

/** Detach and remove the weapon model from its parent. Geometry/materials
 *  are owned by the AssetLib cache (or freshly built for a placeholder)
 *  and are not disposed here, matching character.ts's clone-sharing. */
export function detachWeapon(weapon: WeaponHandle): void {
  weapon.root.removeFromParent()
}
