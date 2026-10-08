/**
 * Game wire codec — independent QA implementation of docs/PROTOCOL.md.
 *
 * frame = u16 type | payload, little-endian, one message per WS message.
 * This re-implements the contract from the spec (not from the product
 * source) so the harness is a third party.
 */

export const MSG = {
  HELLO: 0x0001,
  HELLO_ACK: 0x0002,
  INPUT: 0x0003,
  SNAPSHOT: 0x0004,
  SPAWN: 0x0005,
  DESPAWN: 0x0006,
  EVENT: 0x0007,
  PING: 0x0008,
  PONG: 0x0009,
  TERRAIN: 0x000a,
  BOARD: 0x000b,
  DISEMBARK: 0x000c,
  SEAT_RESULT: 0x000d,
  CMD: 0x000e,
  CMD_RESULT: 0x000f,
}

// seat_result codes (docs/PROTOCOL.md "Constants").
export const SEAT = { GRANTED: 0, OCCUPIED: 1, OUT_OF_RANGE: 2, INVALID: 3 }

// Must track the server: it closes 1002 on any other client_ver
// (docs/PROTOCOL.md "Versioning"). The harness still sends a token-less hello
// on purpose — that is the backward-compatible path, and it stays tested.
export const PROTOCOL_VERSION = 2
export const ACTION = { SPRINT: 0x0001, JUMP: 0x0002 }

// ---------------------------------------------------------------- encode
export function encodeHello(clientVer, name) {
  const nb = Buffer.from(name, 'utf8')
  const out = Buffer.alloc(2 + 4 + nb.length)
  out.writeUInt16LE(clientVer, 0)
  out.writeUInt32LE(nb.length, 2)
  nb.copy(out, 6)
  return out
}

/** input: f32 move_x | f32 move_y | f32 look_dir[3] | u16 action_mask | u16 seq (24 B) */
/**
 * `input` — 25 bytes: f32 v[5] | u16 action_mask | u16 seq | u8 mode.
 *
 * The mode byte is APPENDED, so every field above it sits where it has sat
 * since Phase 1 and the server still accepts a 24-byte payload as mode 0.
 * Several of the self-contained harnesses (t14, t18, t19) deliberately still
 * send 24 bytes: that keeps the compatibility path under live test instead of
 * only under a unit test.
 */
export function encodeInput(moveX, moveY, lookDir, actionMask, seq, mode = 0) {
  const out = Buffer.alloc(25)
  out.writeFloatLE(moveX, 0)
  out.writeFloatLE(moveY, 4)
  out.writeFloatLE(lookDir[0], 8)
  out.writeFloatLE(lookDir[1], 12)
  out.writeFloatLE(lookDir[2], 16)
  out.writeUInt16LE(actionMask & 0xffff, 20)
  out.writeUInt16LE(seq & 0xffff, 22)
  out.writeUInt8(mode & 0xff, 24)
  return out
}

/** Wrap payload into a game frame. */
/** board: u32 vehicle_id | u16 seat (docs/PROTOCOL.md 0x000B). */
export function encodeBoard(vehicleId, seat) {
  const out = Buffer.alloc(6)
  out.writeUInt32LE(vehicleId >>> 0, 0)
  out.writeUInt16LE(seat & 0xffff, 4)
  return out
}

/** disembark: no payload (0x000C). */
export function encodeDisembark() {
  return Buffer.alloc(0)
}

/** seat_result: u32 entity_id | u16 seat | u8 result (0x000D). */
export function decodeSeatResult(p) {
  if (p.length !== 7) throw new Error(`seat_result: ${p.length} bytes, want 7`)
  return { entityId: p.readUInt32LE(0), seat: p.readUInt16LE(4), result: p.readUInt8(6) }
}

export function frame(type, payload) {
  const out = Buffer.alloc(2 + payload.length)
  out.writeUInt16LE(type, 0)
  payload.copy(out, 2)
  return out
}

// ---------------------------------------------------------------- decode
export function decodeHelloAck(p) {
  if (p.length !== 12) throw new Error(`hello_ack: bad size ${p.length}`)
  return {
    serverVer: p.readUInt16LE(0),
    tickHz: p.readUInt16LE(2),
    worldSeed: p.readUInt32LE(4),
    entityId: p.readUInt32LE(8),
  }
}

