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
 * Look is clamped to `look_clamp_deg` off ±up as STATE (not only on the
 * wire): GDD — the client clamps its own camera, "it must not be able to
 * view the poles the server would reject", using the same formula as the
 * server's clampLook (server/internal/sim/sim.go). Keeping look off the
 * poles also keeps the pitch axis (look × up) from degenerating or
 * flipping. A pitch event is dropped (dead stop, like an FPS pitch
 * clamp) when it would push look closer to a pole it is already
 * clamped against.
 *
 * Jump is level-triggered (Space held → re-jumps on every landing): the
 * server rule is level-triggered too, so prediction and authority agree.
 */
import type { Input, Vec3 } from '../sim/index.js'
import { ACTION, RULES, vec } from '../sim/index.js'
import { quatFromAxisAngle, quatRotate } from '../util/quat.js'

/** Radians of view per pixel of mouse movement. */
const LOOK_SENS = 0.0022

/** cos(look_clamp_deg): the max |dot(look, up)| of a clamped look. */
const COS_LOOK_CLAMP = Math.cos((RULES.lookClampDeg * Math.PI) / 180)
/** sin(look_clamp_deg), the tangent pair of COS_LOOK_CLAMP. */
const SIN_LOOK_CLAMP = Math.sin((RULES.lookClampDeg * Math.PI) / 180)

const PREVENT_DEFAULT = new Set(['Space', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'])

/**
 * GDD "clampLook", mirroring the server (server/internal/sim/sim.go):
 * push a unit look onto the circle `look_clamp_deg` off ±up, keeping its
 * tangent direction; unit by construction. The tangent is never zero on
 * client state (a clamped look stays >= look_clamp_deg off a pole), so
 * the server's prevFacing azimuth fallback is not needed here.
 */
function clampLook(l: Vec3, up: Vec3): Vec3 {
  const c = vec.dot(l, up)
  if (c > COS_LOOK_CLAMP) {
    const t = vec.norm(vec.sub(l, vec.scale(up, c)))
    return vec.add(vec.scale(up, COS_LOOK_CLAMP), vec.scale(t, SIN_LOOK_CLAMP))
  }
  if (c < -COS_LOOK_CLAMP) {
    const t = vec.norm(vec.sub(l, vec.scale(up, c)))
    return vec.add(vec.scale(up, -COS_LOOK_CLAMP), vec.scale(t, SIN_LOOK_CLAMP))
  }
  return l
}

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
      // Yaw about local up: a +θ rotation about up moves look toward
      // up × look — the player's LEFT (right is look × up, the
      // quatFromForwardUp convention) — so a rightward drag (dx > 0)
      // needs a negative angle.
      l = quatRotate(quatFromAxisAngle(up, -dx * LOOK_SENS), l)
    }
    if (dy !== 0) {
      const angle = -dy * LOOK_SENS // down drag (dy > 0) < 0
      const du = vec.dot(l, up)
      // Dead stop at the GDD clamp: dropping the event keeps the pitch
      // axis (look × up) from ever pointing past a pole — an event
      // across the pole would end the clamped look on the far side with
      // the axis azimuth flipped. The step carries look along a great
      // circle toward the pole, so it crosses exactly when look is
      // closer to that pole than the step is long.
      const cosStep = Math.cos(Math.abs(angle))
      const deadStop =
        (angle < 0 && (du <= -COS_LOOK_CLAMP || du <= -cosStep)) ||
        (angle > 0 && (du >= COS_LOOK_CLAMP || du >= cosStep))
      if (!deadStop) {
        // Pitch about the local right axis (look × up, perpendicular to
        // look): a +θ rotation moves look toward up (looking up), hence
        // the negative angle for a downward drag.
        const right = vec.norm(vec.cross(l, up))
        l = quatRotate(quatFromAxisAngle(right, angle), l)
      }
    }
    // Clamp the state itself, not just the wire value (see header).
    this.look = clampLook(l, up)
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
    const look = clampLook(this.look, up)
    return { moveX: mx, moveY: my, lookDir: look, actionMask }
  }
}
