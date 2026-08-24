/**
 * HUD overlay: Phase 1 readouts (speed, distance to nearest player,
 * connection state) plus Phase 2 readouts (magazine/reserve ammo, credits,
 * reload indicator, interaction prompt). DOM overlay; textContent only
 * (names/verbs are untrusted server data and must never become markup).
 *
 * Every Phase 2 value is either read straight off a `cmd_result` body or a
 * `defs` lookup — this module never derives an authoritative number of its
 * own (a locally-computed credit balance that disagrees with the server is
 * worse than a stale one).
 *
 * Updates are throttled to ~10 Hz — the DOM is not the render loop.
 */

/** Magazine / reserve ammo for the equipped weapon (`reload`/`inventory`
 *  cmd_result bodies). Null (on HudData) = no weapon equipped. */
export interface AmmoData {
  magazine: number
  reserve: number
}

/** The current interaction target (W2-19 `pickInteractable` + the verb's
 *  bound key). Null (on HudData) = nothing in range/cone. */
export interface PromptData {
  verb: string
  key: string
}

export interface HudData {
  /** Predicted speed, m/s (null until the local state exists). */
  speed: number | null
  /** Distance to the nearest remote player, m (null = nobody → show —). */
  nearest: number | null
  /** Connection line, e.g. "online 24 ms" / "mock" / "connecting…". */
  conn: string
  /** Equipped weapon's magazine/reserve, from a `reload`/`inventory`
   *  cmd_result. Optional so Phase 1 callers keep compiling unchanged;
   *  undefined/null both render as —. */
  ammo?: AmmoData | null
  /** Player credits, from a `shop_buy`/`inventory` cmd_result. */
  credits?: number | null
  /** True while a `reload` cmd is in flight (sent, result not yet applied). */
  reloading?: boolean
  /** The looked-at interactable's verb + key, or null for nothing in range. */
  prompt?: PromptData | null
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
    const ammo = d.ammo == null ? '—' : `${d.ammo.magazine}/${d.ammo.reserve}`
    const credits = d.credits == null ? '—' : `${d.credits}`
    const reload = d.reloading ? 'reloading…' : '—'
    const prompt = d.prompt == null ? '—' : `${d.prompt.verb} [${d.prompt.key}]`
    this.el.textContent =
      `speed    ${speed}\n` +
      `nearest  ${nearest}\n` +
      `conn     ${d.conn}\n` +
      `ammo     ${ammo}\n` +
      `credits  ${credits}\n` +
      `reload   ${reload}\n` +
      `prompt   ${prompt}`
  }

  /** Force an immediate update (connection state changes). */
  flush(d: HudData): void {
    this.lastMs = 0
    this.update(d)
  }
}
