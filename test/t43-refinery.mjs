#!/usr/bin/env node
/**
 * t43 — C183, C185–C187: the refinery, over the wire (Phase 22).
 *
 * ONE bare server with NO SA_START: a guest that starts with 0 cr and an
 * empty bag walks the road from nothing as far as a bot can in budget —
 * scrap by hand at the pad wreck, parts in the hands, the crude drill,
 * iron, ingots at the pad forge until Smithing 3, a steel plate, then the
 * 107 m to the relay bench. On the way it proves the channel's rules:
 * ×3 hand gathering, one duration per unit, craft_end per unit, a
 * gather_cancel and a step that hand the inputs back, a 2-unit craft that
 * runs dry, XP in the recipe's skill, wrong_station everywhere it applies.
 *
 * Not reached, and why: the sidearm wants wiring (copper ingot: Mining 10
 * with a mk2 drill, Smithing 10) and the Scout suit leather (hide from
 * wildlife, a weapon) and Engineering 5 — hours of play, not a harness.
 * The bench is proven to take its own recipe up to missing_materials.
 *
 * Run (from the repo root):
 *   (cd server && go build -o /tmp/sa-server ./cmd/server)
 *   SA_GUESTS=1 /tmp/sa-server -listen :18090 -metrics "" &
 *   node test/t43-refinery.mjs        (SA_SERVER_URL overrides the URL)
 * Budget: about ten minutes, most of it the pad wreck's 120 s respawn.
 */
import { writeFileSync } from 'node:fs'
import { EVENT, OP, decodeCraftEnd } from './lib/wire.mjs'

const enc = new TextEncoder(), dec = new TextDecoder(), u8 = (n) => new Uint8Array(n)
const frame = (t, b) => { const o = u8(2 + b.length); new DataView(o.buffer).setUint16(0, t, true); o.set(b, 2); return o }
function hello (n_, tk) {
  const n = enc.encode(n_), t = enc.encode(tk)
  const b = u8(2 + 4 + n.length + 4 + t.length), d = new DataView(b.buffer)
  d.setUint16(0, 2, true); d.setUint32(2, n.length, true); b.set(n, 6)
  d.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(1, b)
}
function input (v, mask, seq) {
  const b = u8(25), dv = new DataView(b.buffer)
  for (let i = 0; i < 5; i++) dv.setFloat32(i * 4, v[i] ?? 0, true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true)
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
const wait = async (fn, ms = 6000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const count = (inv, item) => (inv ?? []).filter((s) => s.item === item).reduce((n, s) => n + s.qty, 0)

const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok]); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }

function connect (url, name) {
  const ws = new WebSocket(url)
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), results: [], xp: [], gathers: [], crafts: [], defs: null, seq: 1 }
  ws.addEventListener('open', () => ws.send(hello(name, '')))
  // A ping every 2 s, as the client does: the server drops a connection
  // silent for 10 s, and a hand channel (crystal ×3 = 10.5 s) is longer.
  const keep = setInterval(() => { if (ws.readyState === 1) { const b = u8(4); new DataView(b.buffer).setUint32(0, Date.now() >>> 0, true); ws.send(frame(0x0008, b)) } }, 2000)
  ws.addEventListener('close', () => clearInterval(keep))
  ws.addEventListener('error', () => {})
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const pv = new DataView(ev.data, 2), p = new Uint8Array(ev.data, 2)
    if (t === 0x0002) c.id = pv.getUint32(8, true)
    else if (t === 0x0010) c.defs = JSON.parse(dec.decode(p.subarray(4, 4 + pv.getUint32(0, true))))
    else if (t === 0x0005) c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)) })
    else if (t === 0x0006) c.spawns.delete(pv.getUint32(0, true))
    else if (t === 0x0004) {
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) {
        const o = 8 + i * 54
        c.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
          health: pv.getUint16(o + 50, true),
        })
      }
    } else if (t === 0x0007) {
      const id = pv.getUint16(4, true), data = Buffer.from(p.subarray(10, 10 + pv.getUint32(6, true))), at = Date.now()
      if (id === EVENT.SKILL_XP) c.xp.push({ ...JSON.parse(data.toString('utf8')), at })
      if (id === EVENT.GATHER_END) c.gathers.push({ ...JSON.parse(data.toString('utf8')), at })
      if (id === EVENT.CRAFT_END) c.crafts.push({ ...decodeCraftEnd(data), at })
    } else if (t === 0x000f) {
      c.results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    }
  })
  c.me = () => c.ents.get(c.id)
  c.call = async (op, body) => { const seq = c.seq++; c.ws.send(cmd(seq, op, body)); return wait(() => c.results.find((r) => r.seq === seq)) }
  c.lastXP = (skill) => c.xp.filter((x) => x.skill === skill).at(-1)?.xp ?? 0
  c.byDef = (def) => [...c.spawns].filter(([, v]) => v.def === def).map(([id]) => id)
  c.nearest = (def) => c.byDef(def).filter((id) => c.ents.has(id)).sort((a, b) => dist(c.ents.get(a).pos, c.me().pos) - dist(c.ents.get(b).pos, c.me().pos))
  return c
}

