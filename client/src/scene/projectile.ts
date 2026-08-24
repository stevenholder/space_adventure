/**
 * Phase 3 visuals: projectiles (`ENTITY_TYPE_PROJECTILE`) and loot drops
 * (`ENTITY_TYPE_LOOT`).
 *
 * Same asset discipline as world.ts: try the manifest asset via `AssetLib`,
 * fall back to flat-shaded placeholder geometry when the id is unknown or
 * the `.glb` is missing (art/README placeholder rule — neither `proj.bolt`
 * nor `loot.pickup` exists yet, so the placeholder path is what actually
 * runs). One `AssetLib` is shared by every instance of both classes here
 * (module-scoped, lazily created): art/manifest.json is a cache-by-id
 * contract, and projectiles spawn/despawn several times a second in a
 * firefight, so a fresh loader (and manifest fetch) per instance would be
 * wasteful where world.ts's "second instance is cheap" reasoning does not
 * apply.
 *
 * Projectiles: server sends ~45 m/s at 20 Hz snapshots (~2.25 m/tick), so a
 * point/sphere is invisible between frames. Rendered as a short streak
 * oriented along the entity's quaternion (the server already orients
 * projectiles along their travel direction — PROTOCOL `quat`).
 *
 * Loot: idles with a slow spin + bob so a small low-poly pickup doesn't
 * vanish into low-poly ground. No text, no billboard (task brief). The
 * anchor (added to the scene) carries the entity's real position/quat;
 * a child node under it carries the local spin/bob offset, advanced from
 * `setTransform`'s own elapsed-time delta since it is called every frame
 * by the render loop, same as world.ts's `frameRemotes`.
 *
 * `dispose()` releases only geometry/material this module created itself
 * (the placeholder path); a swapped-in GLTF clone is AssetLib-cached and
 * shared across every clone from the same scene, so it is removed from
 * the scene graph but never disposed here — world.ts's `disposeGroup`
 * (geometry-only, no shared-material teardown) follows the same rule.
 */
import * as THREE from 'three'
import { AssetLib } from './assets.js'

const PROJECTILE_ASSET_ID = 'proj.bolt'
const LOOT_ASSET_ID = 'loot.pickup'

const STREAK_LEN = 1.4 // m, along -Z (art-frame forward)
const STREAK_WIDTH = 0.05 // m
const STREAK_COLOR = 0xfff2a0

const LOOT_SIZE = 0.28 // m
const LOOT_COLOR = 0x3fb6a8
const LOOT_SPIN_RATE = 1.6 // rad/s
const LOOT_BOB_RATE = 2.2 // rad/s
const LOOT_BOB_AMP = 0.08 // m

let sharedAssets: AssetLib | null = null
let sharedManifestReady: Promise<void> | null = null

/** Lazily create the one AssetLib these two classes share. */
function assetLib(): { assets: AssetLib; ready: Promise<void> } {
  if (!sharedAssets) {
    sharedAssets = new AssetLib()
    sharedManifestReady = sharedAssets.loadManifest()
  }
  return { assets: sharedAssets, ready: sharedManifestReady! }
}

/** Own-created placeholder geometry/material — disposed on teardown; a
 *  swapped-in GLTF clone carries no entry here (AssetLib-owned). */
interface Owned {
  geometry: THREE.BufferGeometry
  material: THREE.Material
}

export class ProjectileVisual {
  private scene: THREE.Scene
  private root: THREE.Object3D
  private owned: Owned | null
  private disposed = false

  constructor(scene: THREE.Scene) {
    this.scene = scene
    const ph = buildStreakPlaceholder()
    this.root = ph.obj
    this.owned = ph.owned
    scene.add(this.root)
    void this.loadReal()
  }

  private async loadReal(): Promise<void> {
    const { assets, ready } = assetLib()
    await ready
    const gltf = await assets.loadGltf(PROJECTILE_ASSET_ID)
    if (!gltf || this.disposed) return
    const next = gltf.clone(true)
    this.scene.remove(this.root)
    disposeOwned(this.root, this.owned)
    this.root = next
    this.owned = null
    this.scene.add(this.root)
  }

