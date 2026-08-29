#!/usr/bin/env node
/**
 * C41 — the C# codec against the Go one, both directions.
 *
 * Same argument as t12, which does this for TypeScript: each codec implements
 * docs/PROTOCOL.md independently, and a codec's own round-trip test agrees
 * with its own bug. Only a cross-check catches a framing or offset slip. t12
 * exists because the first TypeScript encoders returned bare payloads where
 * the Go parsers expected `u16 type | payload`, which put a seq on the wire in
 * place of the message type and passed both suites.
 *
 *   S->C   Go `server codec emit`   ->  C# decoders
 *   C->S   C# encoders              ->  Go `server codec parse`
 *
 * The vectors are Go's, unchanged, so this and t12 assert the same bytes
 * against two different clients.
 *
 * Run: node test/t22-csharp-codec.mjs      (needs the dotnet SDK, no Editor)
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const tmp = mkdtempSync(path.join(tmpdir(), 'sa-cs-codec-'))
const goHex = path.join(tmp, 'go.hex')
const csHex = path.join(tmp, 'cs.hex')

const fails = []
const check = (name, got, want) => {
  if (got === want) console.log(`PASS ${name}`)
  else {
    console.log(`FAIL ${name}\n  got  ${got}\n  want ${want}`)
    fails.push(name)
  }
}

const go = (...args) =>
  execFileSync('go', ['run', './cmd/server', 'codec', ...args], {
    cwd: path.join(root, 'server'),
    encoding: 'utf8',
  })

const cs = (...args) =>
  execFileSync('dotnet', ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--', ...args], {
    cwd: root,
    encoding: 'utf8',
  })

// ---- S->C : Go encodes, C# must decode -------------------------------------
go('emit', goHex)
const decoded = cs('--codec-decode', goHex).trim().split('\n')

check('S->C cmd_result', decoded[0],
  'cmd_result seq=4097 opcode=2 status=3 data={"reason":"insufficient_credits"}')
check('S->C defs', decoded[1],
  'defs data={"items":[{"id":"weapon.pulse","price":250}]}')
// 0.3 is not representable in f32. The expectation is the f32 value Go wrote,
// printed round-trippably, not the decimal literal — asserting 0.3 here would
// fail on arithmetic that is correct.
check('S->C colliders[0] (box)', decoded[2],
  'collider kind=0 center=12,1.25,6 half=17,1.25,0.3 quat=0,0,0,1')
check('S->C colliders[1] (sphere)', decoded[3],
  'collider kind=1 center=8,1,4 half=1,0,0 quat=0.5,-0.5,0.5,0.5')
// Two props with DIFFERENT-length asset ids: a variable-length row is where a
// decoder that reads the wrong number of bytes still gets row 0 right and then
// walks off the end of row 1.
check('S->C props[0]', decoded[4],
  'prop asset=prop.barrel pos=12,1.25,6 quat=0,0,0,1 scale=1')
check('S->C props[1]', decoded[5],
  'prop asset=prop.dish pos=-8,0.5,4 quat=0.5,-0.5,0.5,0.5 scale=1.5')

// ---- C->S : C# encodes, Go must parse --------------------------------------
cs('--codec-encode', csHex)
let parsed
try {
  parsed = go('parse', csHex).trim().split('\n')
} catch (e) {
  // A framing mismatch surfaces here, which is the whole point of the file.
  console.log(`FAIL C->S: Go could not parse the C# frames\n${e.stderr ?? e.message}`)
  fails.push('C->S parse')
  parsed = []
}
if (parsed.length) {
  check('C->S cmd', parsed[0], 'cmd seq=4097 opcode=2 data={"npc":7,"item":"weapon.pulse","qty":1}')
  check('C->S fire', parsed[1], 'fire seq=513 dir=0,0,1')
}

console.log(fails.length ? `OVERALL: FAIL (${fails.join(', ')})` : `OVERALL: PASS (${4 + parsed.length} checks)`)
process.exit(fails.length ? 1 : 0)
