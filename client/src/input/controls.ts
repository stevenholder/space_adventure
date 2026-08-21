/**
 * On-foot controls (GDD on-foot rule table): WASD wish direction, mouse
 * look, Shift sprint, Space jump.
 *
 * Look is held as an absolute world-space unit vector (PROTOCOL "input":
 * look_dir is the direction the eyes point along — not angles, not rates)
 * and is applied instantly, never reconciled. Mouse movement maps to a
 * rotation in the player's CURRENT tangent frame (up = normalize(pos)); no
 * global yaw/pitch is ever stored — on a sphere there is no global frame
 * for yaw that is singularity-free.
 *
 * Jump is level-triggered (Space held → re-jumps on every landing): the
 * server rule is level-triggered too, so prediction and authority agree.
 */
import type { Input, Vec3 } from '../sim/index.js'
import { ACTION, RULES, vec } from '../sim/index.js'
import { quatFromAxisAngle, quatRotate } from '../util/quat.js'

/** Radians of view per pixel of mouse movement. */
const LOOK_SENS = 0.0022

const PREVENT_DEFAULT = new Set(['Space', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'])

export class Controls {
  /** Called when pointer lock is acquired/released (for the hint overlay). */
  onLock?: (locked: boolean) => void
  private keys = new Set<string>()
  private look: Vec3 = { x: 1, y: 0, z: 0 }
  private up: Vec3 = { x: 0, y: 1, z: 0 }
  private locked = false
  private canvas: HTMLElement

  constructor(canvas: HTMLElement) {
    this.canvas = canvas
    canvas.addEventListener('click', () => {
      if (!this.locked) this.requestLock()
    })
    document.addEventListener('pointerlockchange', () => {
      this.locked = document.pointerLockElement === this.canvas
      this.onLock?.(this.locked)
    })
    window.addEventListener('keydown', (e) => {
      this.keys.add(e.code)
      if (PREVENT_DEFAULT.has(e.code)) e.preventDefault()
    })
    window.addEventListener('keyup', (e) => this.keys.delete(e.code))
    // Keys stick if the window loses focus mid-press.
    window.addEventListener('blur', () => this.keys.clear())
    document.addEventListener('mousemove', (e) => {
      if (!this.locked) return
      this.rotateLook(e.movementX, e.movementY)
    })
  }

  get isLocked(): boolean {
    return this.locked
  }

  requestLock(): void {
    // May be rejected outside a user gesture; the click handler retries.
    try {
      this.canvas.requestPointerLock()
    } catch {
      // pointer lock unavailable (e.g. sandboxed) — keyboard still works
    }
  }

  /** Reset look to the spawn direction (GDD spawn look = facing at spawn). */
  reset(look: Vec3): void {
    this.look = vec.norm(look)
  }

  get lookDir(): Vec3 {
    return this.look
  }

  /**
   * The player's current world up, refreshed each frame from prediction.
   * `up` is already normalized (the render loop normalizes once and
   * reuses it); copied in place so the frame loop allocates nothing.
   */
  setWorldUp(up: Vec3): void {
    this.up.x = up.x
    this.up.y = up.y
    this.up.z = up.z
  }

  private rotateLook(dx: number, dy: number): void {
    const up = this.up
    let l = this.look
    if (dx !== 0) {
      // Yaw about local up: +θ turns the view toward up × look (right).
      l = quatRotate(quatFromAxisAngle(up, dx * LOOK_SENS), l)
    }
    if (dy !== 0) {
      // Pitch about the horizontal right axis: +θ pitches the view down.
      const right = vec.norm(vec.sub(l, vec.scale(up, vec.dot(l, up))))
      l = quatRotate(quatFromAxisAngle(right, dy * LOOK_SENS), l)
    }
    this.look = vec.norm(l)
  }

  /**
   * Current command state (GDD axis mapping: move_y forward, move_x right).
   * Look is clamped to `look_clamp_deg` off ±up so its tangent projection
   * (facing) never degenerates.
   */
  input(): Input {
    const k = this.keys
    let mx = 0
    let my = 0
    if (k.has('KeyW') || k.has('ArrowUp')) my += 1
    if (k.has('KeyS') || k.has('ArrowDown')) my -= 1
    if (k.has('KeyD') || k.has('ArrowRight')) mx += 1
    if (k.has('KeyA') || k.has('ArrowLeft')) mx -= 1
    const actionMask =
      (k.has('ShiftLeft') || k.has('ShiftRight') ? ACTION.SPRINT : 0) |
      (k.has('Space') ? ACTION.JUMP : 0)

    const up = this.up
    const cosMax = Math.cos((RULES.lookClampDeg * Math.PI) / 180)
    let look = this.look
    const du = vec.dot(look, up)
    if (du > cosMax) {
      look = vec.norm(vec.sub(look, vec.scale(up, du - cosMax)))
    } else if (du < -cosMax) {
      look = vec.norm(vec.sub(look, vec.scale(up, du + cosMax)))
    }
    return { moveX: mx, moveY: my, lookDir: look, actionMask }
  }
}