/** Sprint along the surface toward a point, jumping when wedged (t36's walker). */
async function walkTo (c, target, close, timeoutMs = 40000) {
  const at = typeof target === 'function' ? target : () => target
  const t0 = Date.now()
  let lastPos = c.me().pos.slice(), lastMove = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const d = sub(at(), c.me().pos)
    if (Math.hypot(...d) <= close) break
    const up = norm(c.me().pos)
    if (dist(c.me().pos, lastPos) > 0.5) { lastPos = c.me().pos.slice(); lastMove = Date.now() }
    const mask = Date.now() - lastMove > 1000 ? 0x0003 : 0x0001
    const lk = norm(tangent(d, up))
    c.ws.send(input([0, 1, lk[0], lk[1], lk[2]], mask, c.seq++))
    await sleep(50)
  }
  return dist(at(), c.me().pos)
}
/** Stand still looking at the target's aim point (cmd.go aimHeight). */
async function faceAt (c, pos, aim) {
  for (let i = 0; i < 10; i++) {
    const up = norm(c.me().pos), tUp = norm(pos)
    const eye = c.me().pos.map((x, i) => x + up[i] * 1.7)
    const lk = norm(sub(pos.map((x, i) => x + tUp[i] * aim), eye))
    c.ws.send(input([0, 0, lk[0], lk[1], lk[2]], 0, c.seq++))
    await sleep(60)
  }
}
async function approach (c, id, close, aim) {
  const at = () => c.ents.get(id).pos
  const d = await walkTo(c, at, close)
  await faceAt(c, at(), aim)
  return d
}
const NODE_AIM = 0.6, BENCH_AIM = 0.9
/** gather once and wait for its end; returns { r, end, ms }. */
async function gatherOnce (c, node) {
  await faceAt(c, c.ents.get(node).pos, NODE_AIM)
  const n = c.gathers.length, t0 = Date.now()
  const r = await c.call(OP.GATHER, { node })
  if (r?.status !== 0) return { r }
  const end = await wait(() => c.gathers.length > n ? c.gathers.at(-1) : null, (r.body.duration + 3) * 1000)
  return { r, end, ms: end ? end.at - t0 : -1 }
}
const crafted = (c, from) => c.crafts.slice(from)

