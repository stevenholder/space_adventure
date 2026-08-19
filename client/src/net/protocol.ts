/**
 * Wire codec for docs/PROTOCOL.md (frozen).
 *
 * frame = u16 type | payload — little-endian, no padding, one frame per
 * WebSocket message, max 64 KiB.
 *
 * Pure ArrayBuffer code: no DOM, no sockets. Encoders return fresh
 * Uint8Arrays; decoders throw `ProtocolError` on malformed or oversized
 * input so a bad peer can never poison the sim.
 */

export const MSG = {
  hello: 0x0001,
  hello_ack: 0x0002,
  input: 0x0003,
  snapshot: 0x0004,
  spawn: 0x0005,
  despawn: 0x0006,
  event: 0x0007,
  ping: 0x0008,
  pong: 0x0009,
  terrain: 0x000A,
} as const

export const ENTITY_TYPE_PLAYER = 0x0001

export const PROTOCOL_VERSION = 1
/** PROTOCOL: a message over 64 KiB closes the connection (code 1009). */
export const MAX_MESSAGE_SIZE = 64 * 1024

export class ProtocolError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'ProtocolError'
  }
}

// ---------------------------------------------------------------------------
// Frame helpers
// ---------------------------------------------------------------------------

function frame(type: number, payload: Uint8Array): Uint8Array<ArrayBuffer> {
  const out = new Uint8Array(2 + payload.length)
  const dv = new DataView(out.buffer)
  dv.setUint16(0, type, true)
  out.set(payload, 2)
  return out
}

/** Split a raw WebSocket message into (type, payload). */
export function unframe(buf: ArrayBuffer): { type: number; payload: Uint8Array } {
  if (buf.byteLength > MAX_MESSAGE_SIZE) {
    throw new ProtocolError(`message ${buf.byteLength} B exceeds ${MAX_MESSAGE_SIZE} B limit`)
  }
  if (buf.byteLength < 2) {
    throw new ProtocolError('message shorter than frame header')
  }
  const dv = new DataView(buf)
  const type = dv.getUint16(0, true)
  return { type, payload: new Uint8Array(buf, 2) }
}

function need(p: Uint8Array, n: number, what: string): void {
  if (p.length < n) throw new ProtocolError(`${what}: need ${n} B, got ${p.length}`)
}

// ---------------------------------------------------------------------------
// Encoders (C→S)
// ---------------------------------------------------------------------------

export function encodeHello(clientVer: number, name: string): Uint8Array<ArrayBuffer> {
  const nameBytes = new TextEncoder().encode(name)
  const out = new Uint8Array(2 + 4 + nameBytes.length)
  const dv = new DataView(out.buffer)
  dv.setUint16(0, clientVer, true)
  dv.setUint32(2, nameBytes.length, true)
  out.set(nameBytes, 6)
  return frame(MSG.hello, out)
}

/** 24 bytes: f32 move_x | f32 move_y | f32 look_dir[3] | u16 action_mask | u16 seq */
export function encodeInput(
  moveX: number,
  moveY: number,
  lookDir: [number, number, number],
  actionMask: number,
  seq: number,
): Uint8Array<ArrayBuffer> {
  const out = new Uint8Array(24)
  const dv = new DataView(out.buffer)
  dv.setFloat32(0, moveX, true)
  dv.setFloat32(4, moveY, true)
  dv.setFloat32(8, lookDir[0], true)
  dv.setFloat32(12, lookDir[1], true)
  dv.setFloat32(16, lookDir[2], true)
  dv.setUint16(20, actionMask, true)
  dv.setUint16(22, seq, true)
  return frame(MSG.input, out)
}

export function encodePing(tsMs: number): Uint8Array<ArrayBuffer> {
  const out = new Uint8Array(4)
  new DataView(out.buffer).setUint32(0, Math.floor(tsMs), true)
  return frame(MSG.ping, out)
}

// ---------------------------------------------------------------------------
// Decoders (S→C)
// ---------------------------------------------------------------------------

export interface HelloAck {
  serverVer: number
  tickHz: number
  worldSeed: number
  entityId: number
}

export interface EntityState {
  id: number
  pos: [number, number, number]
  quat: [number, number, number, number]
  vel: [number, number, number]
}

export interface Snapshot {
  tick: number
  ackSeq: number
  entities: EntityState[]
}

export interface SpawnMsg {
  entityId: number
  entityType: number
  name: string
}

export interface EventMsg {
  entityId: number
  eventId: number
  data: Uint8Array
}

export interface TerrainWire {
  faceGrid: number
  radiusMin: number
  radiusMax: number
  radii: Uint16Array
}

