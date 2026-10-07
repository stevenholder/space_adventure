#!/usr/bin/env node
/**
 * Phase 4 acceptance, end to end at the wire level: C26–C32.
 *
 * Join -> walk to the parked rover -> refusals (C29) -> board the driver
 * seat -> a second client races the seat (C28) and rides as passenger ->
 * composed seat positions (C26) -> drive with mode-2 input, never below the
 * terrain (C27, asserted as the property the criterion protects: the
 * independent field sampler says the rover is never under the surface —
 * there is no dev-override backdoor in the production binary to force it
 * under, on purpose) -> passenger input is inert (C31) -> disembark at
 * speed lands on the ground near the rover (C32). Between C31 and C32, "a
 * rover never wedges": drive on onto the 46° scarp ahead (past
 * drive_slope_max 40°), uphill throttle is refused, downhill drives it off.
 *
 * Run: node test/t24-rover.mjs   (needs `make up` / `make check-server`)
 */
import { readFileSync } from 'node:fs'
import { quatRotate, SEAT } from './lib/wire.mjs'
import { downhillTangent, loadField, sampleRadius, slopeDeg } from './lib/field.mjs'

const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
const frame = (type, body) => { const o = u8(2 + body.length); new DataView(o.buffer).setUint16(0, type, true); o.set(body, 2); return o }
function hello(name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(2 + 4 + n.length + 4 + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
function input(mx, my, look, mask, seq, mode = 0) {
  const b = u8(25), dv = new DataView(b.buffer)
  dv.setFloat32(0, mx, true); dv.setFloat32(4, my, true)
  dv.setFloat32(8, look[0], true); dv.setFloat32(12, look[1], true); dv.setFloat32(16, look[2], true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true); dv.setUint8(24, mode)
  return frame(0x0003, b)
}
function board(vehicleId, seat) {
  const b = u8(6), dv = new DataView(b.buffer)
  dv.setUint32(0, vehicleId, true); dv.setUint16(4, seat, true)
  return frame(0x000b, b)
}
const disembark = () => frame(0x000c, u8(0))
const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
const dist = (a, b) => Math.hypot(...sub(a, b))
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const tangent = (d, up) => { const k = dot(d, up); return [d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k] }
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}

function connect(name) {
  const ws = new WebSocket(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws')
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), seats: [], seq: 1 }
  ws.addEventListener('open', () => ws.send(hello(name, `t24-${name}-${Date.now()}`)))
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true), pv = new DataView(ev.data, 2)
    if (t === 0x0002) c.id = pv.getUint32(8, true)
    else if (t === 0x0005) c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true) })
    else if (t === 0x000d) c.seats.push({ entityId: pv.getUint32(0, true), seat: pv.getUint16(4, true), result: pv.getUint8(6) })
    else if (t === 0x0004) {
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) {
        const o = 8 + i * 54
        c.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
          quat: [pv.getFloat32(o + 16, true), pv.getFloat32(o + 20, true), pv.getFloat32(o + 24, true), pv.getFloat32(o + 28, true)],
          vel: [pv.getFloat32(o + 32, true), pv.getFloat32(o + 36, true), pv.getFloat32(o + 40, true)],
          parent: pv.getUint32(o + 44, true),
          seat: pv.getUint16(o + 48, true),
        })
      }
    }
  })
  return c
}
const wait = async (fn, ms = 5000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const nextSeat = async (c) => { const n = c.seats.length; return wait(() => c.seats.length > n ? c.seats[c.seats.length - 1] : null) }

async function walkTo(c, target, stopAt) {
  for (let i = 0; i < 300; i++) {
    const me = c.ents.get(c.id).pos
    const d = sub(target, me)
    if (Math.hypot(...d) <= stopAt) break
    const up = norm(me)
    const lk = norm(sub(d, up.map((x) => x * (d[0] * up[0] + d[1] * up[1] + d[2] * up[2]))))
    c.ws.send(input(0, 1, lk, 0x0001, c.seq++))
    await sleep(50)
  }
  c.ws.send(input(0, 0, norm(sub(target, c.ents.get(c.id).pos)), 0, c.seq++))
  await sleep(150)
}

// GDD "Rover seats" seat_pos, for the C26 composition check.
const SEAT_POS = { 1: [-0.35, 0.95, 0.10], 2: [0.35, 0.95, -0.40] }

const field = loadField(JSON.parse(readFileSync(new URL('./out/world-seed1337.json', import.meta.url), 'utf8')))

const a = connect('driver')
await wait(() => a.id && a.ents.size > 1)
const roverId = [...a.spawns].find(([, v]) => v.type === 5)?.[0]
check('a rover exists in the world', !!roverId, `id ${roverId}`)
if (!roverId) { console.log('OVERALL: FAIL'); process.exit(1) }
const rover = () => a.ents.get(roverId)

// C29: board from spawn — beyond board_dist.
a.ws.send(board(roverId, 1))
let r = await nextSeat(a)
check('C29 far board refused out-of-range', r?.result === SEAT.OUT_OF_RANGE, `result ${r?.result}`)
// C29: disembark while unseated.
a.ws.send(disembark())
r = await nextSeat(a)
check('C29 unseated disembark refused invalid', r?.result === SEAT.INVALID, `result ${r?.result}`)

await walkTo(a, rover().pos, 5)
check('walked into board_dist', dist(a.ents.get(a.id).pos, rover().pos) <= 8,
  `${dist(a.ents.get(a.id).pos, rover().pos).toFixed(2)} m`)

// C29: nonexistent seat.
a.ws.send(board(roverId, 5))
r = await nextSeat(a)
check('C29 seat 5 refused invalid', r?.result === SEAT.INVALID, `result ${r?.result}`)

// Board the driver seat.
a.ws.send(board(roverId, 1))
r = await nextSeat(a)
check('driver seat granted', r?.result === SEAT.GRANTED, `result ${r?.result}`)

// Second client: C28 seat race (sequential — exactly one holder), passenger.
const b = connect('rider')
await wait(() => b.id && b.ents.size > 1)
await walkTo(b, b.ents.get(roverId).pos, 5)
b.ws.send(board(roverId, 1))
r = await nextSeat(b)
check('C28 second driver request refused occupied', r?.result === SEAT.OCCUPIED, `result ${r?.result}`)
b.ws.send(board(roverId, 2))
r = await nextSeat(b)
check('passenger seat granted', r?.result === SEAT.GRANTED, `result ${r?.result}`)

// C26: B sees A seated within 1 s, at the composed seat position.
const seatRow = await wait(() => {
  const e = b.ents.get(a.id)
  return e && e.parent === roverId && e.seat === 1 ? e : null
}, 1000)
check('C26 occupancy visible to the other client within 1 s', !!seatRow)
if (seatRow) {
  const rv = b.ents.get(roverId)
  const want = add(rv.pos, quatRotate(rv.quat, SEAT_POS[1]))
  check('C26 composed seat position within 1e-2 m', dist(seatRow.pos, want) < 1e-2,
    `${dist(seatRow.pos, want).toExponential(2)} m`)
}

// Drive: 1 s of mode-2 full throttle, then 2 s of coast. C27 property along
// the way. Not 3 s flat out: that is 16 m/s and a 20 m coast, which ran the
// rover onto a 46° scarp ~29 m ahead of its spawn, past drive_slope_max
// (40°) — parked there, no throttle moves it again until a restart, and every
// later t29 on the same server found it wedged.
const start = rover().pos.slice()
let minClearance = Infinity
for (let i = 0; i < 60; i++) {
  a.ws.send(input(i < 20 ? 1 : 0, 0, [0, 0, 1], 0, a.seq++, 2))
  await sleep(50)
  const p = rover().pos
  minClearance = Math.min(minClearance, Math.hypot(...p) - sampleRadius(field, norm(p)))
}
check('rover drives under mode-2 throttle', dist(rover().pos, start) > 5,
  `${dist(rover().pos, start).toFixed(1)} m in 3 s`)
check('C27 rover never below the surface', minClearance > -1e-2,
  `min clearance ${minClearance.toExponential(2)} m`)
const seated = a.ents.get(a.id)
check('driver body rides the rover', dist(seated.pos, rover().pos) < 2,
  `${dist(seated.pos, rover().pos).toFixed(2)} m from origin`)

// Stop, settle, then C31: passenger movement input leaves the rover alone.
// hold_speed zeroes the coast once it decays under 0.1 m/s; wait for that,
// not for a fixed time.
for (let i = 0; i < 60; i++) { a.ws.send(input(0, 0, [0, 0, 1], 0, a.seq++, 2)); await sleep(50) }
await wait(() => Math.hypot(...rover().vel) < 0.01, 5000)
check('rover parks (hold_speed)', Math.hypot(...rover().vel) < 0.01,
  `residual ${Math.hypot(...rover().vel).toExponential(2)} m/s`)
const parked = rover().pos.slice()
for (let i = 0; i < 20; i++) { b.ws.send(input(1, 1, [0, 0, 1], 0x0003, b.seq++, 0)); await sleep(50) }
check('C31 passenger input leaves the rover invariant', dist(rover().pos, parked) < 0.05,
  `moved ${dist(rover().pos, parked).toExponential(2)} m`)
const bRow = b.ents.get(b.id)
check('C31 passenger stays composed at the seat', bRow.parent === roverId && bRow.seat === 2)

// A rover never wedges: drive on toward the 46° scarp ahead and stop on it
// (throttle off the moment the ground under it passes drive_slope_max, then
// park), then prove uphill throttle is refused and downhill throttle drives
// it off — the rule that keeps a later t29 on this server from finding it
// stuck. Whichever end faces downhill drives, steered onto the fall line.
const slopeAt = () => slopeDeg(field, norm(rover().pos))
const fwdOf = () => { const up = norm(rover().pos); return norm(tangent(quatRotate(rover().quat, [0, 0, 1]), up)) }
let offSign = -1 // C32's direction: away from the scarp
for (let i = 0; i < 80 && slopeAt() <= 40; i++) { a.ws.send(input(1, 0, [0, 0, 1], 0, a.seq++, 2)); await sleep(50) }
for (let i = 0; i < 60 && Math.hypot(...rover().vel) > 0.01; i++) { a.ws.send(input(0, 0, [0, 0, 1], 0, a.seq++, 2)); await sleep(50) }
const steepSlope = slopeAt()
if (steepSlope <= 40) {
  console.log(`SKIP a rover never wedges: the rover parked on ${steepSlope.toFixed(1)}°, not past drive_slope_max 40° — no scarp reached`)
} else {
  // Square the uphill end onto the fall line first (skid steer turns in
  // place at any slope): parked near-across the slope, "uphill" by the wire's
  // float32 pose can be a hair downhill to the server, which then (rightly)
  // lets it creep along the contour.
  const down0 = downhillTangent(field, norm(rover().pos))
  const upSign = dot(fwdOf(), down0) >= 0 ? -1 : 1
  for (let i = 0; i < 60; i++) {
    const u = norm(rover().pos), f = fwdOf(), target = downhillTangent(field, u).map((c) => -upSign * c)
    if (dot(f, target) > 0.97) break
    a.ws.send(input(0, Math.max(-1, Math.min(1, 3 * dot(target, cross(f, u)))), [0, 0, 1], 0, a.seq++, 2))
    await sleep(50)
  }
  const at = rover().pos.slice()
  for (let i = 0; i < 20; i++) { a.ws.send(input(upSign, 0, [0, 0, 1], 0, a.seq++, 2)); await sleep(50) }
  check('a rover never wedges: uphill throttle past drive_slope_max is refused', dist(rover().pos, at) < 0.05,
    `on ${steepSlope.toFixed(1)}°, moved ${dist(rover().pos, at).toFixed(3)} m under 1 s of uphill throttle`)
  let slopeNow = slopeAt()
  for (let i = 0; i < 100 && slopeNow > 40; i++) {
    const u = norm(rover().pos), f = fwdOf(), d = downhillTangent(field, u)
    offSign = dot(f, d) >= 0 ? 1 : -1
    const steer = Math.max(-1, Math.min(1, 3 * offSign * dot(d, cross(f, u))))
    a.ws.send(input(offSign, steer, [0, 0, 1], 0, a.seq++, 2))
    await sleep(50)
    slopeNow = slopeAt()
  }
  check('a rover never wedges: downhill throttle drives it off the scarp', slopeNow <= 40,
    `${steepSlope.toFixed(1)}° -> ${slopeNow.toFixed(1)}° after ${dist(rover().pos, at).toFixed(1)} m`)
  for (let i = 0; i < 60 && Math.hypot(...rover().vel) > 0.01; i++) { a.ws.send(input(0, 0, [0, 0, 1], 0, a.seq++, 2)); await sleep(50) }
}

// C32: drive again and disembark at speed — AWAY from the scarp (reverse
// if it was never reached, else the end that just drove off it), so the
// unmanned coast does not run back onto it.
for (let i = 0; i < 30; i++) { a.ws.send(input(offSign, 0, [0, 0, 1], 0, a.seq++, 2)); await sleep(50) }
a.ws.send(disembark())
r = await nextSeat(a)
check('disembark at speed granted', r?.result === SEAT.GRANTED, `result ${r?.result}`)
const off = await wait(() => {
  const e = a.ents.get(a.id)
  return e && e.parent === 0 ? e : null
}, 1000)
check('C32 body unseated within 1 s', !!off)
if (off) {
  const p = off.pos, up = norm(p)
  check('C32 lands within 10 m of the rover', dist(p, rover().pos) <= 10,
    `${dist(p, rover().pos).toFixed(2)} m`)
  check('C32 lands on the terrain', Math.abs(Math.hypot(...p) - sampleRadius(field, up)) < 0.5,
    `${(Math.hypot(...p) - sampleRadius(field, up)).toExponential(2)} m off the surface`)
}

a.ws.close(); b.ws.close()
const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
