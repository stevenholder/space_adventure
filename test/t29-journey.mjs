#!/usr/bin/env node
/**
 * t29 — one life, the whole game.
 *
 * The other tests each prove one layer. This one plays a session the way a
 * player would, in order, in a single connection:
 *
 *   join fresh -> shop with the quartermaster (list, buy rifle + ammo,
 *   equip, reload) -> board the rover, DRIVE out along the first legs of
 *   the solved camp route with waypoint steering, then drive back and
 *   park where it stood -> disembark clean -> walk the route into the
 *   camp -> pick a hostile and fight it to the death (ours or its) ->
 *   count the receipts.
 *
 * Why a round trip: the rover is WORLD state, not per-player — wherever
 * this run parks it is where the next run finds it, and there is no
 * solved walking route to an arbitrary parking spot (the planet is scarp
 * country; straight lines wedge). Driving out and back proves steering in
 * both directions and leaves the world roughly as found. Driving is
 * proven by covered ground and clearance, not by arrival — the route was
 * solved for a walker. ponytail: each round trip parks ~10 m off, so the
 * rover drifts slowly from spawn across many runs; a server restart
 * resets it (vehicles are in-memory).
 *
 * Combat asserts the full exchange: our shots register (hit events naming
 * us as shooter), the target's health falls to a death event, loot drops,
 * and the camp shoots back (hits naming us as victim). Dying mid-fight is
 * a legitimate outcome — respawn ends the fight, and the checks care that
 * damage flowed both ways, not that we won.
 *
 * Run: node test/t29-journey.mjs   (needs `make up`)
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { downhillTangent, loadField, sampleRadius, slopeDeg } from './lib/field.mjs'
import { quatRotate, SEAT } from './lib/wire.mjs'

const enc = new TextEncoder(), dec = new TextDecoder(), u8 = (n) => new Uint8Array(n)
const frame = (t, b) => { const o = u8(2 + b.length); new DataView(o.buffer).setUint16(0, t, true); o.set(b, 2); return o }
function hello (n_, tk) {
  const n = enc.encode(n_), t = enc.encode(tk)
  const b = u8(2 + 4 + n.length + 4 + t.length), d = new DataView(b.buffer)
  d.setUint16(0, 2, true); d.setUint32(2, n.length, true); b.set(n, 6)
  d.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(1, b)
}
function input (mx, my, look, mask, seq, mode = 0) {
  const b = u8(25), dv = new DataView(b.buffer)
  dv.setFloat32(0, mx, true); dv.setFloat32(4, my, true)
  dv.setFloat32(8, look[0], true); dv.setFloat32(12, look[1], true); dv.setFloat32(16, look[2], true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true); dv.setUint8(24, mode)
  return frame(0x0003, b)
}
function cmd (seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(8 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
function fire (seq, dir) {
  const b = u8(14), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true)
  dv.setFloat32(2, dir[0], true); dv.setFloat32(6, dir[1], true); dv.setFloat32(10, dir[2], true)
  return frame(0x0011, b)
}
function board (vehicleId, seat) {
  const b = u8(6), dv = new DataView(b.buffer)
  dv.setUint32(0, vehicleId, true); dv.setUint16(4, seat, true)
  return frame(0x000b, b)
}
const disembark = () => frame(0x000c, u8(0))

const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const dist = (a, b) => Math.hypot(...sub(a, b))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
/** Project d onto the tangent plane at unit up. */
const tangent = (d, up) => { const k = dot(d, up); return [d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k] }

// ---- connection -------------------------------------------------------------

