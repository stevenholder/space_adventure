#!/usr/bin/env node
/**
 * Phase 5 acceptance, live at the wire level: C33, C35–C38.
 * (C34 is t25's cross-sim diff; C39 latency is t27 with the proxy.)
 *
 * Buy the ship (fresh token, quartermaster, 600 cr of the 1000 start),
 * watch it spawn on the pad, board the pilot seat, take a passenger,
 * then REPLAY THE COMMITTED C34 FLIGHT SCRIPT as live mode-1 input —
 * the same climb / space / boost / return / landing arc t25 proves both
 * sims agree on. Along the way: no position discontinuities, the space
 * flag rises and falls without flapping (C35), the ship never dips below
 * the independent field sampler's surface, and it ends grounded and
 * still (C37). The passenger rides composed, produces no ship input, and
 * disembarks on the ground after landing (C38). Reconnecting on the
 * buyer's token does not duplicate the ship (C33's persistence half).
 *
 * Run: node test/t26-flight.mjs   (needs `make up` / `make check-server`)
 */
import { readFileSync } from 'node:fs'
import { SEAT } from './lib/wire.mjs'
import { loadField, sampleRadius } from './lib/field.mjs'

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
function input(v, mask, seq, mode) {
  const b = u8(25), dv = new DataView(b.buffer)
  for (let i = 0; i < 5; i++) dv.setFloat32(i * 4, v[i] ?? 0, true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true); dv.setUint8(24, mode)
  return frame(0x0003, b)
}
function cmd(seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(8 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
function board(id, seat) { const b = u8(6), dv = new DataView(b.buffer); dv.setUint32(0, id, true); dv.setUint16(4, seat, true); return frame(0x000b, b) }
const disembark = () => frame(0x000c, u8(0))
const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const dist = (a, b) => Math.hypot(...sub(a, b))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const FLAG_GROUNDED = 0x01, FLAG_SPACE = 0x10

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}

function connect(name, token) {
  const ws = new WebSocket(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws')
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), seats: [], results: [], seq: 1, cmdSeq: 1 }
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true), pv = new DataView(ev.data, 2)
    if (t === 0x0002) c.id = pv.getUint32(8, true)
    else if (t === 0x0005) {
      const p = new Uint8Array(ev.data, 2)
      c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10)) })
    }
    else if (t === 0x0006) c.spawns.delete(pv.getUint32(0, true))
    else if (t === 0x000d) c.seats.push({ entityId: pv.getUint32(0, true), seat: pv.getUint16(4, true), result: pv.getUint8(6) })
    else if (t === 0x000f) c.results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4) })
    else if (t === 0x0004) {
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) {
        const o = 8 + i * 54
        c.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
          vel: [pv.getFloat32(o + 32, true), pv.getFloat32(o + 36, true), pv.getFloat32(o + 40, true)],
          parent: pv.getUint32(o + 44, true),
          seat: pv.getUint16(o + 48, true),
          flags: pv.getUint8(o + 52),
        })
      }
    }
  })
  return c
}
const wait = async (fn, ms = 5000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const nextSeat = async (c) => { const n = c.seats.length; return wait(() => c.seats.length > n ? c.seats[c.seats.length - 1] : null) }

async function walkTo(c, targetOf, stopAt) {
  for (let i = 0; i < 500; i++) {
    const me = c.ents.get(c.id).pos
    const target = targetOf()
    const d = sub(target, me)
    if (Math.hypot(...d) <= stopAt) break
    const up = norm(me)
    const lk = norm(sub(d, up.map((x) => x * (d[0] * up[0] + d[1] * up[1] + d[2] * up[2]))))
    c.ws.send(input([0, 1, lk[0], lk[1], lk[2]], 0x0001, c.seq++, 0))
    await sleep(50)
  }
  c.ws.send(input([0, 0, 0, 0, 1], 0, c.seq++, 0))
  await sleep(100)
}

async function aimAt(c, target) {
  const me = c.ents.get(c.id).pos
  const lk = norm(sub(target, me))
  c.ws.send(input([0, 0, lk[0], lk[1], lk[2]], 0, c.seq++, 0))
  await sleep(150)
}

const field = loadField(JSON.parse(readFileSync(new URL('./out/world-seed1337.json', import.meta.url), 'utf8')))
const script = readFileSync(new URL('./t25-flight-script.jsonl', import.meta.url), 'utf8')
  .split('\n').filter((l) => l.trim()).map((l) => JSON.parse(l).input)

const token = `t26-owner-${Date.now()}`
const a = connect('pilot', token)
await wait(() => a.id && a.ents.size > 1)

// --- C33: buy -> pad -> pilot seat ------------------------------------------
// Ships from earlier runs persist for the server's lifetime (GDD: never
// destroyed), so the assertion is "MY purchase adds one", not "the world
// is empty".
const preShips = new Set([...a.spawns].filter(([, v]) => v.type === 2).map(([id]) => id))

// The quartermaster, by spawn label (t14's lesson: never "the first NPC" —
// zone composition once put camp hostiles on the lowest ids).
const npcId = [...a.spawns].find(([, v]) => v.type === 3 && v.data === 'npc.quartermaster')?.[0] ?? 0
check('found the quartermaster', npcId !== 0, `npc ${npcId}`)
await walkTo(a, () => a.ents.get(npcId).pos, 2.4)
await aimAt(a, a.ents.get(npcId).pos)

a.ws.send(cmd(a.cmdSeq, 0x0002, { npc: npcId, item: 'ship.v1', qty: 1 }))
const buySeq = a.cmdSeq++
const buy = await wait(() => a.results.find((x) => x.seq === buySeq))
check('C33 ship purchased', buy?.status === 0, `status ${buy?.status}`)

const shipId = await wait(() => [...a.spawns].find(([id, v]) => v.type === 2 && !preShips.has(id))?.[0], 3000)
check('C33 ship spawned on the pad after purchase', !!shipId, `id ${shipId}`)
if (!shipId) { console.log('OVERALL: FAIL'); process.exit(1) }
const ship = () => a.ents.get(shipId)
await wait(() => ship())

await walkTo(a, () => ship().pos, 5)
a.ws.send(board(shipId, 1))
let r = await nextSeat(a)
check('C33 pilot seat granted', r?.result === SEAT.GRANTED, `result ${r?.result}`)

// Passenger joins and takes a bench seat (requests 1 -> occupied -> 2).
const b = connect('rider', `t26-rider-${Date.now()}`)
await wait(() => b.id && b.ents.size > 1)
await walkTo(b, () => b.ents.get(shipId).pos, 5)
b.ws.send(board(shipId, 1))
r = await nextSeat(b)
check('C38 pilot seat refused occupied', r?.result === SEAT.OCCUPIED)
b.ws.send(board(shipId, 2))
r = await nextSeat(b)
check('C38 passenger seat granted', r?.result === SEAT.GRANTED)

// --- the flight: replay the C34 script live ---------------------------------
let prevPos = null
let maxStep = 0, minClearance = Infinity, spaceTransitions = 0, sawSpace = false
let lastSpace = false
let passengerWorst = 0

const passengerSpam = setInterval(() => {
  // C38/C31: the passenger hammers movement the whole flight.
  b.ws.send(input([1, 1, 0, 0, 1], 0x0003, b.seq++, 0))
}, 100)

for (let i = 0; i < script.length; i++) {
  const s = script[i]
  a.ws.send(input([s.thrust, s.roll, s.yaw_rate, s.pitch_rate, 0], s.boost ? 0x0004 : 0, a.seq++, 1))
  await sleep(50)
  const row = ship()
  if (!row) continue
  if (prevPos) maxStep = Math.max(maxStep, dist(row.pos, prevPos))
  prevPos = row.pos.slice()
  const up = norm(row.pos)
  minClearance = Math.min(minClearance, Math.hypot(...row.pos) - sampleRadius(field, up))
  const inSpace = (row.flags & FLAG_SPACE) !== 0
  if (inSpace) sawSpace = true
  if (inSpace !== lastSpace) { spaceTransitions++; lastSpace = inSpace }
  // Same client's map for both rows: one snapshot, one instant. Mixing
  // a's ship row with b's body row measures snapshot skew (4 m/tick at
  // vmax_boost), not composition.
  const pRow = b.ents.get(b.id)
  const bShip = b.ents.get(shipId)
  if (pRow && bShip && pRow.parent === shipId) passengerWorst = Math.max(passengerWorst, dist(pRow.pos, bShip.pos))
}
clearInterval(passengerSpam)

check('C35/C36 the live flight reached space', sawSpace)
check('C35 the space flag never flapped', spaceTransitions <= 2, `${spaceTransitions} transitions`)
// One snapshot step at vmax_boost is 80 * 0.05 = 4 m; jitter allows a
// missed tick, so the discontinuity bar is two steps.
check('C35 no position discontinuity', maxStep <= 8.5, `max inter-snapshot step ${maxStep.toFixed(2)} m`)
check('C36/C37 never below the surface', minClearance > -1e-2, `min clearance ${minClearance.toExponential(2)} m`)

const landed = await wait(() => {
  const row = ship()
  return row && (row.flags & FLAG_GROUNDED) !== 0 && Math.hypot(...row.vel) < 0.01 ? row : null
}, 8000)
check('C37 landed, grounded and still', !!landed,
  landed ? `radius ${Math.hypot(...landed.pos).toFixed(1)} m` : 'never settled')

check('C38 passenger stayed composed through the flight', passengerWorst < 3,
  `worst seat offset ${passengerWorst.toFixed(2)} m`)

// C38: passenger disembarks after landing, onto the ground.
b.ws.send(disembark())
r = await nextSeat(b)
check('C38 passenger disembark granted', r?.result === SEAT.GRANTED)
const off = await wait(() => { const e = b.ents.get(b.id); return e && e.parent === 0 ? e : null }, 1000)
if (off) {
  const up = norm(off.pos)
  check('C38 passenger lands on the terrain',
    Math.abs(Math.hypot(...off.pos) - sampleRadius(field, up)) < 0.5)
} else check('C38 passenger lands on the terrain', false, 'never unseated')

// --- C33 persistence: reconnect on the buyer's token, no duplicate ----------
a.ws.close()
await sleep(300)
const a2 = connect('pilot', token)
await wait(() => a2.id && a2.ents.size > 1)
await sleep(500)
const shipsNow = [...a2.spawns.values()].filter((v) => v.type === 2).length
const shipsBefore = preShips.size + 1 // the world's carry-overs plus mine
check('C33 ship persists across reconnect without duplicating',
  shipsNow === shipsBefore && a2.spawns.has(shipId),
  `${shipsNow} ships (want ${shipsBefore}), mine ${a2.spawns.has(shipId) ? 'present' : 'GONE'}`)

a2.ws.close(); b.ws.close()
const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([nm]) => nm).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
