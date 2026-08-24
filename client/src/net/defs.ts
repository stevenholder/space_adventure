/**
 * Parses the `defs` message into typed lookups (docs/PROTOCOL.md, "defs —
 * the data the client needs"; server/internal/defs/defs.go, buildPayload).
 *
 * The payload arrives over the socket, so it is untrusted even though the
 * server that sent it is not hostile: a missing or wrong-typed field
 * becomes a typed default here, never an `undefined` that reaches the
 * renderer. A malformed def must not be a denial of service on the player.
 *
 * The client MUST NOT fire, predict damage, or draw an inventory before
 * `defs` arrives — the same rule as `terrain`. Callers gate on `ready`.
 *
 * No DOM: built on decodeDefs (./phase2.ts), which is itself DOM-free.
 */

import { decodeDefs } from './phase2.js'

export interface Weapon {
  damage: number
  fireInterval: number
  magazine: number
  reloadTime: number
  ammoItem: string
  maxRange: number
  falloffStart: number
  falloffEnd: number
  falloffMin: number
  spreadBase: number
  spreadMax: number
  spreadPerShot: number
  spreadDecay: number
}

// server/internal/defs/defs.go, type Item.
export interface ItemDef {
  id: string
  name: string
  kind: string
  slot: string
  asset: string
  stackMax: number
  weapon: Weapon | null
}

// server/internal/defs/defs.go, type EntityDef.
export interface EntityDef {
  type: string
  asset: string
  maxHealth: number
  hitboxRadius: number
  hitboxHeight: number
  damageable: boolean
  respawn: number
}

// server/internal/defs/defs.go, type payloadNPC (display-only: no stock,
// which arrives per-NPC via `shop_list`).
export interface NpcDef {
  name: string
  asset: string
  verb: string
}

// server/internal/defs/defs.go, type payloadConstants (GDD "Interaction").
export interface InteractConstants {
  interactDist: number
  interactCone: number
}

export interface Registry {
  items: Record<string, ItemDef>
  entities: Record<string, EntityDef>
  npcs: Record<string, NpcDef>
  interact: InteractConstants
  /**
   * False until a `defs` message has been successfully parsed. Callers
   * must gate firing, damage prediction and inventory rendering on this,
   * the same way they already gate on terrain having arrived.
   */
  ready: boolean
}

const DEFAULT_INTERACT: InteractConstants = { interactDist: 3.0, interactCone: 20.0 }

/** An empty, `ready: false` registry — the safe starting/fallback state. */
export function emptyRegistry(): Registry {
  return { items: {}, entities: {}, npcs: {}, interact: { ...DEFAULT_INTERACT }, ready: false }
}

// --- untrusted-JSON helpers: every read has a typed default -------------

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}

function str(v: unknown, fallback: string): string {
  return typeof v === 'string' ? v : fallback
}

function num(v: unknown, fallback: number): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback
}

function bool(v: unknown, fallback: boolean): boolean {
  return typeof v === 'boolean' ? v : fallback
}

function parseWeapon(v: unknown): Weapon | null {
  if (!isRecord(v)) return null
  return {
    damage: num(v.damage, 0),
    fireInterval: num(v.fire_interval, 0),
    magazine: num(v.magazine, 0),
    reloadTime: num(v.reload_time, 0),
    ammoItem: str(v.ammo_item, ''),
    maxRange: num(v.max_range, 0),
    falloffStart: num(v.falloff_start, 0),
    falloffEnd: num(v.falloff_end, 0),
    falloffMin: num(v.falloff_min, 0),
    spreadBase: num(v.spread_base, 0),
    spreadMax: num(v.spread_max, 0),
    spreadPerShot: num(v.spread_per_shot, 0),
    spreadDecay: num(v.spread_decay, 0),
  }
}

function parseItem(id: string, v: unknown): ItemDef {
  const r = isRecord(v) ? v : {}
  return {
    id: str(r.id, id),
    name: str(r.name, id),
    kind: str(r.kind, ''),
    slot: str(r.slot, ''),
    asset: str(r.asset, ''),
    stackMax: num(r.stack_max, 1),
    weapon: parseWeapon(r.weapon),
  }
}

function parseEntity(type: string, v: unknown): EntityDef {
  const r = isRecord(v) ? v : {}
  const hitbox = isRecord(r.hitbox) ? r.hitbox : {}
  return {
    type: str(r.type, type),
    asset: str(r.asset, ''),
    maxHealth: num(r.max_health, 0),
    hitboxRadius: num(hitbox.radius, 0),
    hitboxHeight: num(hitbox.height, 0),
    damageable: bool(r.damageable, false),
    respawn: num(r.respawn, 0),
  }
}

function parseNpc(v: unknown): NpcDef {
  const r = isRecord(v) ? v : {}
  return {
    name: str(r.name, ''),
    asset: str(r.asset, ''),
    verb: str(r.verb, ''),
  }
}

function parseInteract(v: unknown): InteractConstants {
  const r = isRecord(v) ? v : {}
  return {
    interactDist: num(r.interact_dist, DEFAULT_INTERACT.interactDist),
    interactCone: num(r.interact_cone, DEFAULT_INTERACT.interactCone),
  }
}

function parseRecordMap<T>(v: unknown, parseOne: (key: string, val: unknown) => T): Record<string, T> {
  if (!isRecord(v)) return {}
  const out: Record<string, T> = {}
  for (const [key, val] of Object.entries(v)) out[key] = parseOne(key, val)
  return out
}

/**
 * Parses one `defs` message payload into a ready Registry. Framing/JSON
 * corruption (decodeDefs) is a wire-level protocol violation and still
 * throws ProtocolError, same as the other decode* calls in netClient; a
 * well-formed JSON body with missing or wrong-typed fields never throws —
 * every field falls back to a typed default instead.
 */
export function parseDefs(payload: Uint8Array): Registry {
  const raw = decodeDefs(payload)
  const r = isRecord(raw) ? raw : {}
  return {
    items: parseRecordMap(r.items, parseItem),
    entities: parseRecordMap(r.entities, parseEntity),
    npcs: parseRecordMap(r.npcs, (_id, val) => parseNpc(val)),
    interact: parseInteract(r.constants),
    ready: true,
  }
}

export function itemDef(reg: Registry, id: string): ItemDef | undefined {
  return reg.items[id]
}

export function entityDef(reg: Registry, type: string): EntityDef | undefined {
  return reg.entities[type]
}
