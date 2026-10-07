#!/usr/bin/env node
/**
 * C14, re-pointed at a MOVING target — the test that can tell a server with
 * lag compensation from one without.
 *
 * The Phase 2 criterion fires at a target dummy. Dummies do not move, so the
 * rewound position equals the live one and rewind is a no-op: `t18` measures
 * 20/20 whether the server rewinds or not. This fires at a camp grunt running
 * at a second player, from a shooter behind 500 ms of injected RTT, aimed
 * where THAT client draws the grunt (interp_delay behind its estimate of the
 * server clock). A shot only counts when that drawn capsule and the one a
 * server WITHOUT rewind would test — the grunt where the shot arrives, live +
 * vel·L — are further apart across the ray than the hitbox plus the weapon's
 * whole spread cone at that range: a server resolving at the present CANNOT
 * score it.
 *
 * Two checks carry the criterion:
 *   - HITS_NEEDED of at most SHOTS such shots land; and
 *   - every hit was resolved at the tick the client drew. The server's
 *     broadcast ray enters exactly one of the bait's recorded per-tick
 *     capsules at the reported hit point (0.000 m), which names the tick it
 *     rewound to. A hit count alone cannot see a rewind short by
 *     interp_delay (2 ticks, 0.4 m at 4 m/s still clips a 0.35 m capsule); a
 *     server built with the pre-fix rule (rewind = L only) scored 3/3 here
 *     and read server − drawn = +1.2..+1.9 ticks, against −0.8..−0.1 for
 *     the real one.
 *
 * The staging is fixed, from the camp's own layout (zones/camp.json), and
 * the grunt AI and spawn are deterministic, so the run repeats:
 *
 *   - BAIT_STAND, 6 m outside the west gate on its axis: the two gate-side
 *     grunts (-4,±4) see it through the 4 m gap inside aggro (22 m), and no
 *     gunner (30 m) or the far grunt (2,-9) does. It stands; the grunts' 18 m
 *     run at it, west at 4 m/s, is the window.
 *   - SHOOTER_STAND, south-west of that on the flattened apron, more than
 *     aggro_radius from every post (24 m from the nearest). The walls do NOT
 *     hide a stand inside 22 m: a first try at 19.7 m had a grunt pressed to
 *     the inside of the west wall, tracking the shooter. The run crosses its
 *     sightline side-on at 17–21 m. Hitscan ignores walls (ResolveShot tests
 *     entities only), so the part of the run behind the wall is in play.
 *
 * Firing stops at HITS_NEEDED: a grunt has 60 hp and the pulse does 25, so
 * the third hit on one grunt is the last shot fired. Arrival-aimed shots fire
 * only on a failure, as diagnosis. Hard ceiling: CEILING_MS for the whole
 * run (a pass takes ~65 s, ~55 of it walking the route).
 *
 * History: the first version shuttled the bait across a sightline it picked
 * at runtime, fired 3 control + 8 stale + 8 live shots needing 8/8, waited up
 * to 60 s per shot for a usable moment, and measured against live-NOW, which
 * is only interp_delay from what the client draws (0.1–0.3 m, rarely past the
 * hitbox). It ran past 300 s, killed grunts mid-volley (20 s respawns) and
 * sat red on main.
 *
 * Run: SA_PORT=18085 node test/t21-lagcomp-moving.mjs   (SA_PORT: the
 * server's port, kind's 18080 by default; the latency proxy takes :18082)
 */