export function decodeTerrain(p) {
  if (p.length < 10) throw new Error(`terrain: bad size ${p.length}`)
  const faceGrid = p.readUInt16LE(0)
  const radiusMin = p.readFloatLE(2)
  const radiusMax = p.readFloatLE(6)
  const want = 10 + 6 * faceGrid * faceGrid * 2
  if (p.length !== want) throw new Error(`terrain: size ${p.length} != ${want}`)
  const radii = new Uint16Array(6 * faceGrid * faceGrid)
  for (let i = 0; i < radii.length; i++) radii[i] = p.readUInt16LE(10 + 2 * i)
  return { faceGrid, radiusMin, radiusMax, radii, bytes: p.length }
}

/**
 * snapshot: u32 tick | u16 ack_seq | u16 count | entity × count
 * entity: u32 id | f32 pos[3] | f32 quat[4] | f32 vel[3] | u32 parent_id
 *         | u16 seat | u16 health | u8 flags | i8 pitch_q (54 B)
 */
const ENTITY_BYTES = 54

export function decodeSnapshot(p) {
  if (p.length < 8) throw new Error(`snapshot: bad size ${p.length}`)
  const tick = p.readUInt32LE(0)
  const ackSeq = p.readUInt16LE(4)
  const count = p.readUInt16LE(6)
  if (p.length !== 8 + ENTITY_BYTES * count) throw new Error(`snapshot: size ${p.length} != ${8 + ENTITY_BYTES * count}`)
  const entities = []
  for (let i = 0; i < count; i++) {
    const o = 8 + i * ENTITY_BYTES
    const e = {
      id: p.readUInt32LE(o),
      pos: [p.readFloatLE(o + 4), p.readFloatLE(o + 8), p.readFloatLE(o + 12)],
      quat: [p.readFloatLE(o + 16), p.readFloatLE(o + 20), p.readFloatLE(o + 24), p.readFloatLE(o + 28)],
      vel: [p.readFloatLE(o + 32), p.readFloatLE(o + 36), p.readFloatLE(o + 40)],
      parentId: p.readUInt32LE(o + 44),
      seat: p.readUInt16LE(o + 48),
      health: p.readUInt16LE(o + 50),
      flags: p.readUInt8(o + 52),
      pitchQ: p.readInt8(o + 53),
    }
    entities.push(e)
  }
  return { tick, ackSeq, entities }
}

export function decodeSpawn(p) {
  if (p.length < 10) throw new Error(`spawn: bad size ${p.length}`)
  const entityId = p.readUInt32LE(0)
  const entityType = p.readUInt16LE(4)
  const dataLen = p.readUInt32LE(6)
  const data = p.slice(10, 10 + dataLen).toString('utf8')
  // A player row is `name`, or `name \0 body` for a body other than
  // char.player (GDD "Characters"); `data` stays the raw string.
  const nul = data.indexOf('\0')
  const name = nul < 0 ? data : data.slice(0, nul)
  const body = nul < 0 ? 'char.player' : data.slice(nul + 1)
  return { entityId, entityType, data, name, body }
}

// event (0x0007): u32 entity_id | u16 event_id | u32 data_len | bytes data.
export const EVENT = { EQUIPPED: 0x0006, SKILL_XP: 0x000d, GATHER_END: 0x000e, WORN: 0x000f, CHAT: 0x0011, CRAFT_END: 0x0012 }
export function decodeEvent(p) {
  if (p.length < 10) throw new Error(`event: bad size ${p.length}`)
  const entityId = p.readUInt32LE(0)
  const eventId = p.readUInt16LE(4)
  const dataLen = p.readUInt32LE(6)
  return { entityId, eventId, data: p.slice(10, 10 + dataLen) }
}
// A `worn` event's data is `slot=item` (item empty when the slot cleared);
// slot `hair` since Phase 17, `skin` and `suit` since Phase 21.
export function decodeWorn(data) {
  const s = Buffer.isBuffer(data) ? data.toString('utf8') : String(data)
  const i = s.indexOf('=')
  return i < 0 ? { slot: s, item: '' } : { slot: s.slice(0, i), item: s.slice(i + 1) }
}