const token = 'journey-' + Date.now() // fresh: this is a first life, always
const ws = new WebSocket(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws')
ws.binaryType = 'arraybuffer'
let myId = 0
const ents = new Map(), spawns = new Map(), results = [], events = [], seats = []
ws.addEventListener('open', () => ws.send(hello('journeyer', token)))
ws.addEventListener('message', (ev) => {
  const dv = new DataView(ev.data), t = dv.getUint16(0, true)
  const pv = new DataView(ev.data, 2), p = new Uint8Array(ev.data, 2)
  if (t === 0x0002) myId = pv.getUint32(8, true)
  else if (t === 0x0005) spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)) })
  else if (t === 0x0004) {
    const n = pv.getUint16(6, true)
    for (let i = 0; i < n; i++) {
      const o = 8 + i * 54
      ents.set(pv.getUint32(o, true), {
        pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
        quat: [pv.getFloat32(o + 16, true), pv.getFloat32(o + 20, true), pv.getFloat32(o + 24, true), pv.getFloat32(o + 28, true)],
        vel: [pv.getFloat32(o + 32, true), pv.getFloat32(o + 36, true), pv.getFloat32(o + 40, true)],
        parent: pv.getUint32(o + 44, true),
        seat: pv.getUint16(o + 48, true),
        health: pv.getUint16(o + 50, true),
        flags: pv.getUint8(o + 52),
      })
    }
  } else if (t === 0x0007) {
    const e = { id: pv.getUint32(0, true), ev: pv.getUint16(4, true) }
    // Hit payload: u32 shooter | f32 point[3] | u16 damage | u16 health_after
    if (e.ev === 3 && p.length >= 6 + 20) {
      const hv = new DataView(ev.data, 2 + 10) // past entity id + event id + data len
      e.shooter = hv.getUint32(0, true)
      e.damage = hv.getUint16(16, true)
    }
    events.push(e)
  } else if (t === 0x000d) seats.push({ result: pv.getUint8(6) })
  else if (t === 0x000f) {
    results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
  }
})

const wait = async (fn, ms = 6000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const me = () => ents.get(myId)
let seq = 1
const nextResult = async (op) => { const n = results.length; return wait(() => results.slice(n - 1).find((r) => r.op === op)) }
const sendCmd = async (op, body) => { ws.send(cmd(seq++, op, body)); const n = results.length; return wait(() => results.slice(n).find((r) => r.op === op)) }
const nextSeat = async () => { const n = seats.length; return wait(() => seats.length > n ? seats[seats.length - 1] : null) }

/**
 * Walk toward a point along the surface; stops within `close` m or timeout.
 * `target` may be a function — a rover moved by an earlier run, say — and
 * is re-read every step. No stop-and-settle at the end of a leg: an idle
 * tick on a scarp slides the player, and the slide changed which side of
 * the leg-5 jam the route recovered on (found the hard way — the settle
 * version wedged 127 m out on every run, t16's continuous walker got in).
 */
async function walkTo (target, close, timeoutMs = 30000) {
  const at = typeof target === 'function' ? target : () => target
  const t0 = Date.now()
  let lastPos = me().pos.slice(), lastMove = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const d = sub(at(), me().pos)
    if (Math.hypot(...d) <= close) break
    const up = norm(me().pos)
    // Wedged on a scarp for a second? Do what a player does: jump at it.
    if (dist(me().pos, lastPos) > 0.5) { lastPos = me().pos.slice(); lastMove = Date.now() }
    const mask = Date.now() - lastMove > 1000 ? 0x0003 : 0x0001 // sprint (+jump when stuck)
    ws.send(input(0, 1, norm(tangent(d, up)), mask, seq++))
    await sleep(50)
  }
  return dist(at(), me().pos)
}

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}

// ---- act 0: join ------------------------------------------------------------

await wait(() => myId && ents.get(myId) && spawns.size > 3)
const field = loadField(JSON.parse(readFileSync(new URL('./out/world-seed1337.json', import.meta.url), 'utf8')))
const route = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8'))
console.log(`joined id=${myId}, ${spawns.size} spawns, health ${me().health}`)

const WALK_ONLY = !!process.env.SA_T29_WALK_ONLY
// Drive results, hoisted past the WALK_ONLY guard for the evidence write.
let out = { odometer: 0 }, parkedOff = 0, minClearance = Infinity, startSlope = null, unwedged = null

// ---- act 1: the quartermaster ----------------------------------------------

