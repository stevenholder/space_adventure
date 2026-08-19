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
}

export const PROTOCOL_VERSION = 1
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
export function encodeInput(moveX, moveY, lookDir, actionMask, seq) {
  const out = Buffer.alloc(24)
  out.writeFloatLE(moveX, 0)
  out.writeFloatLE(moveY, 4)
  out.writeFloatLE(lookDir[0], 8)
  out.writeFloatLE(lookDir[1], 12)
  out.writeFloatLE(lookDir[2], 16)
  out.writeUInt16LE(actionMask & 0xffff, 20)
  out.writeUInt16LE(seq & 0xffff, 22)
  return out
}

/** Wrap payload into a game frame. */
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
 * entity: u32 id | f32 pos[3] | f32 quat[4] | f32 vel[3] (44 B)
 */
export function decodeSnapshot(p) {
  if (p.length < 8) throw new Error(`snapshot: bad size ${p.length}`)
  const tick = p.readUInt32LE(0)
  const ackSeq = p.readUInt16LE(4)
  const count = p.readUInt16LE(6)
  if (p.length !== 8 + 44 * count) throw new Error(`snapshot: size ${p.length} != ${8 + 44 * count}`)
  const entities = []
  for (let i = 0; i < count; i++) {
    const o = 8 + i * 44
    const e = {
      id: p.readUInt32LE(o),
      pos: [p.readFloatLE(o + 4), p.readFloatLE(o + 8), p.readFloatLE(o + 12)],
      quat: [p.readFloatLE(o + 16), p.readFloatLE(o + 20), p.readFloatLE(o + 24), p.readFloatLE(o + 28)],
      vel: [p.readFloatLE(o + 32), p.readFloatLE(o + 36), p.readFloatLE(o + 40)],
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
  return { entityId, entityType, data }
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
