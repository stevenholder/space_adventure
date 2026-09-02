#!/usr/bin/env node
/**
 * C34 — flight conformance: one pilot input script through the Go and C#
 * ship sims, per-tick pos/quat/vel (and ω, and both regime flags)
 * deviation ≤ 1e-6 over ≥ 1000 ticks, plus GDD-table conformance within 5%.
 *
 * Same shape as t20 (C40): a COMMITTED script replayed through both sims
 * on the quantised wire field, both dumps regenerated every run. The
 * script (test/t25-flight-script.jsonl) was synthesized once by a chunked
 * autopilot against the Go dump — climb out keeping the nose outward,
 * through the boundary into space, banked turn, a boost leg, a nose-down
 * return burn, then hands-off to a landing — because blind phase scripts
 * either never reach space or drift there forever (no drag is no drag).
 * Every regime and every input axis is exercised; the script ends with
 * the ship grounded and still.
 *
 * Run: node test/t25-flight-conformance.mjs  (needs go + dotnet, no Editor)
 */
import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const WORLD = 'test/out/world-seed1337.json'
const SCRIPT = 'test/t25-flight-script.jsonl'
const SEED = 1337
const LIMIT = 1e-6
const VMAX = 40, VMAX_BOOST = 80

const TICKS = readFileSync(path.join(root, SCRIPT), 'utf8').split('\n').filter((l) => l.trim()).length
if (TICKS < 1000) throw new Error(`script is ${TICKS} ticks; C34 wants >= 1000`)

console.log('building the Go dump binary...')
execFileSync('go', ['build', '-o', '../test/out/server-dump', './cmd/server'],
  { cwd: path.join(root, 'server') })

console.log('replaying the script through the Go ship sim...')
const goOut = execFileSync(path.join(root, 'test/out/server-dump'),
  ['flight', '-inputs', SCRIPT, '-seed', String(SEED)],
  { cwd: root, maxBuffer: 256 << 20 })

console.log('replaying the same script through the C# ship sim...')
const csOut = execFileSync('dotnet',
  ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--',
    '--flight', SCRIPT, '--world', WORLD],
  { cwd: root, maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'inherit'] })

const parse = (buf) => buf.toString().split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l))
const go = parse(goOut)
const cs = parse(csOut)

const dist3 = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])
const dist4 = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2], a[3] - b[3])

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}

check('both sims ran the full script', go.length === TICKS && cs.length === TICKS,
  `go ${go.length}, cs ${cs.length}, want ${TICKS}`)

let maxPos = 0, maxVel = 0, maxQuat = 0, maxOmega = 0, regimeMismatch = 0
const n = Math.min(go.length, cs.length)
for (let i = 0; i < n; i++) {
  maxPos = Math.max(maxPos, dist3(go[i].pos, cs[i].pos))
  maxVel = Math.max(maxVel, dist3(go[i].vel, cs[i].vel))
  maxQuat = Math.max(maxQuat, dist4(go[i].quat, cs[i].quat))
  maxOmega = Math.max(maxOmega, dist3(go[i].omega, cs[i].omega))
  if (go[i].grounded !== cs[i].grounded || go[i].space !== cs[i].space) regimeMismatch++
}
check(`position agreement within ${LIMIT}`, maxPos <= LIMIT, `max ${maxPos.toExponential(3)} m`)
check(`velocity agreement within ${LIMIT}`, maxVel <= LIMIT, `max ${maxVel.toExponential(3)} m/s`)
check(`quat agreement within ${LIMIT}`, maxQuat <= LIMIT, `max ${maxQuat.toExponential(3)}`)
check(`omega agreement within ${LIMIT}`, maxOmega <= LIMIT, `max ${maxOmega.toExponential(3)}`)
check('grounded and space agree on every tick', regimeMismatch === 0, `${regimeMismatch} mismatches`)

// GDD conformance on the Go dump (the diff makes the sims interchangeable).
const speed = (row) => Math.hypot(...row.vel)
let peak = 0, sawSpace = false
for (let i = 0; i < n; i++) {
  peak = Math.max(peak, speed(go[i]))
  if (go[i].space) sawSpace = true
}
check('speed conforms to vmax_boost within 5%', peak <= VMAX_BOOST * 1.05,
  `peak ${peak.toFixed(2)} of ${VMAX_BOOST}`)
check('the boost leg exceeded plain vmax', peak > VMAX * 1.05,
  `peak ${peak.toFixed(2)} vs vmax ${VMAX}`)
check('the flight reached space', sawSpace)
check('the flight came back down and landed', go[n - 1].grounded && speed(go[n - 1]) < 0.01,
  `final radius ${Math.hypot(...go[n - 1].pos).toFixed(1)} m, |v| ${speed(go[n - 1]).toFixed(3)}`)

const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([nm]) => nm).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
