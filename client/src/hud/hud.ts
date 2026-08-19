/**
 * Minimal HUD (GDD M1 scope): speed, distance to nearest player, connection
 * state. DOM overlay; textContent only (names are untrusted server data and
 * must never become markup).
 *
 * Updates are throttled to ~10 Hz — the DOM is not the render loop.
 */
export interface HudData {
  /** Predicted speed, m/s (null until the local state exists). */
  speed: number | null
  /** Distance to the nearest remote player, m (null = nobody → show —). */
  nearest: number | null
  /** Connection line, e.g. "online 24 ms" / "mock" / "connecting…". */
  conn: string
}

const MIN_INTERVAL_MS = 100

export class Hud {
  private el: HTMLElement
  private lastMs = 0

  constructor(el: HTMLElement) {
    this.el = el
  }

  update(d: HudData): void {
    const now = performance.now()
    if (now - this.lastMs < MIN_INTERVAL_MS) return
    this.lastMs = now
    const speed = d.speed === null ? '—' : `${d.speed.toFixed(1)} m/s`
    // GDD: with nobody else in the world show —, not 0 and not ∞.
    const nearest = d.nearest === null ? '—' : `${d.nearest.toFixed(1)} m`
    this.el.textContent = `speed    ${speed}\nnearest  ${nearest}\nconn     ${d.conn}`
  }

  /** Force an immediate update (connection state changes). */
  flush(d: HudData): void {
    this.lastMs = 0
    this.update(d)
  }
}