const shopId = WALK_ONLY ? null : [...spawns].find(([, v]) => v.type === 3 && v.def === 'npc.quartermaster')?.[0]
if (WALK_ONLY) console.log('SA_T29_WALK_ONLY: skipping shop and rover acts')
if (!WALK_ONLY) {
check('quartermaster exists', !!shopId)
await walkTo(ents.get(shopId).pos, 2.4, 15000)
console.log(`at the quartermaster: ${dist(ents.get(shopId).pos, me().pos).toFixed(1)} m`)

const list = await sendCmd(0x0001, { npc: shopId })
check('shop_list returned stock', list?.status === 0 && list.body.stock?.length > 0)
// Prices and the start purse are data (Phase 22; a dev fleet funds guests
// with SA_START): read them, never hard-code them.
const priceOf = (item) => list?.body?.stock?.find((s) => s.item === item)?.price
const purse = (await sendCmd(0x0004, {}))?.body?.credits ?? 0
const buyGun = await sendCmd(0x0002, { npc: shopId, item: 'weapon.pulse', qty: 1 })
check(`bought the rifle (${purse} -> ${purse - priceOf('weapon.pulse')})`, buyGun?.status === 0 && buyGun.body.credits === purse - priceOf('weapon.pulse'), `credits ${buyGun?.body?.credits}`)
const buyAmmo = await sendCmd(0x0002, { npc: shopId, item: 'ammo.cell', qty: 1 })
check(`bought a cell (-${priceOf('ammo.cell')})`, buyAmmo?.status === 0 && buyAmmo.body.credits === purse - priceOf('weapon.pulse') - priceOf('ammo.cell'), `credits ${buyAmmo?.body?.credits}`)
const eq = await sendCmd(0x0003, { slot: 'primary', item: 'weapon.pulse' })
check('equipped the rifle', eq?.status === 0 && eq.body.equipped?.primary === 'weapon.pulse')
const rl = await sendCmd(0x0005, {})
check('reload accepted', rl?.status === 0, `magazine ${rl?.body?.magazine}/${rl?.body?.reserve}`)

// ---- act 2: drive -----------------------------------------------------------

const roverId = [...spawns].find(([, v]) => v.type === 5)?.[0]
check('a rover exists', !!roverId)
const rover = () => ents.get(roverId)
// The rover sits wherever the LAST run (this test, or t24) parked it —
// vehicles are world state, not per-player. Walk to it wherever it is.
console.log(`rover is ${dist(rover().pos, me().pos).toFixed(0)} m away`)
// A rover an earlier session parked past drive_slope_max (40°) refuses
// uphill throttle, but downhill throttle still works (drive.go step 4: "a
// rover never wedges") — so board it wherever it is and, if it is on a
// scarp, drive it downhill off it before the waypoint plan.
startSlope = slopeDeg(field, norm(rover().pos))
await walkTo(() => rover().pos, 5, 120000)
ws.send(board(roverId, 1))
let seat = await nextSeat()
check('driver seat granted', seat?.result === SEAT.GRANTED, `result ${seat?.result}`)
if (startSlope > 40) {
  const from = rover().pos.slice()
  let slopeNow = startSlope
  for (let i = 0; i < 100 && slopeNow > 40; i++) {
    // Whichever end faces more downhill drives; steer that end onto the
    // fall line — a rover parked across the slope (heading on the contour,
    // where an earlier forward push leaves it) has neither end downhill.
    const rv = rover(), up = norm(rv.pos)
    const fwd = norm(tangent(quatRotate(rv.quat, [0, 0, 1]), up))
    const right = [fwd[1] * up[2] - fwd[2] * up[1], fwd[2] * up[0] - fwd[0] * up[2], fwd[0] * up[1] - fwd[1] * up[0]]
    const down = downhillTangent(field, up)
    const sign = dot(fwd, down) >= 0 ? 1 : -1
    const steer = Math.max(-1, Math.min(1, 3 * sign * dot(down, right)))
    ws.send(input(sign, steer, [0, 0, 1], 0, seq++, 2))
    await sleep(50)
    slopeNow = slopeDeg(field, norm(rover().pos))
  }
  for (let i = 0; i < 40 && Math.hypot(...rover().vel) > 0.01; i++) { ws.send(input(0, 0, [0, 0, 1], 0, seq++, 2)); await sleep(50) }
  unwedged = slopeNow <= 40
  check('a rover never wedges: drove it downhill off the scarp', unwedged,
    `parked on ${startSlope.toFixed(1)}° (past drive_slope_max 40°), now ${slopeNow.toFixed(1)}° after ${dist(rover().pos, from).toFixed(1)} m`)
} else {
  console.log(`rover parked on ${startSlope.toFixed(1)}° (within drive_slope_max 40°)`)
}

// Waypoint steering. Positive steer rotates the heading by −steer·θ
// about up (drive.go), which moves it toward Cross(fwd, up) — the game
// frame's right is −X (Step.cs). Steer by which side of the nose the
// error sits on; give up on a plan that stalls (the point is covered
// ground, not arrival), then drive the same waypoints in reverse so the
// rover ends near where it started.
async function drivePlan (points, budgetMs) {
  const t0 = Date.now()
  let i = 0
  let stallSince = null
  let odometer = 0
  let last = rover().pos.slice()
  while (Date.now() - t0 < budgetMs && i < points.length) {
    const rv = rover()
    const up = norm(rv.pos)
    const err = tangent(sub(points[i], rv.pos), up)
    if (Math.hypot(...err) < 10) { i++; continue }
    const fwd = norm(tangent(quatRotate(rv.quat, [0, 0, 1]), up))
    const right = [fwd[1] * up[2] - fwd[2] * up[1], fwd[2] * up[0] - fwd[0] * up[2], fwd[0] * up[1] - fwd[1] * up[0]]
    const errN = norm(err)
    const steer = Math.max(-1, Math.min(1, 3 * dot(errN, right)))
    // Ease off close to the point: at full tilt the turn radius is wider
    // than the arrival tolerance and the rover orbits its target.
    let throttle = dot(errN, fwd) > 0.3 ? 1 : 0.2
    if (Math.hypot(...err) < 30) throttle = Math.min(throttle, 0.4)
    ws.send(input(throttle, steer, [0, 0, 1], 0, seq++, 2))
    await sleep(50)
    minClearance = Math.min(minClearance, Math.hypot(...rv.pos) - sampleRadius(field, norm(rv.pos)))
    odometer += dist(rv.pos, last); last = rv.pos.slice()
    const speed = Math.hypot(...rv.vel)
    if (speed < 0.3) { stallSince ??= Date.now(); if (Date.now() - stallSince > 4000) break } else stallSince = null
  }
  return { odometer, reached: i }
}

const driveStart = rover().pos.slice()
const outbound = route.waypoints.slice(1, 4)
out = await drivePlan(outbound, 40000)
console.log(`drove out ${out.odometer.toFixed(1)} m, reached waypoint index ${1 + out.reached}`)
const back = await drivePlan(outbound.slice(0, Math.max(0, out.reached - 1)).reverse().concat([driveStart]), 40000)
parkedOff = dist(rover().pos, driveStart)
console.log(`drove back ${back.odometer.toFixed(1)} m, parked ${parkedOff.toFixed(1)} m from where it stood`)
check('rover covered real ground out (> 40 m)', out.odometer > 40, `${out.odometer.toFixed(1)} m`)
check('rover drove back near its start (< 25 m)', parkedOff < 25, `${parkedOff.toFixed(1)} m off`)
check('rover never below the surface', minClearance > -1e-2, `min clearance ${minClearance.toExponential(2)} m`)

// Park (hold_speed) and get out.
for (let i = 0; i < 30 && Math.hypot(...rover().vel) > 0.01; i++) { ws.send(input(0, 0, [0, 0, 1], 0, seq++, 2)); await sleep(50) }
ws.send(disembark())
seat = await nextSeat()
check('disembark granted', seat?.result === SEAT.GRANTED, `result ${seat?.result}`)
await wait(() => me().parent === 0, 2000)
const up0 = norm(me().pos)
check('on foot, on the terrain', me().parent === 0 && Math.abs(Math.hypot(...me().pos) - sampleRadius(field, up0)) < 0.5)
}