/** u16 server_ver | u16 tick_hz | u32 world_seed | u32 entity_id */
export function decodeHelloAck(p: Uint8Array): HelloAck {
  need(p, 12, 'hello_ack')
  const dv = new DataView(p.buffer, p.byteOffset, p.byteLength)
  return {
    serverVer: dv.getUint16(0, true),
    tickHz: dv.getUint16(2, true),
    worldSeed: dv.getUint32(4, true),
    entityId: dv.getUint32(8, true),
  }
}

const ENTITY_BYTES = 4 + 12 + 16 + 12 // id | pos | quat | vel

export function decodeSnapshot(p: Uint8Array): Snapshot {
  need(p, 8, 'snapshot')
  const dv = new DataView(p.buffer, p.byteOffset, p.byteLength)
  const tick = dv.getUint32(0, true)
  const ackSeq = dv.getUint16(4, true)
  const count = dv.getUint16(6, true)
  if (p.length < 8 + count * ENTITY_BYTES) {
    throw new ProtocolError(`snapshot: ${p.length - 8} B for ${count} entities`)
  }
  const entities: EntityState[] = new Array(count)
  for (let i = 0; i < count; i++) {
    const o = 8 + i * ENTITY_BYTES
    entities[i] = {
      id: dv.getUint32(o, true),
      pos: [dv.getFloat32(o + 4, true), dv.getFloat32(o + 8, true), dv.getFloat32(o + 12, true)],
      quat: [
        dv.getFloat32(o + 16, true),
        dv.getFloat32(o + 20, true),
        dv.getFloat32(o + 24, true),
        dv.getFloat32(o + 28, true),
      ],
      vel: [dv.getFloat32(o + 32, true), dv.getFloat32(o + 36, true), dv.getFloat32(o + 40, true)],
    }
  }
  return { tick, ackSeq, entities }
}

/** u32 entity_id | u16 entity_type | u32 data_len | bytes (M1: UTF-8 name) */
export function decodeSpawn(p: Uint8Array): SpawnMsg {
  need(p, 10, 'spawn')
  const dv = new DataView(p.buffer, p.byteOffset, p.byteLength)
  const dataLen = dv.getUint32(8, true)
  need(p, 10 + dataLen, 'spawn data')
  return {
    entityId: dv.getUint32(0, true),
    entityType: dv.getUint16(4, true),
    name: new TextDecoder().decode(p.subarray(10, 10 + dataLen)),
  }
}

export function decodeDespawn(p: Uint8Array): number {
  need(p, 4, 'despawn')
  return new DataView(p.buffer, p.byteOffset, p.byteLength).getUint32(0, true)
}

export function decodeEvent(p: Uint8Array): EventMsg {
  need(p, 10, 'event')
  const dv = new DataView(p.buffer, p.byteOffset, p.byteLength)
  const dataLen = dv.getUint32(8, true)
  need(p, 10 + dataLen, 'event data')
  return {
    entityId: dv.getUint32(0, true),
    eventId: dv.getUint16(4, true),
    data: p.subarray(10, 10 + dataLen),
  }
}

export function decodePong(p: Uint8Array): number {
  need(p, 4, 'pong')
  return new DataView(p.buffer, p.byteOffset, p.byteLength).getUint32(0, true)
}

/** u16 face_grid | f32 radius_min | f32 radius_max | u16 radii[6·G²] */
export function decodeTerrain(p: Uint8Array): TerrainWire {
  need(p, 10, 'terrain')
  const dv = new DataView(p.buffer, p.byteOffset, p.byteLength)
  const faceGrid = dv.getUint16(0, true)
  if (faceGrid < 2 || faceGrid > 73) {
    // PROTOCOL: face_grid above 73 overflows the 64 KiB message limit.
    throw new ProtocolError(`terrain: face_grid ${faceGrid} exceeds 73 (64 KiB message limit)`)
  }
  const n = 6 * faceGrid * faceGrid
  if (p.length !== 10 + 2 * n) {
    throw new ProtocolError(`terrain: ${p.length - 10} B for ${n} radii`)
  }
  return {
    faceGrid,
    radiusMin: dv.getFloat32(2, true),
    radiusMax: dv.getFloat32(6, true),
    radii: new Uint16Array(p.buffer.slice(p.byteOffset + 10, p.byteOffset + 10 + 2 * n)),
  }
}

/**
 * Wraparound-safe seq comparison (PROTOCOL): seq is u16 and wraps every
 * ~18 min at 60 Hz. `int16(a − b) > 0` iff a is newer than b.
 */
export function seqNewer(a: number, b: number): boolean {
  return ((a - b) & 0xffff) <= 0x7fff && a !== b
}
