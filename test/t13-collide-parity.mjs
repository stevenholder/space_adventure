#!/usr/bin/env node
/**
 * Cross-language collider parity — the Go and TypeScript ResolveColliders
 * must agree with EACH OTHER.
 *
 * Replay reconciliation converges only while the two sims run the same rules.
 * Each side has unit tests, but those exercise one implementation against
 * itself and say nothing about agreement. This project has now been bitten by
 * that twice: the strafe axis was wrong in BOTH sims for all of M1 (they
 * agreed, so the conformance diff passed and every criterion stayed green),
 * and the Phase 2 codecs were each internally consistent while disagreeing on
 * framing.
 *
 * The C5 trajectory diff cannot close this gap: its route has no colliders on
 * any tick. So both sides run a shared scenario file and the results are
 * diffed here.
 *
 *   ./client/node_modules/.bin/tsx test/t13-collide-parity.mjs
 */
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { resolveColliders } from '../client/src/sim/collide.js'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const scenarioPath = path.join(root, 'test', 'collide-scenarios.json')
const scenarios = JSON.parse(readFileSync(scenarioPath, 'utf8'))

const goOut = path.join(mkdtempSync(path.join(tmpdir(), 'sa-collide-')), 'go.json')
execFileSync('go', ['run', './cmd/server', 'collide', scenarioPath, goOut], {
  cwd: path.join(root, 'server'),
  encoding: 'utf8',
})
const goResults = JSON.parse(readFileSync(goOut, 'utf8'))

// The Go side converts collider fields to float32 (they are f32 on the wire),
// so the TypeScript side must too or the two disagree on arithmetic that is
// not actually different. Math.fround is exactly that narrowing.
const f32v = (v) => v.map(Math.fround)

const v3 = (a) => ({ x: a[0], y: a[1], z: a[2] })
const arr = (v) => [v.x, v.y, v.z]

// Positions are metres on a 150 m planet and velocities are m/s; 1e-9 is far
// below anything observable and far above f64 round-off across two languages.
const TOL = 1e-9
let fails = 0

for (const [i, s] of scenarios.entries()) {
  const cs = s.colliders.map((c) => ({
    kind: c.kind,
    center: f32v(c.center),
    half: f32v(c.half),
    quat: f32v(c.quat),
  }))
  const got = resolveColliders(v3(s.pos), v3(s.vel), v3(s.up), s.grounded, cs, () => s.radius)
  const want = goResults[i]

  if (want.name !== s.name) {
    console.log(`FAIL ${s.name}: Go result out of order (got ${want.name})`)
    fails++
    continue
  }

  const dPos = Math.max(...arr(got.pos).map((v, k) => Math.abs(v - want.pos[k])))
  const dVel = Math.max(...arr(got.vel).map((v, k) => Math.abs(v - want.vel[k])))
  const gOK = got.grounded === want.grounded

  if (dPos <= TOL && dVel <= TOL && gOK) {
    console.log(`PASS ${s.name.padEnd(30)} dPos=${dPos.toExponential(2)} dVel=${dVel.toExponential(2)}`)
  } else {
    fails++
    console.log(
      `FAIL ${s.name}\n  dPos=${dPos.toExponential(3)} dVel=${dVel.toExponential(3)} grounded ts=${got.grounded} go=${want.grounded}\n` +
        `  ts pos ${JSON.stringify(arr(got.pos))}\n  go pos ${JSON.stringify(want.pos)}`,
    )
  }
}

console.log(fails ? `OVERALL: FAIL (${fails}/${scenarios.length})` : `OVERALL: PASS (${scenarios.length} scenarios)`)
process.exit(fails ? 1 : 0)
