#!/usr/bin/env node
/**
 * t40 — melee, the weapon swap and thrown charges, over the wire
 * (docs/GDD.md "Melee", "Throwables"; PROTOCOL.md `wield`, `explosion`).
 *
 * One fresh player buys a rifle, a cutlass and two frag grenades at the
 * quartermaster and wears the rifle (primary) and the blade (melee).
 *   - `wield melee` / `wield primary` swap the hand: each answers with the
 *     item held and broadcasts it as `equipped`;
 *   - a `fire` with the blade in hand is a SWING: one `attack` event
 *     (target 0) and no `shot_fired`, even for two fires inside the interval;
 *   - `use throw.frag` launches a charge (a projectile spawned with the item
 *     id as its def), which bursts: an `explosion` event naming the item.
 *
 * What a swing or a burst DOES to a body is asserted in Go (TestWieldAndSwing,
 * TestBurstHurtsTheArea, TestNPCDrawsBladeUpClose): the range dummies sit
 * behind walls a scripted walk does not get round.
 *
 * Run: node test/t40-melee.mjs   (needs a live server; SA_PORT picks the port)
 */
const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
function frame (type, body) { const o = u8(2 + body.length); new DataView(o.buffer).setUint16(0, type, true); o.set(body, 2); return o }
function hello (name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(2 + 4 + n.length + 4 + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
function input (mx, my, look, mask, seq) {
  const b = u8(25), dv = new DataView(b.buffer)   // ... | u16 seq | u8 mode (0 = on foot)
  dv.setFloat32(0, mx, true); dv.setFloat32(4, my, true)
  dv.setFloat32(8, look[0], true); dv.setFloat32(12, look[1], true); dv.setFloat32(16, look[2], true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true)
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
const norm = (v) => { const l = Math.hypot(...v); return v.map(x => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const tangent = (d, up) => { const k = dot(d, up); return norm([d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k]) }

const OP = { BUY: 0x0002, EQUIP: 0x0003, USE: 0x0013, WIELD: 0x0015 }
const EV = { EXPLOSION: 0x0001, SHOT: 0x0002, HIT: 0x0003, EQUIPPED: 0x0006, ATTACK: 0x0010 }

const ws = new WebSocket(`ws://127.0.0.1:${process.env.SA_PORT ?? 18080}/ws`); ws.binaryType = 'arraybuffer'
let myId = 0, defs = null
const ents = new Map(), spawns = new Map(), results = [], events = []
ws.addEventListener('open', () => ws.send(hello('blade', 'melee-' + Date.now())))
ws.addEventListener('message', (ev) => {
  const dv = new DataView(ev.data), t = dv.getUint16(0, true), p = new Uint8Array(ev.data, 2)
  const pv = new DataView(ev.data, 2)
  if (t === 0x0002) myId = pv.getUint32(8, true)
  else if (t === 0x0010) defs = JSON.parse(dec.decode(p.subarray(4)))
  else if (t === 0x0005) spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10)) })
  else if (t === 0x0004) {
    const n = pv.getUint16(6, true)
    for (let i = 0; i < n; i++) {
      const o = 8 + i * 54
      ents.set(pv.getUint32(o, true), { pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)], health: pv.getUint16(o + 50, true) })
    }
  } else if (t === 0x000f) results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
  else if (t === 0x0007) {
    const raw = p.slice(10)
    events.push({ id: pv.getUint32(0, true), ev: pv.getUint16(4, true), raw, data: dec.decode(raw) })
  }
})
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
const wait = async (fn, ms = 4000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }

await wait(() => myId && defs && ents.size > 1)
const me = () => ents.get(myId).pos
let seq = 1
async function walkTo (pos, within) {
  for (let i = 0; i < 200; i++) {
    const d = sub(pos, me())
    if (Math.hypot(...d) <= within) break
    ws.send(input(0, 1, tangent(d, norm(me())), 0, seq++)); await sleep(50)
  }
  ws.send(input(0, 0, tangent(sub(pos, me()), norm(me())), 0, seq++)); await sleep(200)
}
async function call (op, body) {
  const s = ++seq
  ws.send(cmd(s, op, body))
  return wait(() => results.find(r => r.seq === s && r.op === op))
}

