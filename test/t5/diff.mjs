#!/usr/bin/env node
/**
 * C5 criterion 5 — Go server vs TS client trajectory diff.
 *
 * Inputs:
 *   test/t5/dump-server.jsonl  Go dump (tick 0-based):   {tick, pos, vel, grounded}
 *   test/t5/dump-client.jsonl  TS dump (tick 1-based):   {tick, pos, vel, grounded}
 *   test/t5/prerun-telemetry.json  per-leg tick counts (leg boundaries)
 *   test/t5/script-go.jsonl + script-ts.jsonl  (harness parity check)
 *
 * Alignment: by INDEX, not tick number (Go tick i <-> TS tick i+1).
 *
 * Verdict (GDD "M1 on-foot movement" rule table):
 *   PASS = max per-tick |dPos| <= 5% of one tick of nominal walk displacement
 *          (5% x walkSpeed x tickDt = 5% x 4.5 x 0.05 = 0.01125 m)
 *        AND max per-tick |dVel| <= 5% of walkSpeed (0.225 m/s)
 *        AND zero grounded mismatches.
 *
 * dPos in metres; dVel in m/s (the dump vel field is m/s; m/tick = m/s x 0.05).
 */
import { readFileSync, writeFileSync } from 'node:fs'

const DIR = 'test/t5'
const TICK_DT = 0.05
const WALK_SPEED = 4.5
const DPOS_LIMIT = 0.05 * WALK_SPEED * TICK_DT // 0.01125 m
const DVEL_LIMIT = 0.05 * WALK_SPEED // 0.225 m/s
const runId = process.argv.includes('--run-id')
  ? process.argv[process.argv.indexOf('--run-id') + 1]
  : new Date().toISOString().slice(0, 16).replace(/[:T]/g, '-')

const readJSONL = (p) =>
  readFileSync(p, 'utf8')
    .split('\n')
    .filter((l) => l.length > 0)
    .map((l) => JSON.parse(l))

const server = readJSONL(`${DIR}/dump-server.jsonl`)
const client = readJSONL(`${DIR}/dump-client.jsonl`)
const goScript = readJSONL(`${DIR}/script-go.jsonl`)
const tsScript = readJSONL(`${DIR}/script-ts.jsonl`)
const telemetry = JSON.parse(readFileSync(`${DIR}/prerun-telemetry.json`, 'utf8'))

// ---------------------------------------------------------------- harness checks
const problems = []
const n = Math.min(server.length, client.length)
if (server.length !== client.length) problems.push(`tick count mismatch: go=${server.length} ts=${client.length}`)
if (goScript.length !== tsScript.length) problems.push(`script line count mismatch: go=${goScript.length} ts=${tsScript.length}`)
if (goScript.length - 1 !== n) problems.push(`script inputs (${goScript.length - 1}) != dump ticks (${n})`)

for (let i = 0; i < n; i++) {
  if (server[i].tick !== i) {
    problems.push(`server tick not 0-based sequential at index ${i} (tick=${server[i].tick})`)
    break
  }
  if (client[i].tick !== i + 1) {
    problems.push(`client tick not 1-based sequential at index ${i} (tick=${client[i].tick})`)
    break
  }
}

// Script parity: the two script variants must encode the same inputs.
const gh = goScript[0].state
const th = tsScript[0]
for (const k of ['pos', 'vel', 'grounded', 'facing']) {
  if (JSON.stringify(gh[k]) !== JSON.stringify(th[k])) problems.push(`header ${k} differs between script variants`)
}
for (let i = 1; i < goScript.length; i++) {
  const g = goScript[i].input
  const t = tsScript[i]
  if (
    g.move_x !== t.move_x ||
    g.move_y !== t.move_y ||
    g.action_mask !== t.action_mask ||
    g.look[0] !== t.look_dir[0] ||
    g.look[1] !== t.look_dir[1] ||
    g.look[2] !== t.look_dir[2]
  ) {
    problems.push(`input parity broken at script line ${i}`)
    break
  }
}

// ---------------------------------------------------------------- leg boundaries
const legs = []
let offset = 0
for (const tel of telemetry.telemetry) {
  legs.push({ label: tel.label, start: offset, end: offset + tel.ticks, maxDPos: 0, maxDVel: 0, argDPosTick: -1, argDVelTick: -1, groundedMismatches: 0 })
  offset += tel.ticks
}
if (offset !== n) problems.push(`telemetry total ticks (${offset}) != dump ticks (${n})`)
const legAt = (i) => legs.find((l) => i >= l.start && i < l.end) ?? { label: '?' }

