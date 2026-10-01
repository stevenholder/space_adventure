#!/usr/bin/env node
/**
 * Phase 2 acceptance: the loop, end to end, at the wire level.
 *
 * Join -> walk to the shopkeeper -> shop_list -> shop_buy -> equip -> walk to
 * the range -> fire -> hit. Every earlier test covers one layer; this is the
 * only one that plays the game, and it is the one that found the bugs the
 * layered tests could not:
 *
 *   - nothing in the client ever sent `equip`, so a bought rifle could never
 *     be drawn (every module was individually correct);
 *   - the interaction range check targeted an NPC's FEET rather than its eye,
 *     so talking to someone standing in front of you was refused
 *     out_of_range at 2.04 m against a 3.0 m limit;
 *   - PROTOCOL.md documented an `input` mode byte the code does not have,
 *     misaligning every field for anyone who believed the doc.
 *
 * Run: node test/t14-buy-and-shoot.mjs   (needs `make up`)
 */
import { readFileSync } from 'node:fs'
const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
function frame(type, body) { const o = u8(2 + body.length); new DataView(o.buffer).setUint16(0, type, true); o.set(body, 2); return o }
function hello(name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(2 + 4 + n.length + 4 + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
// 24 bytes: f32 move_x | f32 move_y | f32 look_dir[3] | u16 mask | u16 seq.
// PROTOCOL.md documents a leading mode byte that the implementation does not
// have — following the doc misaligns every field by one.
function input(mx, my, look, mask, seq) {
  const b = u8(24), dv = new DataView(b.buffer)
  dv.setFloat32(0, mx, true); dv.setFloat32(4, my, true)
  dv.setFloat32(8, look[0], true); dv.setFloat32(12, look[1], true); dv.setFloat32(16, look[2], true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true)
  return frame(0x0003, b)
}
function cmd(seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(2 + 2 + 4 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
function fire(seq, dir) {
  const b = u8(2 + 12), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true)
  dv.setFloat32(2, dir[0], true); dv.setFloat32(6, dir[1], true); dv.setFloat32(10, dir[2], true)
  return frame(0x0011, b)
}
const norm = (v) => { const l = Math.hypot(...v); return v.map(x => x / l) }
const sub = (a, b) => [a[0]-b[0], a[1]-b[1], a[2]-b[2]]

// A FRESH token per run. Persistence is real now, so a fixed token carries the
// previous run's rifle and credits into this one — the second run then starts
// at 750 and the buy assertion fails against a player who already owns it.
// This test is about a first purchase; it has to start as a new player.
const token = 'e2e-loop-' + Date.now()
const ws = new WebSocket(`ws://127.0.0.1:${process.env.SA_PORT ?? 18080}/ws`); ws.binaryType = 'arraybuffer'
let myId = 0, ents = new Map(), spawns = new Map(), results = [], events = [], defs = null
ws.addEventListener('open', () => ws.send(hello('shopper', token)))
ws.addEventListener('message', (ev) => {
  const dv = new DataView(ev.data), t = dv.getUint16(0, true), p = new Uint8Array(ev.data, 2)
  const pv = new DataView(ev.data, 2)
  if (t === 0x0002) myId = pv.getUint32(8, true)
  else if (t === 0x0010) defs = JSON.parse(dec.decode(p.subarray(4)))
  else if (t === 0x0005) spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10)) })
  else if (t === 0x0004) {
    const n = pv.getUint16(6, true)
    for (let i = 0; i < n; i++) { const o = 8 + i * 54
      ents.set(pv.getUint32(o, true), { pos: [pv.getFloat32(o+4,true), pv.getFloat32(o+8,true), pv.getFloat32(o+12,true)], health: pv.getUint16(o+50,true) }) }
  }
  else if (t === 0x000f) results.push({ seq: pv.getUint16(0,true), op: pv.getUint16(2,true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
  else if (t === 0x0007) events.push({ id: pv.getUint32(0,true), ev: pv.getUint16(4,true), data: Buffer.from(p.slice(6 + 4)).toString('utf8') })
})
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
const wait = async (fn, ms = 4000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }

await wait(() => myId && defs && ents.size > 1)
console.log(`joined id=${myId}, ${spawns.size} spawns, defs ${JSON.stringify(defs).length} B`)
// Select the SHOPKEEPER by name, not "the first NPC". Zones compose in sorted
// id order, so once the camp existed its grunts and gunners took the lowest
// entity ids and this picked a hostile 272 m away — every shop cmd then failed
// with an empty refusal and the loop looked broken rather than mis-targeted.
const npcId = [...spawns].find(([, v]) => v.type === 3 && v.data === 'npc.quartermaster')?.[0]
if (!npcId) { console.log('FAIL: no quartermaster in spawns'); process.exit(1) }
const targetIds = [...spawns].filter(([, v]) => v.type === 4).map(([k]) => k)
console.log(`npc=${npcId} (${spawns.get(npcId)?.data}), targets=${targetIds.length}`)

// Walk to the NPC.
const me = () => ents.get(myId).pos
const npcPos = ents.get(npcId).pos
let seq = 1
for (let i = 0; i < 80; i++) {
  const d = sub(npcPos, me()); const dist = Math.hypot(...d)
  if (dist <= 2.4) break
  const up = norm(me()); const look = norm(sub(d, up.map(x => x * (d[0]*up[0]+d[1]*up[1]+d[2]*up[2]))))
  ws.send(input(0, 1, look, 0, seq++)); await sleep(50)
}
const up = norm(me()); const toNpc = sub(npcPos, me())
const look = norm(sub(toNpc, up.map(x => x * (toNpc[0]*up[0]+toNpc[1]*up[1]+toNpc[2]*up[2]))))
ws.send(input(0, 0, look, 0, seq++)); await sleep(150)
console.log(`walked to ${Math.hypot(...sub(npcPos, me())).toFixed(2)} m from the NPC`)

ws.send(cmd(seq, 0x0001, { npc: npcId })); const list = await wait(() => results.find(r => r.op === 1))
console.log('shop_list ->', list?.status === 0 ? JSON.stringify(list.body.stock) : `REFUSED ${JSON.stringify(list?.body)}`)
ws.send(cmd(++seq, 0x0002, { npc: npcId, item: 'weapon.pulse', qty: 1 })); const buy = await wait(() => results.find(r => r.op === 2))
console.log('shop_buy  ->', buy?.status === 0 ? `OK credits=${buy.body.credits} inv=${JSON.stringify(buy.body.inventory)}` : `REFUSED ${JSON.stringify(buy?.body)}`)
ws.send(cmd(++seq, 0x0003, { slot: 'primary', item: 'weapon.pulse' })); const eq = await wait(() => results.find(r => r.op === 3))
console.log('equip     ->', eq?.status === 0 ? JSON.stringify(eq.body.equipped) : `REFUSED ${JSON.stringify(eq?.body)}`)

// --- wear armor: the chest slot rides the wire as a `worn` event (0x000F) ---
ws.send(cmd(++seq, 0x0002, { npc: npcId, item: 'armor.suit.scout', qty: 1 })); const buyArmor = await wait(() => results.find(r => r.op === 2 && r.seq === seq))
ws.send(cmd(++seq, 0x0003, { slot: 'chest', item: 'armor.suit.scout' })); const eqArmor = await wait(() => results.find(r => r.op === 3 && r.seq === seq))
await sleep(200)
const worn = events.find(e => e.ev === 0x000F && e.id === myId && e.data === 'chest=armor.suit.scout')
console.log('wear      ->', eqArmor?.status === 0 ? JSON.stringify(eqArmor.body.equipped) : `REFUSED ${JSON.stringify(eqArmor?.body)}`, worn ? 'worn event seen' : 'NO worn event')

// --- shoot a target ------------------------------------------------------
const tid = targetIds[0]
const tPos = ents.get(tid).pos
console.log(`
target ${tid} is ${Math.hypot(...sub(tPos, me())).toFixed(1)} m away, health ${ents.get(tid).health}`)

// Walk into range of the range zone, then aim at the target's centre.
for (let i = 0; i < 400; i++) {
  const d = sub(tPos, me()); const dist = Math.hypot(...d)
  if (dist <= 25) break
  const u = norm(me()); const lk = norm(sub(d, u.map(x => x * (d[0]*u[0]+d[1]*u[1]+d[2]*u[2]))))
  ws.send(input(0, 1, lk, 0x0001, seq++)); await sleep(50)
}
const dist = Math.hypot(...sub(tPos, me()))
console.log(`walked to ${dist.toFixed(1)} m from the target`)

const u2 = norm(me())
const eye = me().map((x, i) => x + u2[i] * 1.7)
const tEye = tPos.map((x, i) => x + norm(tPos)[i] * 0.9) // aim mid-body
const aim = norm(sub(tEye, eye))
ws.send(input(0, 0, aim, 0, seq++)); await sleep(120)

const before = ents.get(tid).health
events.length = 0
ws.send(fire(seq, aim))
await sleep(400)
const after = ents.get(tid).health
const shot = events.filter(e => e.ev === 2).length
const hits = events.filter(e => e.ev === 3).length
console.log(`fire -> shot_fired=${shot} hit=${hits} target health ${before} -> ${after}`)

// ASSERT, do not merely report. This script printed shot_fired=0 / no damage
// while still exiting 0, so a regression that stopped firing entirely was
// recorded as a PASS — a test that cannot fail is not a test.
const checks = [
  ['shop_list returned stock', list?.status === 0 && Array.isArray(list.body.stock) && list.body.stock.length > 0],
  ['shop_buy granted the rifle', buy?.status === 0 && buy.body.credits === 750],
  ['equip set primary', eq?.status === 0 && eq.body.equipped?.primary === 'weapon.pulse'],
  ['equip set chest', eqArmor?.status === 0 && eqArmor.body.equipped?.chest === 'armor.suit.scout'],
  ['worn event announced the chest slot', !!worn],
  ['fire produced a shot_fired event', shot > 0],
  ['the shot hit the target', hits > 0],
  ['the target lost health', after < before],
]
let bad = 0
for (const [name, ok] of checks) {
  if (!ok) { console.log(`FAIL ${name}`); bad++ }
}
console.log(bad ? `OVERALL: FAIL (${bad}/${checks.length})` : `OVERALL: PASS (${checks.length} checks)`)
process.exit(bad ? 1 : 0)