// ======================================================= from nothing
const A = connect(process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18090/ws', 'fresh')
await wait(() => A.id && A.me() && A.defs && A.spawns.size > 3, 15000)
await sleep(500)
const defs = A.defs
const R = Object.fromEntries(defs.recipes.map((r) => [r.id, r]))
const N = Object.fromEntries(defs.nodes.map((n) => [n.id, n]))
const bag = async () => (await A.call(OP.INVENTORY, {}))?.body
const inv0 = await bag()
check('C187 a fresh guest has 0 cr', inv0?.credits === 0, `credits ${inv0?.credits} (a server with SA_START set is not fresh)`)
check('C187 a fresh guest has an empty bag', (inv0?.inventory ?? []).length === 0, JSON.stringify(inv0?.inventory))

// C183: every item past the raws is some recipe's output (vehicles wait for Phase 24).
const RAWS = new Set(['mat.ore.iron', 'mat.ore.copper', 'mat.scrap', 'mat.hide', 'mat.crystal'])
const outs = {}
for (const r of defs.recipes) outs[r.output.item] = (outs[r.output.item] ?? 0) + 1
const orphans = Object.values(defs.items).filter((it) => !RAWS.has(it.id) && it.kind !== 'vehicle' && outs[it.id] !== 1).map((it) => `${it.id}:${outs[it.id] ?? 0}`)
check('C183 every item past the raws is exactly one recipe\'s output', orphans.length === 0, orphans.join(' ') || `${defs.recipes.length} recipes`)
check('defs recipes carry station/skill/seconds', defs.recipes.every((r) => ['hand', 'bench', 'forge'].includes(r.station) && r.seconds > 0 && defs.skills.some((s) => s.id === r.skill)))
check('defs carries the forge with kind forge', defs.npcs['npc.forge']?.kind === 'forge', JSON.stringify(defs.npcs['npc.forge']))

const handRefuse = async (recipe, name) => {
  const r = await A.call(OP.CRAFT, { npc: 0, recipe, qty: 1 })
  check(`C186 the hands refuse ${name} (wrong_station)`, r?.status === 3 && r.body.reason === 'wrong_station', JSON.stringify(r?.body))
}
await handRefuse('recipe.mat.ingot.iron', 'a forge recipe')
await handRefuse('recipe.weapon.sidearm', 'a bench recipe')

// copper and crystal at the pad rocks, by hand
const cu = A.nearest('node.ore.copper')[0]
await approach(A, cu, 2.0, NODE_AIM)
const cuR = await A.call(OP.GATHER, { node: cu })
check('C187 copper by hand is no_tool', cuR?.status === 3 && cuR.body.reason === 'no_tool', JSON.stringify(cuR?.body))
const crys = A.nearest('node.crystal')[0]
await approach(A, crys, 2.0, NODE_AIM)
const cry = await gatherOnce(A, crys)
check('C187 crystal by hand: one unit, ×3 channel', cry.r?.body?.hand === true && Math.abs(cry.r.body.duration - N['node.crystal'].channel * 3) < 1e-6 && cry.end?.item === 'mat.crystal' && cry.end.qty === 1, `${JSON.stringify(cry.r?.body)} ${JSON.stringify(cry.end)}`)

// ---- the road: scrap → parts → crude drill → iron → ingots → Smithing 3 → plate
const forge = A.byDef('npc.forge')[0]
const wreck = A.nearest('node.wreck')[0]
const level = (skill) => A.xp.filter((x) => x.skill === skill).at(-1)?.level ?? 1
const have = (inv, item) => count(inv?.inventory, item)
const plateR = R['recipe.mat.plate.steel']
let firstHandIron = null, firstWreck = null, firstParts = null, firstIngot = null, crudeMade = false, drillFaster = null
let cancelProved = false, movedProved = false, dryProved = false, forgeLook = ''
/** one craft, waiting out every unit; returns the craft_end events. */
async function craft (npc, recipe, qty) {
  const from = A.crafts.length
  const r = await A.call(OP.CRAFT, { npc, recipe, qty })
  if (r?.status !== 0) return { r, ends: [] }
  const per = r.body.duration
  await wait(() => { const e = crafted(A, from); return e.length >= qty || e.some((x) => x.reason !== 'done') }, (per * qty + 4) * 1000)
  return { r, ends: crafted(A, from) }
}
async function atForge () {
  const d = await approach(A, forge, 2.0, BENCH_AIM)
  forgeLook = `${dist(A.me().pos, A.ents.get(forge).pos).toFixed(2)} m feet-to-feet, look at pos + up×${BENCH_AIM}`
  return d
}
const T0 = Date.now(), BUDGET = Number(process.env.T43_BUDGET_MS ?? 16 * 60000)
let plateEnd = null
while (Date.now() - T0 < BUDGET) {
  const inv = await bag()
  const scrap = have(inv, 'mat.scrap'), parts = have(inv, 'mat.parts'), ore = have(inv, 'mat.ore.iron'), ingots = have(inv, 'mat.ingot.iron')
  const crude = crudeMade || have(inv, 'tool.drill.crude') > 0
  const partsWanted = (crude ? 0 : 2) + 1
  const scrapWanted = Math.max(0, (partsWanted - parts) * 3 + (crude ? 0 : 4) - scrap)
  if (level('smithing') >= plateR.level && ingots >= 2 && parts >= 1) {
    await atForge()
    const p = await craft(forge, plateR.id, 1)
    plateEnd = p.ends[0]
    break
  }
  if (!crude && scrap >= 4 && parts >= 2) {
    const c = await craft(0, 'recipe.tool.drill.crude', 1)
    crudeMade = c.ends[0]?.reason === 'done'
    check('C187 the crude drill crafts in the hands', crudeMade, JSON.stringify(c.ends))
    const eq = await A.call(OP.EQUIP, { slot: 'tool', item: 'tool.drill.crude' })
    check('the crude drill goes in TOOL', eq?.status === 0 && eq.body.equipped?.tool === 'tool.drill.crude')
    continue
  }
  if (scrap >= 3 && parts < partsWanted) {
    if (!cancelProved) {
      const from = A.crafts.length
      await A.call(OP.CRAFT, { npc: 0, recipe: 'recipe.mat.parts', qty: 1 })
      const cx = await A.call(OP.GATHER_CANCEL, {})
      const end = await wait(() => crafted(A, from)[0], 2000)
      const after = await bag()
      cancelProved = cx?.status === 0 && end?.reason === 'cancel' && have(after, 'mat.scrap') === scrap
      check('C185 gather_cancel ends a craft `cancel`, the unit\'s inputs back', cancelProved, `${JSON.stringify(end)} scrap ${scrap}→${have(after, 'mat.scrap')}`)
    }
    const t0 = Date.now()
    const c = await craft(0, 'recipe.mat.parts', 1)
    if (!firstParts) {
      firstParts = c
      check('C185 parts in the hands: duration = seconds, craft_end done', c.r?.body?.duration === R['recipe.mat.parts'].seconds && c.ends[0]?.reason === 'done' && c.ends[0].item === 'mat.parts' && c.ends[0].qty === 1 && Date.now() - t0 >= 900, `${JSON.stringify(c.r?.body)} ${JSON.stringify(c.ends)}`)
    }
    continue
  }
  if (scrapWanted > 0 && A.ents.get(wreck)?.health > 0) {
    await approach(A, wreck, 2.0, NODE_AIM)
    const g = await gatherOnce(A, wreck)
    if (!firstWreck && g.end) {
      firstWreck = g
      check('C187 scrap by hand at the pad wreck: ×3, one unit', g.r.body.hand === true && Math.abs(g.r.body.duration - N['node.wreck'].channel * 3) < 1e-6 && g.end.item === 'mat.scrap' && g.end.qty === 1, `${JSON.stringify(g.r.body)} ${JSON.stringify(g.end)}`)
    }
    continue
  }
  if (ore >= 2 && (level('smithing') < plateR.level || ingots < 2)) {
    await atForge()
    let qty = Math.floor(ore / 2)
    if (!dryProved && qty >= 1) qty += 1 // one unit more than the ore: the last runs dry
    if (!movedProved && qty >= 1) {
      const from = A.crafts.length
      const before = ore
      const r = await A.call(OP.CRAFT, { npc: forge, recipe: 'recipe.mat.ingot.iron', qty: 1 })
      check(`C186 the pad forge takes its own recipe (${forgeLook})`, r?.status === 0, JSON.stringify(r?.body))
      const up = norm(A.me().pos), away = norm(tangent(sub(A.me().pos, A.ents.get(forge).pos), up))
      for (let i = 0; i < 12; i++) { A.ws.send(input([0, 1, away[0], away[1], away[2]], 0x0001, A.seq++)); await sleep(50) }
      for (let i = 0; i < 6; i++) { A.ws.send(input([0, 0, away[0], away[1], away[2]], 0, A.seq++)); await sleep(50) }
      const end = await wait(() => crafted(A, from)[0], 3000)
      await sleep(300)
      const after = await bag()
      movedProved = end?.reason === 'moved' && have(after, 'mat.ore.iron') === before
      check('C185 stepping away ends it `moved`, the ore back', movedProved, `${JSON.stringify(end)} ore ${before}→${have(after, 'mat.ore.iron')}`)
      continue
    }
    const t0 = Date.now()
    const c = await craft(forge, 'recipe.mat.ingot.iron', qty)
    if (!firstIngot && c.ends.length) {
      firstIngot = c
      check('C185 ingots at the forge: one duration per unit, a craft_end per unit', c.r.body.duration === R['recipe.mat.ingot.iron'].seconds && c.ends.slice(0, Math.floor(ore / 2)).every((e) => e.reason === 'done' && e.item === 'mat.ingot.iron'), `${JSON.stringify(c.r.body)} ${c.ends.map((e) => e.reason).join(',')} in ${Date.now() - t0} ms`)
    }
    if (!dryProved) {
      dryProved = c.ends.at(-1)?.reason === 'missing_materials' && c.ends.filter((e) => e.reason === 'done').length === qty - 1
      check('C185 a craft past the ore ends missing_materials after the last output', dryProved, c.ends.map((e) => e.reason).join(','))
    }
    continue
  }
  // nothing else to do: dig iron (by hand until the crude drill exists)
  const iron = A.nearest('node.ore.iron').find((id) => A.ents.get(id).health > 0)
  if (!iron) { await sleep(1000); continue }
  await approach(A, iron, 2.0, NODE_AIM)
  const g = await gatherOnce(A, iron)
  if (g.end && g.r.body.hand && !firstHandIron) {
    firstHandIron = g
    check('C187 iron by hand: hand=true, channel ×3, lands when promised', Math.abs(g.r.body.duration - N['node.ore.iron'].channel * 3) < 1e-6 && Math.abs(g.ms - g.r.body.duration * 1000) < 600, `${JSON.stringify(g.r.body)} ${g.ms} ms`)
    check('C187 one unit per yield by hand', g.end.item === 'mat.ore.iron' && g.end.qty === 1, JSON.stringify(g.end))
    await wait(() => A.lastXP('mining') > 0, 2500)
  }
  if (g.end && !g.r.body.hand && !drillFaster) {
    drillFaster = g
    const mult = defs.items['tool.drill.crude'].gather_mult
    check('C187 the crude drill gathers faster: channel × gather_mult (less Mining efficacy), the table\'s count', g.r.body.duration <= N['node.ore.iron'].channel * mult + 1e-6 && g.r.body.duration > N['node.ore.iron'].channel * mult * 0.9 && mult < 3 && g.end.qty > 1, `${JSON.stringify(g.r.body)} ${JSON.stringify(g.end)}`)
  }
}
const roadMs = Date.now() - T0
check(`C186 a steel plate beaten at the pad forge from nothing (${(roadMs / 60000).toFixed(1)} min)`, plateEnd?.reason === 'done' && plateEnd.item === 'mat.plate.steel', JSON.stringify(plateEnd))
check('C185 Smithing paid for the work (level ≥ 3)', level('smithing') >= plateR.level, `level ${level('smithing')}, ${A.lastXP('smithing')} xp`)
check('C187 hand gathering paid half XP (salvaging 1 unit = half the wreck)', A.xp.some((x) => x.skill === 'salvaging') && A.lastXP('salvaging') % Math.floor(N['node.wreck'].xp / 2) === 0, `${A.lastXP('salvaging')} xp`)

// ---- the relay bench
const bench = A.byDef('npc.workbench')[0]
const walked = await approach(A, bench, 2.2, BENCH_AIM)
check('reached the relay bench', walked <= 2.6, `${walked.toFixed(1)} m`)
const atBench = await A.call(OP.CRAFT, { npc: bench, recipe: 'recipe.mat.ingot.iron', qty: 1 })
check('C186 the bench refuses a forge recipe (wrong_station)', atBench?.status === 3 && atBench.body.reason === 'wrong_station', JSON.stringify(atBench?.body))
const side = await A.call(OP.CRAFT, { npc: bench, recipe: 'recipe.weapon.sidearm', qty: 1 })
check('C186 the bench takes the sidearm up to its materials (wiring owed)', side?.status === 3 && side.body.reason === 'missing_materials', JSON.stringify(side?.body))
const fin = await bag()
check('C186 the shop untouched: still 0 cr', fin?.credits === 0, `${fin?.credits}`)
A.ws.close()

const fails = checks.filter(([, ok]) => !ok)
writeFileSync(new URL('./out/t43-refinery.json', import.meta.url), JSON.stringify({
  when: new Date().toISOString(), roadMs, forgeLook, checks: checks.map(([n, ok]) => ({ n, ok })),
}, null, 1))
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
