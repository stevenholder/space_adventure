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
 * Second scenario, "a rover never wedges" (2026-10-07): park on a scarp
 * past drive_slope_max (40°) facing uphill, hold throttle (refused — no
 * acceleration), reverse (downhill — allowed, it drives off), then drive on
 * the flatter ground below (normal). Diffed at STEEP_LIMIT (1e-10).
 *
 * Run: node test/t23-drive-conformance.mjs   (needs go + dotnet, no Editor)
 */
import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { loadField, norm3, slopeDeg, surfaceNormal } from './lib/field.mjs'

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

// ---- scenario 2: a rover never wedges ---------------------------------------
// A deterministic steep site: scan a lat/long grid of the captured wire field
// for the first direction measuring 44–50° (GDD normal_eps, 2°) — past
// drive_slope_max with margin, under max_slope so it is a scarp not a wall.
const STEEP_SCRIPT = 'test/out/t23-drive-steep.jsonl'
const STEEP_LIMIT = 1e-10
const field = loadField(JSON.parse(readFileSync(path.join(root, WORLD), 'utf8')))
const NEPS = (2 * Math.PI) / 180
let site = null
for (let la = -80; la <= 80 && !site; la += 0.5) {
  for (let lo = -180; lo < 180 && !site; lo += 0.5) {
    const a = la * Math.PI / 180, b = lo * Math.PI / 180
    const d = [Math.cos(a) * Math.cos(b), Math.sin(a), Math.cos(a) * Math.sin(b)]
    const sd = slopeDeg(field, d, NEPS)
    if (sd >= 44 && sd <= 50) site = { d, slope: sd }
  }
}
check('found a > drive_slope_max site to park on', site !== null,
  site ? `slope ${site.slope.toFixed(2)}° at dir [${site.d.map((c) => c.toFixed(4))}]` : 'none in the scan')
if (site) {
  // uphill = −downhill, downhill = normalize(g − n·dot(g, n)), g = −up
  const up = site.d, n = surfaceNormal(field, up, NEPS)
  const gn = -(up[0] * n[0] + up[1] * n[1] + up[2] * n[2])
  const uphill = norm3([up[0] + n[0] * gn, up[1] + n[1] * gn, up[2] + n[2] * gn])
  const PH = { up: 40, down: 100, coast: 40, flat: 200 }
  const sl = [JSON.stringify({ start: { dir: up, facing: uphill } })]
  for (let i = 0; i < PH.up; i++) sl.push(JSON.stringify({ input: { throttle: 1, steer: 0 } }))
  for (let i = 0; i < PH.down; i++) sl.push(JSON.stringify({ input: { throttle: -1, steer: 0 } }))
  for (let i = 0; i < PH.coast; i++) sl.push(JSON.stringify({ input: { throttle: 0, steer: 0 } }))
  for (let i = 0; i < PH.flat; i++) sl.push(JSON.stringify({ input: { throttle: 1, steer: 1 } }))
  writeFileSync(path.join(root, STEEP_SCRIPT), sl.join('\n') + '\n')
  const total = PH.up + PH.down + PH.coast + PH.flat
  const sgo = parse(execFileSync(path.join(root, 'test/out/server-dump'),
    ['drive', '-inputs', STEEP_SCRIPT, '-seed', String(SEED)], { cwd: root, maxBuffer: 256 << 20 }))
  const scs = parse(execFileSync('dotnet',
    ['run', '--project', 'client/simdump', '--nologo', '--', '--drive', STEEP_SCRIPT, '--world', WORLD],
    { cwd: root, maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'inherit'] }))
  check('steep: both sims ran the full script', sgo.length === total && scs.length === total,
    `go ${sgo.length}, cs ${scs.length}, want ${total}`)
  let mp = 0, mv = 0, mq = 0, gm = 0
  for (let i = 0; i < Math.min(sgo.length, scs.length); i++) {
    mp = Math.max(mp, dist3(sgo[i].pos, scs[i].pos))
    mv = Math.max(mv, dist3(sgo[i].vel, scs[i].vel))
    mq = Math.max(mq, dist4(sgo[i].quat, scs[i].quat))
    if (sgo[i].grounded !== scs[i].grounded) gm++
  }
  check(`steep: pos/vel/quat agreement within ${STEEP_LIMIT}`, mp <= STEEP_LIMIT && mv <= STEEP_LIMIT && mq <= STEEP_LIMIT,
    `max pos ${mp.toExponential(3)} m, vel ${mv.toExponential(3)} m/s, quat ${mq.toExponential(3)}`)
  check('steep: grounded agrees on every tick', gm === 0, `${gm} mismatches`)
  const start = sgo[0].pos
  const moved = (i) => dist3(sgo[i].pos, start)
  let upPeak = 0
  for (let i = 0; i < PH.up; i++) upPeak = Math.max(upPeak, tangSpeed(sgo[i]))
  check('steep: uphill throttle past drive_slope_max is refused', upPeak < 1e-3 && moved(PH.up - 1) < 1e-2,
    `peak ${upPeak.toExponential(2)} m/s, moved ${moved(PH.up - 1).toFixed(4)} m in ${PH.up} ticks`)
  const dEnd = PH.up + PH.down - 1
  const slopeAt = (i) => slopeDeg(field, norm3(sgo[i].pos), NEPS)
  check('steep: downhill throttle drives it off the scarp', moved(dEnd) > 5 && slopeAt(dEnd) <= 40,
    `moved ${moved(dEnd).toFixed(2)} m, now on ${slopeAt(dEnd).toFixed(1)}° (from ${site.slope.toFixed(1)}°)`)
  const fStart = PH.up + PH.down + PH.coast
  let fPeak = 0
  for (let i = fStart; i < total && i < sgo.length; i++) fPeak = Math.max(fPeak, tangSpeed(sgo[i]))
  check('steep: off the scarp it drives normally', fPeak >= VMAX * 0.5,
    `peak ${fPeak.toFixed(2)} m/s over the last ${PH.flat} ticks`)
}

const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([nm]) => nm).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
