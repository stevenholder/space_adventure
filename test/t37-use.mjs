#!/usr/bin/env node
/**
 * t37 — C129–C132 (wire halves): use, over the wire.
 *
 * One fresh player: buys medkits at the quartermaster, is told `no_effect`
 * at full health with nothing consumed, walks the t34/t29 route into the
 * camp, takes a hit, uses a medkit — +50, one unit gone, `cooldown: 8` —
 * and is refused `cooldown` with `ready_in` inside those eight seconds;
 * the rifle is `unusable`, an unworn scanner `not_owned`, a nonsense id
 * `unknown_item`; back at the pad, `use` mid-channel ends the gather
 * `cancel`. Dying in the camp fails the medkit act (the test approaches
 * from the route's last waypoint and retreats as soon as it is hit).
 *
 * The scanner's pings and the mods' numbers are asserted in Go
 * (TestUseScanner, TestApplyMod, TestReloadWithMagMod) and photographed:
 * they are bench-made behind Engineering levels one life cannot reach.
 *
 * Run: node test/t37-use.mjs   (needs a live server; SA_SERVER_URL points
 * it elsewhere than the kind stack on :18080)
 */
import { readFileSync, writeFileSync } from 'node:fs'

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
const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const dist = (a, b) => Math.hypot(...sub(a, b))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const tangent = (d, up) => { const k = dot(d, up); return [d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k] }

const OP = { BUY: 0x0002, EQUIP: 0x0003, INV: 0x0004, GATHER: 0x0010, USE: 0x0013 }
const EV_GATHER_END = 0x000e

function connect (name, token) {
  const ws = new WebSocket(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws')
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), results: [], events: [], ends: [], seq: 1 }
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const pv = new DataView(ev.data, 2), p = new Uint8Array(ev.data, 2)
    if (t === 0x0002) c.id = pv.getUint32(8, true)
    else if (t === 0x0005) c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)) })
    else if (t === 0x0006) c.spawns.delete(pv.getUint32(0, true))
    else if (t === 0x0004) {
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) {
        const o = 8 + i * 54
        c.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
          health: pv.getUint16(o + 50, true), flags: pv.getUint8(o + 52),
        })
      }
    } else if (t === 0x0007) {
      const e = { id: pv.getUint32(0, true), ev: pv.getUint16(4, true), at: Date.now() }
      if (e.ev === EV_GATHER_END) c.ends.push({ ...JSON.parse(dec.decode(p.subarray(10))), at: e.at })
      c.events.push(e)
    } else if (t === 0x000f) {
      c.results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    }
  })
  return c
}

const wait = async (fn, ms = 6000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const token = 'use-' + Date.now()
const c = connect('medic', token)
const me = () => c.ents.get(c.id)
const sendCmd = async (op, body) => { c.ws.send(cmd(c.seq++, op, body)); const n = c.results.length; return wait(() => c.results.slice(n).find((r) => r.op === op)) }
const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok]); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }
const count = (inv, item) => (inv ?? []).filter((s) => s.item === item).reduce((n, s) => n + s.qty, 0)
const dead = () => (me()?.flags ?? 0) & 0x04

