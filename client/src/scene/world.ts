/**
 * World composition: terrain, sky, rocks, the local player's own body with
 * the first-person camera, and remote players with nametags.
 *
 * Up is radial, everywhere: every orientation derives from normalize(pos) —
 * camera up, body facing, prop placement, nametag offsets. A hardcoded +Y
 * works at spawn and breaks on the far side of the world.
 *
 * Camera (GDD M1): first person, ONLY first person — mounted at the
 * `eye` node of the local body (falling back to GDD `eye_height` above the
 * entity origin when the asset or node is missing). The camera pitches with
 * look; the body stays upright. Own body rendered, `head` hidden.
 *
 * Remotes: server quat applied (with the art-frame flip), through the
 * ~100 ms interpolation buffer. Nametags are plain DOM text (names are
 * untrusted — textContent, never markup), fading out past ~40 m.
 *
 * Phase 2 entities: NPC (`npc.shopkeeper`) and target dummies
 * (`prop.target`, `plate` node recoloured while dead) render through the
 * same remote pipeline as players, self-loaded via this module's own
 * `AssetLib` (art/manifest.json is a cache-by-id contract, so a second
 * instance is cheap and keeps this file free of a `main.ts` wiring
 * dependency). `pitch_q` is applied to the `head` node and the held weapon
 * ONLY — never the body (GDD "First-person body"). Static colliders render
 * visually only; the `colliders` message is the sole authority for shape.
 */
import * as THREE from 'three'
import type { Terrain, Vec3 } from '../sim/index.js'
import { RULES } from '../sim/index.js'
import type { EntityState } from '../net/protocol.js'
import {
  ENTITY_TYPE_LOOT,
  ENTITY_TYPE_NPC,
  ENTITY_TYPE_PLAYER,
  ENTITY_TYPE_PROJECTILE,
  ENTITY_TYPE_TARGET,
  FLAG,
} from '../net/protocol.js'
import { LootVisual, ProjectileVisual } from './projectile.js'
import type { Collider } from '../net/phase2.js'
import { COLLIDER_BOX } from '../net/phase2.js'
import { InterpBuffer, type RemoteRender } from '../net/interp.js'
import type { Quat } from '../util/quat.js'
import { QUAT_FLIP_Y, quatFromForwardUpUnit, quatMulInto } from '../util/quat.js'
import { buildTerrain } from './terrain.js'
import { buildSky } from './sky.js'
import type { RockPlacement } from './rocks.js'
import { buildRockGroup, scatterRocks } from './rocks.js'
import type { CharRig } from './character.js'
import { makePlaceholderRig, makeRigFromGltf } from './character.js'
import type { WeaponHandle } from './weapon.js'
import { attachWeapon, detachWeapon } from './weapon.js'
import { AssetLib } from './assets.js'

const TAG_FADE_START = 40 // m (GDD: fading out past ~40 m)
const TAG_FADE_END = 45 // m (fully gone)
const TAG_HEIGHT = 2.1 // m above the entity origin
const TARGET_DEAD_COLOR = 0x8a2020

type ColorMat = THREE.Material & { color: THREE.Color }

interface TargetVisual {
  root: THREE.Object3D
  plateMats: ColorMat[]
  aliveColors: number[]
}

interface RemoteView {
  id: number
  entityType: number
  anchor: THREE.Group
  rig: CharRig | null
  target: TargetVisual | null
  weapon: WeaponHandle | null
  /** Projectile/loot visuals own their own scene object, so they hang off the
   *  view rather than the shared anchor. */
  projectile: ProjectileVisual | null
  loot: LootVisual | null
  tagEl: HTMLDivElement
  interp: InterpBuffer
  /** Reused interpolation output (renderInto writes here — no allocation). */
  rr: RemoteRender
  pos: THREE.Vector3
  quat: THREE.Quaternion
  speed: number
  /** Latest raw wire fields (not interpolated — display-only). */
  flags: number
  pitchQ: number
  targetDead: boolean
  /** Last applied tag state — DOM writes are skipped when unchanged. */
  tagX: number
  tagY: number
  tagO: number
  tagVis: boolean
}

export class World {
  scene = new THREE.Scene()
  camera: THREE.PerspectiveCamera

