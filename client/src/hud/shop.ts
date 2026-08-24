/**
 * Shop panel (GDD "Items and currency"; docs/PROTOCOL.md `cmd`/`cmd_result`).
 *
 * DOM overlay, same pattern as hud.ts: a plain element this module owns,
 * updated from typed state, never innerHTML. Item names arrive over the
 * wire (defs) and are untrusted display text, same rule the nametag code
 * in scene/world.ts already follows — textContent only.
 *
 * `shop_buy` is a `cmd`: not idempotent, not predicted (PROTOCOL, "cmd /
 * cmd_result"). This panel never shows a purchase before the matching
 * `cmd_result` arrives — the stock list re-renders only from what the
 * server actually granted.
 *
 * Pointer lock is released while the panel is open (you are using a mouse
 * cursor on a DOM panel, not aiming) and restored when it closes, via the
 * injected port — this module never reaches into Controls directly.
 */
import { OP, STATUS } from '../net/protocol.js'
import type { CmdResult } from '../net/phase2.js'
import type { Registry } from '../net/defs.js'
import { itemDef } from '../net/defs.js'

export interface ShopPort {
  /** Encode + send one `cmd`; returns the seq it was sent under, or -1 when not sent (socket not open). */
  sendCmd(opcode: number, body: unknown): number
  /** false while the panel is open (pointer lock released), true once it closes (lock restored). */
  setPointerLock(locked: boolean): void
  /** The player's last known credit balance, read once when the panel opens. */
  credits(): number
}

interface StockEntry {
  item: string
  price: number
}

const MOVE_KEYS = new Set([
  'KeyW',
  'KeyA',
  'KeyS',
  'KeyD',
  'ArrowUp',
  'ArrowDown',
  'ArrowLeft',
  'ArrowRight',
])

const REFUSAL_TEXT: Record<string, string> = {
  insufficient_credits: "You can't afford that.",
  out_of_range: "You're too far from the shop.",
  unknown_item: 'That item no longer exists.',
  no_stock: 'Out of stock.',
  no_space: 'Your inventory is full.',
}
const GENERIC_REFUSAL = 'The shop refused that.'

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}

function num(v: unknown, fallback: number): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback
}

function parseStock(v: unknown): StockEntry[] {
  if (!Array.isArray(v)) return []
  const out: StockEntry[] = []
  for (const e of v) {
    if (!isRecord(e)) continue
    if (typeof e.item === 'string' && typeof e.price === 'number' && Number.isFinite(e.price)) {
      out.push({ item: e.item, price: e.price })
    }
  }
  return out
}

/** Text for a non-ok `cmd_result` — a mapped reason for a refusal (status 3), a generic line otherwise. */
function resultMessage(result: CmdResult): string {
  if (result.status === STATUS.refused) {
    const body = isRecord(result.body) ? result.body : {}
    const reason = body.reason
    return typeof reason === 'string' && reason in REFUSAL_TEXT ? REFUSAL_TEXT[reason] : GENERIC_REFUSAL
  }
  if (result.status === STATUS.rate_limited) return 'Too fast — try again.'
  return GENERIC_REFUSAL
}

export class ShopPanel {
  private root: HTMLDivElement
  private listEl: HTMLDivElement
  private msgEl: HTMLDivElement
  private registry: Registry
  private port: ShopPort
  private npcId = -1
  private pendingSeq = -1
  private pendingKind: 'list' | 'buy' | null = null
  private stock: StockEntry[] = []
  private credits = 0
  private openFlag = false

  constructor(registry: Registry, port: ShopPort) {
    this.registry = registry
    this.port = port

    this.root = document.createElement('div')
    this.root.style.cssText =
      'position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);' +
      'min-width:260px;font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,monospace;' +
      'color:#eaf2ff;background:rgba(8,14,28,0.92);padding:14px 16px;border-radius:8px;' +
      'border:1px solid rgba(120,160,255,0.35);display:none;'
    const title = document.createElement('div')
    title.textContent = 'SHOP'
    title.style.cssText = 'font-weight:700;margin-bottom:8px;letter-spacing:0.05em;'
    this.listEl = document.createElement('div')
    this.msgEl = document.createElement('div')
    this.msgEl.style.cssText = 'margin-top:8px;color:#ff8a80;min-height:1.2em;'
    const hint = document.createElement('div')
    hint.textContent = 'Esc to close'
    hint.style.cssText = 'margin-top:10px;opacity:0.6;font-size:11px;'
    this.root.append(title, this.listEl, this.msgEl, hint)
    document.body.appendChild(this.root)

    window.addEventListener('keydown', (e) => {
      if (!this.openFlag) return
      if (e.code === 'Escape' || MOVE_KEYS.has(e.code)) this.close()
    })
  }

  get isOpen(): boolean {
    return this.openFlag
  }

  /** Open on interact with a shop NPC: sends `shop_list`, renders once the result arrives. */
  open(npcId: number): void {
    if (this.openFlag) return
    this.openFlag = true
    this.npcId = npcId
    this.stock = []
    this.credits = this.port.credits()
    this.port.setPointerLock(false)
    this.root.style.display = ''
    this.msgEl.textContent = ''
    this.renderStock()
    this.pendingKind = 'list'
    this.pendingSeq = this.port.sendCmd(OP.shop_list, { npc: npcId })
  }

  close(): void {
    if (!this.openFlag) return
    this.openFlag = false
    this.pendingSeq = -1
    this.pendingKind = null
    this.root.style.display = 'none'
    this.port.setPointerLock(true)
  }

  /** Feed a `cmd_result`; ignored unless it matches the outstanding request. */
  handleResult(result: CmdResult): void {
    if (!this.openFlag || result.seq !== this.pendingSeq) return
    const kind = this.pendingKind
    this.pendingSeq = -1
    this.pendingKind = null
    if (result.status !== STATUS.ok) {
      this.msgEl.textContent = resultMessage(result)
      return
    }
    this.msgEl.textContent = ''
    const body = isRecord(result.body) ? result.body : {}
    if (kind === 'list') this.stock = parseStock(body.stock)
    else if (kind === 'buy') this.credits = num(body.credits, this.credits)
    this.renderStock()
  }

  private buy(item: string): void {
    if (this.pendingKind) return // one outstanding request at a time
    this.msgEl.textContent = ''
    this.pendingKind = 'buy'
    this.pendingSeq = this.port.sendCmd(OP.shop_buy, { npc: this.npcId, item, qty: 1 })
  }

  private renderStock(): void {
    this.listEl.replaceChildren()
    for (const entry of this.stock) {
      const def = itemDef(this.registry, entry.item)
      const row = document.createElement('div')
      row.style.cssText = 'display:flex;justify-content:space-between;gap:12px;align-items:center;padding:3px 0;'
      const label = document.createElement('span')
      label.textContent = `${def?.name ?? entry.item} — ${entry.price}cr` // untrusted name: textContent only
      const buyBtn = document.createElement('button')
      buyBtn.textContent = 'Buy'
      const afford = this.credits >= entry.price
      buyBtn.disabled = !afford
      row.style.opacity = afford ? '1' : '0.5'
      buyBtn.addEventListener('click', () => this.buy(entry.item))
      row.append(label, buyBtn)
      this.listEl.appendChild(row)
    }
    if (this.stock.length === 0) {
      const empty = document.createElement('div')
      empty.textContent = this.pendingKind === 'list' ? 'Loading…' : 'Nothing for sale.'
      this.listEl.appendChild(empty)
    }
  }
}
