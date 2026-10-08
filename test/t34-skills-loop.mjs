#!/usr/bin/env node
/**
 * t34 — C78: doing trains. One life, seven skills.
 *
 * A fresh player plays the verbs the roster hooks, in one connection, and
 * watches the sheet move:
 *
 *   sprint to the quartermaster (Athletics) -> buy rifle, ammo, ship
 *   (Commerce) -> fly the committed C34 arc as pilot and land (Piloting,
 *   plus the clean-landing bonus) -> drive the rover out and back
 *   (Driving) -> walk the solved route into the camp (Recon fires on
 *   entering the POI) -> fight (Marksmanship) -> walk onto the kill's
 *   drop while still shooting (Scavenging) -> read the sheet, drop the
 *   connection, come back on the same token and read it again.
 *
 * Asserts: every one of the seven skills reports XP on the wire, each
 * skill_xp event carries the level the curve says its xp means, events
 * for one skill never arrive closer than the 1 Hz flush allows, the sheet
 * agrees with the last event per skill, the camp is on the discovered
 * list, and the sheet after reconnect is byte-identical. Dying in the
 * camp is tolerated for the fight (t29's rule) but fails the loot act —
 * the test walks onto the drop while shooting the next hostile precisely
 * so it usually lives long enough.
 *
 * Run: node test/t34-skills-loop.mjs   (needs `make up`)
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { loadField, sampleRadius } from './lib/field.mjs'
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
function input (v, mask, seq, mode = 0) {
  const b = u8(25), dv = new DataView(b.buffer)
  for (let i = 0; i < 5; i++) dv.setFloat32(i * 4, v[i] ?? 0, true)
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
const tangent = (d, up) => { const k = dot(d, up); return [d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k] }

// The curve, for the level-on-the-wire check (server/internal/skills).
function pointsForLevel (level) {
  if (level <= 1) return 0
  let pts = 0
  for (let l = 1; l < Math.min(level, 99); l++) pts += Math.trunc(l + 300 * Math.pow(2, l / 7))
  return Math.trunc(pts / 4)
}
function levelForXP (xp) { for (let l = 99; l >= 2; l--) if (xp >= pointsForLevel(l)) return l; return 1 }

const SEVEN = ['marksmanship', 'athletics', 'driving', 'piloting', 'scavenging', 'commerce', 'recon']
const OP_SKILLS = 0x000e, EV_SKILL_XP = 0x000d

// ---- connection -------------------------------------------------------------

function connect (name, token) {
  const ws = new WebSocket(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws')
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), results: [], events: [], seats: [], xp: [], seq: 1 }
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const pv = new DataView(ev.data, 2), p = new Uint8Array(ev.data, 2)
    if (t === 0x0002) c.id = pv.getUint32(8, true)
    else if (t === 0x0005) c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)), at: Date.now() })
    else if (t === 0x0006) c.spawns.delete(pv.getUint32(0, true))
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
          health: pv.getUint16(o + 50, true),
          flags: pv.getUint8(o + 52),
        })
      }
    } else if (t === 0x0007) {
      const e = { id: pv.getUint32(0, true), ev: pv.getUint16(4, true) }
      if (e.ev === 3 && p.length >= 6 + 20) {
        const hv = new DataView(ev.data, 2 + 10)
        e.shooter = hv.getUint32(0, true)
        e.damage = hv.getUint16(16, true)
      }
      if (e.ev === EV_SKILL_XP) {
        const x = JSON.parse(dec.decode(p.subarray(10)))
        c.xp.push({ ...x, at: Date.now() })
      }
      c.events.push(e)
    } else if (t === 0x000d) c.seats.push({ result: pv.getUint8(6) })
    else if (t === 0x000f) {
      c.results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    }
  })
  return c
}

const wait = async (fn, ms = 6000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const token = 'skills-' + Date.now()
let c = connect('trainee', token)
const me = () => c.ents.get(c.id)
const sendCmd = async (op, body) => { c.ws.send(cmd(c.seq++, op, body)); const n = c.results.length; return wait(() => c.results.slice(n).find((r) => r.op === op)) }
const nextSeat = async () => { const n = c.seats.length; return wait(() => c.seats.length > n ? c.seats[c.seats.length - 1] : null) }

/** Sprint toward a point along the surface (t29's walker: jump when wedged). */
async function walkTo (target, close, timeoutMs = 30000) {
  const at = typeof target === 'function' ? target : () => target
  const t0 = Date.now()
  let lastPos = me().pos.slice(), lastMove = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const d = sub(at(), me().pos)
    if (Math.hypot(...d) <= close) break
    const up = norm(me().pos)
    if (dist(me().pos, lastPos) > 0.5) { lastPos = me().pos.slice(); lastMove = Date.now() }
    const mask = Date.now() - lastMove > 1000 ? 0x0003 : 0x0001
    const lk = norm(tangent(d, up))
    c.ws.send(input([0, 1, lk[0], lk[1], lk[2]], mask, c.seq++))
    await sleep(50)
  }
  return dist(at(), me().pos)
}

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}
const xpOf = (skill) => c.xp.filter((x) => x.skill === skill)
const lastXP = (skill) => xpOf(skill).at(-1)?.xp ?? 0