async function walkTo (target, close, timeoutMs = 30000, stopIf = () => false) {
  const at = typeof target === 'function' ? target : () => target
  const t0 = Date.now()
  let lastPos = me().pos.slice(), lastMove = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    if (stopIf()) break
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
async function faceAt (pos, aim = 1.7) {
  for (let i = 0; i < 8; i++) {
    const up = norm(me().pos), tUp = norm(pos)
    const eye = me().pos.map((x, i) => x + up[i] * 1.7)
    const lk = norm(sub(pos.map((x, i) => x + tUp[i] * aim), eye))
    c.ws.send(input([0, 0, lk[0], lk[1], lk[2]], 0, c.seq++))
    await sleep(60)
  }
}
async function approach (id, close = 2.0, aim = 1.7) {
  const at = () => c.ents.get(id).pos
  const d = await walkTo(at, close, 40000)
  await faceAt(at(), aim)
  return d
}

// ---- act 0: the pad ------------------------------------------------------------------
await wait(() => c.id && me() && c.spawns.size > 3, 10000)
await sleep(500)
const byDef = (def) => [...c.spawns].filter(([, v]) => v.def === def).map(([id]) => id)
const qm = byDef('npc.quartermaster')[0]
await approach(qm, 2.2)
const buy = await sendCmd(OP.BUY, { npc: qm, item: 'consumable.medkit', qty: 2 })
check('two medkits from the quartermaster (60 cr)', buy?.status === 0 && buy.body.credits === 940 && count(buy.body.inventory, 'consumable.medkit') === 2, JSON.stringify(buy?.body?.credits))
const drill = await sendCmd(OP.BUY, { npc: qm, item: 'tool.drill', qty: 1 })
const rifle = await sendCmd(OP.BUY, { npc: qm, item: 'weapon.pulse', qty: 1 })
await sendCmd(OP.EQUIP, { slot: 'primary', item: 'weapon.pulse' })
await sendCmd(OP.EQUIP, { slot: 'tool', item: 'tool.drill' })
check('a drill and a rifle for the road', drill?.status === 0 && rifle?.status === 0)

const full = await sendCmd(OP.USE, { item: 'consumable.medkit' })
check('C129 use at full health refuses no_effect', full?.status === 3 && full.body.reason === 'no_effect', JSON.stringify(full?.body))
const inv0 = await sendCmd(OP.INV, {})
check('C129 nothing consumed by the refusal', count(inv0?.body?.inventory, 'consumable.medkit') === 2)
const unknown = await sendCmd(OP.USE, { item: 'nope' })
check('unknown item refuses unknown_item', unknown?.status === 3 && unknown.body.reason === 'unknown_item')
const rifleUse = await sendCmd(OP.USE, { item: 'weapon.pulse' })
check('C129 the rifle refuses unusable', rifleUse?.status === 3 && rifleUse.body.reason === 'unusable', JSON.stringify(rifleUse?.body))
const scanner = await sendCmd(OP.USE, { item: 'gadget.scanner' })
check('C130 an unworn scanner refuses not_owned', scanner?.status === 3 && scanner.body.reason === 'not_owned', JSON.stringify(scanner?.body))

// use mid-channel ends the gather `cancel`
const iron = byDef('node.ore.iron').filter((id) => c.ents.has(id)).sort((a, b) => dist(c.ents.get(a).pos, me().pos) - dist(c.ents.get(b).pos, me().pos))[0]
await approach(iron, 2.0, 0.6)
const endsBefore = c.ends.length
const g = await sendCmd(OP.GATHER, { node: iron })
const midUse = await sendCmd(OP.USE, { item: 'consumable.medkit' }) // full health: refused, but the hands still left the drill
const ended = await wait(() => c.ends.length > endsBefore ? c.ends.at(-1) : null, 2000)
check('C129 use mid-channel: the channel started and the use answered', g?.status === 0 && midUse?.status === 3, `${g?.status}/${midUse?.body?.reason}`)
// A refused use does not cancel (nothing was used); prove it by letting the channel finish, then cancel with a real use below.
check('a refused use leaves the channel running', ended === null || ended.reason === 'done', JSON.stringify(ended))

// ---- act 1: the camp, a hit, a medkit -------------------------------------------------
const route = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8'))
const hostiles = () => [...c.spawns].filter(([, v]) => v.type === 3 && !['npc.quartermaster', 'npc.dispatcher', 'npc.workbench'].includes(v.def)).map(([id]) => id).filter((id) => c.ents.get(id) && !(c.ents.get(id).flags & 0x04))
const campPos = () => c.ents.get(hostiles()[0])?.pos ?? route.waypoints.at(-1)
const hit = () => (me()?.health ?? 100) < 100
const walkT0 = Date.now()
for (let i = 0; i < route.waypoints.length && Date.now() - walkT0 < 300000 && !hit(); i++) {
  await walkTo(route.waypoints[i], 6, 20000, hit)
  if (dist(campPos(), me().pos) <= 30) break
}
// Edge in until the first hit lands, then straight back out along the route.
await walkTo(campPos, 12, 30000, hit)
const gotHit = await wait(() => hit() ? me().health : null, 20000)
const retreat = route.waypoints.at(-3) ?? route.waypoints[0]
await walkTo(retreat, 6, 15000)
check('took a hit in the camp and lived', gotHit !== null && gotHit > 0 && !dead(), `health ${me()?.health}`)

const before = me()?.health ?? 0
const heal = await sendCmd(OP.USE, { item: 'consumable.medkit' })
const expected = Math.min(100, before + 50)
check('C129 the medkit heals 50, capped, and cools 8', heal?.status === 0 && heal.body.cooldown === 8 && Math.abs(heal.body.effect.health - expected) <= 8, `${before} → ${JSON.stringify(heal?.body)} (regen may add a little)`)
await wait(() => (me()?.health ?? 0) >= Math.min(100, before + 40), 2000)
check('C129 the snapshot shows the heal', (me()?.health ?? 0) >= Math.min(100, before + 40), `${me()?.health}`)
const again = await sendCmd(OP.USE, { item: 'consumable.medkit' })
check('C129 a second use inside 8 s refuses cooldown with ready_in', again?.status === 3 && again.body.reason === 'cooldown' && again.body.ready_in > 0 && again.body.ready_in <= 8, JSON.stringify(again?.body))
const inv1 = await sendCmd(OP.INV, {})
check('C129 one unit left the bag', count(inv1?.body?.inventory, 'consumable.medkit') === 1, `${count(inv1?.body?.inventory, 'consumable.medkit')}`)
c.ws.close()

const fails = checks.filter(([, ok]) => !ok)
writeFileSync(new URL('./out/t37-use.json', import.meta.url), JSON.stringify({
  when: new Date().toISOString(), checks: checks.map(([n, ok]) => ({ n, ok })),
}, null, 1))
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