import { spawn } from 'node:child_process'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { loadField, sampleRadius } from './lib/field.mjs'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
const norm = (v) => { const l = Math.hypot(...v); return v.map(x => x / l) }
const sub = (a, b) => [a[0]-b[0], a[1]-b[1], a[2]-b[2]]
const add = (a, b) => [a[0]+b[0], a[1]+b[1], a[2]+b[2]]
const mul = (a, k) => [a[0]*k, a[1]*k, a[2]*k]
const dot = (a, b) => a[0]*b[0] + a[1]*b[1] + a[2]*b[2]
const cross = (a, b) => [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
const dist = (a, b) => Math.hypot(...sub(a, b))

// --- world constants this script reasons about (server/data) ---------------
const HITBOX_RADIUS = 0.35   // items.json entity_defs, npc capsule radius
const SPREAD_BASE_DEG = 0.6  // items.json weapon.pulse spread_base: the server's whole cone
const AIM_MARGIN = 0.1       // m: tick quantisation of the rewind (50 ms × 4 m/s / 2)
const ONE_WAY_MS = 250       // injected per direction; 500 ms RTT (rewind 5 + 2 ticks, under rewind_max 10)
const INTERP_DELAY_MS = 100  // GDD "Lag compensation" interp_delay; the client owes this
const SHOTS = 4              // qualifying stale shots, at most
const HITS_NEEDED = 3        // of SHOTS; firing stops here, so one 60 hp grunt dies on the last shot at worst
const SHOT_SPACING_MS = 520  // a shot's hit event lands within one RTT + a tick
const CEILING_MS = 120000
const T0 = Date.now()
const stamp = () => `[${((Date.now() - T0) / 1000).toFixed(0).padStart(3)} s]`
const ceiling = setTimeout(() => { console.log(`${stamp()} FAIL hard ceiling: ${CEILING_MS / 1000} s`); process.exit(1) }, CEILING_MS)

function frame(type, body) { const o = u8(2 + body.length); new DataView(o.buffer).setUint16(0, type, true); o.set(body, 2); return o }
function hello(name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(2 + 4 + n.length + 4 + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
function input(mx, my, look, mask, seq) {
  const b = u8(24), dv = new DataView(b.buffer)
  dv.setFloat32(0, mx, true); dv.setFloat32(4, my, true)
  dv.setFloat32(8, look[0], true); dv.setFloat32(12, look[1], true); dv.setFloat32(16, look[2], true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true)
  return frame(0x0003, b)
}
function cmd(seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(8 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
function fire(seq, dir) {
  const b = u8(14), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true)
  dv.setFloat32(2, dir[0], true); dv.setFloat32(6, dir[1], true); dv.setFloat32(10, dir[2], true)
  return frame(0x0011, b)
}

async function session(name, port, lagMs = 0) {
  const s = { name, byTick: new Map(), myId: 0, ents: new Map(), spawns: new Map(), results: [], events: [], defs: null, seq: 1, snaps: 0, closed: null }
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`); ws.binaryType = 'arraybuffer'
  const token = `${name}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  ws.addEventListener('close', (e) => { s.closed = e.code })
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const p = new Uint8Array(ev.data, 2), pv = new DataView(ev.data, 2)
    if (t === 0x0002) s.myId = pv.getUint32(8, true)
    else if (t === 0x0010) s.defs = JSON.parse(dec.decode(p.subarray(4)))
    else if (t === 0x0005) s.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)) })
    else if (t === 0x0006) s.ents.delete(pv.getUint32(0, true))
    else if (t === 0x0004) {
      s.snaps++
      const tick = pv.getUint32(0, true)
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) { const o = 8 + i * 54
        s.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o+4,true), pv.getFloat32(o+8,true), pv.getFloat32(o+12,true)],
          vel: [pv.getFloat32(o+32,true), pv.getFloat32(o+36,true), pv.getFloat32(o+40,true)],
          health: pv.getUint16(o+50,true), flags: pv.getUint8(o+52),
        }) }
      // The server tick this snapshot describes, and when it turned up here:
      // together they are the whole of a client's knowledge of the server
      // clock, and rendering the contract's way needs both.
      s.newest = { tick, at: Date.now() }
      s.byTick.set(tick, new Map([...s.ents].map(([k, v]) => [k, v.pos])))
      s.byTick.delete(tick - 60)
    }
    else if (t === 0x000f) s.results.push({ seq: pv.getUint16(0,true), op: pv.getUint16(2,true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    else if (t === 0x0007) {
      // A `hit` carries the shooter in its first data word; without it a shot
      // from this harness is indistinguishable from a grunt hitting the bait.
      const e = { id: pv.getUint32(0,true), ev: pv.getUint16(4,true), at: Date.now() }
      // Read the body only if it is actually there: a short `hit` is a server
      // bug (one shipped), and a harness that dies on it reports nothing at
      // all about the run it was measuring.
      if (e.ev === 2 && pv.getUint32(6, true) >= 28) {
        e.ray = { o: [0, 1, 2].map(k => pv.getFloat32(10 + 4 * k, true)), d: [0, 1, 2].map(k => pv.getFloat32(22 + 4 * k, true)) }
      }
      if (e.ev === 3 && pv.getUint32(6, true) >= 20) {
        e.shooter = pv.getUint32(10, true)
        // The hit point is on the capsule the server actually resolved
        // against, which is the only direct read of WHERE it rewound to.
        e.point = [pv.getFloat32(14,true), pv.getFloat32(18,true), pv.getFloat32(22,true)]
      }
      s.events.push(e)
    }
  })
  s.ws = ws
  s.wait = async (fn, ms = 8000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
  s.me = () => s.ents.get(s.myId).pos
  s.send = (f) => ws.send(f)
  s.call = async (op, body) => { const q = s.seq++; ws.send(cmd(q, op, body)); return await s.wait(() => s.results.find(r => r.seq === q)) }
  // look is kept as state so the keep-alive input does not swing the body
  // around between shots.
  s.look = null
  s.walking = false
  s.hold = () => { if (s.look) ws.send(input(0, 0, s.look, 0, s.seq++)) }
  // The server drops a connection silent for 10 s, and each session stands
  // idle for far longer than that while the other one walks. A stopped
  // session that goes quiet does not error — it freezes, and every position
  // read off it afterwards is a lie that looks like lag.
  s.heartbeat = setInterval(() => { if (!s.walking) { try { s.hold() } catch {} } }, 1000)
  // lookAt turns to a world point without moving, in the tangent plane.
  s.lookAt = (p_) => {
    const up = norm(s.me()), d = sub(p_, s.me())
    s.look = norm(sub(d, mul(up, dot(d, up))))
    ws.send(input(0, 0, s.look, 0, s.seq++))
  }
  // A lagged walker sees itself one trip late and its stop lands one trip
  // later still: it coasts 2·lag·speed past where it let go. Let go early by
  // that much — at 250 ms that is 3.75 m of sprint, which overshot the
  // quartermaster's 3 m interact range on every attempt.
  s.walkTo = async (dst, stop, steps = 500) => {
    s.walking = true
    const lead = 7.5 * 2 * lagMs / 1000
    try {
    for (let i = 0; i < steps; i++) {
      const d = sub(dst, s.me()); if (Math.hypot(...d) <= stop + lead) break
      const up = norm(s.me()); const lk = norm(sub(d, mul(up, dot(d, up))))
      s.look = lk
      ws.send(input(0, 1, lk, 0x0001, s.seq++)); await sleep(50)
    }
    // Face the destination on the way out: a stopped walker still has to be
    // looking at what it means to interact with (cmd.go inRange).
    s.lookAt(dst)
    await sleep(150)
    return dist(dst, s.me())
    } finally { s.walking = false }
  }
  await s.wait(() => s.myId && s.defs && s.ents.size > 1)
  return s
}

// armed walks to the quartermaster by spawn, buys the pulse and equips it.
async function armed(s) {
  const npcId = [...s.spawns].find(([, v]) => v.type === 3 && v.def === 'npc.quartermaster')?.[0]
  if (!npcId) throw new Error('no quartermaster in the spawn set')
  const npcPos = s.ents.get(npcId).pos
  // interact_dist is 3.0 m and this session's view of its own position is one
  // one-way delay stale, so a single walk-and-fire can stop just outside. Walk
  // in, face the shopkeeper, let the look reach the server, and retry.
  let buy = null
  for (let attempt = 1; attempt <= 4 && buy?.status !== 0; attempt++) {
    if (attempt === 1) await s.walkTo(npcPos, 2.0, 200)
    else {
      // Settled, so the view is current: step the rest blind, one sprint
      // tick (0.375 m) per input, rather than walk-and-watch a stale view.
      const n = Math.max(0, Math.ceil((dist(s.me(), npcPos) - 2.0) / 0.375))
      for (let i = 0; i < n; i++) {
        const d = sub(npcPos, s.me()), up = norm(s.me())
        s.send(input(0, 1, norm(sub(d, mul(up, dot(d, up)))), 0x0001, s.seq++)); await sleep(50)
      }
    }
    s.lookAt(npcPos)
    await sleep(2 * ONE_WAY_MS + 100)
    buy = await s.call(0x0002, { npc: npcId, item: 'weapon.pulse', qty: 1 })
    console.log(`  buy attempt ${attempt}: ${dist(s.me(), npcPos).toFixed(2)} m from the quartermaster -> status ${buy?.status}`)
  }
  if (buy?.status !== 0) throw new Error(`buy refused: ${JSON.stringify(buy)}`)
  const eq = await s.call(0x0003, { slot: 'primary', item: 'weapon.pulse' })
  if (eq?.status !== 0) throw new Error(`equip refused: ${JSON.stringify(eq)}`)
}

/**
 * displayed returns where a CONFORMING client would be drawing entity `id`
 * right now: at `serverClock - interp_delay` (GDD "Lag compensation").
 *
 * The newest snapshot in hand describes server tick T and arrived one one-way
 * trip after it was made, so the server's clock now reads
 * `T + L + (time since it arrived)`. Subtract interp_delay and the render
 * point is `elapsed + L - interp_delay` PAST that snapshot — a small forward
 * extrapolation, which is what a real client does when its buffer runs dry
 * (client/src/net/interp.ts caps it at 150 ms).
 *
 * Aiming at the raw newest snapshot instead — the obvious thing, and what
 * this harness did first — sits about half a tick further back, which was
 * enough to leave the shot resolving between the two aim points and make the
 * whole measurement inconclusive.
 */
function displayed(s, id) {
  const e = s.ents.get(id)
  if (!e || !s.newest) return null
  const elapsedMs = Date.now() - s.newest.at
  const leadMs = elapsedMs + ONE_WAY_MS - INTERP_DELAY_MS
  const dt = Math.max(0, leadMs) / 1000
  return e.pos.map((x, i) => x + e.vel[i] * dt)
}

// aimAt returns the unit direction from s's eye to a body's centre of mass.
function aimAt(s, targetPos) {
  const up = norm(s.me())
  const eye = add(s.me(), mul(up, 1.7))
  const centre = add(targetPos, mul(norm(targetPos), 0.9))
  return norm(sub(centre, eye))
}

// walkRoute follows the solved camp route; `stopAt` ends it early once the
// walker is within that many metres of the camp.
async function walkRoute(s, route, stopAt) {
  const camp = route.waypoints[route.waypoints.length - 1]
  const t0 = Date.now()
  s.walking = true
  try {
  for (const [i, wp] of route.waypoints.entries()) {
    const legT0 = Date.now()
    let lastPos = s.me().slice(), lastMove = Date.now()
    while (Date.now() - legT0 < 20000 && Date.now() - t0 < 240000) {
      if (dist(camp, s.me()) <= stopAt) return
      if (dist(wp, s.me()) <= 6) break
      const d = sub(wp, s.me()), up = norm(s.me())
      s.look = norm(sub(d, mul(up, dot(d, up))))
      // Wedged on a scarp for a second? Jump at it, as t29's walker does —
      // without this, legs 5-11 burned their whole 20 s each.
      if (dist(s.me(), lastPos) > 0.5) { lastPos = s.me().slice(); lastMove = Date.now() }
      const mask = Date.now() - lastMove > 1000 ? 0x0003 : 0x0001 // sprint (+jump when stuck)
      s.send(input(0, 1, s.look, mask, s.seq++)); await sleep(50)
    }
    if (dist(wp, s.me()) > 6) console.log(`${stamp()}   ${s.name}: leg ${i} timed out ${dist(wp, s.me()).toFixed(1)} m short`)
  }
  // Stop explicitly: the server holds the last input until a new one lands,
  // so a walker that just goes quiet sprints on until the next heartbeat —
  // 7 m past its mark, which put the shooter inside grunt aggro.
  } finally { s.walking = false; s.hold() }
}

const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok]); console.log(`${stamp()} ${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }


/**
 * shoot fires one round at `aim` and reports what the server did with it.
 * `fired` separates a shot the server dropped outright (cadence, empty
 * magazine, no history) from one it resolved and missed — without it a zero
 * reads as "rewind is broken" when the round was never spent.
 */
async function shoot(aim, victimId) {
  const mark = bait.events.length
  // The (fractional) server tick this client is drawing right now — what
  // displayed() extrapolated to — and so the tick a correct rewind lands on.
  const displayTick = shooter.newest.tick + (Date.now() - shooter.newest.at + ONE_WAY_MS - INTERP_DELAY_MS) / 50
  shooter.look = aimAt(shooter, aim)
  shooter.send(fire(shooter.seq++, shooter.look))
  await sleep(SHOT_SPACING_MS)
  const seen = bait.events.slice(mark)
  const hit = seen.find(e => e.ev === 3 && e.id === victimId && e.shooter === shooter.myId)
  const other = seen.find(e => e.ev === 3 && e.id !== victimId && e.shooter === shooter.myId)
  const fe = seen.find(e => e.ev === 2 && e.id === shooter.myId && e.ray)
  return {
    displayTick,
    rewoundTick: hit && fe ? rewoundTick(fe.ray, hit.point, victimId) : null,
    other: other?.id,
    fired: seen.some(e => e.ev === 2 && e.id === shooter.myId),
    hit: !!hit,
  }
}

/**
 * rewoundTick names the tick the server ACTUALLY resolved a hit against: the
 * one whose capsule (bait's per-tick record, near-live) the server's own
 * broadcast ray first enters at exactly the reported hit point. A hit/miss
 * count cannot see a rewind that is short by a tick or two (0.4 m at 4 m/s
 * still clips a 0.35 m capsule) — this can, to the tick.
 */
const HITBOX_HEIGHT = 1.8 // items.json entity_defs npc hitbox height
function rewoundTick(ray, point, victimId) {
  const segDist = (p, a, b) => { const ab = sub(b, a), t = Math.max(0, Math.min(1, dot(sub(p, a), ab) / dot(ab, ab))); return dist(p, add(a, mul(ab, t))) }
  let best = null
  for (const [tk, m] of bait.byTick) {
    const feet = m.get(victimId); if (!feet) continue
    const head = add(feet, mul(norm(feet), HITBOX_HEIGHT))
    const f = (t) => segDist(add(ray.o, mul(ray.d, t)), feet, head) - HITBOX_RADIUS
    // Closest approach by scan, then bisect back to the entry.
    let tMin = 0, fMin = Infinity
    for (let t = 0; t < 60; t += 0.02) { const v = f(t); if (v < fMin) { fMin = v; tMin = t } }
    if (fMin > 0) continue
    let lo = 0, hi = tMin
    for (let k = 0; k < 40; k++) { const mid = (lo + hi) / 2; if (f(mid) > 0) lo = mid; else hi = mid }
    const err = dist(add(ray.o, mul(ray.d, hi)), point)
    if (!best || err < best.err) best = { tick: tk, err }
  }
  return best
}

// --- the camp frame (server/internal/defs/zone.go worldTransform) -----------
const field = loadField(JSON.parse(readFileSync(new URL('./out/world-seed1337.json', import.meta.url), 'utf8')))
const campZone = JSON.parse(readFileSync(new URL('../server/data/zones/camp.json', import.meta.url), 'utf8'))
const campUp = norm(campZone.origin_dir)
const campNorth = norm(sub([0, 0, 1], mul(campUp, campUp[2])))
const campEast = cross(campUp, campNorth)
const campOrigin = mul(campUp, sampleRadius(field, campUp))
const campPoint = (x, z) => { const d = norm(add(campOrigin, add(mul(campEast, x), mul(campNorth, z)))); return mul(d, sampleRadius(field, d)) }
const BAIT_STAND = campPoint(-22, 0)
const SHOOTER_STAND = campPoint(-26, -14)

// --- setup -----------------------------------------------------------------
const SA_PORT = Number(process.env.SA_PORT ?? 18080)
const proxy = spawn('node', [path.join(root, 'test/lib/proxy.mjs'), '18082', '127.0.0.1', String(SA_PORT), String(ONE_WAY_MS)], { stdio: 'ignore' })
process.on('exit', () => proxy.kill()) // the ceiling exits too; an orphan holds :18082 with ITS delay
await sleep(400)
// A proxy left by a killed run still listens on :18082 with whatever delay
// it was given, and this one dies on EADDRINUSE without a word: every
// number below would then be measured at the wrong latency.
if (proxy.exitCode !== null) throw new Error('latency proxy exited at start — is :18082 held by an orphan from an earlier run?')
const route = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8'))
const camp = route.waypoints[route.waypoints.length - 1]
const wp12 = route.waypoints[12] // the last waypoint outside the gate

const shooter = await session('lagshooter', 18082, ONE_WAY_MS)
const bait = await session('lagbait', SA_PORT)
console.log(`${stamp()} shooter id=${shooter.myId} (RTT ${2 * ONE_WAY_MS} ms), bait id=${bait.myId} (direct)`)
await armed(shooter)
console.log(`${stamp()} shooter armed with the pulse rifle`)

// Both walk the solved route to 34 m from the camp — outside aggro of
// anything that can see them through the gate. The shooter goes straight to
// its stand from there, NOT via wp12: wp12 is 14 m from the gate grunts and
// in their sight, and a grunt that picks the shooter up there follows it to
// the stand (the first run of this staging did exactly that).
await Promise.all([walkRoute(bait, route, 34), walkRoute(shooter, route, 34)])
await shooter.walkTo(SHOOTER_STAND, 1.0, 200)
console.log(`${stamp()} shooter at its stand, ${dist(shooter.me(), SHOOTER_STAND).toFixed(1)} m off`)

const gruntIds = [...bait.spawns].filter(([, v]) => v.type === 3 && v.def === 'npc.grunt').map(([k]) => k)
  .filter(id => bait.ents.get(id) && dist(bait.ents.get(id).pos, camp) < 30)
if (gruntIds.length === 0) throw new Error('no camp grunts in the spawn set')
const posts = new Map(gruntIds.map(id => [id, bait.ents.get(id).pos.slice()]))

await bait.walkTo(wp12, 2.0, 200)
await bait.walkTo(BAIT_STAND, 1.0, 200)
console.log(`${stamp()} bait at the gate, ${dist(bait.me(), BAIT_STAND).toFixed(1)} m off; waiting for a grunt`)
// The two gate grunts see the bait through the gap and run at it, 18 m at
// 4 m/s, west — side-on to the shooter. The bait stands; the run is the window.
const answered = await bait.wait(() => gruntIds.find(id => {
  const e = bait.ents.get(id)
  return e && Math.hypot(...e.vel) > 2 && dist(e.pos, bait.me()) < dist(e.pos, shooter.me())
}), 20000)
check('a gate grunt runs at the bait', !!answered, answered ? `grunt ${answered}` : 'none within 20 s')

/**
 * qualifying finds a running grunt whose capsule as this client SEES it and
 * as a server without rewind would test it are further apart across the ray
 * than the hitbox plus the cone at that range: a shot at it can land ONLY if
 * the server rewound to what the shooter saw. "Without rewind" is the grunt
 * where the shot ARRIVES, one one-way trip from now: live + vel·L. (The first
 * draft measured against live-now, which is only interp_delay away from what
 * the client sees and read 0.1–0.3 m — never a qualifying gap.)
 */
const coneAt = (range) => range * Math.tan(SPREAD_BASE_DEG * Math.PI / 180)
function qualifying() {
  let best = null
  const eye = add(shooter.me(), mul(norm(shooter.me()), 1.7))
  for (const id of gruntIds) {
    const live = bait.ents.get(id), seen = shooter.ents.get(id)
    if (!seen || !live || seen.health === 0 || live.health === 0) continue
    // Chasing the bait, not the shooter: nearer the bait, and moving.
    if (dist(live.pos, bait.me()) >= dist(live.pos, shooter.me())) continue
    if (Math.hypot(...live.vel) < 2) continue
    const stale = displayed(shooter, id)
    if (!stale) continue
    const arrive = add(live.pos, mul(live.vel, ONE_WAY_MS / 1000))
    const ray = norm(sub(stale, eye)), d = sub(arrive, stale)
    const lateral = Math.hypot(...sub(d, mul(ray, dot(d, ray))))
    const need = HITBOX_RADIUS + coneAt(dist(eye, live.pos)) + AIM_MARGIN
    if (lateral <= need) continue
    if (!best || lateral - need > best.margin) best = { id, off: lateral, need, margin: lateral - need, stale: stale.slice(), live: arrive }
  }
  return best
}

function gruntTable() {
  const eye = add(shooter.me(), mul(norm(shooter.me()), 1.7))
  for (const id of gruntIds) {
    const live = bait.ents.get(id), stale = displayed(shooter, id)
    if (!live || !stale) { console.log(`    grunt ${id}: not in view`); continue }
    const ray = norm(sub(stale, eye)), d = sub(add(live.pos, mul(live.vel, ONE_WAY_MS / 1000)), stale)
    console.log(`    grunt ${id}: hp ${live.health}, ${dist(live.pos, bait.me()).toFixed(1)} m from bait, ${dist(live.pos, shooter.me()).toFixed(1)} from shooter,` +
                ` ${Math.hypot(...live.vel).toFixed(1)} m/s, across-ray offset ${Math.hypot(...sub(d, mul(ray, dot(d, ray)))).toFixed(2)} m`)
  }
}

const tickRows = []
let hits = 0, shots = 0
while (answered && shots < SHOTS && hits < HITS_NEEDED) {
  const t = await shooter.wait(qualifying, 3000)
  if (!t) { console.log(`${stamp()}   no qualifying grunt for 3 s; bait hp ${bait.ents.get(bait.myId)?.health}`); gruntTable(); break }
  const r = await shoot(t.stale, t.id)
  shots++
  if (r.hit) hits++
  let where = ''
  if (r.rewoundTick) {
    tickRows.push(r.rewoundTick.tick - r.displayTick)
    where += `  [server resolved tick ${r.rewoundTick.tick} (entry ${r.rewoundTick.err.toFixed(3)} m off the hit point), client drew ${r.displayTick.toFixed(1)}]`
  }
  console.log(`${stamp()}   stale shot ${shots}: grunt ${t.id} seen-to-arrival offset ${t.off.toFixed(2)} m across the ray (needs > ${t.need.toFixed(2)}),` +
              ` range ${dist(shooter.me(), t.live).toFixed(1)} m -> ${r.fired ? (r.hit ? 'HIT' : r.other ? `hit grunt ${r.other} instead` : 'miss') : 'NOT FIRED'}${where}`)
}

check(`C14: shots aimed where the client SEES a moving target register (${HITS_NEEDED} of at most ${SHOTS})`,
      hits >= HITS_NEEDED, `${hits}/${shots}`)
// Correct rewind lands in (-1, 0] of the drawn tick (the server rewinds from
// the last COMPLETED tick); one short by interp_delay lands in (1, 2] — read
// +1.2..+1.9 against a server built with the pre-fix rule, which still scored
// 3/3 hits, because 0.4 m of error still clips a 0.35 m capsule.
check('every hit resolved at the tick the client drew (-1.5 < server - drawn < 1)',
      tickRows.length > 0 && tickRows.every(d => d > -1.5 && d < 1),
      tickRows.map(d => (d >= 0 ? '+' : '') + d.toFixed(1)).join(' '))

// Diagnosis, on a failure only: does a shot aimed where a NON-rewinding
// server would test land instead? Each costs a grunt hit points.
if (hits < HITS_NEEDED) {
  for (let i = 0; i < 2; i++) {
    const t = qualifying()
    if (!t) break
    const r = await shoot(t.live, t.id)
    console.log(`  diagnosis, ARRIVAL-aimed shot ${i + 1}: offset ${t.off.toFixed(2)} m -> ${r.fired ? (r.hit ? 'HIT' : 'miss') : 'NOT FIRED'}`)
  }
  console.log('  An arrival-aimed hit where stale-aimed shots missed means rewind is pointed at the\n' +
              '  wrong instant: check rewindTicks (server/internal/server/client.go) still carries\n' +
              '  staleness, RTT/2 and interp_delay (GDD "Lag compensation").')
}

clearInterval(shooter.heartbeat); clearInterval(bait.heartbeat)
clearTimeout(ceiling)
proxy.kill()
shooter.ws.close(); bait.ws.close()
const bad = checks.filter(([, ok]) => !ok).length
console.log(`\n${stamp()} OVERALL: ${bad ? `FAIL (${bad}/${checks.length})` : `PASS (${checks.length} checks)`}`)
process.exit(bad ? 1 : 0)