// ---- act 0: join ------------------------------------------------------------

await wait(() => c.id && me() && c.spawns.size > 3)
const field = loadField(JSON.parse(readFileSync(new URL('./out/world-seed1337.json', import.meta.url), 'utf8')))
const route = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8'))
const script = readFileSync(new URL('./t25-flight-script.jsonl', import.meta.url), 'utf8')
  .split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l).input)
console.log(`joined id=${c.id}, ${c.spawns.size} spawns`)

const fresh = await sendCmd(OP_SKILLS, {})
check('a fresh sheet is empty', fresh?.status === 0 && Object.keys(fresh.body.xp ?? {}).length === 0 && (fresh.body.discovered ?? []).length === 0,
  JSON.stringify(fresh?.body))

// ---- act 1: sprint to the quartermaster, trade -------------------------------

const shopId = [...c.spawns].find(([, v]) => v.type === 3 && v.def === 'npc.quartermaster')?.[0]
check('quartermaster exists', !!shopId)
// The shop is a few metres from spawn; Athletics pays per WHOLE 10 m, so
// sprint away first and come back — the walk that also proves the drip.
const spawnPos = me().pos.slice()
const upS = norm(spawnPos)
const away = spawnPos.map((x, i) => x + norm(tangent([1, 0, 0], upS))[i] * 30)
await walkTo(away, 4, 15000)
await walkTo(c.ents.get(shopId).pos, 2.4, 20000)
console.log(`at the quartermaster: ${dist(c.ents.get(shopId).pos, me().pos).toFixed(1)} m`)

const preShips = new Set([...c.spawns].filter(([, v]) => v.type === 2).map(([id]) => id))
// Prices are data (Phase 22 moved them): Commerce pays 1 XP per whole 5 cr
// of each purchase.
const stock = (await sendCmd(0x0001, { npc: shopId }))?.body?.stock ?? []
const priceOf = (item) => stock.find((s) => s.item === item)?.price ?? 0
const commerceWant = ['weapon.pulse', 'ammo.cell', 'ship.v1'].reduce((n, it) => n + Math.floor(priceOf(it) / 5), 0)
const buyGun = await sendCmd(0x0002, { npc: shopId, item: 'weapon.pulse', qty: 1 })
const buyAmmo = await sendCmd(0x0002, { npc: shopId, item: 'ammo.cell', qty: 1 })
const buyShip = await sendCmd(0x0002, { npc: shopId, item: 'ship.v1', qty: 1 })
check('bought rifle, cell and ship', buyGun?.status === 0 && buyAmmo?.status === 0 && buyShip?.status === 0,
  `credits ${buyShip?.body?.credits}`)
const eq = await sendCmd(0x0003, { slot: 'primary', item: 'weapon.pulse' })
const rl = await sendCmd(0x0005, {})
check('armed', eq?.status === 0 && rl?.status === 0)

