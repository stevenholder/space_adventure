#!/usr/bin/env node
/**
 * Rock-scatter parity -- the server's Go RockScatter must place exactly the
 * rocks the client's C# RockScatter draws.
 *
 * Rocks became solid (playtest 2026-10-02, "full collision"). Neither side
 * ships rocks to the other: both scatter them from the world seed on the
 * wire-quantized field. If the two ever disagree on a single accept/reject,
 * every rock after it differs and players collide with rocks they cannot see
 * (or pass through ones they can), so the whole list is diffed: same count,
 * same variants, positions/directions/scales/spins within 1e-9.
 *
 *   node test/t38-rock-parity.mjs     (needs the Go toolchain and dotnet)
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const WORLD = path.join(root, 'test/out/world-seed1337.json')
const SEED = '1337'
const tmp = mkdtempSync(path.join(tmpdir(), 'sa-rocks-'))
const goOut = path.join(tmp, 'go.json')
const csOut = path.join(tmp, 'cs.json')

execFileSync('go', ['run', './cmd/server', 'rocks', '-seed', SEED, goOut], { cwd: path.join(root, 'server') })
execFileSync('dotnet', ['run', '--project', 'client/simdump', '--nologo', '--', '--rocks', WORLD, SEED, csOut], { cwd: root })

const go = JSON.parse(readFileSync(goOut, 'utf8'))
const cs = JSON.parse(readFileSync(csOut, 'utf8'))
const TOL = 1e-9
let fails = 0
const check = (name, ok, detail) => {
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}  ${detail}`)
  if (!ok) fails++
}
check('same rock count', go.length === cs.length && go.length > 0, `go=${go.length} cs=${cs.length}`)
let worst = 0, variants = 0
for (let i = 0; i < Math.min(go.length, cs.length); i++) {
  const a = go[i], b = cs[i]
  for (const k of ['Pos', 'Dir', 'Scale']) for (let j = 0; j < 3; j++) worst = Math.max(worst, Math.abs(a[k][j] - b[k][j]))
  worst = Math.max(worst, Math.abs(a.Spin - b.Spin))
  if (a.Variant !== b.Variant) variants++
}
check('every rock in the same place', worst < TOL, `max diff ${worst.toExponential(2)} (tol ${TOL})`)
check('every rock the same model', variants === 0, `${variants} mismatched variants`)
console.log(`OVERALL: ${fails === 0 ? 'PASS' : `FAIL (${fails})`}`)
process.exit(fails === 0 ? 0 : 1)
