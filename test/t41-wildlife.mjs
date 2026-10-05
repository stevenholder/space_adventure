#!/usr/bin/env node
/**
 * t41 — Phase 14 wildlife over the wire: C142–C146 (docs/ROADMAP.md
 * "Phase 14 — wildlife"; contract GDD "Wildlife — herds and wandering").
 *
 *   C142 the joiner's `spawn` frames carry every herd of wildlife.json, by def
 *        and count, with ids above the rover's;
 *   C143 every member's first-snapshot position is within spread + 1 m of its
 *        herd centre (surface distance, R 150), no two members of a herd are
 *        closer than the archetype's diameter, every herd centre is >= 30 m
 *        from spawn and from every zone origin;
 *   C144 a far member (the yeti, wander 15), watched 30 s from spawn with
 *        nobody near, walks >= 2 m in total and never strays > wander + 2 m
 *        from where it stood at join (a pause is recorded, not asserted);
 *   C145 buy a pulse rifle, walk ~99 m to the proof herd (herd.blobs.east):
 *        a member closes > 3 m once we are inside its aggro radius and a
 *        `hit` lands on us; shoot one to its `death`; a drop from
 *        loot.wild.small appears within 3 m of where it fell; within 25 s
 *        (npc_respawn 20 s) the same id is alive at full health within
 *        1.5 m of its join position;
 *   C146 a camp grunt over the same 30 s window moves < 0.01 m.
 *
 * Posts. The wire does not carry an NPC's post, so the harness composes it
 * from wildlife.json exactly as GDD "Placement (defs.ComposeHerd)" says
 * (golden-angle disc in the origin_dir east/north frame, glued with the
 * `terrain` frame's radius). Members stand still for idle_dwell (3 s) + the
 * first wander_pause after boot, so on a FRESH server (first snapshot at
 * tick <= 60) C143 asserts the observed join positions and that they match
 * the composed posts; on a warm server the herds are already wandering, so
 * C143 checks the composed posts and says so. C144's "from its post" and
 * C145's "back at its post" use the composed post either way.
 * Measurements go to test/out/t41-wildlife.json.
 *
 * Run: node test/t41-wildlife.mjs   (needs a live server; SA_PORT picks the port)
 */
import { readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { sampleRadius } from './lib/field.mjs'

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
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
const scale = (a, k) => a.map(x => x * k)
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const dist = (a, b) => Math.hypot(...sub(a, b))
const tangent = (d, up) => { const k = dot(d, up); return norm([d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k]) }
const R = 150
const surf = (a, b) => R * Math.acos(Math.max(-1, Math.min(1, dot(norm(a), norm(b)))))   // surface distance
const rotAbout = (v, axis, ang) => {   // Rodrigues, axis unit
  const c = Math.cos(ang), s = Math.sin(ang), k = dot(axis, v)
  const x = [axis[1] * v[2] - axis[2] * v[1], axis[2] * v[0] - axis[0] * v[2], axis[0] * v[1] - axis[1] * v[0]]
  return [0, 1, 2].map(i => v[i] * c + x[i] * s + axis[i] * k * (1 - c))
}
const r2 = (x) => Math.round(x * 100) / 100
const r3 = (v) => v.map(x => Math.round(x * 1000) / 1000)

const OP = { BUY: 0x0002, EQUIP: 0x0003 }
const EV = { SHOT: 0x0002, HIT: 0x0003, DEATH: 0x0004, ATTACK: 0x0010 }
const T = { NPC: 3, VEHICLE: 5, LOOT: 6 }

const data = (p) => JSON.parse(readFileSync(new URL('../server/data/' + p, import.meta.url), 'utf8'))
const herds = data('wildlife.json').herds
const mobs = new Map(data('mobs.json').npcs.map(m => [m.id, m]))
const loot = data('loot.json').tables
const zones = readdirSync(new URL('../server/data/zones/', import.meta.url)).filter(f => f.endsWith('.json'))
  .map(f => data('zones/' + f)).map(z => ({ id: z.id, dir: norm(z.origin_dir) }))
const PROOF = 'herd.blobs.east', FAR = 'herd.yeti'
// The committed harness route t16/t17 walk unarmed: a herd must not be able
// to notice a walker on it (aggro_radius + wander + 5 m, ROADMAP Phase 14).
const ROUTE = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8')).waypoints.map(norm)

// --- connection -------------------------------------------------------------
const ws = new WebSocket(`ws://127.0.0.1:${process.env.SA_PORT ?? 18080}/ws`); ws.binaryType = 'arraybuffer'
let myId = 0, tick = 0, openAt = 0, field = null
const ents = new Map(), spawns = new Map(), results = [], events = [], despawns = []
const snapHooks = new Set()
ws.addEventListener('open', () => { openAt = Date.now(); ws.send(hello('warden', 'wild-' + Date.now())) })
ws.addEventListener('message', (ev) => {
  const dv = new DataView(ev.data), t = dv.getUint16(0, true), p = new Uint8Array(ev.data, 2)
  const pv = new DataView(ev.data, 2)
  if (t === 0x0002) myId = pv.getUint32(8, true)
  else if (t === 0x0005) spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)), at: Date.now() })
  else if (t === 0x000a) {   // terrain: u16 face_grid | f32 radius_min | f32 radius_max | u16 radii[]
    const g = pv.getUint16(0, true), radii = new Uint16Array(6 * g * g)
    for (let i = 0; i < radii.length; i++) radii[i] = pv.getUint16(10 + 2 * i, true)
    field = { faceGrid: g, radiusMin: pv.getFloat32(2, true), radiusMax: pv.getFloat32(6, true), radii }
  } else if (t === 0x0006) despawns.push({ id: pv.getUint32(0, true), at: Date.now() })
  else if (t === 0x0004) {
    tick = pv.getUint32(0, true)
    const n = pv.getUint16(6, true)
    for (let i = 0; i < n; i++) {
      const o = 8 + i * 54
      ents.set(pv.getUint32(o, true), { pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)], health: pv.getUint16(o + 50, true), flags: pv.getUint8(o + 52), tick })
    }
    const now = Date.now()
    for (const h of snapHooks) h(now)
  } else if (t === 0x000f) results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
  else if (t === 0x0007) {
    const raw = p.slice(10)
    const e = { id: pv.getUint32(0, true), ev: pv.getUint16(4, true), raw, at: Date.now() }
    if (e.ev === 0x0004 && ents.has(e.id)) e.pos = [...ents.get(e.id).pos]   // where it fell: the last row before the death
    events.push(e)
  }
})
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
const wait = async (fn, ms = 4000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const u32 = (raw, o = 0) => raw.length >= o + 4 ? new DataView(raw.buffer, raw.byteOffset).getUint32(o, true) : null
const u16 = (raw, o) => raw.length >= o + 2 ? new DataView(raw.buffer, raw.byteOffset).getUint16(o, true) : null

const checks = []
const record = (id, name, ok, detail) => { checks.push({ id, name, ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name} — ${detail}`) }
const out = { herds: {}, c143: {}, c144: {}, c145: {} }

const herdIdsOf = (h) => [...spawns].filter(([, v]) => v.type === T.NPC && v.def === h.def).map(([id]) => id).sort((a, b) => a - b)
const allHerdIds = () => herds.flatMap(herdIdsOf)
const joined = await wait(() => myId && field && ents.get(myId) && spawns.size > 10 && allHerdIds().length && allHerdIds().every(id => ents.has(id)), 8000)
if (!joined) { console.log('FAIL: never saw our own row and every herd member in a snapshot'); process.exit(1) }
const joinMs = Date.now() - openAt, joinTick = tick, fresh = joinTick <= 60   // idle_dwell 3 s at 20 Hz
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
function composeHerd (h) {   // GDD "Placement (defs.ComposeHerd)"; mirrors internal/defs/wildlife.go
  const up = norm(h.origin_dir)
  let ref = [0, 0, 1]; if (Math.abs(dot(ref, up)) > 0.999) ref = [1, 0, 0]
  const north = norm(sub(ref, scale(up, dot(ref, up)))), east = cross(up, north)
  const origin = scale(up, sampleRadius(field, up))
  return Array.from({ length: h.count }, (_, i) => {
    const r = h.spread * Math.sqrt((i + 0.5) / h.count), th = i * 2.399963
    const d = norm(add(origin, add(scale(east, r * Math.cos(th)), scale(north, r * Math.sin(th)))))
    return scale(d, sampleRadius(field, d))
  })
}
const me = () => ents.get(myId).pos
const myUp = () => norm(me())
let seq = 1

// --- C142: herds exist ------------------------------------------------------
const rover = [...spawns].find(([, v]) => v.type === T.VEHICLE)?.[0] ?? 0
const post = new Map(), seen0 = new Map()   // id -> composed post, id -> first-snapshot position
for (const h of herds) {
  const ids = herdIdsOf(h)
  const composed = composeHerd(h)
  ids.forEach((id, i) => { seen0.set(id, [...ents.get(id).pos]); if (composed[i]) post.set(id, composed[i]) })
  out.herds[h.id] = { def: h.def, ids, posts: ids.map(id => post.get(id) && r3(post.get(id))), joinPos: ids.map(id => r3(seen0.get(id))) }
  record('C142', `${h.id}: ${h.count}× ${h.def}, ids > rover`, ids.length === h.count && rover > 0 && ids.every(id => id > rover),
    `got ${ids.length} (${ids.join(',')}), rover ${rover}`)
}

// --- C143: placed clear and apart -------------------------------------------
for (const h of herds) {
  const ids = out.herds[h.id].ids, centre = norm(h.origin_dir), rad = mobs.get(h.def).radius
  const at = fresh ? seen0 : post   // a warm server's members are already off their posts
  const off = ids.map(id => surf(at.get(id), centre))
  const vsComposed = Math.max(0, ...ids.map(id => post.has(id) ? dist(seen0.get(id), post.get(id)) : Infinity))
  let minPair = Infinity
  for (let i = 0; i < ids.length; i++) for (let j = i + 1; j < ids.length; j++) minPair = Math.min(minPair, dist(at.get(ids[i]), at.get(ids[j])))
  const fromSpawn = surf(centre, [0, 1, 0])
  const fromZones = Math.min(...zones.map(z => surf(centre, z.dir)))
  const fromRoute = Math.min(...ROUTE.map(w => surf(centre, w))), routeNeed = mobs.get(h.def).aggro_radius + h.wander + 5
  Object.assign(out.herds[h.id], { maxOffset: r2(Math.max(...off)), minPair: Number.isFinite(minPair) ? r2(minPair) : null, fromSpawn: r2(fromSpawn), fromZones: r2(fromZones), fromRoute: r2(fromRoute), joinVsComposed: r2(vsComposed) })
  record('C143', `${h.id}: within spread+1, apart, clear${fresh ? ', at the composed posts' : ' (warm server: composed posts)'}`,
    off.length > 0 && Math.max(...off) <= h.spread + 1 && (ids.length < 2 || minPair >= 2 * rad) && fromSpawn >= 30 && fromZones >= 30 && fromRoute >= routeNeed && (!fresh || vsComposed <= 0.1),
    `${fresh ? `join vs composed ${vsComposed.toFixed(3)} m, ` : ''}max off ${Math.max(...off).toFixed(2)}/${h.spread + 1} m, min pair ${Number.isFinite(minPair) ? minPair.toFixed(2) : '-'}/${(2 * rad).toFixed(2)} m, spawn ${fromSpawn.toFixed(0)} m, zones ${fromZones.toFixed(0)} m, camp route ${fromRoute.toFixed(0)}/${routeNeed} m`)
}
console.log(`joined id=${myId} in ${joinMs} ms at tick ${joinTick} (${fresh ? 'fresh server' : 'WARM server: C143 reads composed posts'})`)

// --- C144 + C146: 30 s of standing at spawn --------------------------------
const campDir = zones.find(z => z.id === 'camp').dir
const grunts = [...spawns].filter(([id, v]) => v.type === T.NPC && v.def === 'npc.grunt' && ents.has(id) && surf(ents.get(id).pos, campDir) < 40).map(([id]) => id)
const watched = [...allHerdIds(), ...grunts]
const tracks = new Map(watched.map(id => [id, []]))
let lastTick = -1
const sampler = () => {
  if (tick === lastTick) return
  lastTick = tick
  for (const id of watched) { const e = ents.get(id); if (e) tracks.get(id).push({ tick, pos: [...e.pos] }) }
}
snapHooks.add(sampler)
const look0 = tangent([1, 0, 0], myUp())
const watchT0 = Date.now()
while (Date.now() - watchT0 < 30000) { ws.send(input(0, 0, look0, 0, seq++)); await sleep(50) }
snapHooks.delete(sampler)
const stats = (id) => {
  const tr = tracks.get(id), p0 = post.get(id) ?? tr[0].pos
  let path = 0, maxFrom = 0, stillTicks = 0, longestStill = 0, longestEndTick = 0
  for (let i = 1; i < tr.length; i++) {
    const step = dist(tr[i].pos, tr[i - 1].pos); path += step
    if (step < 1e-3) { stillTicks += tr[i].tick - tr[i - 1].tick; if (stillTicks > longestStill) { longestStill = stillTicks; longestEndTick = tr[i].tick } } else stillTicks = 0
    maxFrom = Math.max(maxFrom, surf(tr[i].pos, p0))
  }
  const disp = Math.max(...tr.map(s => dist(s.pos, tr[0].pos)))
  return { samples: tr.length, path: r2(path), maxFrom: r2(maxFrom), maxDisp: Math.round(disp * 1e5) / 1e5, longestPauseS: r2(longestStill / 20), longestPauseEndTick: longestEndTick, firstTick: tr[0]?.tick }
}
const far = herds.find(h => h.id === FAR), farId = out.herds[FAR].ids[0]
const fs = stats(farId)
for (const h of herds) out.c143[h.id] = out.herds[h.id].ids.map(id => ({ id, ...stats(id) }))
console.log(`C144 info: per-herd path m over 30 s: ${herds.map(h => `${h.id.replace('herd.', '')} [${out.c143[h.id].map(s => s.path).join(',')}]`).join(' ')}`)
record('C144', `${FAR} ${farId} wanders: path >= 2 m, within wander+2 of post`, fs.samples > 400 && fs.path >= 2 && fs.maxFrom <= far.wander + 2,
  `${fs.samples} samples, path ${fs.path} m, max from post ${fs.maxFrom}/${far.wander + 2} m, longest pause ${fs.longestPauseS} s ending tick ${fs.longestPauseEndTick} (window from tick ${fs.firstTick}; info)`)
const gs = grunts.map(id => ({ id, ...stats(id) }))
out.c145 = gs
const worst = gs.length ? Math.max(...gs.map(g => g.maxDisp)) : NaN
record('C146', `camp grunts idle (${grunts.length}) move < 0.01 m over 30 s`, gs.length > 0 && worst < 0.01 && gs.every(g => g.samples > 400),
  `max displacement ${worst} m (${gs.map(g => `${g.id}:${g.maxDisp}`).join(' ')})`)

// --- C145: buy, walk, fight, drop, return -----------------------------------
async function call (op, body) {
  const s = ++seq
  ws.send(cmd(s, op, body))
  return wait(() => results.find(r => r.seq === s && r.op === op))
}
async function walkTo (pos, within, maxSteps = 200) {
  for (let i = 0; i < maxSteps; i++) {
    const d = sub(pos, me())
    if (Math.hypot(...d) <= within) break
    ws.send(input(0, 1, tangent(d, myUp()), 0, seq++)); await sleep(50)
  }
  ws.send(input(0, 0, tangent(sub(pos, me()), myUp()), 0, seq++)); await sleep(200)
}
const qm = [...spawns].find(([, v]) => v.type === T.NPC && v.def === 'npc.quartermaster')?.[0]
await walkTo(ents.get(qm).pos, 2.4)
const buy = await call(OP.BUY, { npc: qm, item: 'weapon.pulse', qty: 1 })
const eq = await call(OP.EQUIP, { slot: 'primary', item: 'weapon.pulse' })
console.log(`buy ${buy?.status} equip ${eq?.status} ${JSON.stringify(eq?.body?.equipped)}`)

const proof = herds.find(h => h.id === PROOF), arch = mobs.get(proof.def)
const blobIds = out.herds[PROOF].ids
const alive = (id) => { const e = ents.get(id); return e && e.health > 0 && !(e.flags & 0x04) }
const nearestBlob = () => blobIds.filter(alive).map(id => [id, dist(ents.get(id).pos, me())]).sort((a, b) => a[1] - b[1])[0]
// closure: per member, distance at the moment we came inside its aggro radius, then its minimum
const closure = new Map()
const closeHook = () => {
  for (const id of blobIds) {
    if (!alive(id)) continue
    const d = dist(ents.get(id).pos, me())
    const c = closure.get(id)
    if (!c) { if (d <= arch.aggro_radius) closure.set(id, { d0: d, min: d }) } else c.min = Math.min(c.min, d)
  }
}
snapHooks.add(closeHook)
// position history for lag-compensated aim (render at serverClock - interp_delay)
const hist = new Map(blobIds.map(id => [id, []]))
const histHook = (now) => { for (const id of blobIds) { const e = ents.get(id); if (e) { const h = hist.get(id); h.push({ at: now, pos: [...e.pos] }); if (h.length > 40) h.shift() } } }
snapHooks.add(histHook)
const posAgo = (id, ms) => { const h = hist.get(id), t = Date.now() - ms; let best = h[h.length - 1]; for (const s of h) if (s.at <= t) best = s; return best.pos }

// The walk: bearing to the herd centre, sprinting; on a wedge (< 1 m in 3 s)
// swing the bearing +/-35 degrees for 3 s and try again.
const centre = scale(norm(proof.origin_dir), R)
const walkT0 = Date.now(), walkLog = []
let lastProgressAt = Date.now(), lastProgressPos = [...me()], detour = 0, detourUntil = 0, wedges = 0
while (Date.now() - walkT0 < 90000) {
  const nb = nearestBlob()
  if (nb && nb[1] <= 15) break
  const up = myUp()
  let dir = tangent(sub(nb ? ents.get(nb[0]).pos : centre, me()), up)
  if (Date.now() < detourUntil) dir = norm(rotAbout(dir, up, detour))
  ws.send(input(0, 1, dir, 0x0001, seq++)); await sleep(50)
  if (Date.now() - lastProgressAt > 3000) {
    if (dist(me(), lastProgressPos) < 1) { wedges++; detour = (wedges % 2 ? 1 : -1) * 35 * Math.PI / 180; detourUntil = Date.now() + 3000; walkLog.push({ t: Date.now() - walkT0, wedgedAt: r3(me()) }) }
    lastProgressAt = Date.now(); lastProgressPos = [...me()]
  }
}
const arrivedNb = nearestBlob()
const walkS = (Date.now() - walkT0) / 1000
console.log(`walk: ${walkS.toFixed(1)} s, ${wedges} wedge(s), nearest blob ${arrivedNb ? arrivedNb[1].toFixed(1) : '-'} m, at ${r3(me())}`)
Object.assign(out.c144, { walkS: r2(walkS), wedges, walkLog, arrivedAt: r3(me()), nearestOnArrival: arrivedNb ? r2(arrivedNb[1]) : null })
const reached = !!arrivedNb && arrivedNb[1] <= 15

let hitsOnMe = [], victim = 0, deathAt = 0, deathPos = null, dropIds = [], respawn = null
if (!reached) {
  record('C145', 'reached the proof herd', false, `stopped ${arrivedNb ? arrivedNb[1].toFixed(1) : '-'} m from the nearest blob at ${r3(me())} after ${walkS.toFixed(0)} s, ${wedges} wedges — SKIPPED the fight`)
} else {
  // Stand and let them come: closure > 3 m and a hit on us.
  const evMark = events.length
  const standT0 = Date.now()
  const closed = () => [...closure.values()].some(c => c.d0 - c.min > 3)
  const hitMe = () => events.slice(evMark).filter(e => e.ev === EV.HIT && e.id === myId)
  while (Date.now() - standT0 < 15000 && !(closed() && hitMe().length)) {
    const nb = nearestBlob(); const lk = nb ? tangent(sub(ents.get(nb[0]).pos, me()), myUp()) : look0
    ws.send(input(0, 0, lk, 0, seq++)); await sleep(50)
  }
  hitsOnMe = hitMe()
  const hitFrom = hitsOnMe.map(e => u32(e.raw))
  out.c144.closure = Object.fromEntries([...closure].map(([id, c]) => [id, { d0: r2(c.d0), min: r2(c.min) }]))
  out.c144.hitsOnMe = hitsOnMe.length; out.c144.hitFrom = [...new Set(hitFrom)]
  const best = [...closure].map(([id, c]) => [id, c.d0 - c.min]).sort((a, b) => b[1] - a[1])[0]
  record('C145', 'aggro: a member closes > 3 m and hits us', closed() && hitsOnMe.length > 0,
    `best closure ${best ? `${best[0]} ${best[1].toFixed(1)} m` : 'none'}, hits on us ${hitsOnMe.length} from ${[...new Set(hitFrom)].join(',')} after ${((Date.now() - standT0) / 1000).toFixed(1)} s`)

  // Break contact (sprint 7.5 m/s vs blob 3.6) so the kill falls outside the
  // 1.5 m pickup radius, then shoot every member that follows.
  const away = tangent(sub(me(), ents.get(nearestBlob()?.[0] ?? blobIds[0]).pos), myUp())
  for (let i = 0; i < 24; i++) { ws.send(input(0, 1, away, 0x0001, seq++)); await sleep(50) }
  const deathsMark = events.length
  const herdDeaths = () => events.slice(deathsMark).filter(e => e.ev === EV.DEATH && blobIds.includes(e.id))
  const shootT0 = Date.now()
  let shots = 0
  while (Date.now() - shootT0 < 20000 && blobIds.some(alive) && herdDeaths().length < blobIds.length) {
    const cands = blobIds.filter(alive).map(id => [id, dist(ents.get(id).pos, me())]).filter(c => c[1] < 30).sort((a, b) => a[1] - b[1])
    if (!cands.length) { ws.send(input(0, 0, look0, 0, seq++)); await sleep(50); continue }
    const [tid] = cands.find(c => c[1] > 2.2) ?? cands[0]
    const tp = posAgo(tid, 100)
    const eye = add(me(), scale(myUp(), 1.7))
    const aim = norm(sub(add(tp, scale(norm(tp), arch.height * 0.5)), eye))
    ws.send(input(0, 0, aim, 0, seq++))
    ws.send(fire(seq - 1, aim)); shots++
    await sleep(200)
  }
  const kill = herdDeaths()[0]
  out.c144.shots = shots; out.c144.herdDeaths = herdDeaths().map(e => ({ id: e.id, killer: u32(e.raw), atMs: e.at - shootT0 }))
  if (kill) {
    victim = kill.id; deathAt = kill.at
    deathPos = kill.pos
  }
  // An NPC's `death` carries no killer (sim/spawner.go emits an empty data,
  // PROTOCOL says u32 killer): the kill is ours when our `hit` on it left 0.
  const fatal = kill && events.find(e => e.ev === EV.HIT && e.id === victim && e.at <= kill.at && u32(e.raw) === myId && u16(e.raw, 18) === 0)   // hit: u32 shooter | f32 point[3] | u16 damage | u16 health_after
  out.c144.deathDataLen = kill ? kill.raw.length : null
  record('C145', 'a member dies to our rifle', !!fatal,
    kill ? `${victim} died (death data ${kill.raw.length} B, fatal hit from ${fatal ? u32(fatal.raw) : 'none'}) after ${shots} shots, ${herdDeaths().length}/${blobIds.length} herd deaths` : `no death in ${shots} shots`)

  // The drop: a loot entity spawned after the death, within 3 m, from loot.wild.small.
  if (kill) {
    const table = new Set(loot['loot.wild.small'].map(e => e.item))
    await wait(() => [...spawns].some(([id, v]) => v.type === T.LOOT && v.at >= deathAt - 50 && ents.has(id)), 2000)
    const drops = [...spawns].filter(([id, v]) => v.type === T.LOOT && v.at >= deathAt - 50 && ents.has(id))
      .map(([id, v]) => ({ id, def: v.def, d: dist(ents.get(id).pos, deathPos) }))
    const near = drops.filter(d => d.d <= 3)
    dropIds = near.map(d => d.id)
    out.c144.drops = drops.map(d => ({ ...d, d: r2(d.d) }))
    record('C145', 'drop within 3 m names a loot.wild.small item', near.length > 0 && near.every(d => table.has(d.def)) && near.some(d => d.def === 'mat.scrap'),
      `${near.map(d => `${d.id}:${d.def}@${d.d.toFixed(2)}m`).join(' ') || 'none'} (all drops since death: ${drops.length})`)

    // The return: alive again at full health near its post within 25 s.
    let seenAt = 0, seenPos = null
    const respHook = (now) => { if (!seenAt && ents.get(victim).health > 0 && !(ents.get(victim).flags & 0x04)) { seenAt = now; seenPos = [...ents.get(victim).pos]; respawn = { health: ents.get(victim).health } } }
    snapHooks.add(respHook)
    // walk back toward spawn so the respawned herd has nobody to chase
    const respT0 = Date.now()
    while (!seenAt && Date.now() - deathAt < 25000) {
      ws.send(input(0, 1, tangent(sub([0, R, 0], me()), myUp()), 0x0001, seq++)); await sleep(50)
    }
    snapHooks.delete(respHook)
    const tookS = seenAt ? (seenAt - deathAt) / 1000 : NaN
    const off = seenPos ? dist(seenPos, post.get(victim)) : NaN
    Object.assign(out.c144, { victim, deathPos: r3(deathPos), respawnS: r2(tookS), respawnPos: seenPos && r3(seenPos), respawnHealth: respawn?.health, respawnOffPost: r2(off) })
    record('C145', `${victim} back within 25 s at full health at its post`,
      !!seenAt && tookS <= 25 && respawn.health === arch.max_health && off <= 1.5,
      seenAt ? `alive after ${tookS.toFixed(1)} s, health ${respawn.health}/${arch.max_health}, ${off.toFixed(2)} m from post` : `not alive after ${((Date.now() - respT0) / 1000).toFixed(0)} s`)
  }
}

// --- results ------------------------------------------------------------------
writeFileSync(new URL('./out/t41-wildlife.json', import.meta.url), JSON.stringify({ at: new Date().toISOString(), joinMs, joinTick, fresh, myId, rover, ...out, checks }, null, 1) + '\n')
console.log('\n  crit  result  check')
for (const c of checks) console.log(`  ${c.id}  ${c.ok ? 'PASS' : 'FAIL'}    ${c.name}\n                ${c.detail}`)
const bad = checks.filter(c => !c.ok).length
console.log(bad ? `OVERALL: FAIL (${bad}/${checks.length})` : `OVERALL: PASS (${checks.length} checks)`)
ws.close()
process.exit(bad ? 1 : 0)