  private terrain: Terrain | null = null
  private rockPlacements: RockPlacement[] | null = null
  private rockGroup: THREE.Group | null = null
  private localAnchor = new THREE.Group()
  private localRig: CharRig | null = null
  private remotes = new Map<number, RemoteView>()
  private tagsEl: HTMLElement
  private up = new THREE.Vector3()
  private lpV = new THREE.Vector3()
  private f = new THREE.Vector3()
  private camPos = new THREE.Vector3()
  private toTag = new THREE.Vector3()
  private fwd = new THREE.Vector3()
  private anchorV = new THREE.Vector3()
  /** Own asset loader (art/manifest.json is a cache-by-id contract — a
   *  second AssetLib instance costs one extra manifest fetch, not a second
   *  loading mechanism). */
  private assets = new AssetLib()
  private manifestReady: Promise<void>
  private colliderGroup = new THREE.Group()

  constructor(tagsEl: HTMLElement) {
    this.camera = new THREE.PerspectiveCamera(
      72,
      window.innerWidth / window.innerHeight,
      RULES.nearClip,
      RULES.farClip,
    )
    this.tagsEl = tagsEl
    this.manifestReady = this.assets.loadManifest()

    this.scene.add(buildSky())
    const sun = new THREE.DirectionalLight(0xfff2df, 2.2)
    sun.position.set(0.55, 0.7, -0.45).normalize().multiplyScalar(100)
    this.scene.add(sun)
    this.scene.add(new THREE.HemisphereLight(0x8899bb, 0x1a2016, 0.7))
    this.scene.add(this.colliderGroup)
  }

  setAspect(w: number, h: number): void {
    this.camera.aspect = w / h
    this.camera.updateProjectionMatrix()
  }

  get ready(): boolean {
    return this.terrain !== null
  }

  /** Build the terrain mesh, rocks, and the local body. */
  addTerrain(t: Terrain, seed: number): void {
    this.terrain = t
    this.scene.add(buildTerrain(t))
    this.rockPlacements = scatterRocks(t, seed)
    this.rockGroup = buildRockGroup(this.rockPlacements, proceduralRockGeoms())
    this.scene.add(this.rockGroup)
    this.localRig = makePlaceholderRig()
    if (this.localRig.head) this.localRig.head.visible = false // local: head hidden, camera in eye
    this.localAnchor.add(this.localRig.root)
    this.scene.add(this.localAnchor)
  }

  get terrainRef(): Terrain | null {
    return this.terrain
  }

  /** Swap the local body for the loaded GLB rig (art arrived). */
  setLocalRig(rig: CharRig): void {
    const old = this.localRig
    if (old) {
      this.localAnchor.remove(old.root)
      disposeGroup(old.root)
    }
    if (rig.head) rig.head.visible = false
    this.localRig = rig
    this.localAnchor.add(rig.root)
  }

  /** Swap rock geometry once the GLB props arrive (placement is unchanged). */
  setRockGeoms(geoms: THREE.BufferGeometry[]): void {
    if (!this.rockPlacements || !this.rockGroup) return
    const old = this.rockGroup
    this.rockGroup = buildRockGroup(this.rockPlacements, geoms)
    this.scene.remove(old)
    disposeGroup(old)
  }

  // ------------------------------------------------------------------ local

  /**
   * Place the local body and camera for this frame. `facing` orients the
   * body (upright, no pitch); `lookDir` orients the camera (it pitches).
   */
  setLocal(pos: Vec3, facing: Vec3, lookDir: Vec3): void {
    const up = this.up.set(pos.x, pos.y, pos.z).normalize()
    _upV.x = up.x
    _upV.y = up.y
    _upV.z = up.z
    const f = this.f.set(facing.x, facing.y, facing.z)
    if (f.lengthSq() < 1e-12) f.set(1, 0, 0)
    f.normalize()
    _fV.x = f.x
    _fV.y = f.y
    _fV.z = f.z
    this.localAnchor.position.set(pos.x, pos.y, pos.z)
    this.localAnchor.quaternion.copy(toTHREE(quatFromForwardUpUnit(_fV, _upV, _q2)))
    this.localAnchor.updateMatrixWorld()

    // Camera mounts on the eye node (GDD); fallback: eye_height above origin.
    const eye = this.localRig?.eye
    const camPos = this.camPos
    if (eye) {
      eye.getWorldPosition(camPos)
    } else {
      camPos.set(
        pos.x + up.x * RULES.eyeHeight,
        pos.y + up.y * RULES.eyeHeight,
        pos.z + up.z * RULES.eyeHeight,
      )
    }
    this.camera.position.copy(camPos)
    // Body does not pitch: camera up stays the world (radial) up.
    const look = this.fwd.set(lookDir.x, lookDir.y, lookDir.z)
    if (look.lengthSq() < 1e-12) look.set(1, 0, 0)
    look.normalize()
    _fV.x = look.x
    _fV.y = look.y
    _fV.z = look.z
    this.camera.quaternion.copy(toTHREE(quatFromForwardUpUnit(_fV, _upV, _q2)))
  }