  /** Position + orient along the entity's quaternion (server-authored
   *  travel direction). No allocation — called every projectile, every
   *  frame. */
  setTransform(pos: THREE.Vector3, quat: THREE.Quaternion): void {
    this.root.position.copy(pos)
    this.root.quaternion.copy(quat)
  }

  dispose(): void {
    this.disposed = true
    this.scene.remove(this.root)
    disposeOwned(this.root, this.owned)
  }
}

export class LootVisual {
  private scene: THREE.Scene
  private anchor: THREE.Group
  private visual: THREE.Object3D
  private owned: Owned | null
  private disposed = false
  private lastMs: number | null = null
  private phase = 0 // rad, accumulated spin/bob angle

  constructor(scene: THREE.Scene) {
    this.scene = scene
    this.anchor = new THREE.Group()
    const ph = buildLootPlaceholder()
    this.visual = ph.obj
    this.owned = ph.owned
    this.anchor.add(this.visual)
    scene.add(this.anchor)
    void this.loadReal()
  }

  private async loadReal(): Promise<void> {
    const { assets, ready } = assetLib()
    await ready
    const gltf = await assets.loadGltf(LOOT_ASSET_ID)
    if (!gltf || this.disposed) return
    const next = gltf.clone(true)
    this.anchor.remove(this.visual)
    disposeOwned(this.visual, this.owned)
    this.visual = next
    this.owned = null
    this.anchor.add(this.visual)
  }

  /** Position + orient the anchor at the entity's transform, and advance
   *  the local idle spin/bob by the elapsed time since the last call. No
   *  allocation — called every loot drop, every frame. */
  setTransform(pos: THREE.Vector3, quat: THREE.Quaternion): void {
    this.anchor.position.copy(pos)
    this.anchor.quaternion.copy(quat)
    const nowMs = performance.now()
    const dt = this.lastMs === null ? 0 : Math.min(0.25, (nowMs - this.lastMs) / 1000)
    this.lastMs = nowMs
    this.phase += dt
    this.visual.rotation.y = this.phase * LOOT_SPIN_RATE
    this.visual.position.y = Math.sin(this.phase * LOOT_BOB_RATE) * LOOT_BOB_AMP
  }

  dispose(): void {
    this.disposed = true
    this.scene.remove(this.anchor)
    disposeOwned(this.visual, this.owned)
  }
}

/** Flat-shaded placeholder projectile: a short bright streak, long axis
 *  along -Z (art-frame forward) so it visibly reads as motion at range. */
function buildStreakPlaceholder(): { obj: THREE.Object3D; owned: Owned } {
  const geometry = new THREE.BoxGeometry(STREAK_WIDTH, STREAK_WIDTH, STREAK_LEN)
  const material = new THREE.MeshBasicMaterial({ color: STREAK_COLOR })
  const mesh = new THREE.Mesh(geometry, material)
  mesh.name = 'projectile'
  return { obj: mesh, owned: { geometry, material } }
}

/** Flat-shaded placeholder loot pickup: a small crate. */
function buildLootPlaceholder(): { obj: THREE.Object3D; owned: Owned } {
  const geometry = new THREE.BoxGeometry(LOOT_SIZE, LOOT_SIZE, LOOT_SIZE)
  const material = new THREE.MeshLambertMaterial({ color: LOOT_COLOR, flatShading: true })
  const mesh = new THREE.Mesh(geometry, material)
  mesh.name = 'loot'
  return { obj: mesh, owned: { geometry, material } }
}

/** Dispose geometry it created ITSELF (`owned`); a swapped-in GLTF clone
 *  (`owned === null`) is AssetLib-cached and shared across every clone
 *  from the same scene, so only its transform node is removed, never its
 *  geometry/material (world.ts's `disposeGroup` follows the same rule). */
function disposeOwned(root: THREE.Object3D, owned: Owned | null): void {
  if (!owned) return
  root.traverse((c) => {
    const m = c as THREE.Mesh
    if (m.geometry) m.geometry.dispose()
  })
  owned.material.dispose()
}
