#!/usr/bin/env node
/**
 * t36 — C120–C126: the artisan loop, over the wire.
 *
 * One fresh player at the pad: buys a drill, is refused at the ore for
 * lack of a worn tool, wears it, channels — the yield lands when the
 * server said it would, never before — is cut off by stepping away, drills
 * the node dark, sells the ore back (Commerce moves, ammo is unsellable),
 * walks the 107 m to the relay bench and is refused for level and for
 * materials with nothing consumed, then reconnects and finds Mining where
 * it left it. Copper refuses for the tool it lacks.
 *
 * Crafting a thing and the death spill are asserted in Go (TestCraft,
 * TestDeathSpillsMaterials, TestLootExpires): scrap lies inside the
 * guarded outpost 258 m away, and a scripted death there is not a test.
 *
 * Run: node test/t36-artisan.mjs   (needs a live server; SA_SERVER_URL
 * points it elsewhere than the kind stack on :18080)
 */
import { writeFileSync } from 'node:fs'

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

const OP = { BUY: 0x0002, EQUIP: 0x0003, INV: 0x0004, SKILLS: 0x000e, SELL: 0x000f, GATHER: 0x0010, CANCEL: 0x0011, CRAFT: 0x0012 }
const EV_SKILL_XP = 0x000d, EV_GATHER_END = 0x000e, TYPE_NODE = 8, TYPE_NPC = 3

function connect (name, token) {
  const ws = new WebSocket(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws')
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), results: [], events: [], xp: [], ends: [], seq: 1 }
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
      if (e.ev === EV_SKILL_XP) c.xp.push({ ...JSON.parse(dec.decode(p.subarray(10))), at: e.at })
      if (e.ev === EV_GATHER_END) c.ends.push({ ...JSON.parse(dec.decode(p.subarray(10))), at: e.at })
      c.events.push(e)
    } else if (t === 0x000f) {
      c.results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    }
  })
  return c
}

const wait = async (fn, ms = 6000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const token = 'artisan-' + Date.now()
let c = connect('artisan', token)
const me = () => c.ents.get(c.id)
const sendCmd = async (op, body) => { c.ws.send(cmd(c.seq++, op, body)); const n = c.results.length; return wait(() => c.results.slice(n).find((r) => r.op === op)) }
const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok]); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }
const lastXP = (skill) => c.xp.filter((x) => x.skill === skill).at(-1)?.xp ?? 0
const count = (inv, item) => (inv ?? []).filter((s) => s.item === item).reduce((n, s) => n + s.qty, 0)

/** Sprint toward a point along the surface, jumping when wedged (t34's walker). */
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
/**
 * Stand still and look at the target's aim point — a person's eye (1.7 m),
 * the bench slab (0.9) or a node's middle (0.6), the same points the
 * server's cone is measured against (cmd.go aimHeight).
 */
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
const NODE_AIM = 0.6, BENCH_AIM = 0.9

// ---- act 0: the world ---------------------------------------------------------
await wait(() => c.id && me() && c.spawns.size > 3, 10000)
await sleep(500)
const byDef = (def) => [...c.spawns].filter(([, v]) => v.def === def).map(([id]) => id)
const nodes = (def) => byDef(def).filter((id) => c.ents.has(id)).sort((a, b) => dist(c.ents.get(a).pos, me().pos) - dist(c.ents.get(b).pos, me().pos))
const iron = nodes('node.ore.iron'), copper = nodes('node.ore.copper'), wrecks = byDef('node.wreck')
check('C120 nodes spawn as type 8', iron.length === 3 && copper.length === 1 && wrecks.length === 2 && [...iron, ...copper].every((id) => c.spawns.get(id).type === TYPE_NODE), `iron ${iron.length} copper ${copper.length} wreck ${wrecks.length}`)
check('C120 iron health = yields (5)', iron.every((id) => c.ents.get(id).health === 5), iron.map((id) => c.ents.get(id).health).join(','))
const qm = byDef('npc.quartermaster')[0], bench = byDef('npc.workbench')[0]
check('the bench stands in the world', !!bench && c.spawns.get(bench).type === TYPE_NPC)

// ---- act 1: the shop, then the ore without a tool --------------------------------
await approach(qm, 2.2)
const buy = await sendCmd(OP.BUY, { npc: qm, item: 'tool.drill', qty: 1 })
check('buy a drill (120 cr)', buy?.status === 0 && buy.body.credits === 880, JSON.stringify(buy?.body?.credits))
const node = iron[0]
await approach(node, 2.0, NODE_AIM)
const bare = await sendCmd(OP.GATHER, { node })
check('C121 no tool worn refuses no_tool', bare?.status === 3 && bare.body.reason === 'no_tool', JSON.stringify(bare?.body))
const eq = await sendCmd(OP.EQUIP, { slot: 'tool', item: 'tool.drill' })
check('the drill goes in TOOL', eq?.status === 0 && eq.body.equipped?.tool === 'tool.drill')

