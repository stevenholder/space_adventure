/**
 * C80: efficacy multipliers are conformant across both sims.
 *
 * The movement multipliers (Phase 11) enter the MIRRORED simulation, so a
 * non-unit sprint_mult must produce bit-for-bit-close trajectories in the Go
 * sim and the C# sim — otherwise a trained player's prediction diverges from
 * the server and reconciles into a rubber-band. This drives a sprint script
 * at 1.30x through both dumps and diffs at the same 1e-10 m bar t20 holds.
 *
 * Both dumps read `sprint_mult` off the input line (Go dump.go, C# SimDump);
 * a fresh field is captured the same way t20's is.
 *
 * Run: node test/t35-skill-conformance.mjs   (needs `make up` for the world)
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(fileURLToPath(import.meta.url)).replace(/\/test$/, '')
const SCRIPT = 'test/out/skill-sprint-script.jsonl'
const WORLD = 'test/out/world-seed1337.json'
const SEED = 1337
const MULT = 1.30 // Athletics ~ level 76
const DPOS = 1e-10
const DVEL = 1e-8

mkdirSync(path.join(root, 'test/out'), { recursive: true })

// A sprint straight ahead for 400 ticks at the trained multiplier. The look
// is the spawn facing; move_y 1 with the sprint bit and sprint_mult set.
const lines = [
  JSON.stringify({ state: { pos: [0, 150.00018310826277, 0], vel: [0, 0, 0], grounded: true, facing: [1, 0, 0] } }),
]
for (let i = 0; i < 400; i++) {
  lines.push(JSON.stringify({
    input: { move_x: 0, move_y: 1, look: [1, 0, 0], action_mask: 1, sprint_mult: MULT },
  }))
}
writeFileSync(path.join(root, SCRIPT), lines.join('\n') + '\n')

console.log('building the Go dump binary...')
execFileSync('go', ['build', '-o', '../test/out/server-dump', './cmd/server'],
  { cwd: path.join(root, 'server') })

const goOut = execFileSync(path.join(root, 'test/out/server-dump'),
  ['dump', '-inputs', SCRIPT, '-seed', String(SEED)], { cwd: root, maxBuffer: 256 << 20 })
const csOut = execFileSync('dotnet',
  ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--',
    '--dump', SCRIPT, '--world', WORLD],
  { cwd: root, maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'inherit'] })

const parse = (b) => b.toString().split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l))
const go = parse(goOut), cs = parse(csOut)
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])

let worstPos = 0, worstVel = 0
for (let i = 0; i < Math.min(go.length, cs.length); i++) {
  worstPos = Math.max(worstPos, dist(go[i].pos, cs[i].pos))
  worstVel = Math.max(worstVel, dist(go[i].vel, cs[i].vel))
}

// Prove the multiplier ACTUALLY changed the run: at 1.30x sprint the top
// speed is 7.5 * 1.30 = 9.75 m/s, well past the 7.5 baseline.
const topSpeed = Math.max(...go.map((l) => Math.hypot(...l.vel)))

const checks = [
  ['same tick count', go.length === cs.length && go.length > 0, `go ${go.length} c# ${cs.length}`],
  ['position conformant', worstPos <= DPOS, `worst ${worstPos.toExponential(2)} m`],
  ['velocity conformant', worstVel <= DVEL, `worst ${worstVel.toExponential(2)} m/s`],
  ['the multiplier bit', topSpeed > 9.0, `top ${topSpeed.toFixed(2)} m/s (baseline 7.5)`],
]
// --- drive: rover accel at 1.25x through both drive dumps ---
const DRIVE_SCRIPT = 'test/out/skill-drive-script.jsonl'
const driveLines = []
for (let i = 0; i < 120; i++) {
  driveLines.push(JSON.stringify({ input: { throttle: 1, steer: 0, eff_mult: 1.25 } }))
}
writeFileSync(path.join(root, DRIVE_SCRIPT), driveLines.join('\n') + '\n')
const goDrive = parse(execFileSync(path.join(root, 'test/out/server-dump'),
  ['drive', '-inputs', DRIVE_SCRIPT, '-seed', String(SEED)], { cwd: root, maxBuffer: 256 << 20 }))
const csDrive = parse(execFileSync('dotnet',
  ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--', '--drive', DRIVE_SCRIPT, '--world', WORLD],
  { cwd: root, maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'inherit'] }))
let driveWorst = 0
for (let i = 0; i < Math.min(goDrive.length, csDrive.length); i++) driveWorst = Math.max(driveWorst, dist(goDrive[i].pos, csDrive[i].pos))
checks.push(['drive conformant at 1.25x', driveWorst <= 1e-6 && goDrive.length === csDrive.length, `worst ${driveWorst.toExponential(2)} m`])

// --- flight: ship thrust at 1.25x through both flight dumps ---
const FLIGHT_SCRIPT = 'test/out/skill-flight-script.jsonl'
const flightLines = []
for (let i = 0; i < 120; i++) {
  flightLines.push(JSON.stringify({ input: { thrust: 1, roll: 0, yaw_rate: 0, pitch_rate: 0, boost: false, eff_mult: 1.25 } }))
}
writeFileSync(path.join(root, FLIGHT_SCRIPT), flightLines.join('\n') + '\n')
const goFlight = parse(execFileSync(path.join(root, 'test/out/server-dump'),
  ['flight', '-inputs', FLIGHT_SCRIPT, '-seed', String(SEED)], { cwd: root, maxBuffer: 256 << 20 }))
const csFlight = parse(execFileSync('dotnet',
  ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--', '--flight', FLIGHT_SCRIPT, '--world', WORLD],
  { cwd: root, maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'inherit'] }))
let flightWorst = 0
for (let i = 0; i < Math.min(goFlight.length, csFlight.length); i++) flightWorst = Math.max(flightWorst, dist(goFlight[i].pos, csFlight[i].pos))
checks.push(['flight conformant at 1.25x', flightWorst <= 1e-6 && goFlight.length === csFlight.length, `worst ${flightWorst.toExponential(2)} m`])

let bad = 0
for (const [n, ok, d] of checks) { console.log(`${ok ? 'PASS' : 'FAIL'} ${n}  ${d || ''}`); if (!ok) bad++ }
console.log(bad ? `\nOVERALL: FAIL (${bad}/${checks.length})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(bad ? 1 : 0)