await wait(() => lastXP('commerce') >= commerceWant, 3000)
check(`Commerce: 1 XP per 5 cr moved = ${commerceWant} XP`, lastXP('commerce') === commerceWant, `${lastXP('commerce')} xp`)
check('Athletics trained on the sprint', lastXP('athletics') > 0, `${lastXP('athletics')} xp`)

// ---- act 2: fly the C34 arc as pilot -----------------------------------------

const shipId = await wait(() => [...c.spawns].find(([id, v]) => v.type === 2 && !preShips.has(id))?.[0], 3000)
check('the ship spawned on the pad', !!shipId, `id ${shipId}`)
const ship = () => c.ents.get(shipId)
await wait(() => ship())
await walkTo(() => ship().pos, 5, 30000)
c.ws.send(board(shipId, 1))
let seat = await nextSeat()
check('pilot seat granted', seat?.result === SEAT.GRANTED, `result ${seat?.result}`)
const flyFrom = ship().pos.slice()
let apex = 0
for (const s of script) {
  c.ws.send(input([s.thrust, s.roll, s.yaw_rate, s.pitch_rate, 0], s.boost ? 0x0004 : 0, c.seq++, 1))
  await sleep(50)
  apex = Math.max(apex, Math.hypot(...ship().pos) - sampleRadius(field, norm(ship().pos)))
}
const landed = await wait(() => (ship().flags & 0x01) && Math.hypot(...ship().vel) < 0.05, 8000)
console.log(`flew to ${apex.toFixed(0)} m altitude, ${landed ? 'landed' : 'NOT landed'}, ${dist(ship().pos, flyFrom).toFixed(0)} m from the pad`)
c.ws.send(disembark())
seat = await nextSeat()
check('disembarked the ship', seat?.result === SEAT.GRANTED)
await wait(() => me().parent === 0, 2000)
await wait(() => lastXP('piloting') > 0, 3000)
check('Piloting trained on the flight', lastXP('piloting') > 0, `${lastXP('piloting')} xp`)
// The arc is t25's: it climbs to space and back, thousands of metres of
// flown distance, plus 50 for the clean landing when the sim calls it one.
check('Piloting paid real distance (>= 100 XP)', lastXP('piloting') >= 100, `${lastXP('piloting')} xp`)

// ---- act 3: drive the rover ---------------------------------------------------

const roverId = [...c.spawns].find(([, v]) => v.type === 5)?.[0]
check('a rover exists', !!roverId)
const rover = () => c.ents.get(roverId)
console.log(`rover is ${dist(rover().pos, me().pos).toFixed(0)} m away`)
await walkTo(() => rover().pos, 5, 120000)
c.ws.send(board(roverId, 1))
seat = await nextSeat()
check('driver seat granted', seat?.result === SEAT.GRANTED, `result ${seat?.result}`)
// Out along the first route legs and back (t29's steering), so the rover
// ends near where it stood.
async function drivePlan (points, budgetMs) {
  const t0 = Date.now()
  let i = 0, stallSince = null, odometer = 0, last = rover().pos.slice()
  while (Date.now() - t0 < budgetMs && i < points.length) {
    const rv = rover(), up = norm(rv.pos)
    const err = tangent(sub(points[i], rv.pos), up)
    if (Math.hypot(...err) < 10) { i++; continue }
    const fwd = norm(tangent(quatRotate(rv.quat, [0, 0, 1]), up))
    const right = [fwd[1] * up[2] - fwd[2] * up[1], fwd[2] * up[0] - fwd[0] * up[2], fwd[0] * up[1] - fwd[1] * up[0]]
    const errN = norm(err)
    const steer = Math.max(-1, Math.min(1, 3 * dot(errN, right)))
    let throttle = dot(errN, fwd) > 0.3 ? 1 : 0.2
    if (Math.hypot(...err) < 30) throttle = Math.min(throttle, 0.4)
    c.ws.send(input([throttle, steer, 0, 0, 1], 0, c.seq++, 2))
    await sleep(50)
    odometer += dist(rv.pos, last); last = rv.pos.slice()
    if (Math.hypot(...rv.vel) < 0.3) { stallSince ??= Date.now(); if (Date.now() - stallSince > 4000) break } else stallSince = null
  }
  return { odometer, reached: i }
}
const driveStart = rover().pos.slice()
const outbound = route.waypoints.slice(1, 3)
const out = await drivePlan(outbound, 25000)
const back = await drivePlan(outbound.slice(0, Math.max(0, out.reached - 1)).reverse().concat([driveStart]), 25000)
console.log(`drove ${out.odometer.toFixed(0)} m out, ${back.odometer.toFixed(0)} m back, parked ${dist(rover().pos, driveStart).toFixed(0)} m off`)
for (let i = 0; i < 30 && Math.hypot(...rover().vel) > 0.01; i++) { c.ws.send(input([0, 0, 0, 0, 1], 0, c.seq++, 2)); await sleep(50) }
c.ws.send(disembark())
seat = await nextSeat()
check('disembarked the rover', seat?.result === SEAT.GRANTED)
await wait(() => me().parent === 0, 2000)
await wait(() => lastXP('driving') > 0, 3000)
check('Driving trained on the drive', lastXP('driving') > 0, `${lastXP('driving')} xp, ${(out.odometer + back.odometer).toFixed(0)} m`)