// ---------------------------------------------------------------- per-tick diff
const failures = []
let maxDPos = 0
let maxDVel = 0
let argDPosTick = -1
let argDVelTick = -1
let groundedMismatches = 0
for (let i = 0; i < n; i++) {
  const gp = server[i].pos
  const tp = client[i].pos
  const dPos = Math.hypot(gp[0] - tp[0], gp[1] - tp[1], gp[2] - tp[2])
  const gv = server[i].vel
  const tv = client[i].vel
  const dVel = Math.hypot(gv[0] - tv[0], gv[1] - tv[1], gv[2] - tv[2])
  const gMismatch = server[i].grounded !== client[i].grounded
  const leg = legAt(i)
  if (dPos > maxDPos) {
    maxDPos = dPos
    argDPosTick = i
  }
  if (dVel > maxDVel) {
    maxDVel = dVel
    argDVelTick = i
  }
  if (dPos > leg.maxDPos) {
    leg.maxDPos = dPos
    leg.argDPosTick = i
  }
  if (dVel > leg.maxDVel) {
    leg.maxDVel = dVel
    leg.argDVelTick = i
  }
  if (gMismatch) {
    groundedMismatches++
    leg.groundedMismatches++
  }
  if (dPos > DPOS_LIMIT || dVel > DVEL_LIMIT || gMismatch) {
    failures.push({
      tick: i,
      tsTick: i + 1,
      leg: leg.label,
      dPos: +dPos.toPrecision(6),
      dVel: +dVel.toPrecision(6),
      grounded: [server[i].grounded, client[i].grounded],
    })
  }
}

const pass = maxDPos <= DPOS_LIMIT && maxDVel <= DVEL_LIMIT && groundedMismatches === 0 && problems.length === 0
const report = {
  run_id: runId,
  timestamp: new Date().toISOString(),
  verdict: pass ? 'PASS' : 'FAIL',
  harnessProblems: problems,
  thresholds: {
    dPosLimit_m: DPOS_LIMIT,
    dVelLimit_mps: DVEL_LIMIT,
    basis: `5% of one-tick nominal walk displacement (${WALK_SPEED} m/s x ${TICK_DT} s) and 5% of walkSpeed`,
    units: 'dPos: m; dVel: m/s (m/tick = m/s x 0.05)',
  },
  overall: {
    ticks: n,
    maxDPos_m: maxDPos,
    maxDPos_tick: argDPosTick,
    maxDPos_pct_ofTickWalk: (100 * maxDPos) / (WALK_SPEED * TICK_DT),
    maxDVel_mps: maxDVel,
    maxDVel_tick: argDVelTick,
    maxDVel_pct_ofWalkSpeed: (100 * maxDVel) / WALK_SPEED,
    groundedMismatches,
    failureCount: failures.length,
  },
  perLeg: legs.map((l) => ({
    label: l.label,
    tickRange: [l.start, l.end - 1],
    ticks: l.end - l.start,
    maxDPos_m: l.maxDPos,
    maxDPos_tick: l.argDPosTick,
    maxDVel_mps: l.maxDVel,
    maxDVel_tick: l.argDVelTick,
    groundedMismatches: l.groundedMismatches,
  })),
  failures,
}
// run history: prior runs (keyed by run_id) are kept in the report
let priorRuns = []
try {
  const prev = JSON.parse(readFileSync(`${DIR}/diff-report.json`, 'utf8'))
  if (Array.isArray(prev.runs)) priorRuns = prev.runs
} catch {
  // first run
}
const entry = {
  run_id: report.run_id,
  verdict: report.verdict,
  overall: report.overall,
  perLeg: report.perLeg,
  failures: report.failures.length,
}
report.runs = [...priorRuns, entry]
writeFileSync(`${DIR}/diff-report.json`, JSON.stringify(report, null, 1))

console.log(`C5 trajectory diff — ${n} ticks (aligned by index: go tick i <-> ts tick i+1)`)
console.log(`harness: ${problems.length ? 'PROBLEMS: ' + problems.join('; ') : 'ok (counts, tick numbering, script parity)'}`)
console.log(
  `overall: maxDPos=${maxDPos.toExponential(3)} m @tick${argDPosTick} (${(100 * maxDPos / (WALK_SPEED * TICK_DT)).toExponential(2)}% of 1-tick walk), maxDVel=${maxDVel.toExponential(3)} m/s @tick${argDVelTick} (${(100 * maxDVel / WALK_SPEED).toExponential(2)}% of walkSpeed), groundedMismatches=${groundedMismatches}, failures=${failures.length}`,
)
console.log(`limits: dPos<=${DPOS_LIMIT} m, dVel<=${DVEL_LIMIT} m/s`)
console.log('per-leg (max dPos m / max dVel m/s / grounded mismatches):')
for (const l of report.perLeg) {
  console.log(
    `  ${l.label.padEnd(9)} ticks ${String(l.tickRange[0]).padStart(4)}-${String(l.tickRange[1]).padStart(4)}  dPos=${l.maxDPos_m.toExponential(3)} @${l.maxDPos_tick}  dVel=${l.maxDVel_mps.toExponential(3)} @${l.maxDVel_tick}  gMM=${l.groundedMismatches}`,
  )
}
if (failures.length) {
  console.log(`failures (${failures.length}), first 25:`)
  for (const f of failures.slice(0, 25)) {
    console.log(`  tick ${f.tick} (ts ${f.tsTick}) leg=${f.leg} dPos=${f.dPos} dVel=${f.dVel} grounded go/ts=${f.grounded.join('/')}`)
  }
}
console.log(`VERDICT: ${report.verdict}`)
console.log(`wrote ${DIR}/diff-report.json`)
if (!pass) process.exit(1)