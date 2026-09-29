#!/usr/bin/env node
/**
 * C30 — drive conformance: one input script through the Go and C# rover
 * sims, per-tick pos/quat/vel deviation ≤ 1e-6 over ≥ 1000 ticks, plus
 * conformance to the GDD drive table within 5%.
 *
 * Same shape as t20 (C40): both dumps regenerated every run, both sims on
 * the quantised wire field, trajectories diffed tick-aligned. Quat is in
 * the diff because a drive model that agrees on position but disagrees on
 * heading diverges one tick later.
 *
 * The script is generated here, deterministically: accelerate, arc at full
 * lock, coast, reverse, then S-curves — every branch of stepRover gets
 * driven (throttle sign, steer sign, grounded and airborne ticks).
 *
 * Run: node test/t23-drive-conformance.mjs   (needs go + dotnet, no Editor)
 */
import { execFileSync } from 'node:child_process'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const WORLD = 'test/out/world-seed1337.json'
const SCRIPT = 'test/out/t23-drive-script.jsonl'
const SEED = 1337
const TICKS = 1200
const LIMIT = 1e-6
const VMAX = 16 // GDD vmax_drive

// ---- deterministic script ---------------------------------------------------
const lines = []
for (let i = 0; i < TICKS; i++) {
  let throttle = 0
  let steer = 0
  if (i < 200) throttle = 1
  else if (i < 400) { throttle = 1; steer = 1 }
  else if (i < 500) { /* coast */ }
  else if (i < 650) throttle = -1
  else { throttle = 1; steer = Math.sin(i / 25) }
  lines.push(JSON.stringify({ input: { throttle, steer } }))
}
mkdirSync(path.join(root, 'test/out'), { recursive: true })
writeFileSync(path.join(root, SCRIPT), lines.join('\n') + '\n')

// ---- both sims --------------------------------------------------------------
console.log('building the Go dump binary...')
execFileSync('go', ['build', '-o', '../test/out/server-dump', './cmd/server'],
  { cwd: path.join(root, 'server') })

console.log('replaying the script through the Go rover sim...')
const goOut = execFileSync(path.join(root, 'test/out/server-dump'),
  ['drive', '-inputs', SCRIPT, '-seed', String(SEED)],
  { cwd: root, maxBuffer: 256 << 20 })

console.log('replaying the same script through the C# rover sim...')
const csOut = execFileSync('dotnet',
  ['run', '--project', 'client/simdump', '--nologo', '--',
    '--drive', SCRIPT, '--world', WORLD],
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

let maxPos = 0, maxVel = 0, maxQuat = 0, groundedMismatch = 0
const n = Math.min(go.length, cs.length)
for (let i = 0; i < n; i++) {
  maxPos = Math.max(maxPos, dist3(go[i].pos, cs[i].pos))
  maxVel = Math.max(maxVel, dist3(go[i].vel, cs[i].vel))
  maxQuat = Math.max(maxQuat, dist4(go[i].quat, cs[i].quat))
  if (go[i].grounded !== cs[i].grounded) groundedMismatch++
}
check(`position agreement within ${LIMIT}`, maxPos <= LIMIT, `max ${maxPos.toExponential(3)} m`)
check(`velocity agreement within ${LIMIT}`, maxVel <= LIMIT, `max ${maxVel.toExponential(3)} m/s`)
check(`quat agreement within ${LIMIT}`, maxQuat <= LIMIT, `max ${maxQuat.toExponential(3)}`)
check('grounded agrees on every tick', groundedMismatch === 0, `${groundedMismatch} mismatches`)

// ---- GDD drive-table conformance (on the Go dump; the diff above makes the
// two sims interchangeable for this) -----------------------------------------
const tangSpeed = (row) => {
  const p = row.pos, v = row.vel
  const pl = Math.hypot(...p)
  const up = p.map((c) => c / pl)
  const r = v[0] * up[0] + v[1] * up[1] + v[2] * up[2]
  return Math.hypot(v[0] - r * up[0], v[1] - r * up[1], v[2] - r * up[2])
}
let peak = 0
for (const row of go) peak = Math.max(peak, tangSpeed(row))
check('top speed conforms to vmax_drive within 5%',
  peak <= VMAX * 1.05 && peak >= VMAX * 0.95, `peak ${peak.toFixed(3)} m/s of ${VMAX}`)

// accel_drive: ~2 s to vmax on the straight. Terrain steals some, so the bar
// is "past 90% of vmax within 3 s".
const at3s = tangSpeed(go[59])
check('accelerates to vmax on the GDD curve', tangSpeed(go[Math.min(59, n - 1)]) > 0 && peakWithin(go, 60, VMAX),
  `3 s speed ${at3s.toFixed(2)} m/s`)

function peakWithin (rows, ticks, vmax) {
  let m = 0
  for (let i = 0; i < Math.min(ticks, rows.length); i++) m = Math.max(m, tangSpeed(rows[i]))
  return m >= vmax * 0.9
}

const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([nm]) => nm).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