// ---- act 4: into the camp (Recon on the way in) ------------------------------

const hostiles = () => [...c.spawns]
  .filter(([, v]) => v.type === 3 && v.def !== 'npc.quartermaster')
  .map(([id]) => id)
  .filter((id) => c.ents.get(id) && !(c.ents.get(id).flags & 0x04))
check('the camp has hostiles', hostiles().length > 0, `${hostiles().length} alive`)
const campPos = () => c.ents.get(hostiles()[0])?.pos ?? route.waypoints.at(-1)
const walkT0 = Date.now()
for (let i = 0; i < route.waypoints.length && Date.now() - walkT0 < 300000; i++) {
  await walkTo(route.waypoints[i], 6, 20000)
  if (dist(campPos(), me().pos) <= 30) break
}
const standoff = dist(campPos(), me().pos)
check('reached the camp (<= 30 m)', standoff <= 30, `${standoff.toFixed(1)} m`)
await wait(() => lastXP('recon') > 0, 3000)
// 250 per first discovery; the flight may have crossed the relay from the
// air on the way, so this is "a multiple", and the sheet check below ties
// it to the discovered list exactly.
check('Recon: the camp paid 250 on entry', lastXP('recon') >= 250 && lastXP('recon') % 250 === 0, `${lastXP('recon')} xp`)

// ---- act 5: the fight, then the drop -----------------------------------------

const lootBefore = new Set([...c.spawns].filter(([, v]) => v.type === 6).map(([id]) => id))
const aimAt = (id) => {
  const t = c.ents.get(id), up = norm(me().pos)
  const eye = me().pos.map((x, i) => x + up[i] * 1.7)
  const tUp = norm(t.pos)
  return norm(sub(t.pos.map((x, i) => x + tUp[i] * 0.9), eye))
}
const dead = () => c.events.some((e) => e.ev === 4 && e.id === c.id)
let targetId = hostiles()[0]
const fightT0 = Date.now()
let killedId = 0
while (Date.now() - fightT0 < 45000 && !dead()) {
  if (!hostiles().includes(targetId)) {
    if (c.events.some((e) => e.ev === 4 && e.id === targetId)) { killedId = targetId; break }
    targetId = hostiles()[0]
    if (!targetId) break
  }
  const aim = aimAt(targetId)
  c.ws.send(input([0, 0, aim[0], aim[1], aim[2]], 0, c.seq++))
  c.ws.send(fire(c.seq - 1, aim))
  await sleep(200)
}
const myHits = c.events.filter((e) => e.ev === 3 && e.shooter === c.id).length
check('our shots registered', myHits > 0, `${myHits} hits`)
check('a hostile died to us', killedId !== 0, `entity ${killedId}${dead() ? ' (we died too)' : ''}`)
await wait(() => lastXP('marksmanship') > 0, 3000)
check('Marksmanship trained on damage and the kill', lastXP('marksmanship') >= 2 * myHits, `${lastXP('marksmanship')} xp for ${myHits} hits`)

