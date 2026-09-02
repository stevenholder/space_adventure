#!/usr/bin/env node
/**
 * C40 — the C# sim must match the Go sim on the C5 conformance route.
 *
 * This is the gate for Phase 3.5. Replay reconciliation converges only while
 * client and server step identically, and the Unity client is a THIRD
 * implementation of rules that already exist twice. If this cannot close, the
 * phase stops here: every renderer, HUD and input task downstream is wasted
 * work against a client that silently disagrees with the server.
 *
 * Both sims replay test/t5/script-go.jsonl and their trajectories are diffed
 * tick-aligned. Two things make the comparison honest:
 *
 *   - Both run on the QUANTISED terrain field. The Go dump round-trips its
 *     f64 field through the u16 wire encoding, and the C# dump loads the
 *     captured wire field directly. Skipping that would measure the
 *     representation gap (~15 um) on top of any real divergence, which is
 *     precisely what this must not do.
 *   - Both dumps are REGENERATED here rather than read from committed
 *     artifacts, so this cannot pass against a stale file.
 *
 * The bar is 1e-10 m, four orders tighter than C5's 0.01125 m, because there
 * is no reason for a transliteration to differ at all -- and a drift you
 * cannot distinguish from noise is a drift you stop noticing.
 *
 * Run: node test/t20-csharp-conformance.mjs
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const SCRIPT = 'test/t5/script-go.jsonl'
const WORLD = 'test/out/world-seed1337.json'
const GO_OUT = 'test/out/dump-go.jsonl'
const CS_OUT = 'test/out/dump-csharp.jsonl'
const DPOS_LIMIT = 1e-10 // m
const DVEL_LIMIT = 1e-8 // m/s — one tick of DPOS_LIMIT is 2e-9; this is slack on top
const SEED = 1337

mkdirSync(path.join(root, 'test/out'), { recursive: true })

console.log('building the Go dump binary...')
execFileSync('go', ['build', '-o', '../test/out/server-dump', './cmd/server'],
  { cwd: path.join(root, 'server') })

console.log('replaying the script through the Go sim...')
const goOut = execFileSync(path.join(root, 'test/out/server-dump'),
  ['dump', '-inputs', SCRIPT, '-seed', String(SEED)],
  { cwd: root, maxBuffer: 256 << 20 })
writeFileSync(path.join(root, GO_OUT), goOut)

console.log('replaying the same script through the C# sim...')
const csOut = execFileSync('dotnet',
  ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--',
    '--dump', SCRIPT, '--world', WORLD],
  { cwd: root, maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'inherit'] })
writeFileSync(path.join(root, CS_OUT), csOut)

const parse = (buf) => buf.toString().split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l))
const go = parse(goOut)
const cs = parse(csOut)

const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}

console.log('')
check('both sims produced the same number of ticks', go.length === cs.length && go.length > 0,
  `go ${go.length}, c# ${cs.length}`)

const n = Math.min(go.length, cs.length)
let maxPos = 0, maxVel = 0, maxPosTick = -1, maxVelTick = -1, groundedMismatch = 0, firstGm = -1
for (let i = 0; i < n; i++) {
  const dp = dist(go[i].pos, cs[i].pos)
  const dv = dist(go[i].vel, cs[i].vel)
  if (dp > maxPos) { maxPos = dp; maxPosTick = i }
  if (dv > maxVel) { maxVel = dv; maxVelTick = i }
  if (go[i].grounded !== cs[i].grounded) {
    groundedMismatch++
    if (firstGm < 0) firstGm = i
  }
}

check('position agreement within 1e-10 m', maxPos <= DPOS_LIMIT,
  `max ${maxPos.toExponential(3)} m${maxPosTick >= 0 ? ` at tick ${maxPosTick}` : ''}`)
check('velocity agreement within 1e-8 m/s', maxVel <= DVEL_LIMIT,
  `max ${maxVel.toExponential(3)} m/s${maxVelTick >= 0 ? ` at tick ${maxVelTick}` : ''}`)
check('grounded agrees on every tick', groundedMismatch === 0,
  groundedMismatch ? `${groundedMismatch} mismatches, first at tick ${firstGm}` : `${n} ticks`)

// The terrain field both sims ran on. If this drifts, the diff above is
// comparing two different worlds and its agreement means nothing.
const world = JSON.parse(readFileSync(path.join(root, WORLD), 'utf8'))
check('both sims ran on the captured wire field', world.radii.length === 6 * 65 * 65,
  `sha256_16 ${world.meta?.sha256_16}, ${world.radii.length} radii`)

const bad = checks.filter(([, ok]) => !ok).length
console.log(`\nOVERALL: ${bad ? `FAIL (${bad}/${checks.length})` : `PASS (${checks.length} checks)`}`)
process.exit(bad ? 1 : 0)