// ---- act 3: walk the rest of the route into the camp ------------------------

const hostiles = () => [...spawns]
  .filter(([, v]) => v.type === 3 && v.def !== 'npc.quartermaster')
  .map(([id]) => id)
  .filter((id) => ents.get(id) && !(ents.get(id).flags & 0x04))
check('the camp has hostiles', hostiles().length > 0, `${hostiles().length} alive`)

// The route was solved FROM SPAWN, and legs walked from anywhere else
// wedge on scarps the corridor was solved around. The round trip parked
// us near spawn; walk to waypoint 0 across the open ground first, then
// replay the route exactly as solved (the t16 lesson, relearned here:
// legs 5+ jammed when picked up mid-route from 40 m off-corridor).
// t16's pacing, kept deliberately: legs 5–11 of the solved route jam on
// scarps and burn their timeout, and the later waypoints recover from
// exactly where the jams leave you. Short legs so the budget survives to
// the recovering ones.
const campPos = () => ents.get(hostiles()[0])?.pos ?? route.waypoints.at(-1)
const walkT0 = Date.now()
for (let i = 0; i < route.waypoints.length && Date.now() - walkT0 < 300000; i++) {
  const off = await walkTo(route.waypoints[i], 6, 20000)
  if (off > 12) {
    const pp = me().pos, uu = norm(pp)
    console.log(`  leg ${i}: stopped ${off.toFixed(1)} m short at [${pp.map(x=>x.toFixed(1))}] slope-site r=${Math.hypot(...pp).toFixed(1)} field=${sampleRadius(field, uu).toFixed(1)}`)
  }
  if (dist(campPos(), me().pos) <= 30) break
}
const standoff = dist(campPos(), me().pos)
console.log(`at the camp: ${standoff.toFixed(1)} m from the nearest hostile`)
check('reached the camp (<= 30 m)', standoff <= 30, `${standoff.toFixed(1)} m`)

