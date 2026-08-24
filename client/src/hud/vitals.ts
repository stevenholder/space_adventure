/**
 * Vitals HUD: health bar, directional damage indicator, death/respawn
 * overlay (docs/GDD.md "Phase 3 — NPC combat at an encampment" >
 * "Player death and respawn"). DOM overlay, same pattern as hud.ts: a
 * plain element this module owns, built once and updated from typed
 * state. Every string is set via textContent, never innerHTML — health,
 * hit direction and the respawn countdown are all untrusted numbers off
 * the network.
 *
 * DISPLAY ONLY: every value here is read off the server's snapshot (the
 * `dead` FLAG, `health`) or its `hit`/`death` events. This module never
 * decides a player died — a locally-predicted death the server disagrees
 * with is a player stuck on a respawn screen while still alive.
 *
 * No camera effects: no shake, no look modification (docs/ARCHITECTURE.md
 * — look is moved by nothing but the mouse). The only "effect" here is a
 * flat 2D screen-space arrow.
 */

type Vec3 = [number, number, number]

const EPS = 1e-6

function dot(a: Vec3, b: Vec3): number {
  return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}
function sub(a: Vec3, b: Vec3): Vec3 {
  return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
function scale(a: Vec3, s: number): Vec3 {
  return [a[0] * s, a[1] * s, a[2] * s]
}
function cross(a: Vec3, b: Vec3): Vec3 {
  return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
}
function len(a: Vec3): number {
  return Math.sqrt(dot(a, a))
}
function norm(a: Vec3): Vec3 {
  const l = len(a)
  return l < EPS ? [0, 0, 0] : scale(a, 1 / l)
}

/** Tangent projection of world axis `w` onto the plane perpendicular to
 *  `up` (same fallback shape as sim/step.ts's spawnLook: try +X, then +Z,
 *  before giving up — `up` is never re-exported from there, so this is a
 *  small local copy, not a divergent second way of doing it). */
function tangentOf(w: Vec3, up: Vec3): Vec3 | null {
  const t = sub(w, scale(up, dot(w, up)))
  return len(t) >= EPS ? norm(t) : null
}

/** Seconds a directional hit arrow stays visible before fully faded. */
const INDICATOR_LIFE = 1.4
const MAX_INDICATORS = 5

interface Indicator {
  el: HTMLElement
  age: number
}

export class Vitals {
  private healthFill: HTMLElement
  private healthText: HTMLElement
  private indicatorLayer: HTMLElement
  private deathOverlay: HTMLElement
  private deathText: HTMLElement
  private indicators: Indicator[] = []
  private dead = false
  private respawnRemaining: number | null = null

  constructor(el: HTMLElement) {
    // Never captures pointer events, hidden or not: a death screen that
    // eats the click which re-acquires pointer lock leaves the player
    // stuck looking "stuck" after respawn.
    el.style.cssText = 'position:fixed;inset:0;pointer-events:none;font:12px monospace;color:#fff;'

    const healthTrack = document.createElement('div')
    healthTrack.style.cssText =
      'position:absolute;left:16px;bottom:16px;width:180px;height:14px;' +
      'background:rgba(0,0,0,0.45);border:1px solid rgba(255,255,255,0.35);'
    this.healthFill = document.createElement('div')
    this.healthFill.style.cssText = 'height:100%;width:100%;background:#5ac85a;'
    healthTrack.appendChild(this.healthFill)

    this.healthText = document.createElement('div')
    this.healthText.style.cssText = 'position:absolute;left:16px;bottom:32px;text-shadow:0 1px 2px #000;'

    this.indicatorLayer = document.createElement('div')
    this.indicatorLayer.style.cssText = 'position:absolute;left:50%;top:50%;width:0;height:0;'

    this.deathOverlay = document.createElement('div')
    this.deathOverlay.style.cssText =
      'position:absolute;inset:0;display:none;align-items:center;justify-content:center;' +
      'background:rgba(80,0,0,0.55);pointer-events:none;'
    this.deathText = document.createElement('div')
    this.deathText.style.cssText = 'font:bold 28px monospace;text-shadow:0 2px 4px #000;'
    this.deathOverlay.appendChild(this.deathText)

    el.appendChild(healthTrack)
    el.appendChild(this.healthText)
    el.appendChild(this.indicatorLayer)
    el.appendChild(this.deathOverlay)
  }

  /** Health bar: `health`/`max` straight off the entity's snapshot row. */
  setHealth(health: number, max: number): void {
    const clampedMax = max > 0 ? max : 1
    const frac = Math.max(0, Math.min(1, health / clampedMax))
    this.healthFill.style.width = `${(frac * 100).toFixed(1)}%`
    this.healthFill.style.background = frac > 0.5 ? '#5ac85a' : frac > 0.25 ? '#d9a83c' : '#d9432c'
    this.healthText.textContent = `${Math.max(0, Math.round(health))} / ${Math.round(clampedMax)}`
  }

  /**
   * Directional hit indicator. `fromWorldDir` is a world-space vector
   * (need not be unit) pointing from the player toward whatever hurt
   * them. Projected into the player's own tangent frame — forward/right
   * built from `lookDir` and `worldUp` — never a fixed world axis: on a
   * round world "up" is radial and differs per position, so a
   * screen-space arrow built from world axes points wrong everywhere but
   * directly under the spawn.
   */
  takeDamage(fromWorldDir: Vec3, lookDir: Vec3, worldUp: Vec3): void {
    const up = norm(worldUp)
    if (len(up) < EPS) return
    const dir = norm(fromWorldDir)
    if (len(dir) < EPS) return // no direction info — nothing to show

    const forward = tangentOf(lookDir, up) ?? tangentOf([1, 0, 0], up) ?? tangentOf([0, 0, 1], up)
    if (!forward) return
    const right = norm(cross(forward, up))

    // angle 0 = straight ahead, +90 = right, ±180 = behind — atan2 over
    // the local (forward, right) pair, not world axes.
    const angleDeg = (Math.atan2(dot(dir, right), dot(dir, forward)) * 180) / Math.PI

    const arrow = document.createElement('div')
    arrow.style.cssText =
      'position:absolute;left:0;top:0;width:0;height:0;opacity:0.95;' +
      'border-left:9px solid transparent;border-right:9px solid transparent;' +
      'border-bottom:16px solid #ff4444;transform-origin:0 0;' +
      `transform:rotate(${angleDeg}deg) translateY(-120px);`
    this.indicatorLayer.appendChild(arrow)
    this.indicators.push({ el: arrow, age: 0 })
    while (this.indicators.length > MAX_INDICATORS) {
      this.indicators.shift()?.el.remove()
    }
  }

  /** Death overlay + respawn countdown, straight off the server's `dead`
   *  FLAG and `death` event — never decided locally. */
  setDead(dead: boolean, respawnInSeconds: number | null): void {
    this.dead = dead
    this.respawnRemaining = dead ? respawnInSeconds : null
    this.deathOverlay.style.display = dead ? 'flex' : 'none'
    this.renderDeathText()
  }

  /** Fade hit indicators and tick the displayed respawn countdown. */
  update(dt: number): void {
    for (let i = this.indicators.length - 1; i >= 0; i--) {
      const ind = this.indicators[i]
      ind.age += dt
      if (ind.age >= INDICATOR_LIFE) {
        ind.el.remove()
        this.indicators.splice(i, 1)
        continue
      }
      ind.el.style.opacity = `${Math.max(0, 1 - ind.age / INDICATOR_LIFE)}`
    }
    if (this.dead && this.respawnRemaining !== null) {
      this.respawnRemaining = Math.max(0, this.respawnRemaining - dt)
      this.renderDeathText()
    }
  }

  private renderDeathText(): void {
    if (!this.dead) {
      this.deathText.textContent = ''
      return
    }
    const secs = this.respawnRemaining
    this.deathText.textContent = secs === null ? 'You died' : `You died — respawning in ${secs.toFixed(1)}s`
  }
}