  // --------------------------------------------------------------- remotes

  /** `entityType` (protocol ENTITY_TYPE_*) selects the renderer: player
   *  (default, unchanged Phase 1 body + weapon), NPC (`npc.shopkeeper`),
   *  or target dummy (`prop.target`). */
  upsertRemote(id: number, name: string, entityType: number = ENTITY_TYPE_PLAYER): void {
    let rv = this.remotes.get(id)
    if (!rv) {
      const anchor = new THREE.Group()
      const tagEl = document.createElement('div')
      tagEl.className = 'tag'
      tagEl.textContent = name // untrusted name: text, never markup
      // Placement is transform-only (see placeTag): pin the box at 0,0.
      tagEl.style.left = '0px'
      tagEl.style.top = '0px'
      this.tagsEl.appendChild(tagEl)
      rv = {
        id,
        entityType,
        anchor,
        rig: null,
        target: null,
        projectile: null,
        loot: null,
        weapon: null,
        tagEl,
        interp: new InterpBuffer(),
        rr: {
          pos: { x: 0, y: 0, z: 0 },
          quat: { x: 0, y: 0, z: 0, w: 1 },
          vel: { x: 0, y: 0, z: 0 },
          extrapolatedMs: 0,
        },
        pos: new THREE.Vector3(),
        quat: new THREE.Quaternion(),
        speed: 0,
        flags: 0,
        pitchQ: 0,
        targetDead: false,
        tagX: -1,
        tagY: -1,
        tagO: -1,
        tagVis: false,
      }
      this.remotes.set(id, rv)
      this.scene.add(anchor)
      this.spawnVisual(rv)
    } else {
      rv.tagEl.textContent = name
    }
  }

  /** Build the entity_type-appropriate placeholder and kick off the real
   *  asset load. */
  private spawnVisual(rv: RemoteView): void {
    // Projectiles and loot are not characters: no rig, no nametag, no weapon.
    // Handling them before the rig path keeps a bolt in flight from being
    // given a body and a name to render.
    if (rv.entityType === ENTITY_TYPE_PROJECTILE) {
      rv.projectile = new ProjectileVisual(this.scene)
      rv.tagEl.style.display = 'none'
      return
    }
    if (rv.entityType === ENTITY_TYPE_LOOT) {
      rv.loot = new LootVisual(this.scene)
      rv.tagEl.style.display = 'none'
      return
    }
    if (rv.entityType === ENTITY_TYPE_TARGET) {
      const ph = buildTargetPlaceholder()
      const { mats, colors } = collectPlateMats(ph)
      rv.target = { root: ph, plateMats: mats, aliveColors: colors }
      rv.anchor.add(ph)
      void this.loadTargetVisual(rv)
      return
    }
    const rig = makePlaceholderRig()
    rv.rig = rig
    rv.anchor.add(rig.root)
    if (rv.entityType === ENTITY_TYPE_NPC) {
      void this.loadCharacterAsset(rv, 'npc.shopkeeper')
    } else {
      // Player body itself keeps the Phase 1 path (main.ts's setRemoteRig
      // swaps in char.player); only the weapon is new here.
      void this.attachWeaponTo(rv)
    }
  }

  private async loadAsset(id: string): Promise<THREE.Object3D | null> {
    await this.manifestReady
    return this.assets.loadGltf(id)
  }

  private async loadCharacterAsset(rv: RemoteView, assetId: string): Promise<void> {
    const obj = await this.loadAsset(assetId)
    if (!obj || this.remotes.get(rv.id) !== rv) return
    const rig = makeRigFromGltf(obj.clone(true))
    const old = rv.rig
    if (old) {
      rv.anchor.remove(old.root)
      disposeGroup(old.root)
    }
    rv.rig = rig
    rv.anchor.add(rig.root)
  }

