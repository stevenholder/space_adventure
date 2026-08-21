#!/usr/bin/env node
/**
 * C10 STEP 2 - sim-in-the-loop candidate verification.
 *
 * Takes the scarp-scan rows (test/out/t10-scarp-scan.jsonl), filters to
 * spec-legal candidates (maxSlope <= 50.0 GDD hard limit, 6 faces,
 * total >= 942 m), sorts by worst step-down, and runs each through the
 * REAL client sim (test/t10/sim-lap.ts - canonical post-fix dynamics,
 * Go<->TS verified to 3.3e-13 m). The sim is the oracle: the static
 * prefilter only ranks.
 *
 *   node test/t10/sim-candidates.mjs [limit]
 *
 * Evidence: test/out/t10-sim-candNN.json (full sim result per candidate)
 * + summary on stdout.
 */
import { readFileSync, existsSync } from 'node:fs'
import { execFileSync } from 'node:child_process'

const LIMIT = Number(process.argv[2] || 8)
const SCAN = 'test/out/t10-scarp-scan.jsonl'

const rows = readFileSync(SCAN, 'utf8')
  .trim()
  .split('\n')
  .map((l) => JSON.parse(l))

const legal = rows.filter((r) => r.maxSlope <= 50.0 && r.faces.length === 6 && r.total >= 942)
legal.sort((a, b) => a.maxStepDown - b.maxStepDown || a.maxSlope - b.maxSlope)
const cands = legal.slice(0, LIMIT)
console.error(`candidates: ${legal.length} legal, testing top ${cands.length} (by stepDown, then slope)`)

const results = []
for (let i = 0; i < cands.length; i++) {
  const c = cands[i]
  const tag = String(i + 1).padStart(2, '0')
  const wps = `test/out/t10-wps-cand${tag}.json`
  const out = `test/out/t10-sim-cand${tag}.json`
  execFileSync('node', ['test/t10/lap-waypoints.mjs', c.a1az, c.q2az, c.p3colat, c.p3az, c.q5az, wps], { stdio: 'pipe' })
  let simOut = ''
  let failed = false
  try {
    simOut = execFileSync('npx', ['tsx', 'test/t10/sim-lap.ts', wps, out], { stdio: 'pipe' })
  } catch (e) {
    failed = true // sim-lap exits 1 on assertion failure (expected while searching)
    simOut = e.stdout || ''
  }
  const res = JSON.parse(readFileSync(out, 'utf8'))
  const param = `a1=${c.a1az} q2=${c.q2az} p3c=${c.p3colat} p3az=${c.p3az} q5=${c.q5az}`
  const fails = res.assertResults.filter((a) => !a.ok).map((a) => a.label)
  results.push({ tag, param, ok: res.ok, maxH: res.maxH, maxSlope: res.maxSlope, endpointDelta: res.endpointDelta, ticks: res.ticks, pathLen: res.pathLen, fails })
  console.error(
    `cand${tag} ${param}: ok=${res.ok} ticks=${res.ticks} pathLen=${res.pathLen} endpoint=${res.endpointDelta} maxH=${res.maxH} maxSlope=${res.maxSlope} fails=[${fails}]${failed ? '' : ' (clean exit)'}`,
  )
  if (res.ok) break
}

console.error('--- summary ---')
for (const r of results) {
  console.error(`cand${r.tag} ${r.param} ok=${r.ok} maxH=${r.maxH} maxSlope=${r.maxSlope} endpoint=${r.endpointDelta} ticks=${r.ticks} fails=[${r.fails}]`)
}
const winner = results.find((r) => r.ok)
if (winner) console.error(`WINNER: cand${winner.tag} ${winner.param}`)
else console.error('no winner yet')