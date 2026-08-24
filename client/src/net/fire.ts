/**
 * Fire input → wire, and the visuals for what comes back (docs/PROTOCOL.md
 * `fire` and the `event` payload table; GDD "Weapons").
 *
 * The trigger check (pointer-locked, defs ready, a weapon equipped, not
 * reloading) plus a client cadence gate mirroring `fire_interval` live here
 * so a held button can't flood the socket — the server still enforces the
 * real cadence and the real spread with one tick of tolerance.
 *
 * Tracers are drawn from the `shot fired` EVENT's own origin/dir/dist, never
 * from local aim: spread is resolved server-side (GDD "Weapons"), so the
 * local look direction is not where the shot actually went. Drawing it
 * locally would show the player a lie that disagrees with their own hit
 * markers (GDD, the open question this settles).
 *
 * `death` is forwarded only — the target's own renderer owns that state.
 */
import * as THREE from 'three'
import { encodeFire } from './phase2.js'
import type { EventMsg } from './protocol.js'
import { EVENT } from './protocol.js'
import type { Registry } from './defs.js'
import { itemDef } from './defs.js'

export interface FireDeps {
  scene: THREE.Scene
  camera: THREE.PerspectiveCamera
  /** DOM layer hit markers are appended to (e.g. the existing #tags overlay). */
  overlay: HTMLElement
  /** Encode + send one `fire` frame; returns true if it actually went out. */
  send: (bytes: Uint8Array<ArrayBuffer>) => boolean
  /** Forward a `death` event; this module renders nothing for it itself. */
  onDeath?: (victimId: number, killerId: number) => void
}

/** Everything tryFire needs to decide whether the click is allowed to fire. */
export interface FireState {
  locked: boolean
  registry: Registry
  equippedItem: string | null
  reloading: boolean
  /** input.seq in effect right now (PROTOCOL "fire": correlates the shot with the input stream). */
  seq: number
  lookDir: [number, number, number]
}

const TRACER_LIFE = 0.08 // s — a hitscan flash, not a travelling bolt
const HIT_LIFE = 0.5 // s

interface Tracer {
  line: THREE.Line
  mat: THREE.LineBasicMaterial
  life: number
}

interface Marker {
  el: HTMLDivElement
  world: THREE.Vector3
  life: number
}

function need(data: Uint8Array, n: number): boolean {
  return data.length >= n
}

export class FireController {
  private deps: FireDeps
  private held = false
  private nextFireAtMs = 0
  private tracers: Tracer[] = []
  private markers: Marker[] = []

  constructor(target: HTMLElement, deps: FireDeps) {
    this.deps = deps
    target.addEventListener('mousedown', (e) => {
      if (e.button === 0) this.held = true
    })
    window.addEventListener('mouseup', (e) => {
      if (e.button === 0) this.held = false
    })
    window.addEventListener('blur', () => {
      this.held = false
    })
  }

  /**
   * Call once per render frame: fires while the button is held and every
   * gate passes (cadence included), and ages tracers/hit markers. `nowMs`
   * is a monotonic clock (performance.now()); `dt` is the frame delta, s.
   */
  update(nowMs: number, dt: number, state: FireState): void {
    if (this.held) this.tryFire(nowMs, state)
    this.age(dt)
  }

  private tryFire(nowMs: number, state: FireState): void {
    if (!state.locked || !state.registry.ready || !state.equippedItem || state.reloading) return
    const weapon = itemDef(state.registry, state.equippedItem)?.weapon
    if (!weapon) return
    if (nowMs < this.nextFireAtMs) return
    if (!this.deps.send(encodeFire(state.seq, state.lookDir))) return
    // Client cadence mirror only — a held button can't spam the socket
    // between server ticks. The server owns the real fire_interval rule.
    this.nextFireAtMs = nowMs + weapon.fireInterval * 1000
  }

  /** Dispatch a decoded `event` frame for shot fired / hit / death. */
  handleEvent(event: EventMsg): void {
    switch (event.eventId) {
      case EVENT.shot_fired:
        this.onShotFired(event.data)
        break
      case EVENT.hit:
        this.onHit(event.data)
        break
      case EVENT.death:
        this.onDeath(event.data)
        break
    }
  }

  private onShotFired(data: Uint8Array): void {
    if (!need(data, 28)) return
    const dv = new DataView(data.buffer, data.byteOffset, data.byteLength)
    const origin = new THREE.Vector3(dv.getFloat32(0, true), dv.getFloat32(4, true), dv.getFloat32(8, true))
    const dir = new THREE.Vector3(dv.getFloat32(12, true), dv.getFloat32(16, true), dv.getFloat32(20, true))
    const dist = dv.getFloat32(24, true)
    const end = origin.clone().addScaledVector(dir, dist)
    const mat = new THREE.LineBasicMaterial({ color: 0xfff2b0, transparent: true, opacity: 0.9 })
    const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints([origin, end]), mat)
    this.deps.scene.add(line)
    this.tracers.push({ line, mat, life: TRACER_LIFE })
  }

  private onHit(data: Uint8Array): void {
    if (!need(data, 20)) return
    const dv = new DataView(data.buffer, data.byteOffset, data.byteLength)
    const point = new THREE.Vector3(dv.getFloat32(4, true), dv.getFloat32(8, true), dv.getFloat32(12, true))
    const damage = dv.getUint16(16, true)
    const el = document.createElement('div')
    el.style.cssText =
      'position:absolute;left:0;top:0;pointer-events:none;transform:translate(-50%,-100%);' +
      "font:700 13px system-ui,sans-serif;color:#ff5252;text-shadow:0 1px 3px #000;"
    el.textContent = `✕ ${damage}` // formatted from a wire number, never markup
    this.deps.overlay.appendChild(el)
    this.markers.push({ el, world: point, life: HIT_LIFE })
  }

  private onDeath(data: Uint8Array): void {
    if (!need(data, 4)) return
    const dv = new DataView(data.buffer, data.byteOffset, data.byteLength)
    const killer = dv.getUint32(0, true)
    // event.entityId (from the frame header) is the victim; forward only.
    this.deps.onDeath?.(0, killer)
  }

  private age(dt: number): void {
    for (let i = this.tracers.length - 1; i >= 0; i--) {
      const t = this.tracers[i]
      t.life -= dt
      if (t.life <= 0) {
        this.deps.scene.remove(t.line)
        t.line.geometry.dispose()
        t.mat.dispose()
        this.tracers.splice(i, 1)
      } else {
        t.mat.opacity = t.life / TRACER_LIFE
      }
    }
    for (let i = this.markers.length - 1; i >= 0; i--) {
      const m = this.markers[i]
      m.life -= dt
      if (m.life <= 0) {
        m.el.remove()
        this.markers.splice(i, 1)
        continue
      }
      const proj = m.world.clone().project(this.deps.camera)
      const visible = proj.z <= 1
      m.el.style.opacity = visible ? String(m.life / HIT_LIFE) : '0'
      if (visible) {
        const x = Math.round((proj.x * 0.5 + 0.5) * window.innerWidth)
        const y = Math.round((-proj.y * 0.5 + 0.5) * window.innerHeight)
        m.el.style.transform = `translate3d(${x}px, ${y}px, 0) translate(-50%, -100%)`
      }
    }
  }
}