const npc = [...spawns].find(([, v]) => v.type === 3 && v.data === 'npc.quartermaster')?.[0]
await walkTo(ents.get(npc).pos, 2.4)
for (const item of ['weapon.pulse', 'melee.sword']) await call(OP.BUY, { npc, item, qty: 1 })
const buyFrag = await call(OP.BUY, { npc, item: 'throw.frag', qty: 2 })
await call(OP.EQUIP, { slot: 'primary', item: 'weapon.pulse' })
const eqMelee = await call(OP.EQUIP, { slot: 'melee', item: 'melee.sword' })
console.log('equipped', JSON.stringify(eqMelee?.body?.equipped), 'frag buy', buyFrag?.status)

// --- the swap -------------------------------------------------------------
const heldNow = () => events.filter(e => e.ev === EV.EQUIPPED && e.id === myId).at(-1)?.data
const w1 = await call(OP.WIELD, { slot: 'melee' }); await sleep(150); const h1 = heldNow()
const w2 = await call(OP.WIELD, { slot: 'primary' }); await sleep(150); const h2 = heldNow()
const w3 = await call(OP.WIELD, { slot: 'melee' }); await sleep(150); const h3 = heldNow()
console.log(`wield: melee -> ${w1?.body?.item}/${h1}, primary -> ${w2?.body?.item}/${h2}, melee -> ${w3?.body?.item}/${h3}`)

// --- swing where we stand ---------------------------------------------------
const face = tangent([0, 0, 1], norm(me()))
ws.send(input(0, 0, face, 0, seq++)); await sleep(150)
events.length = 0
ws.send(fire(seq, face)); ws.send(fire(seq, face))      // the second is inside the interval
await sleep(500)
const attacks = events.filter(e => e.ev === EV.ATTACK && e.id === myId)
const shots = events.filter(e => e.ev === EV.SHOT && e.id === myId).length
console.log(`swing: attack events ${attacks.length}, shots ${shots}`)

// --- throw a grenade -----------------------------------------------------------
const lob = norm(face.map((x, i) => x + norm(me())[i] * 0.5))
ws.send(input(0, 0, lob, 0, seq++)); await sleep(150)
events.length = 0
const use = await call(OP.USE, { item: 'throw.frag' })
await wait(() => events.find(e => e.ev === EV.EXPLOSION), 3000)
const flew = [...spawns].some(([, v]) => v.type === 7 && v.data === 'throw.frag')
const boom = events.find(e => e.ev === EV.EXPLOSION && e.id === myId)
const boomItem = boom ? dec.decode(boom.raw.subarray(16)) : ''
console.log(`throw: use ${use?.status} ${JSON.stringify(use?.body)}, projectile spawned ${flew}, explosion ${boomItem || 'none'}`)

const checks = [
  ['wield melee holds the cutlass', w1?.status === 0 && w1.body.item === 'melee.sword' && h1 === 'melee.sword'],
  ['wield primary holds the rifle', w2?.status === 0 && w2.body.item === 'weapon.pulse' && h2 === 'weapon.pulse'],
  ['a swing is an attack event with no target', attacks.length === 1 && new DataView(attacks[0].raw.buffer, attacks[0].raw.byteOffset).getUint32(0, true) === 0],
  ['a swing fires no shot', shots === 0],
  ['use throws the grenade', use?.status === 0 && use.body.effect?.thrown === true],
  ['the charge flew as a projectile named for its item', flew],
  ['it burst with an explosion event', boomItem === 'throw.frag'],
]
let bad = 0
for (const [name, ok] of checks) if (!ok) { console.log(`FAIL ${name}`); bad++ }
console.log(bad ? `OVERALL: FAIL (${bad}/${checks.length})` : `OVERALL: PASS (${checks.length} checks)`)
ws.close()
process.exit(bad ? 1 : 0)
