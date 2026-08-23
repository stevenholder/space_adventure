#!/usr/bin/env node
/**
 * Cross-language wire parity — the Go and TypeScript codecs must agree with
 * EACH OTHER, not merely round-trip themselves.
 *
 * Both codecs implement docs/PROTOCOL.md independently. Each one's unit tests
 * encode and decode with the same implementation, which proves self-consistency
 * and nothing about interoperability. Phase 2's first cut compiled on both
 * ends and passed both suites while still disagreeing: the TypeScript encoders
 * returned bare payloads where the Go parsers expected `u16 type | payload`,
 * so `sendRaw(encodeCmd(...))` would have put the seq on the wire as the
 * message type. Only a Go-parses-TS check could see it.
 *
 * Same shape as the C5 trajectory diff: each side writes a file, the other
 * reads it, and the comparison is a diff rather than bespoke harness code.
 *
 *   S->C   Go `server codec emit`  ->  TS decoders
 *   C->S   TS encoders             ->  Go `server codec parse`
 *
 * Run with tsx (it imports the client's TypeScript codec directly):
 *   ./client/node_modules/.bin/tsx test/t12-codec-parity.mjs
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { encodeCmd, encodeFire, decodeCmdResult, decodeDefs, decodeColliders } from '../client/src/net/phase2.js'
import { OP, STATUS, COLLIDER_BOX, COLLIDER_SPHERE } from '../client/src/net/protocol.js'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const tmp = mkdtempSync(path.join(tmpdir(), 'sa-codec-'))
const goHex = path.join(tmp, 'go.hex')
const tsHex = path.join(tmp, 'ts.hex')

const fails = []
const check = (name, got, want) => {
  const g = JSON.stringify(got)
  const w = JSON.stringify(want)
  if (g === w) console.log(`PASS ${name}`)
  else {
    console.log(`FAIL ${name}\n  got  ${g}\n  want ${w}`)
    fails.push(name)
  }
}

const go = (...args) =>
  execFileSync('go', ['run', './cmd/server', 'codec', ...args], {
    cwd: path.join(root, 'server'),
    encoding: 'utf8',
  })

// ---- S->C : Go encodes, TypeScript must decode -----------------------------
go('emit', goHex)
const emitted = Object.fromEntries(
  readFileSync(goHex, 'utf8').trim().split('\n').map((l) => l.split(' ')),
)
const payloadOf = (hex) => new Uint8Array(Buffer.from(hex, 'hex')).subarray(2) // strip u16 type

check('S->C cmd_result', decodeCmdResult(payloadOf(emitted.cmd_result)), {
  seq: 4097,
  opcode: OP.shop_buy,
  status: STATUS.refused,
  body: { reason: 'insufficient_credits' },
})
check('S->C defs', decodeDefs(payloadOf(emitted.defs)), {
  items: [{ id: 'weapon.pulse', price: 250 }],
})
// 0.3 is not representable in f32; assert the f32 value Go actually wrote
// rather than the decimal literal, or this "fails" on arithmetic that is correct.
check('S->C colliders', decodeColliders(payloadOf(emitted.colliders)), [
  { kind: COLLIDER_BOX, center: [12, 1.25, 6], half: [17, 1.25, Math.fround(0.3)], quat: [0, 0, 0, 1] },
  { kind: COLLIDER_SPHERE, center: [8, 1, 4], half: [1, 0, 0], quat: [0.5, -0.5, 0.5, 0.5] },
])

// ---- C->S : TypeScript encodes, Go must parse ------------------------------
const hex = (u) => Buffer.from(u).toString('hex')
writeFileSync(
  tsHex,
  `cmd ${hex(encodeCmd(4097, OP.shop_buy, { npc: 7, item: 'weapon.pulse', qty: 1 }))}\n` +
    `fire ${hex(encodeFire(513, [0, 0, 1]))}\n`,
)

let parsed
try {
  parsed = go('parse', tsHex).trim().split('\n')
} catch (e) {
  // A framing mismatch surfaces here, which is the whole point of the file.
  console.log(`FAIL C->S: Go could not parse the TypeScript frames\n${e.stderr ?? e.message}`)
  fails.push('C->S parse')
  parsed = []
}
if (parsed.length) {
  check('C->S cmd', parsed[0], 'cmd seq=4097 opcode=2 data={"npc":7,"item":"weapon.pulse","qty":1}')
  check('C->S fire', parsed[1], 'fire seq=513 dir=0,0,1')
}

console.log(fails.length ? `OVERALL: FAIL (${fails.join(', ')})` : 'OVERALL: PASS')
process.exit(fails.length ? 1 : 0)
