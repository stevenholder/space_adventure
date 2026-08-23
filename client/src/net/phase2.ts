/**
 * Wire codec for the Phase 2 messages (docs/PROTOCOL.md, frozen).
 *
 * Mirrors server/internal/protocol/phase2.go. Payload-only: callers pair
 * Encoders return FRAMED messages (u16 type | payload), exactly like
 * encodeHello/encodeInput/encodePing in protocol.ts — a caller must never
 * have to remember which half of the codec frames and which does not.
 * Decoders take the payload with the type header already stripped by
 * unframe(), which is how the receive path already works.
 *
 * No DOM: TextEncoder/TextDecoder only (present under tsconfig.sim.json's
 * ES2022 lib), nothing from `window`.
 */

import {
  COLLIDER_BOX,
  COLLIDER_MAX,
  COLLIDER_SIZE,
  COLLIDER_SPHERE,
  MAX_CMD_BODY,
  MSG,
  ProtocolError,
  frame,
} from './protocol.js'

export { COLLIDER_BOX, COLLIDER_SPHERE }

function need(p: Uint8Array, n: number, what: string): void {
  if (p.length < n) throw new ProtocolError(`${what}: need ${n} B, got ${p.length}`)
}

function parseJson(bytes: Uint8Array, what: string): unknown {
  const text = new TextDecoder().decode(bytes)
  try {
    return JSON.parse(text)
  } catch (err) {
    throw new ProtocolError(`${what}: malformed JSON (${err instanceof Error ? err.message : String(err)})`)
  }
}

// ---------------------------------------------------------------------------
// cmd (C→S): u16 seq | u16 opcode | u32 data_len | bytes (UTF-8 JSON)
// ---------------------------------------------------------------------------

export function encodeCmd(seq: number, opcode: number, body: unknown): Uint8Array<ArrayBuffer> {
  const bodyBytes = new TextEncoder().encode(JSON.stringify(body))
  if (bodyBytes.length > MAX_CMD_BODY) {
    throw new ProtocolError(`cmd: body ${bodyBytes.length} B exceeds ${MAX_CMD_BODY} B limit`)
  }
  const out = new Uint8Array(8 + bodyBytes.length)
  const dv = new DataView(out.buffer)
  dv.setUint16(0, seq, true)
  dv.setUint16(2, opcode, true)
  dv.setUint32(4, bodyBytes.length, true)
  out.set(bodyBytes, 8)
  return frame(MSG.cmd, out)
}

// ---------------------------------------------------------------------------
// cmd_result (S→C): u16 seq | u16 opcode | u8 status | u32 data_len | bytes
// ---------------------------------------------------------------------------

export interface CmdResult {
  seq: number
  opcode: number
  status: number
  body: unknown
}

export function decodeCmdResult(payload: Uint8Array): CmdResult {
  need(payload, 9, 'cmd_result')
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength)
  const seq = dv.getUint16(0, true)
  const opcode = dv.getUint16(2, true)
  const status = dv.getUint8(4)
  const dataLen = dv.getUint32(5, true)
  if (dataLen > MAX_CMD_BODY) {
    throw new ProtocolError(`cmd_result: body ${dataLen} B exceeds ${MAX_CMD_BODY} B limit`)
  }
  need(payload, 9 + dataLen, 'cmd_result data')
  const body = parseJson(payload.subarray(9, 9 + dataLen), 'cmd_result')
  return { seq, opcode, status, body }
}

// ---------------------------------------------------------------------------
// defs (S→C): u32 data_len | bytes (UTF-8 JSON)
// ---------------------------------------------------------------------------

export function decodeDefs(payload: Uint8Array): unknown {
  need(payload, 4, 'defs')
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength)
  const dataLen = dv.getUint32(0, true)
  if (dataLen > MAX_CMD_BODY) {
    throw new ProtocolError(`defs: body ${dataLen} B exceeds ${MAX_CMD_BODY} B limit`)
  }
  need(payload, 4 + dataLen, 'defs data')
  return parseJson(payload.subarray(4, 4 + dataLen), 'defs')
}

// ---------------------------------------------------------------------------
// fire (C→S): u16 seq | f32 dir[3]
// ---------------------------------------------------------------------------

export function encodeFire(seq: number, dir: [number, number, number]): Uint8Array<ArrayBuffer> {
  const out = new Uint8Array(14)
  const dv = new DataView(out.buffer)
  dv.setUint16(0, seq, true)
  dv.setFloat32(2, dir[0], true)
  dv.setFloat32(6, dir[1], true)
  dv.setFloat32(10, dir[2], true)
  return frame(MSG.fire, out)
}

// ---------------------------------------------------------------------------
// colliders (S→C): u16 count | collider × count
// collider (42 B): u8 kind | u8 _pad | f32 center[3] | f32 half[3] | f32 quat[4]
// ---------------------------------------------------------------------------

export interface Collider {
  kind: number
  center: [number, number, number]
  half: [number, number, number]
  quat: [number, number, number, number]
}

export function decodeColliders(payload: Uint8Array): Collider[] {
  need(payload, 2, 'colliders')
  const dv = new DataView(payload.buffer, payload.byteOffset, payload.byteLength)
  const count = dv.getUint16(0, true)
  if (count > COLLIDER_MAX) {
    throw new ProtocolError(`colliders: count ${count} exceeds ${COLLIDER_MAX} limit`)
  }
  need(payload, 2 + count * COLLIDER_SIZE, 'colliders data')
  const colliders: Collider[] = new Array(count)
  for (let i = 0; i < count; i++) {
    const o = 2 + i * COLLIDER_SIZE
    colliders[i] = {
      kind: dv.getUint8(o),
      center: [dv.getFloat32(o + 2, true), dv.getFloat32(o + 6, true), dv.getFloat32(o + 10, true)],
      half: [dv.getFloat32(o + 14, true), dv.getFloat32(o + 18, true), dv.getFloat32(o + 22, true)],
      quat: [
        dv.getFloat32(o + 26, true),
        dv.getFloat32(o + 30, true),
        dv.getFloat32(o + 34, true),
        dv.getFloat32(o + 38, true),
      ],
    }
  }
  return colliders
}