if (WALK_ONLY) {
  console.log('SA_T29_WALK_ONLY: stopping before the fight (no rifle was bought)')
  ws.close()
  process.exit(checks.some(([, ok]) => !ok) ? 1 : 0)
}

// ---- act 4: the fight -------------------------------------------------------

const hpBefore = me().health
const lootSpawns0 = [...spawns.values()].filter((v) => v.type === 6).length
let targetId = hostiles()[0]
const targetHp0 = ents.get(targetId)?.health ?? 0
const fightT0 = Date.now()
let iDied = false
while (Date.now() - fightT0 < 45000) {
  // Re-pick if the target died; stop when the camp is clear or we are dead.
  if (!hostiles().includes(targetId)) {
    if (events.some((e) => e.ev === 4 && e.id === targetId)) break
    targetId = hostiles()[0]
    if (!targetId) break
  }
  if (events.some((e) => e.ev === 4 && e.id === myId)) { iDied = true; break }
  const t = ents.get(targetId)
  const up = norm(me().pos)
  const eye = me().pos.map((x, i) => x + up[i] * 1.7)
  const tUp = norm(t.pos)
  const tEye = t.pos.map((x, i) => x + tUp[i] * 0.9)
  const aim = norm(sub(tEye, eye))
  ws.send(input(0, 0, aim, 0, seq++))
  ws.send(fire(seq - 1, aim))
  await sleep(200) // fire_interval is 150 ms; 200 keeps every shot legal
}
// Session-wide counts on purpose: the camp opens fire during the walk in
// (that IS the camp fighting back), and the kill can land its loot event
// a beat after the loop exits.
await sleep(500)
const myHits = events.filter((e) => e.ev === 3 && e.shooter === myId).length
const hitsOnMe = events.filter((e) => e.ev === 3 && e.id === myId).length
const kills = events.filter((e) => e.ev === 4 && e.id !== myId).length
const lootEvents = events.filter((e) => e.ev === 5).length
const lootSpawned = [...spawns.values()].filter((v) => v.type === 6).length - lootSpawns0
const hpAfter = ents.get(myId)?.health ?? 0
console.log(`fight: our hits ${myHits}, hits on us ${hitsOnMe}, kills ${kills}, ` +
  `loot events ${lootEvents} / spawns ${lootSpawned}, health ${hpBefore} -> ${hpAfter}${iDied ? ' (died fighting)' : ''}`)

check('our shots registered on the hostile', myHits > 0, `${myHits} hits, target hp0 ${targetHp0}`)
check('a hostile died to us', kills > 0, `${kills} deaths`)
check('the kill dropped loot (event + spawn)', lootEvents > 0 && lootSpawned > 0,
  `${lootEvents} events, ${lootSpawned} spawns`)
check('the camp fought back', hitsOnMe > 0 || iDied, `${hitsOnMe} hits on us`)

// ---- act 5: the receipts ----------------------------------------------------

const inv = await sendCmd(0x0004, {})
check('inventory answers after the fight', inv?.status === 0)
if (inv?.status === 0) {
  const names = (inv.body.inventory ?? []).map((i) => `${i.item}x${i.qty}`).join(' ')
  console.log(`carrying: ${names} | credits ${inv.body.credits}`)
  check('still owns the rifle', (inv.body.inventory ?? []).some((i) => i.item === 'weapon.pulse'))
}

ws.close()
const fails = checks.filter(([, ok]) => !ok)
writeFileSync(new URL('./out/t29-journey.json', import.meta.url), JSON.stringify({
  when: new Date().toISOString(),
  driveSkipped: WALK_ONLY ? 'walk-only' : null,
  roverStartSlope: startSlope,
  unwedged,
  drivenOut: WALK_ONLY ? null : +out.odometer.toFixed(1),
  parkedOff: WALK_ONLY ? null : +parkedOff.toFixed(1),
  minClearance, standoff: +standoff.toFixed(1),
  myHits, hitsOnMe, kills, lootEvents, lootSpawned, died: iDied,
  checks: checks.map(([n, ok]) => ({ n, ok })),
}, null, 1))
console.log(fails.length
  ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})`
  : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
