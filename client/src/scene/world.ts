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
 */
import * as THREE from 'three'
import type { Terrain, Vec3 } from '../sim/index.js'
import { RULES } from '../sim/index.js'
import type { EntityState } from '../net/protocol.js'
import { InterpBuffer } from '../net/interp.js'
import type { Quat } from '../util/quat.js'
import { QUAT_FLIP_Y, quatFromForwardUp, quatMul } from '../util/quat.js'
import { buildTerrain } from './terrain.js'
import { buildSky } from './sky.js'
import type { RockPlacement } from './rocks.js'
import { buildRockGroup, scatterRocks } from './rocks.js'
import type { CharRig } from './character.js'
import { makePlaceholderRig } from './character.js'

const TAG_FADE_START = 40 // m (GDD: fading out past ~40 m)
const TAG_FADE_END = 45 // m (fully gone)
const TAG_HEIGHT = 2.1 // m above the entity origin

interface RemoteView {
  anchor: THREE.Group
  rig: CharRig | null
  tagEl: HTMLDivElement
  interp: InterpBuffer
  pos: THREE.Vector3
  quat: THREE.Quaternion
  speed: number
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

  constructor(tagsEl: HTMLElement) {
    this.camera = new THREE.PerspectiveCamera(
      72,
      window.innerWidth / window.innerHeight,
      RULES.nearClip,
      RULES.farClip,
    )
    this.tagsEl = tagsEl

    this.scene.add(buildSky())
    const sun = new THREE.DirectionalLight(0xfff2df, 2.2)
    sun.position.set(0.55, 0.7, -0.45).normalize().multiplyScalar(100)
    this.scene.add(sun)
    this.scene.add(new THREE.HemisphereLight(0x8899bb, 0x1a2016, 0.7))
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
      disposeRig(old)
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
    const f = this.f.set(facing.x, facing.y, facing.z)
    if (f.lengthSq() < 1e-12) f.set(1, 0, 0)
    f.normalize()
    this.localAnchor.position.set(pos.x, pos.y, pos.z)
    this.localAnchor.quaternion.copy(
      toTHREE(quatFromForwardUp({ x: f.x, y: f.y, z: f.z }, upToVec3(up))),
    )
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
    this.camera.quaternion.copy(toTHREE(quatFromForwardUp({ x: look.x, y: look.y, z: look.z }, upToVec3(up))))
  }

  // --------------------------------------------------------------- remotes

  upsertRemote(id: number, name: string): void {
    let rv = this.remotes.get(id)
    if (!rv) {
      const anchor = new THREE.Group()
      const rig = makePlaceholderRig()
      anchor.add(rig.root)
      const tagEl = document.createElement('div')
      tagEl.className = 'tag'
      tagEl.textContent = name // untrusted name: text, never markup
      this.tagsEl.appendChild(tagEl)
      rv = {
        anchor,
        rig,
        tagEl,
        interp: new InterpBuffer(),
        pos: new THREE.Vector3(),
        quat: new THREE.Quaternion(),
        speed: 0,
      }
      this.remotes.set(id, rv)
      this.scene.add(anchor)
    } else {
      rv.tagEl.textContent = name
    }
  }

  /** Feed one snapshot entity row into the interpolation buffer. */
  feedRemote(id: number, e: EntityState, tick: number, nowMs: number): void {
    const rv = this.remotes.get(id)
    if (!rv) return
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
    this.scene.remove(rv.anchor)
    if (rv.rig) disposeRig(rv.rig)
    rv.tagEl.remove()
    this.remotes.delete(id)
  }

  /** Swap a remote body for the loaded GLB rig. */
  setRemoteRig(id: number, rig: CharRig): void {
    const rv = this.remotes.get(id)
    if (!rv) return
    const old = rv.rig
    if (old) {
      rv.anchor.remove(old.root)
      disposeRig(old)
    }
    rv.rig = rig
    rv.anchor.add(rig.root)
  }

  /** Interpolate + place remotes, animate them, position nametags. */
  frameRemotes(nowMs: number, dt: number, localPos: Vec3): void {
    const lp = this.lpV.set(localPos.x, localPos.y, localPos.z)
    for (const rv of this.remotes.values()) {
      const r = rv.interp.render(nowMs)
      if (!r) {
        rv.tagEl.style.display = 'none'
        continue
      }
      rv.pos.set(r.pos.x, r.pos.y, r.pos.z)
      // Wire quat (GDD +Z frame) → authored art frame (−Z forward).
      const q = quatMul(r.quat, QUAT_FLIP_Y)
      rv.quat.set(q.x, q.y, q.z, q.w)
      rv.speed = Math.hypot(r.vel.x, r.vel.y, r.vel.z)
      rv.anchor.position.copy(rv.pos)
      rv.anchor.quaternion.copy(rv.quat)
      rv.rig?.animate(dt, rv.speed)
      this.placeTag(rv, lp)
    }
  }

  private placeTag(rv: RemoteView, localPos: THREE.Vector3): void {
    const el = rv.tagEl
    const d = rv.pos.distanceTo(localPos)
    if (d > TAG_FADE_END) {
      el.style.display = 'none'
      return
    }
    // Behind the camera?
    const toTag = this.toTag.copy(rv.pos).sub(this.camera.position)
    const fwd = this.fwd.set(0, 0, -1).applyQuaternion(this.camera.quaternion)
    if (toTag.dot(fwd) <= 0) {
      el.style.display = 'none'
      return
    }
    // Tag anchor: TAG_HEIGHT along the entity's radial up, projected.
    const anchor = this.anchorV.copy(rv.pos).normalize().multiplyScalar(TAG_HEIGHT).add(rv.pos)
    const proj = anchor.project(this.camera)
    if (proj.z > 1) {
      el.style.display = 'none'
      return
    }
    const x = (proj.x * 0.5 + 0.5) * window.innerWidth
    const y = (-proj.y * 0.5 + 0.5) * window.innerHeight
    el.style.display = ''
    el.style.opacity =
      d > TAG_FADE_START ? String(1 - (d - TAG_FADE_START) / (TAG_FADE_END - TAG_FADE_START)) : '1'
    el.style.left = `${x}px`
    el.style.top = `${y}px`
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

function toTHREE(q: Quat): THREE.Quaternion {
  return _q.set(q.x, q.y, q.z, q.w)
}

function upToVec3(v: THREE.Vector3): { x: number; y: number; z: number } {
  return { x: v.x, y: v.y, z: v.z }
}

function proceduralRockGeoms(): THREE.BufferGeometry[] {
  return [
    new THREE.IcosahedronGeometry(0.5, 0), // rounded boulder
    new THREE.IcosahedronGeometry(0.55, 1), // finer slab
    new THREE.OctahedronGeometry(0.55, 0), // angular shard
  ]
}

function disposeRig(rig: CharRig): void {
  rig.root.traverse((o) => {
    const m = o as THREE.Mesh
    if (m.geometry) m.geometry.dispose()
  })
}

function disposeGroup(g: THREE.Group): void {
  g.traverse((o) => {
    const m = o as THREE.Mesh
    if (m.geometry) m.geometry.dispose()
  })
}