// ---- act 2: the channel ---------------------------------------------------------
await faceAt(c.ents.get(node).pos, NODE_AIM)
let endsBefore = c.ends.length
const t0 = Date.now()
const g = await sendCmd(OP.GATHER, { node })
check('C121 gather answers a duration', g?.status === 0 && g.body.duration === 3, JSON.stringify(g?.body))
const busy = await sendCmd(OP.GATHER, { node })
check('C121 a second gather is busy', busy?.status === 3 && busy.body.reason === 'busy', JSON.stringify(busy?.body))
const end = await wait(() => c.ends.length > endsBefore ? c.ends.at(-1) : null, 6000)
const elapsed = end ? end.at - t0 : -1
check('C121 the yield lands when promised (3.0 s ± 0.25)', end?.reason === 'done' && elapsed >= 2750 && elapsed <= 3400, `${elapsed} ms → ${JSON.stringify(end)}`)
check('C121 two iron ore came out', end?.item === 'mat.ore.iron' && end?.qty === 2)
await wait(() => lastXP('mining') > 0, 2500)
check('C125 Mining paid 25', lastXP('mining') === 25, `${lastXP('mining')} xp`)
const inv1 = await sendCmd(OP.INV, {})
check('the ore is in the bag', count(inv1?.body?.inventory, 'mat.ore.iron') === 2)
check('C120 the node lost one yield', c.ents.get(node).health === 4, `${c.ents.get(node).health}`)

// move-cancel: start, step away, expect `moved`
endsBefore = c.ends.length
const g2 = await sendCmd(OP.GATHER, { node })
check('a channel restarts', g2?.status === 0)
const up = norm(me().pos), away = norm(tangent(sub(me().pos, c.ents.get(node).pos), up))
for (let i = 0; i < 10; i++) { c.ws.send(input([0, 1, away[0], away[1], away[2]], 0x0001, c.seq++)); await sleep(50) }
const moved = await wait(() => c.ends.length > endsBefore ? c.ends.at(-1) : null, 3000)
check('C121 stepping away ends it `moved`, nothing granted', moved?.reason === 'moved' && moved.item === undefined, JSON.stringify(moved))
check('the interrupted node kept its yield', c.ents.get(node).health === 4, `${c.ents.get(node).health}`)

// verb cancel
await approach(node, 2.0, NODE_AIM)
endsBefore = c.ends.length
await sendCmd(OP.GATHER, { node })
const cx = await sendCmd(OP.CANCEL, {})
const cancelled = await wait(() => c.ends.length > endsBefore ? c.ends.at(-1) : null, 2000)
check('gather_cancel ends it `cancel`', cx?.status === 0 && cancelled?.reason === 'cancel', JSON.stringify(cancelled))
const notG = await sendCmd(OP.CANCEL, {})
check('cancel with nothing running is not_gathering', notG?.status === 3 && notG.body.reason === 'not_gathering')

// drill it dark: four more yields
for (let i = 0; i < 4; i++) {
  await faceAt(c.ents.get(node).pos, NODE_AIM)
  endsBefore = c.ends.length
  const r = await sendCmd(OP.GATHER, { node })
  if (r?.status !== 0) { check(`yield ${i + 2} started`, false, JSON.stringify(r?.body)); break }
  await wait(() => c.ends.length > endsBefore, 6000)
}
await wait(() => c.ents.get(node).health === 0, 2000)
check('C120 five yields dark the node (health 0)', c.ents.get(node).health === 0, `${c.ents.get(node).health}`)
const dep = await sendCmd(OP.GATHER, { node })
check('C121 a dark node refuses depleted', dep?.status === 3 && dep.body.reason === 'depleted', JSON.stringify(dep?.body))
const inv2 = await sendCmd(OP.INV, {})
check('ten ore in the bag', count(inv2?.body?.inventory, 'mat.ore.iron') === 10, `${count(inv2?.body?.inventory, 'mat.ore.iron')}`)
await wait(() => lastXP('mining') === 125, 2500) // awards flush at most once a second per skill
check('C125 Mining paid per yield (125)', lastXP('mining') === 125, `${lastXP('mining')} xp`)

// copper: the tool check comes before the level check
await approach(copper[0], 2.0, NODE_AIM)
const cu = await sendCmd(OP.GATHER, { node: copper[0] })
check('C121 copper refuses the hand drill (no_tool)', cu?.status === 3 && cu.body.reason === 'no_tool', JSON.stringify(cu?.body))