// Walk onto the drop while still shooting whoever is left: the camp keeps
// firing, and standing still on a crate is how t16's player died.
const lootId = await wait(() => [...c.spawns].find(([id, v]) => v.type === 6 && !lootBefore.has(id) && c.ents.get(id))?.[0], 3000)
check('the kill dropped loot', !!lootId, `entity ${lootId}`)
if (lootId && !dead()) {
  const lootPos = c.ents.get(lootId).pos.slice()
  const t0 = Date.now()
  let lastFire = 0
  while (Date.now() - t0 < 30000 && !dead()) {
    if (!c.spawns.has(lootId)) break // taken: the drop despawns on grant
    const d = sub(lootPos, me().pos)
    const close = Math.hypot(...d) <= 1.0
    const up = norm(me().pos), lk = close ? [1, 0, 0] : norm(tangent(d, up))
    c.ws.send(input([0, close ? 0 : 1, lk[0], lk[1], lk[2]], close ? 0 : 0x0001, c.seq++))
    const foe = hostiles()[0]
    if (foe && Date.now() - lastFire >= 200) { c.ws.send(fire(c.seq - 1, aimAt(foe))); lastFire = Date.now() }
    await sleep(50)
  }
}
await wait(() => lastXP('scavenging') > 0, 3500)
check('Scavenging trained on the pickup', lastXP('scavenging') > 0, `${lastXP('scavenging')} xp${dead() ? ' (died in the camp)' : ''}`)

// ---- act 6: the sheet, then the reconnect ------------------------------------

await sleep(1500) // the last flush
const sheet = await sendCmd(OP_SKILLS, {})
check('the sheet answers', sheet?.status === 0)
const xp = sheet?.body?.xp ?? {}
const missing = SEVEN.filter((s) => !(xp[s] > 0))
check('all seven skills moved', missing.length === 0, missing.length ? `missing ${missing.join(', ')}` : SEVEN.map((s) => `${s}=${xp[s]}`).join(' '))
check('the sheet agrees with the last event per skill',
  SEVEN.every((s) => !xpOf(s).length || xp[s] === lastXP(s)))
check('every event carried the curve\'s level and next_at',
  c.xp.every((x) => x.level === levelForXP(x.xp) && x.next_at === pointsForLevel(x.level + 1)),
  `${c.xp.length} events`)
check('the sheet\'s levels match the curve',
  SEVEN.every((s) => (sheet.body.levels?.[s] ?? 1) === levelForXP(xp[s] ?? 0)))
const discovered = sheet?.body?.discovered ?? []
check('the camp is on the discovered list', discovered.includes('camp'), JSON.stringify(discovered))
check('Recon XP is exactly 250 per discovery', xp.recon === discovered.length * 250, `${xp.recon} xp, ${discovered.length} POIs`)

// Batching: consecutive events for one skill are a flush apart.
let minGap = Infinity
for (const s of SEVEN) {
  const ev = xpOf(s)
  for (let i = 1; i < ev.length; i++) minGap = Math.min(minGap, ev[i].at - ev[i - 1].at)
}
check('events batch (>= 0.5 s between a skill\'s events)', minGap >= 500, `min gap ${minGap === Infinity ? 'n/a' : minGap + ' ms'}`)

const eventCount = c.xp.length
c.ws.close()
await sleep(800)
c = connect('trainee', token)
await wait(() => c.id && me())
const again = await sendCmd(OP_SKILLS, {})
check('the sheet survives reconnect', again?.status === 0 && JSON.stringify(again.body.xp) === JSON.stringify(xp)
  && JSON.stringify(again.body.discovered) === JSON.stringify(discovered),
  JSON.stringify(again?.body?.xp))
c.ws.close()

const fails = checks.filter(([, ok]) => !ok)
writeFileSync(new URL('./out/t34-skills-loop.json', import.meta.url), JSON.stringify({
  when: new Date().toISOString(), xp, discovered, events: eventCount, minGapMs: minGap === Infinity ? null : minGap,
  checks: checks.map(([n, ok]) => ({ n, ok })),
}, null, 1))
console.log(fails.length
  ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})`
  : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
