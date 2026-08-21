/**
 * Codec round-trip smoke for src/net/protocol.ts (docs/PROTOCOL.md).
 *
 * Builds hello_ack / spawn / event / snapshot frames byte-by-byte per
 * PROTOCOL.md (hand-crafted — deliberately NOT via the client's own
 * encoders, so a shared offset bug cannot hide) and asserts the client
 * decoders read every field at the documented offset.
 *
 *   npx tsx tools/codec-smoke.ts
 */
import {
  MSG,
  ProtocolError,
  decodeDespawn,
  decodeEvent,
  decodeHelloAck,
  decodePong,
  decodeSnapshot,
  decodeSpawn,
  encodeInput,
  unframe,
} from '../src/net/protocol.js'

let failures = 0

function check(label: string, cond: boolean, detail: string): void {
  if (cond) return
  failures++
  console.error(`FAIL ${label}: ${detail}`)
}

// ---- byte writer (little-endian, per PROTOCOL.md "Transport") ----

function writer(len: number) {
  const buf = new ArrayBuffer(len)
  const dv = new DataView(buf)
  const u8 = new Uint8Array(buf)
  let off = 0
  return {
    u16(v: number) {
      dv.setUint16(off, v, true)
      off += 2
    },
    u32(v: number) {
      dv.setUint32(off, v, true)
      off += 4
    },
    f32(v: number) {
      dv.setFloat32(off, v, true)
      off += 4
    },
    raw(b: Uint8Array) {
      u8.set(b, off)
      off += b.length
    },
    done(): Uint8Array {
      if (off !== len) throw new Error(`writer: wrote ${off} of ${len} B`)
      return u8
    },
  }
}

/** frame = u16 type | payload (PROTOCOL.md "Frame"). */
function frame(type: number, payload: Uint8Array): ArrayBuffer {
  const buf = new ArrayBuffer(2 + payload.length)
  const dv = new DataView(buf)
  dv.setUint16(0, type, true)
  new Uint8Array(buf).set(payload, 2)
  return buf
}

function payloadOf(buf: ArrayBuffer): Uint8Array {
  return unframe(buf).payload
}

// ---- hello_ack: u16 server_ver | u16 tick_hz | u32 world_seed | u32 entity_id ----

{
  const w = writer(12)
  w.u16(1)
  w.u16(20)
  w.u32(0xdeadbeef)
  w.u32(7)
  const ack = decodeHelloAck(payloadOf(frame(MSG.hello_ack, w.done())))
  check(
    'hello_ack',
    ack.serverVer === 1 && ack.tickHz === 20 && ack.worldSeed === 0xdeadbeef && ack.entityId === 7,
    JSON.stringify(ack),
  )
}

// ---- spawn: u32 entity_id | u16 entity_type | u32 data_len | bytes data ----

{
  const name = 'qa-smoke' // 8 bytes
  const nameBytes = new TextEncoder().encode(name)
  const w = writer(4 + 2 + 4 + nameBytes.length)
  w.u32(7)
  w.u16(1)
  w.u32(nameBytes.length)
  w.raw(nameBytes)
  const spawn = decodeSpawn(payloadOf(frame(MSG.spawn, w.done())))
  check('spawn.id', spawn.entityId === 7, `got ${spawn.entityId}`)
  check('spawn.type', spawn.entityType === 1, `got ${spawn.entityType}`)
  check('spawn.name', spawn.name === name, `got ${JSON.stringify(spawn.name)}`)
}

// ---- event: u32 entity_id | u16 event_id | u32 data_len | bytes data ----

{
  const data = new Uint8Array([1, 2, 3])
  const w = writer(10 + 3)
  w.u32(9)
  w.u16(1)
  w.u32(3)
  w.raw(data)
  const ev = decodeEvent(payloadOf(frame(MSG.event, w.done())))
  const evOk =
    ev.entityId === 9 && ev.eventId === 1 && ev.data.length === 3 && ev.data.every((b, i) => b === data[i])
  check('event', evOk, `id=${ev.entityId} event=${ev.eventId} data=${[...ev.data]}`)
}

// ---- snapshot: u32 tick | u16 ack_seq | u16 count | entity × count
// entity: u32 id | f32 pos[3] | f32 quat[4] | f32 vel[3] (44 B, f32 per PROTOCOL.md) ----

{
  const pos: [number, number, number] = [6371, 0.5, -1.25] // all exactly representable in f32
  const quat: [number, number, number, number] = [1, 0, 0, 0]
  const vel: [number, number, number] = [0.25, -0.5, 0]
  const w = writer(8 + 4 + 12 + 16 + 12)
  w.u32(1234)
  w.u16(999)
  w.u16(1)
  w.u32(7)
  for (const v of pos) w.f32(v)
  for (const v of quat) w.f32(v)
  for (const v of vel) w.f32(v)
  const snap = decodeSnapshot(payloadOf(frame(MSG.snapshot, w.done())))
  const e = snap.entities[0]
  const eq = (a: number[], b: number[]) => a.length === b.length && a.every((v, i) => v === b[i])
  check('snapshot.tick', snap.tick === 1234, `got ${snap.tick}`)
  check('snapshot.ackSeq', snap.ackSeq === 999, `got ${snap.ackSeq}`)
  check('snapshot.count', snap.entities.length === 1, `got ${snap.entities.length}`)
  check('snapshot.id', e.id === 7, `got ${e.id}`)
  check('snapshot.pos', eq(e.pos, pos), `got ${JSON.stringify(e.pos)}`)
  check('snapshot.quat', eq(e.quat, quat), `got ${JSON.stringify(e.quat)}`)
  check('snapshot.vel', eq(e.vel, vel), `got ${JSON.stringify(e.vel)}`)
}

// ---- despawn / pong: bare u32 ----

{
  const w = writer(4)
  w.u32(42)
  check('despawn', decodeDespawn(payloadOf(frame(MSG.despawn, w.done()))) === 42, 'mismatch')
  check('pong', decodePong(payloadOf(frame(MSG.pong, w.done()))) === 42, 'mismatch')
}

// ---- input encoder: verify client C→S bytes against the documented offsets ----

{
  const fr = encodeInput(1, -0.5, [0, 0, 1], 0x0003, 0x1234)
  const dv = new DataView(fr.buffer)
  const ok =
    dv.getUint16(0, true) === MSG.input &&
    dv.getFloat32(2, true) === 1 &&
    dv.getFloat32(6, true) === -0.5 &&
    dv.getFloat32(10, true) === 0 &&
    dv.getFloat32(14, true) === 0 &&
    dv.getFloat32(18, true) === 1 &&
    dv.getUint16(22, true) === 0x0003 &&
    dv.getUint16(24, true) === 0x1234 &&
    fr.length === 2 + 24
  check('input.encode', ok, `len=${fr.length}`)
}

// ---- negative: truncated spawn must throw ProtocolError ----

{
  const w = writer(4 + 2 + 4) // header only, no data bytes
  w.u32(7)
  w.u16(1)
  w.u32(8)
  let threw = false
  try {
    decodeSpawn(payloadOf(frame(MSG.spawn, w.done())))
  } catch (err) {
    threw = err instanceof ProtocolError
  }
  check('spawn.truncated', threw, 'no ProtocolError on short payload')
}

console.log(
  JSON.stringify({
    frames: ['hello_ack', 'spawn', 'event', 'snapshot', 'despawn', 'pong', 'input.encode', 'spawn.truncated'],
    failures,
  }),
)
if (failures > 0) process.exit(1)
console.log('codec smoke OK')