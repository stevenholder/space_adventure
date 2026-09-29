#!/usr/bin/env node
/**
 * Cross-language collider parity — the Go and C# ResolveColliders must agree
 * with EACH OTHER.
 *
 * Replay reconciliation converges only while the two sims run the same rules.
 * Each side has unit tests, but those exercise one implementation against
 * itself and say nothing about agreement. This project has been bitten by that
 * twice: the strafe axis was wrong in BOTH sims for all of M1 (they agreed, so
 * the conformance diff passed and every criterion stayed green), and the
 * Phase 2 codecs were each internally consistent while disagreeing on framing.
 *
 * The C5 trajectory diff cannot close this gap: its route has no colliders on
 * any tick. So both sides run a shared scenario file and the results are
 * diffed here.
 *
 * The C# side replaced the TypeScript side when the browser client was retired
 * (ROADMAP U18). Same scenarios, same tolerance, same argument — the client
 * whose collision has to agree with the server's is the one that ships.
 *
 *   node test/t13-collide-parity.mjs      (needs the Go toolchain and dotnet)
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const scenarioPath = path.join(root, 'test', 'collide-scenarios.json')
const scenarios = JSON.parse(readFileSync(scenarioPath, 'utf8'))

const tmp = mkdtempSync(path.join(tmpdir(), 'sa-collide-'))
const goOut = path.join(tmp, 'go.json')
const csOut = path.join(tmp, 'cs.json')

execFileSync('go', ['run', './cmd/server', 'collide', scenarioPath, goOut], {
  cwd: path.join(root, 'server'),
  encoding: 'utf8',
})
execFileSync(
  'dotnet',
  ['run', '--project', 'client/simdump', '--nologo', '--',
    '--collide', scenarioPath, csOut],
  { cwd: root, encoding: 'utf8' },
)

const goResults = JSON.parse(readFileSync(goOut, 'utf8'))
const csResults = JSON.parse(readFileSync(csOut, 'utf8'))

// Positions are metres on a 150 m planet and velocities are m/s; 1e-9 is far
// below anything observable and far above f64 round-off across two languages.
const TOL = 1e-9
let fails = 0

if (csResults.length !== scenarios.length || goResults.length !== scenarios.length) {
  console.log(
    `FAIL result count: go=${goResults.length} cs=${csResults.length} want ${scenarios.length}`,
  )
  fails++
}

for (const [i, s] of scenarios.entries()) {
  const want = goResults[i]
  const got = csResults[i]

  // Both sides emit results in scenario order. A mismatch here is not a
  // rounding question, it means one side reordered or dropped a case and every
  // comparison below would be against the wrong scenario.
  if (want?.name !== s.name || got?.Name !== s.name) {
    console.log(`FAIL ${s.name}: result out of order (go=${want?.name} cs=${got?.Name})`)
    fails++
    continue
  }

  const dPos = Math.max(...got.Pos.map((v, k) => Math.abs(v - want.pos[k])))
  const dVel = Math.max(...got.Vel.map((v, k) => Math.abs(v - want.vel[k])))
  const gOK = got.Grounded === want.grounded

  if (dPos <= TOL && dVel <= TOL && gOK) {
    console.log(`PASS ${s.name.padEnd(30)} dPos=${dPos.toExponential(2)} dVel=${dVel.toExponential(2)}`)
  } else {
    fails++
    console.log(
      `FAIL ${s.name}\n  dPos=${dPos.toExponential(3)} dVel=${dVel.toExponential(3)} grounded cs=${got.Grounded} go=${want.grounded}\n` +
        `  cs pos ${JSON.stringify(got.Pos)}\n  go pos ${JSON.stringify(want.pos)}`,
    )
  }
}

console.log(fails ? `OVERALL: FAIL (${fails}/${scenarios.length})` : `OVERALL: PASS (${scenarios.length} scenarios)`)
process.exit(fails ? 1 : 0)