  private async loadTargetVisual(rv: RemoteView): Promise<void> {
    const obj = await this.loadAsset('prop.target')
    if (!obj || this.remotes.get(rv.id) !== rv) return
    const clone = obj.clone(true)
    const { mats, colors } = collectPlateMats(clone)
    const old = rv.target
    if (old) {
      rv.anchor.remove(old.root)
      disposeGroup(old.root)
    }
    rv.target = { root: clone, plateMats: mats, aliveColors: colors }
    rv.anchor.add(clone)
    if (rv.targetDead) for (const m of mats) m.color.setHex(TARGET_DEAD_COLOR)
  }

  /** Attach `weapon.pulse` to the current rig's `arm.r` (players only). */
  private async attachWeaponTo(rv: RemoteView): Promise<void> {
    const armR = rv.rig?.armR
    if (!armR) return
    await this.manifestReady
    const handle = await attachWeapon(this.assets, armR, 'weapon.pulse')
    if (this.remotes.get(rv.id) !== rv || rv.rig?.armR !== armR) {
      detachWeapon(handle) // stale: rig swapped or remote gone while loading
      return
    }
    rv.weapon = handle
  }

  /** Feed one snapshot entity row into the interpolation buffer. */
  feedRemote(id: number, e: EntityState, tick: number, nowMs: number): void {
    const rv = this.remotes.get(id)
    if (!rv) return
    rv.flags = e.flags
    rv.pitchQ = e.pitchQ
    rv.interp.push({
      tick,
      pos: { x: e.pos[0], y: e.pos[1], z: e.pos[2] },
      quat: { x: e.quat[0], y: e.quat[1], z: e.quat[2], w: e.quat[3] },
      vel: { x: e.vel[0], y: e.vel[1], z: e.vel[2] },
      recvMs: nowMs,
    })
  }

  removeRemote(id: number): void {
    const rv = this.remotes.get(id)
    if (!rv) return
    // Projectiles despawn several times a second in a firefight; leaking one
    // scene object per shot is a crash in minutes, not a slow drift.
    rv.projectile?.dispose()
    rv.loot?.dispose()
    this.scene.remove(rv.anchor)
    if (rv.rig) disposeGroup(rv.rig.root)
    if (rv.target) disposeGroup(rv.target.root)
    if (rv.weapon) detachWeapon(rv.weapon)
    rv.tagEl.remove()
    this.remotes.delete(id)
  }

  /** Drop every remote (reconnect resync: the fresh spawn list is truth). */
  clearRemotes(): void {
    for (const id of [...this.remotes.keys()]) this.removeRemote(id)
  }

  /** Swap a remote body for the loaded GLB rig. */
  setRemoteRig(id: number, rig: CharRig): void {
    const rv = this.remotes.get(id)
    if (!rv) return
    const old = rv.rig
    if (old) {
      rv.anchor.remove(old.root)
      disposeGroup(old.root)
    }
    if (rv.weapon) {
      detachWeapon(rv.weapon)
      rv.weapon = null
    }
    rv.rig = rig
    rv.anchor.add(rig.root)
    if (rv.entityType !== ENTITY_TYPE_NPC && rv.entityType !== ENTITY_TYPE_TARGET) {
      void this.attachWeaponTo(rv)
    }
  }

  /** Render the static collider list. VISUAL ONLY — the `colliders`
   *  message is the sole authority for shape on both sims. */
  async setColliders(list: Collider[]): Promise<void> {
    clearGroup(this.colliderGroup)
    await this.manifestReady
    const [wall, post] = await Promise.all([
      this.assets.loadGltf('struct.wall'),
      this.assets.loadGltf('struct.post'),
    ])
    for (const c of list) {
      const isBox = c.kind === COLLIDER_BOX
      const src = isBox ? wall : post
      const obj = src ? src.clone(true) : isBox ? buildWallPlaceholder() : buildPostPlaceholder()
      if (isBox) obj.scale.set(c.half[0], c.half[1], c.half[2])
      else obj.scale.setScalar(c.half[0])
      obj.position.set(c.center[0], c.center[1], c.center[2])
      obj.quaternion.set(c.quat[0], c.quat[1], c.quat[2], c.quat[3])
      this.colliderGroup.add(obj)
    }
  }

