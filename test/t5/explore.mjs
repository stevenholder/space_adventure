#!/usr/bin/env node
/**
 * C5 field explorer: walk great circles out of the spawn point and find the
 * six terrains the conformance script must cover:
 *   1. level ground      (slope < 8°, sustained)
 *   2. walkable slope    (15° <= slope <= 45°, sustained)
 *   3. steep > max_slope (slope > 50°, sustained — slide)
 *   4. max_step ledge    (radius step 0.15..0.29 m within 0.5 m arc — climbable)
 *   5. jump spot         (any flat run)
 *   6. cube-face seam crossing
 *
 * Operates on the CAPTURED wire field (what the TS dump sees). Pure Node.
 */
import { readFileSync } from 'node:fs'
import { loadField, sampleRadius, surfaceNormal, slopeDeg, faceOf, norm3 } from '../lib/field.mjs'

const world = JSON.parse(readFileSync(process.argv[2] ?? 'test/out/world-seed1337.json', 'utf8'))
const F = loadField(world)

const SPAWN = [0, 1, 0]
const r0 = sampleRadius(F, SPAWN)
console.error(`spawn r=${r0.toFixed(3)} face=${faceOf(SPAWN)}`)

// great-circle walker: start dir u, tangent t (unit, ⊥ u)
function walk(u, t, arcM, stepM = 0.5) {
  const out = []
  let dir = norm3(u)
  for (let s = 0; s <= arcM + 1e-9; s += stepM) {
    const r = sampleRadius(F, dir)
    const up = norm3(dir)
    const n = surfaceNormal(F, up)
    const sl = slopeDeg(F, up)
    const dr = out.length ? r - out[out.length - 1].r : 0
    out.push({
      s: +s.toFixed(3),
      r: +r.toFixed(4),
      slope: +sl.toFixed(2),
      face: faceOf(dir),
      dr: +dr.toFixed(4),
      dir: [+dir[0].toFixed(6), +dir[1].toFixed(6), +dir[2].toFixed(6)],
    })
    // advance along the great circle: dir' = cos(d)·dir + sin(d)·t, d = step/r
    const d = stepM / r
    const c = Math.cos(d), sn = Math.sin(d)
    dir = norm3([dir[0] * c + t[0] * sn, dir[1] * c + t[1] * sn, dir[2] * c + t[2] * sn])
  }
  return out
}

function longestRun(samples, pred) {
  let best = { len: 0, from: 0, to: 0 }
  let cur = { len: 0, from: 0 }
  for (const s of samples) {
    if (pred(s)) {
      if (cur.len === 0) cur = { len: stepM0, from: s.s }
      else cur.len += stepM0
      cur.to = s.s
    } else cur = { len: 0, from: 0, to: 0 }
    if (cur.len > best.len) best = { ...cur }
  }
  return best
}
const stepM0 = 0.5

const azims = []
for (let a = 0; a < 360; a += 2.5) azims.push(a)

const results = []
for (const a of azims) {
  const rad = (a * Math.PI) / 180
  // tangent = spawnLook (+X at spawn) rotated about +Y by `a`
  const t = [Math.cos(rad), 0, -Math.sin(rad)]
  const prof = walk(SPAWN, t, 120, 0.5)
  const faces = [...new Set(prof.map((p) => p.face))]
  const seamAt = prof.find((p) => p.face !== faceOf(SPAWN))?.s ?? null
  const maxStep = prof.reduce((m, p) => (p.dr > m.dr ? p : m), prof[0])
  results.push({
    az: a,
    maxSlope: Math.max(...prof.map((p) => p.slope)),
    minSlope: Math.min(...prof.map((p) => p.slope)),
    flat: longestRun(prof, (p) => p.slope < 8),
    walkSlope: longestRun(prof, (p) => p.slope >= 15 && p.slope <= 45),
    steep: longestRun(prof, (p) => p.slope > 50),
    ledge: maxStep.dr >= 0.15 && maxStep.dr <= 0.29 ? { at: maxStep.s, dr: maxStep.dr, dir: maxStep.dir } : null,
    faces,
    seamAt,
    endR: prof[prof.length - 1].r,
  })
}

const fmt = (x) => (x.len ? `${x.from}->${x.to} (${x.len}m)` : '-')
console.log('az  maxSl minSl  flat              walkSlope           steep             seam  ledge      faces')
for (const r of results) {
  const ledge = r.ledge ? `@${r.ledge.at} ${r.ledge.dr}` : '-'
  console.log(
    `${String(r.az).padStart(4)}  ${String(r.maxSlope).padStart(5)}  ${String(r.minSlope).padStart(5)}  ${fmt(r.flat).padStart(17)}  ${fmt(r.walkSlope).padStart(17)}  ${fmt(r.steep).padStart(17)}  ${String(r.seamAt).padStart(5)}  ${ledge.padStart(9)}  ${r.faces.join(',')}`,
  )
}

// detail dumps for the best candidates
const best = {
  flat: [...results].sort((a, b) => b.flat.len - a.flat.len)[0],
  walkSlope: [...results].sort((a, b) => b.walkSlope.len - a.walkSlope.len)[0],
  steep: [...results].sort((a, b) => b.steep.len - a.steep.len)[0],
  ledge: results.find((r) => r.ledge) ?? null,
  seam: [...results].filter((r) => r.seamAt !== null).sort((a, b) => a.seamAt - b.seamAt)[0],
}
console.error('\nbest flat azimuth:', best.flat?.az, best.flat && fmt(best.flat.flat))
console.error('best walkSlope azimuth:', best.walkSlope?.az, best.walkSlope && fmt(best.walkSlope.walkSlope))
console.error('best steep azimuth:', best.steep?.az, best.steep && fmt(best.steep.steep))
console.error('first ledge azimuth:', best.ledge?.az, best.ledge?.ledge)
console.error('earliest seam azimuth:', best.seam?.az, 'at', best.seam?.seamAt)

// full profiles for the interesting azimuths (every 2 m)
for (const tag of ['flat', 'walkSlope', 'steep', 'ledge', 'seam']) {
  const b = best[tag]
  if (!b) continue
  const a = b.az
  const rad = (a * Math.PI) / 180
  const t = [Math.cos(rad), 0, -Math.sin(rad)]
  const prof = walk(SPAWN, t, 120, 0.5)
  console.error(`\n--- profile az=${a} (${tag}) ---`)
  for (const p of prof.filter((_, i) => i % 4 === 0)) {
    console.error(`  s=${String(p.s).padStart(6)} r=${String(p.r).padStart(9)} slope=${String(p.slope).padStart(6)} face=${p.face} dir=[${p.dir.join(',')}]`)
  }
}