// ---- act 3: sell ---------------------------------------------------------------
await approach(qm, 2.2)
const comBefore = lastXP('commerce')
const sell = await sendCmd(OP.SELL, { npc: qm, item: 'mat.ore.iron', qty: 4 })
check('C122 four iron → 12 cr at sell_rate 0.5', sell?.status === 0 && sell.body.credits === 892 && count(sell.body.inventory, 'mat.ore.iron') === 6, JSON.stringify(sell?.body?.credits))
await wait(() => lastXP('commerce') > comBefore, 2500)
check('C122 Commerce moved on the sale', lastXP('commerce') > comBefore, `${comBefore} → ${lastXP('commerce')}`)
const ammo = await sendCmd(OP.SELL, { npc: qm, item: 'ammo.cell', qty: 1 })
check('C122 ammo is unsellable', ammo?.status === 3 && ammo.body.reason === 'unsellable', JSON.stringify(ammo?.body))
const worn = await sendCmd(OP.SELL, { npc: qm, item: 'tool.drill', qty: 1 })
check('C122 the worn drill refuses equipped', worn?.status === 3 && worn.body.reason === 'equipped', JSON.stringify(worn?.body))
// buyback (Phase 13): the sale is on the list at what the shop paid, and comes back whole
const listed = await sendCmd(0x0001, { npc: qm })
check('C138 shop_list carries the sale as buyback', listed?.status === 0 && listed.body.buyback?.length === 1 && listed.body.buyback[0].item === 'mat.ore.iron' && listed.body.buyback[0].qty === 4 && listed.body.buyback[0].price === 12, JSON.stringify(listed?.body?.buyback))
const back = await sendCmd(0x0014, { npc: qm, item: 'mat.ore.iron' })
check('C138 buyback returns the stack for the same 12 cr', back?.status === 0 && back.body.credits === 880 && count(back.body.inventory, 'mat.ore.iron') === 10, JSON.stringify(back?.body?.credits))
const none = await sendCmd(0x0014, { npc: qm, item: 'mat.ore.iron' })
check('C138 a second buyback refuses no_buyback', none?.status === 3 && none.body.reason === 'no_buyback', JSON.stringify(none?.body))
const resell = await sendCmd(OP.SELL, { npc: qm, item: 'mat.ore.iron', qty: 4 })
check('sold again for the bench act', resell?.status === 0 && resell.body.credits === 892)

// ---- act 4: the bench at the relay ------------------------------------------------
const walked = await approach(bench, 2.2, BENCH_AIM)
check('reached the workbench (107 m)', walked <= 2.5, `${walked.toFixed(1)} m`)
const far = c.results.length
const plate = await sendCmd(OP.CRAFT, { npc: bench, recipe: 'recipe.plate.iron', qty: 1 })
check('C123 the plate is locked below Engineering 5', plate?.status === 3 && plate.body.reason === 'locked', JSON.stringify(plate?.body))
const cells = await sendCmd(OP.CRAFT, { npc: bench, recipe: 'recipe.cells', qty: 1 })
check('C123 cells without scrap refuse missing_materials', cells?.status === 3 && cells.body.reason === 'missing_materials', JSON.stringify(cells?.body))
const inv3 = await sendCmd(OP.INV, {})
check('C123 a refused craft consumed nothing', count(inv3?.body?.inventory, 'mat.ore.iron') === 6)
const atShop = await sendCmd(OP.CRAFT, { npc: qm, recipe: 'recipe.cells', qty: 1 })
check('the quartermaster is no bench', atShop?.status === 3 && ['out_of_range', 'unknown_recipe'].includes(atShop.body.reason), JSON.stringify(atShop?.body))

// ---- act 5: reconnect ------------------------------------------------------------
const sheet = await sendCmd(OP.SKILLS, {})
c.ws.close()
await sleep(400)
c = connect('artisan', token)
await wait(() => c.id && me(), 8000)
const again = await sendCmd(OP.SKILLS, {})
check('C125 Mining survives reconnect', again?.status === 0 && again.body.xp?.mining === 125 && JSON.stringify(again.body.xp) === JSON.stringify(sheet?.body?.xp), JSON.stringify(again?.body?.xp))
const inv4 = await sendCmd(OP.INV, {})
check('the ore survives reconnect', count(inv4?.body?.inventory, 'mat.ore.iron') === 6 && inv4.body.equipped?.tool === 'tool.drill')
c.ws.close()

const fails = checks.filter(([, ok]) => !ok)
writeFileSync(new URL('./out/t36-artisan.json', import.meta.url), JSON.stringify({
  when: new Date().toISOString(), elapsedMs: elapsed, xp: again?.body?.xp, checks: checks.map(([n, ok]) => ({ n, ok })),
}, null, 1))
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