  /** Interpolate + place remotes, animate them, position nametags. */
  frameRemotes(nowMs: number, dt: number, localPos: Vec3): void {
    const lp = this.lpV.set(localPos.x, localPos.y, localPos.z)
    for (const rv of this.remotes.values()) {
      if (!rv.interp.renderInto(nowMs, rv.rr)) {
        this.hideTag(rv)
        continue
      }
      const r = rv.rr
      rv.pos.set(r.pos.x, r.pos.y, r.pos.z)
      // Wire quat (GDD +Z frame) → authored art frame (−Z forward).
      quatMulInto(r.quat, QUAT_FLIP_Y, _q2)
      rv.quat.set(_q2.x, _q2.y, _q2.z, _q2.w)
      rv.speed = Math.hypot(r.vel.x, r.vel.y, r.vel.z)
      rv.anchor.position.copy(rv.pos)
      rv.anchor.quaternion.copy(rv.quat)
      if (rv.rig) {
        rv.rig.animate(dt, rv.speed)
        // PROTOCOL pitch_q -> radians, head + held weapon ONLY. The body
        // stays upright (GDD "First-person body") — pitch on the torso is
        // exactly the bug this field exists to avoid.
        const pitchRad = (rv.pitchQ * (Math.PI / 2)) / 127
        if (rv.rig.head) rv.rig.head.rotation.x = pitchRad
        if (rv.weapon) rv.weapon.root.rotation.x = pitchRad
      }
      if (rv.target) this.updateTargetDead(rv)
      // Projectiles and loot own their scene object, so they take the
      // interpolated transform directly instead of riding the anchor.
      if (rv.projectile) {
        rv.projectile.setTransform(rv.pos, rv.quat)
        continue // no nametag for a bolt in flight
      }
      if (rv.loot) {
        rv.loot.setTransform(rv.pos, rv.quat)
        continue
      }
      this.placeTag(rv, lp)
    }
  }

  private updateTargetDead(rv: RemoteView): void {
    const dead = (rv.flags & FLAG.dead) !== 0
    if (dead === rv.targetDead) return
    rv.targetDead = dead
    const t = rv.target
    if (!t) return
    for (let i = 0; i < t.plateMats.length; i++) {
      t.plateMats[i].color.setHex(dead ? TARGET_DEAD_COLOR : t.aliveColors[i])
    }
  }

  /**
   * Nametag: projected anchor, transform-only placement. left/top stay
   * pinned at 0, so moving a tag is a compositor job (translate3d), not a
   * main-thread layout — it matters at 10 tags × 60 fps. Writes are
   * skipped when the applied value is unchanged.
   */
  private placeTag(rv: RemoteView, localPos: THREE.Vector3): void {
    const el = rv.tagEl
    const d = rv.pos.distanceTo(localPos)
    if (d > TAG_FADE_END) {
      this.hideTag(rv)
      return
    }
    // Behind the camera?
    const toTag = this.toTag.copy(rv.pos).sub(this.camera.position)
    const fwd = this.fwd.set(0, 0, -1).applyQuaternion(this.camera.quaternion)
    if (toTag.dot(fwd) <= 0) {
      this.hideTag(rv)
      return
    }
    // Tag anchor: TAG_HEIGHT along the entity's radial up, projected.
    const anchor = this.anchorV.copy(rv.pos).normalize().multiplyScalar(TAG_HEIGHT).add(rv.pos)
    const proj = anchor.project(this.camera)
    if (proj.z > 1) {
      this.hideTag(rv)
      return
    }
    const x = Math.round((proj.x * 0.5 + 0.5) * window.innerWidth)
    const y = Math.round((-proj.y * 0.5 + 0.5) * window.innerHeight)
    if (rv.tagX !== x || rv.tagY !== y) {
      rv.tagX = x
      rv.tagY = y
      el.style.transform = `translate3d(${x}px, ${y}px, 0) translate(-50%, -100%)`
    }
    const o =
      d > TAG_FADE_START
        ? Math.round((1 - (d - TAG_FADE_START) / (TAG_FADE_END - TAG_FADE_START)) * 100)
        : 100
    if (rv.tagO !== o) {
      rv.tagO = o
      el.style.opacity = String(o / 100)
    }
    if (!rv.tagVis) {
      rv.tagVis = true
      el.style.display = ''
    }
  }