// A `chat` event's data is `name` NUL `text` (Phase 20); split on the first NUL.
export function decodeChat(data) {
  const s = Buffer.isBuffer(data) ? data.toString('utf8') : String(data)
  const i = s.indexOf('\0')
  return i < 0 ? { name: s, text: '' } : { name: s.slice(0, i), text: s.slice(i + 1) }
}

// A `craft_end` event's data is JSON `{recipe, reason, item, qty}` (Phase 22):
// one per finished unit (`done`, item/qty what landed) or the channel's
// early end (the reason, item/qty the unit output that did NOT land).
export function decodeCraftEnd(data) {
  const o = JSON.parse(Buffer.isBuffer(data) ? data.toString('utf8') : Buffer.from(data).toString('utf8'))
  return { recipe: o.recipe, reason: o.reason, item: o.item, qty: o.qty }
}

// cmd (0x000E) opcodes — docs/PROTOCOL.md "Constants" (only those a harness here names).
export const OP = { INVENTORY: 0x0004, EQUIP: 0x0003, GATHER: 0x0010, GATHER_CANCEL: 0x0011, CRAFT: 0x0012, CHAT: 0x0016 }

/** cmd: u16 seq | u16 opcode | u32 data_len | bytes data (UTF-8 JSON; a string/Buffer is sent as is). */
export function encodeCmd(seq, opcode, body) {
  const d = Buffer.isBuffer(body) ? body : Buffer.from(typeof body === 'string' ? body : JSON.stringify(body), 'utf8')
  const out = Buffer.alloc(8 + d.length)
  out.writeUInt16LE(seq & 0xffff, 0)
  out.writeUInt16LE(opcode & 0xffff, 2)
  out.writeUInt32LE(d.length, 4)
  d.copy(out, 8)
  return out
}

/** cmd_result: u16 seq | u16 opcode | u8 status | u32 data_len | bytes data (UTF-8 JSON). */
export function decodeCmdResult(p) {
  if (p.length < 9) throw new Error(`cmd_result: bad size ${p.length}`)
  const dataLen = p.readUInt32LE(5)
  const raw = p.slice(9, 9 + dataLen).toString('utf8')
  return { seq: p.readUInt16LE(0), opcode: p.readUInt16LE(2), status: p.readUInt8(4), raw, body: raw ? JSON.parse(raw) : null }
}

export function decodeDespawn(p) {
  if (p.length !== 4) throw new Error(`despawn: bad size ${p.length}`)
  return { entityId: p.readUInt32LE(0) }
}

// ---------------------------------------------------------------- math
export function norm3(v) {
  const n = Math.hypot(v[0], v[1], v[2])
  return n < 1e-12 ? [0, 0, 0] : [v[0] / n, v[1] / n, v[2] / n]
}

export function len3(v) {
  return Math.hypot(v[0], v[1], v[2])
}

/** wraparound-safe seq comparison (PROTOCOL "seq"): int16(a-b) > 0 */
export function seqNewer(a, b) {
  let d = (a - b) & 0xffff
  if (d >= 0x8000) d -= 0x10000
  return d > 0
}

/** Quaternion helpers (x,y,z,w) — same math as the GDD wire frame. */
export function quatMul(a, b) {
  const [ax, ay, az, aw] = a
  const [bx, by, bz, bw] = b
  return [
    aw * bx + ax * bw + ay * bz - az * by,
    aw * by - ax * bz + ay * bw + az * bx,
    aw * bz + ax * by - ay * bx + az * bw,
    aw * bw - ax * bx - ay * by - az * bz,
  ]
}

export function quatRotate(q, v) {
  const [x, y, z, w] = q
  const [vx, vy, vz] = v
  // t = 2 cross(qv, v)
  const tx = 2 * (y * vz - z * vy)
  const ty = 2 * (z * vx - x * vz)
  const tz = 2 * (x * vy - y * vx)
  return [
    vx + w * tx + (y * tz - z * ty),
    vy + w * ty + (z * tx - x * tz),
    vz + w * tz + (x * ty - y * tx),
  ]
}

export function dot3(a, b) {
  return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

export function angleBetweenDeg(a, b) {
  const d = Math.max(-1, Math.min(1, dot3(a, b) / (len3(a) * len3(b))))
  return (Math.acos(d) * 180) / Math.PI
}
