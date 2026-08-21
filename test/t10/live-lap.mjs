#!/usr/bin/env node
/**
 * C10 STEP 4 - live scripted closed-loop walk against the real server
 * (WS 127.0.0.1:18080 /ws), 20 Hz inputs, walk speed 4.5 m/s (no sprint,
 * no jump). POST-FIX run (glue-band walker deployed: sim.go mtime
 * 2026-08-20 18:23 UTC < pod start 18:38 UTC, pods server-8888898bc-zsrtp /
 * client-6d4b878cf9-hwqzk). On-surface flights are permitted ONLY as the
 * GDD AIR case (kink scarp / lip > max_step / rim drop) and each is
 * field-audited at its peak (see t10-flight-audit.json for the audit).
 *
 * Invariants asserted from the LIVE stream (post-fix decision rule):
 *   1. endpoint < 1 m from spawn
 *   2. 6 distinct faces; all 7 face crossings at design s (+/-8 m)
 *   3. no seam hitch, three-part: (a) dPos <= 0.35 m at every face
 *      crossing / leg junction (1.5 walk ticks); (b) every per-snapshot
 *      dPos <= 0.45 m (physics bound); (c) every dPos > 0.375 m must sit
 *      inside a documented flight window (+/-1 tick)
 *   4. flight audit: every flight (h > 0.05 m) is a GDD AIR scarp case:
 *      slope at peak <= 50 deg AND 0.2 m step feature > 0.10 m
 *   5. upright: at face 3 the body quat up ~ -spawn_up (< -0.8) and the
 *      quat up stays within 55 deg of the local radial (terrain-normal
 *      upright at <=50 deg slope)
 *   6. tick rate: snapshot tick deltas 1 at 20 Hz (p50/p95 inter-snapshot
 *      interval ~50 ms)
 *
 * Evidence: test/out/t10-live-result.json (+ own-entity snapshot stream).
 *
 *   node test/t10/live-lap.mjs
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { WSClient } from '../lib/ws.mjs'
import {
  MSG,
  PROTOCOL_VERSION,
  encodeHello,
  encodeInput,
  frame,
  decodeHelloAck,
  decodeTerrain,
  decodeSnapshot,
  norm3,
  len3,
  dot3,
  quatRotate,
} from '../lib/wire.mjs'
import { sampleRadius, faceOf, slopeDeg } from '../lib/field.mjs'

const HOST = '127.0.0.1'
const PORT = 18080
const PATH = '/ws'
const NAME = 'QA-C10-lap'
const TICK_MS = 50
const ADVANCE_M = 2.5
const STOP_M = 0.5
const SEAM_LIMIT = 0.35 // (a) dPos at crossings/junctions: ~1.5 walk ticks
const PHYS_LIMIT = 0.45 // (b) physics bound on any per-snapshot dPos
const BIG_D = 0.375 // (c) deltas above this must sit inside a flight window
const FLIGHT_H = 0.05 // flight = h > 0.05 m (matches sim flight audit)
const SETTLE_TICKS = 30
const MAX_SNAPSHOTS = 5600 // ~280 s at 20 Hz: sim lap 4735 ticks + margin

// design crossings / junctions: from the waypoint file (single source of
// truth - same as sim-lap.ts).
const SHA16_EXPECT = 'c80269c44a757a8f'

const WPS_PATH = process.argv[2] || 'test/out/t10-lap-waypoints.json'
const wpFile = JSON.parse(readFileSync(WPS_PATH, 'utf8'))
const wps = wpFile.wps
const TOTAL = wpFile.total
const spawnDir = [0, 1, 0]

const DESIGN_X = wpFile.crossings ?? [
  { s: 136.0, from: 2, to: 0 },
  { s: 297.74, from: 0, to: 5 },
  { s: 474.48, from: 5, to: 3 },
  { s: 658.75, from: 3, to: 1 },
  { s: 793.25, from: 1, to: 4 },
  { s: 797.21, from: 4, to: 1 },
  { s: 916.43, from: 1, to: 2 },
]
const JUNCTIONS = []
{
  let prevSeg = null
  for (const w of wps) {
    if (w.seg !== prevSeg) {
      if (prevSeg !== null) JUNCTIONS.push({ label: `${prevSeg}->${w.seg}`, s: w.s })
      prevSeg = w.seg
    }
  }
}

const wpPoint = (w) => [w.dir[0] * w.r, w.dir[1] * w.r, w.dir[2] * w.r]

const ws = new WSClient(HOST, PORT, PATH)
let helloAck = null
let terrain = null
let terrainPayload = null
let myId = null
let seq = 0
let lastSnap = null // {tick, pos, quat, vel, t}
let running = false
let arrivedTicks = 0
let dead = null

const snaps = [] // own-entity telemetry

function onMessage(type, payload, recvNs) {
  if (type !== 2 || payload.length < 2) return
  const msgType = payload.readUInt16LE(0)
  const body = payload.subarray(2)
  const t = Number(process.hrtime.bigint()) / 1e6
  if (msgType === MSG.HELLO_ACK) {
    helloAck = decodeHelloAck(body)
    myId = helloAck.entityId
  } else if (msgType === MSG.TERRAIN) {
    terrain = decodeTerrain(body)
    terrainPayload = Buffer.from(body)
  } else if (msgType === MSG.SNAPSHOT && myId !== null) {
    const s = decodeSnapshot(body)
    const me = s.entities.find((e) => e.id === myId)
    if (!me) return
    const up = norm3(me.pos)
    const r = terrain ? sampleRadius(terrain, up) : 0
    const h = len3(me.pos) - r
    const face = faceOf(up)
    const quatUp = quatRotate(me.quat, [0, 1, 0])
    let dPos = null
    if (lastSnap) dPos = len3([me.pos[0] - lastSnap.pos[0], me.pos[1] - lastSnap.pos[1], me.pos[2] - lastSnap.pos[2]])
    snaps.push({
      t: +t.toFixed(1),
      tick: s.tick,
      ackSeq: s.ackSeq,
      pos: me.pos.map((x) => +x.toFixed(3)),
      quatUp: quatUp.map((x) => +x.toFixed(4)),
      vel: me.vel.map((x) => +x.toFixed(3)),
      h: +h.toFixed(4),
      face,
      upDotSpawn: +(dot3(up, spawnDir)).toFixed(4),
      quatUpDotRadial: +(dot3(quatUp, up)).toFixed(4),
      dPos: dPos === null ? null : +dPos.toFixed(4),
    })
    lastSnap = { tick: s.tick, pos: me.pos, quat: me.quat, vel: me.vel, t }
  }
}

// ---------------------------------------------------------------- drive
function driveTick() {
  if (!running || !lastSnap || snaps.length === 0) return
  const pos = lastSnap.pos
  const target = wpPoint(wps[targetWp])
  const dT = len3([target[0] - pos[0], target[1] - pos[1], target[2] - pos[2]])
  // advance pointer using server pos
  while (
    targetWp < wps.length - 1 &&
    len3([wps[targetWp + 1].dir[0] * wps[targetWp + 1].r - pos[0], wps[targetWp + 1].dir[1] * wps[targetWp + 1].r - pos[1], wps[targetWp + 1].dir[2] * wps[targetWp + 1].r - pos[2]]) < ADVANCE_M
  ) {
    targetWp++
  }
  const atEnd = targetWp >= wps.length - 1
  const moving = atEnd ? dT > STOP_M : true
  const look = norm3([target[0] - pos[0], target[1] - pos[1], target[2] - pos[2]])
  ws.sendBinary(frame(MSG.INPUT, encodeInput(0, moving ? 1 : 0, look, 0, (seq++) % 65536)))
  if (!moving) {
    arrivedTicks++
    const v = len3(lastSnap.vel)
    if (arrivedTicks >= SETTLE_TICKS && v < 0.05) stop('arrived')
    if (arrivedTicks > 400) stop('settle-timeout')
  } else {
    arrivedTicks = 0
  }
}
let targetWp = 1
let timer = null

function stop(reason) {
  if (!running) return
  running = false
  clearInterval(timer)
  console.error(`stop: ${reason} after ${snaps.length} snapshots (arrivedTicks=${arrivedTicks}, targetWp=${targetWp})`)
  ws.sendClose(1000)
  setTimeout(() => ws.kill(), 500).unref()
  finish(reason)
}

function finish(reason) {
  const n = snaps.length
  if (n === 0) {
    writeFileSync('test/out/t10-live-result.json', JSON.stringify({ ok: false, error: `no snapshots (${reason || dead || 'unknown'})` }, null, 1))
    console.error('FATAL: no snapshots')
    process.exit(1)
  }
  const first = snaps[0]
  const last = snaps[n - 1]
  // endpoint: spawn surface point
  const spawnR = sampleRadius(terrain, spawnDir)
  const spawnPos = [spawnDir[0] * spawnR, spawnDir[1] * spawnR, spawnDir[2] * spawnR]
  const endpoint = len3([last.pos[0] - spawnPos[0], last.pos[1] - spawnPos[1], last.pos[2] - spawnPos[2]])

  // faces + crossings (path s from waypoint pointer at each snapshot is
  // approximated by projecting the position onto the waypoint chain:
  // nearest waypoint s - good enough for +/-8 m tolerance)
  function nearestS(p) {
    let best = 0
    let bd = Infinity
    for (let i = 0; i < wps.length; i++) {
      const w = wps[i]
      const d = (w.dir[0] - p[0] / len3(p)) ** 2 + (w.dir[1] - p[1] / len3(p)) ** 2 + (w.dir[2] - p[2] / len3(p)) ** 2
      if (d < bd) {
        bd = d
        best = w.s
      }
    }
    return best
  }
  const faces = new Set(snaps.map((x) => x.face))
  const crossings = []
  let prevFace = snaps[0].face
  for (const x of snaps) {
    if (x.face !== prevFace) {
      crossings.push({ tick: x.tick, s: +nearestS(x.pos).toFixed(1), from: prevFace, to: x.face })
      prevFace = x.face
    }
  }
  const xTol = 8
  const xOk =
    crossings.length === 7 &&
    DESIGN_X.every((dx) =>
      crossings.some((cx) => cx.from === dx.from && cx.to === dx.to && Math.abs(cx.s - dx.s) <= xTol),
    )

  // deltas + flight windows (three-part no-hitch spec, post-fix decision rule)
  const deltas = snaps.slice(1).map((x) => x.dPos).filter((d) => d !== null)
  const sorted = [...deltas].sort((a, b) => a - b)
  const maxDelta = sorted[sorted.length - 1]
  const p95 = sorted[Math.floor(0.95 * sorted.length)]
  // flight windows: h > FLIGHT_H, widened +/-1 tick for the landing tick
  const flightTicks = new Set()
  const flightWins = []
  {
    let cur = null
    for (let i = 0; i < snaps.length; i++) {
      if (snaps[i].h > FLIGHT_H) {
        if (!cur) cur = { from: i, to: i }
        else cur.to = i
      } else if (cur) {
        flightWins.push(cur)
        cur = null
      }
    }
    if (cur) flightWins.push(cur)
    for (const f of flightWins) {
      for (let t = snaps[f.from].tick - 1; t <= snaps[f.to].tick + 1; t++) flightTicks.add(t)
    }
  }
  const bigDeltas = snaps.map((x) => ({ tick: x.tick, d: x.dPos })).filter((x) => x.d !== null && x.d > BIG_D)
  // (a) seam check: snapshot at/after each design crossing / junction s
  const near = (s0) => {
    let best = null
    for (const x of snaps) if (Math.abs(nearestS(x.pos) - s0) < 2) best = x
    return best
  }
  const specialDeltas = []
  for (const dx of [...DESIGN_X.map((x) => ({ label: `x ${x.from}->${x.to}`, s: x.s })), ...JUNCTIONS]) {
    const x = near(dx.s)
    specialDeltas.push({ label: dx.label, s: dx.s, dPos: x ? x.dPos : null, ok: x ? x.dPos <= SEAM_LIMIT : false })
  }
  const seamOk = specialDeltas.every((d) => d.ok)
  const physOk = maxDelta <= PHYS_LIMIT
  const bigDInFlights = bigDeltas.every((b) => flightTicks.has(b.tick))

  // upright
  const face3 = snaps.filter((x) => x.face === 3)
  const minUpDotFace3 = Math.min(...face3.map((x) => x.upDotSpawn))
  const minQuatUpRadial = Math.min(...snaps.map((x) => x.quatUpDotRadial))
  const face3MinQuatUpRadial = Math.min(...face3.map((x) => x.quatUpDotRadial))

  // tick rate
  const dticks = []
  const dms = []
  for (let i = 1; i < n; i++) {
    dticks.push(snaps[i].tick - snaps[i - 1].tick)
    dms.push(snaps[i].t - snaps[i - 1].t)
  }
  const dtSorted = [...dticks].sort((a, b) => a - b)
  const dmsSorted = [...dms].sort((a, b) => a - b)
  const median = (a) => a[Math.floor(a.length / 2)]

  // flights (post-fix decision rule): GDD AIR cases only - kink scarps /
  // lips > max_step / rim drops, field-verified at each flight peak
  const maxH = Math.max(...snaps.map((x) => x.h))
  const onSurfTicks = snaps.filter((x) => x.h <= 0.05).length
  const minH = Math.min(...snaps.map((x) => x.h))
  const EPS = (2 * Math.PI) / 180
  const flightAudit = flightWins.map((f) => {
    const p = snaps[f.from]
    const r = len3(p.pos)
    const up = norm3(p.pos)
    const az = ((Math.atan2(-up[2], up[0]) * 180) / Math.PI + 360) % 360
    const lat = (Math.asin(Math.max(-1, Math.min(1, up[1]))) * 180) / Math.PI
    const slope = slopeDeg(terrain, up, EPS)
    const sPeak = nearestS(p.pos)
    // +/-10 m along-path profile at 0.2 m: scarp/step signature
    let nw = wps[wps.length - 1]
    for (const w of wps) if (w.s >= sPeak) { nw = w; break }
    const dn = norm3([nw.dir[0], nw.dir[1], nw.dir[2]])
    const dot = up[0] * dn[0] + up[1] * dn[1] + up[2] * dn[2]
    const tang = norm3([dn[0] - up[0] * dot, dn[1] - up[1] * dot, dn[2] - up[2] * dot])
    let step02m = 0
    let lo = Infinity
    let hi = -Infinity
    let prevR = 0
    for (let k = 0; k <= 100; k++) {
      const dd = (k - 50) * 0.2
      const cs = Math.cos(dd / r)
      const ss = Math.sin(dd / r)
      const at = norm3([up[0] * cs + tang[0] * ss, up[1] * cs + tang[1] * ss, up[2] * cs + tang[2] * ss])
      const rr = sampleRadius(terrain, at)
      if (k > 0) step02m = Math.max(step02m, Math.abs(rr - prevR))
      prevR = rr
      lo = Math.min(lo, rr)
      hi = Math.max(hi, rr)
    }
    return {
      ticks: [snaps[f.from].tick, snaps[f.to].tick],
      s: +sPeak.toFixed(1),
      az: +az.toFixed(1),
      lat: +lat.toFixed(1),
      maxH: +p.h.toFixed(3),
      hangS: +(((snaps[f.to].tick - snaps[f.from].tick + 1) * 0.05).toFixed(2)),
      slopeDeg: +slope.toFixed(2),
      step02m: +step02m.toFixed(3),
      relief10m: +(hi - lo).toFixed(2),
      scarpClass: slope <= 50.0 && step02m > 0.1,
    }
  })
  const allFlightsScarp = flightAudit.every((f) => f.scarpClass)

  const durationS = ((last.t - first.t) / 1000).toFixed(1)
  const assertResults = [
    {
      label: 'endpoint',
      assert: 'endpoint < 1 m from spawn',
      ok: endpoint < 1.0,
      detail: `delta=${endpoint.toFixed(4)} m final=${JSON.stringify(last.pos)}`,
    },
    {
      label: 'faces',
      assert: '6 distinct faces',
      ok: faces.size === 6,
      detail: `[${[...faces].sort((a, b) => a - b).join(',')}]`,
    },
    {
      label: 'crossings',
      assert: 'all 7 face crossings at design s (+/-8 m)',
      ok: xOk,
      detail: JSON.stringify(crossings),
    },
    {
      label: 'seam',
      assert: `(a) seam: dPos <= ${SEAM_LIMIT} m at every face crossing and leg junction`,
      ok: seamOk,
      detail: `specials=${JSON.stringify(specialDeltas)}`,
    },
    {
      label: 'physicsBound',
      assert: `(b) physics: every per-snapshot dPos <= ${PHYS_LIMIT} m`,
      ok: physOk,
      detail: `max=${maxDelta} m p95=${p95} m n=${deltas.length}`,
    },
    {
      label: 'bigDInFlights',
      assert: `(c) every dPos > ${BIG_D} m inside a documented flight window (+/-1 tick)`,
      ok: bigDInFlights && physOk,
      detail: `bigD=${JSON.stringify(bigDeltas)} flightWindows=${flightWins.length}`,
    },
    {
      label: 'flightAudit',
      assert: `every flight (h > ${FLIGHT_H} m) is a GDD AIR scarp case: slope at peak <= 50 deg AND 0.2 m step feature > 0.10 m (zero tolerance for smooth-slope flights)`,
      ok: allFlightsScarp,
      detail: `${flightAudit.length} flight(s), maxH=${maxH.toFixed(3)} m, total hang=${flightAudit.reduce((a, f) => a + f.hangS, 0).toFixed(2)} s; ` + flightAudit.map((f) => `t${f.ticks[0]}-${f.ticks[1]} @s${f.s} (az ${f.az} lat ${f.lat}, maxH ${f.maxH} m, slope ${f.slopeDeg} deg, step0.2m ${f.step02m} m, relief10m ${f.relief10m} m)${f.scarpClass ? '' : ' [NOT SCARP CLASS]'}`).join(' | '),
    },
    {
      label: 'upright',
      assert: 'face 3 quat up ~ -spawn_up (< -0.8); quat up within 55 deg of radial everywhere',
      ok: minUpDotFace3 < -0.8 && minQuatUpRadial > Math.cos((55 * Math.PI) / 180),
      detail: `minUpDotSpawn(face3)=${minUpDotFace3} minQuatUpDotRadial=${minQuatUpRadial} (face3 min=${face3MinQuatUpRadial})`,
    },
    {
      label: 'tickrate',
      assert: 'snapshot tick delta median 1, inter-snapshot median ~50 ms',
      ok: dtSorted[Math.floor(dtSorted.length / 2)] === 1 && Math.abs(median(dms) - 50) <= 15,
      detail: `tickDeltaMedian=${dtSorted[Math.floor(dtSorted.length / 2)]} msMedian=${median(dms).toFixed(1)} snapshots=${n}`,
    },
  ]
  const ok = assertResults.every((r) => r.ok)
  const out = {
    run: reason,
    note: 'POST-FIX run (glue-band walker); on-surface flights are the GDD AIR case record - see test/out/t10-flight-audit.json',
    worldSeed: helloAck.worldSeed,
    tickHz: helloAck.tickHz,
    entityId: myId,
    terrainSha16: createHash('sha256').update(terrainPayload).digest('hex').slice(0, 16),
    terrainSha16Expected: SHA16_EXPECT,
    totalDesign: TOTAL,
    durationS: +durationS,
    snapshots: n,
    endpointDelta: +endpoint.toFixed(4),
    onSurface: { maxH: +maxH.toFixed(4), minH: +minH.toFixed(4), ticksWithin05: onSurfTicks, total: n },
    deltas: { max: +maxDelta.toFixed(4), p95: +p95.toFixed(4) },
    flights: flightAudit,
    faces: [...faces].sort((a, b) => a - b),
    crossings,
    upright: { minUpDotSpawnFace3: +minUpDotFace3.toFixed(4), minQuatUpDotRadial: +minQuatUpRadial.toFixed(4), face3MinQuatUpDotRadial: +face3MinQuatUpRadial.toFixed(4) },
    assertResults,
    ok,
    snaps,
  }
  writeFileSync('test/out/t10-live-result.json', JSON.stringify(out, null, 1))
  console.error(`duration=${durationS} s snapshots=${n} endpoint=${endpoint.toFixed(4)} m maxH=${maxH.toFixed(3)} maxDelta=${maxDelta} m`)
  for (const r of assertResults) console.error(`${r.ok ? 'ASSERT PASS' : 'ASSERT FAIL'} [${r.label}] ${r.assert}  (${r.detail})`)
  console.error(`ok=${ok} (on-surface bar reported separately: ${onSurfTicks}/${n} ticks within 0.05 m)`)
  process.exit(ok ? 0 : 1)
}

// ---------------------------------------------------------------- main
async function main() {
  ws.onMessage = onMessage
  ws.onClose = (info) => {
    if (running) {
      dead = `closed: ${JSON.stringify(info)}`
      stop('connection-closed')
    }
  }
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, NAME)))
  // wait for hello_ack + terrain
  const t0 = Date.now()
  while (!helloAck || !terrain) {
    if (Date.now() - t0 > 10000) throw new Error('timeout waiting hello_ack/terrain')
    await new Promise((r) => setTimeout(r, 20))
  }
  const sha = createHash('sha256').update(terrainPayload).digest('hex').slice(0, 16)
  console.error(
    `hello_ack: seed=${helloAck.worldSeed} tickHz=${helloAck.tickHz} entity=${myId}; terrain faceGrid=${terrain.faceGrid} radiiSha16=${sha} (expect ${SHA16_EXPECT})`,
  )
  if (sha !== SHA16_EXPECT) throw new Error('terrain mismatch - not world-seed1337')
  running = true
  timer = setInterval(driveTick, TICK_MS)
  // hard cap
  const cap = setTimeout(() => stop('hard-cap'), MAX_SNAPSHOTS * TICK_MS + 60000)
  cap.unref()
}
main().catch((e) => {
  console.error('FATAL', e)
  process.exit(1)
})