  private hideTag(rv: RemoteView): void {
    if (rv.tagVis) {
      rv.tagVis = false
      rv.tagEl.style.display = 'none'
    }
  }

  /** Distance to the nearest remote (m), or null when there are none. */
  nearestDist(localPos: Vec3): number | null {
    const lp = this.lpV.set(localPos.x, localPos.y, localPos.z)
    let best: number | null = null
    for (const rv of this.remotes.values()) {
      if (rv.interp.size === 0) continue
      const d = lp.distanceTo(rv.pos)
      if (best === null || d < best) best = d
    }
    return best
  }

  remoteCount(): number {
    return this.remotes.size
  }
}

const _q = new THREE.Quaternion()
const _q2: Quat = { x: 0, y: 0, z: 0, w: 1 }
const _upV: Vec3 = { x: 0, y: 0, z: 0 }
const _fV: Vec3 = { x: 0, y: 0, z: 0 }

function toTHREE(q: Quat): THREE.Quaternion {
  return _q.set(q.x, q.y, q.z, q.w)
}

function proceduralRockGeoms(): THREE.BufferGeometry[] {
  return [
    new THREE.IcosahedronGeometry(0.5, 0), // rounded boulder
    new THREE.IcosahedronGeometry(0.55, 1), // finer slab
    new THREE.OctahedronGeometry(0.55, 0), // angular shard
  ]
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

/**
 * Own-material every mesh under `root`'s `plate` node (a GLTF clone shares
 * material instances with every other clone from the same cached scene —
 * character.ts hits the same issue swapping materials on load) and return
 * them plus their spawn-time colors, so dead/alive recoloring never bleeds
 * across target instances.
 */
function collectPlateMats(root: THREE.Object3D): { mats: ColorMat[]; colors: number[] } {
  const plate = findNamed(root, 'plate')
  const mats: ColorMat[] = []
  const colors: number[] = []
  if (!plate) return { mats, colors }
  plate.traverse((o) => {
    const m = o as THREE.Mesh
    if (!m.isMesh || !m.material) return
    const src = (Array.isArray(m.material) ? m.material[0] : m.material) as ColorMat
    const owned = src.clone() as ColorMat
    m.material = owned
    mats.push(owned)
    colors.push(owned.color.getHex())
  })
  return { mats, colors }
}

/** Flat-shaded placeholder target: a post + a recolourable `plate`, ~1.8 m
 *  tall, matching prop.target's authored silhouette (art/manifest.json). */
function buildTargetPlaceholder(): THREE.Object3D {
  const postMat = new THREE.MeshLambertMaterial({ color: 0x55524a, flatShading: true })
  const plateMat = new THREE.MeshLambertMaterial({ color: 0xd6d0c4, flatShading: true })
  const root = new THREE.Group()
  root.name = 'target'
  const post = new THREE.Mesh(new THREE.BoxGeometry(0.08, 1.4, 0.08), postMat)
  post.position.y = 0.7
  root.add(post)
  const plate = new THREE.Mesh(new THREE.CylinderGeometry(0.35, 0.35, 0.08, 8), plateMat)
  plate.name = 'plate'
  plate.rotation.x = Math.PI / 2
  plate.position.y = 1.55
  root.add(plate)
  return root
}

/** Unit box (half-extent 1 on every axis) — the client scales it to each
 *  box collider's half-extents. */
function buildWallPlaceholder(): THREE.Object3D {
  const mat = new THREE.MeshLambertMaterial({ color: 0x77716a, flatShading: true })
  return new THREE.Mesh(new THREE.BoxGeometry(2, 2, 2), mat)
}

/** Unit sphere (radius 1) — the client scales it to each sphere collider's
 *  radius. */
function buildPostPlaceholder(): THREE.Object3D {
  const mat = new THREE.MeshLambertMaterial({ color: 0x6d6a63, flatShading: true })
  return new THREE.Mesh(new THREE.SphereGeometry(1, 10, 8), mat)
}

function clearGroup(group: THREE.Group): void {
  for (const child of [...group.children]) {
    group.remove(child)
    disposeGroup(child)
  }
}

function disposeGroup(o: THREE.Object3D): void {
  o.traverse((c) => {
    const m = c as THREE.Mesh
    if (m.geometry) m.geometry.dispose()
  })
}
