#!/usr/bin/env node
/**
 * C14, re-pointed at a MOVING target — the test that can tell a server with
 * lag compensation from one without.
 *
 * The Phase 2 criterion fires at a target dummy. Dummies do not move, so the
 * rewound position equals the live one and rewind is a no-op: `t18` measures
 * 20/20 whether the server rewinds or not. Its own note claims "rewind is what
 * makes the first number 20 and not 12", and nothing in it tests that.
 *
 * This does, by firing at a camp grunt that is chasing a second player and
 * splitting the shots into two volleys aimed at two different points:
 *
 *   STALE aim — at the grunt where THIS client currently sees it. This is the
 *               criterion: it is what a player does, and lag compensation
 *               exists to make it land.
 *   LIVE  aim — at the grunt where it actually is right now, read from a
 *               second, undelayed session. No real client can know this; it is
 *               here to say WHERE the server resolved, when the first volley
 *               misses.
 *
 * Every shot records the stale-to-live offset, and a shot is only fired once
 * that offset exceeds the grunt's own hitbox radius (0.35 m) — otherwise both
 * aim points name the same capsule and the volleys measure nothing. A control
 * volley fires first, because a zero in both volleys says nothing about rewind
 * if a shot from that spot could never land.
 *
 * Before the fix, at 300 ms RTT and 31 m: control 3/3, stale 0/8, live 8/8 —
 * the server was resolving at the target's present position.
 *
 * Deviations from the criterion's letter, and why:
 *
 *   - 300 ms RTT, not 100. The two aim points have to be separated by more
 *     than a whole body or both of them hit the same capsule and the run
 *     proves nothing. A 4.0 m/s grunt covers 0.2 m per 100 ms of RTT against
 *     a capsule 0.7 m across, so 100 ms RTT is unmeasurable by construction
 *     and 200 ms was still inconclusive in practice: the resolved hit points
 *     landed BETWEEN the aim points and both volleys scored alike. 300 ms
 *     gives ~1.2 m, comfortably more than the body.
 *   - 8 shots per volley, not 20. The pulse does 25 and a grunt has 60, so
 *     every third hit is a corpse and a 20 s respawn. `t18` keeps the 20-shot
 *     volume run against the dummies; this measures the property they cannot.
 *
 * Run: node test/t21-lagcomp-moving.mjs   (needs `make up`)
 */
import { spawn } from 'node:child_process'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

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
const ONE_WAY_MS = 150       // injected per direction; 300 ms RTT
const INTERP_DELAY_MS = 100  // GDD "Lag compensation" interp_delay; the client owes this
const SHOTS = 8              // per volley
const SHOT_SPACING_MS = 520  // spread_per_shot 0.35 deg vs spread_decay 3.0/s
const AGGRO_RADIUS = 22      // npcs.json npc.grunt; the shooter must stay out
const KITE_STANDOFF = 15     // bait to the grunt it baits: inside that radius
const SHOOT_RANGE = 24       // shooter to the shuttle: outside aggro, inside spread
const MAX_SHOT_RANGE = 27    // past this, spread_base alone is wider than the hitbox

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

async function session(name, port) {
  const s = { myId: 0, ents: new Map(), spawns: new Map(), results: [], events: [], defs: null, seq: 1, snaps: 0, closed: null }
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
    }
    else if (t === 0x000f) s.results.push({ seq: pv.getUint16(0,true), op: pv.getUint16(2,true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    else if (t === 0x0007) {
      // A `hit` carries the shooter in its first data word; without it a shot
      // from this harness is indistinguishable from a grunt hitting the bait.
      const e = { id: pv.getUint32(0,true), ev: pv.getUint16(4,true), at: Date.now() }
      // Read the body only if it is actually there: a short `hit` is a server
      // bug (one shipped), and a harness that dies on it reports nothing at
      // all about the run it was measuring.
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
  s.walkTo = async (dst, stop, steps = 500) => {
    s.walking = true
    try {
    for (let i = 0; i < steps; i++) {
      const d = sub(dst, s.me()); if (Math.hypot(...d) <= stop) break
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
    await s.walkTo(npcPos, 2.0, 200)
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
  for (const wp of route.waypoints) {
    const legT0 = Date.now()
    while (Date.now() - legT0 < 20000 && Date.now() - t0 < 240000) {
      if (dist(camp, s.me()) <= stopAt) return
      if (dist(wp, s.me()) <= 6) break
      const d = sub(wp, s.me()), up = norm(s.me())
      s.look = norm(sub(d, mul(up, dot(d, up))))
      s.send(input(0, 1, s.look, 0x0001, s.seq++)); await sleep(50)
    }
  }
  } finally { s.walking = false }
}

const resolved = []
const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok]); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }

// --- setup -----------------------------------------------------------------
const proxy = spawn('node', [path.join(root, 'test/lib/proxy.mjs'), '18082', '127.0.0.1', '18080', String(ONE_WAY_MS)], { stdio: 'ignore' })
await sleep(400)
const route = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8'))
const camp = route.waypoints[route.waypoints.length - 1]

const shooter = await session('lagshooter', 18082)
const bait = await session('lagbait', 18080)
console.log(`shooter id=${shooter.myId} (RTT ${2 * ONE_WAY_MS} ms), bait id=${bait.myId} (direct)`)
await armed(shooter)
console.log('shooter armed with the pulse rifle')

// Both stop ON the solved route, at different distances: only the route is
// known to be walkable, and the first version of this script lost 20 s and
// both marks trying to step the shooter sideways across a scarp.
//
// The shooter stops far enough back to stay outside grunt aggro (22 m,
// npcs.json), because a grunt targets the NEAREST player in radius
// (ai/brain.go selectTarget) — pull it onto the shooter and its motion turns
// radial, which is exactly the motion rewind cannot be measured against.
await Promise.all([walkRoute(bait, route, 29), walkRoute(shooter, route, 60)])
// Stopping on the route is not enough: the route passes ~28 m from the nearest
// post and aggro_radius is 22, so the camp simply ignores a bait parked there.
// Close on the nearest grunt until it notices, then shuttle around a point
// KITE_STANDOFF out from it.
const gruntIds = [...bait.spawns].filter(([, v]) => v.type === 3 && v.def === 'npc.grunt').map(([k]) => k)
if (gruntIds.length === 0) throw new Error('no camp grunts in the spawn set')
const nearest = gruntIds.map(id => ({ id, pos: bait.ents.get(id).pos }))
  .sort((a, b) => dist(a.pos, bait.me()) - dist(b.pos, bait.me()))[0]
const centre = add(nearest.pos, mul(norm(sub(bait.me(), nearest.pos)), KITE_STANDOFF))
await bait.walkTo(centre, 3.0, 200)

// The shuttle runs perpendicular to the SHOOTER's line of sight, so a grunt
// chasing the bait crosses that line instead of running along it. Motion along
// it is invisible to this test: a capsule hit is indifferent to where along the
// ray the target sits.
// Pull the shooter to a fixed range from the shuttle. Range is not cosmetic:
// the spread cone is an angle, so at 46 m spread_base alone is 0.48 m against
// a 0.35 m capsule and even a perfectly aimed shot mostly misses. At 30 m it
// is 0.31 m and aim decides the outcome, which is what this measures.
const shooterMark = add(centre, mul(norm(sub(shooter.me(), centre)), SHOOT_RANGE))
await shooter.walkTo(shooterMark, 3.0, 200)

const losDir = norm(sub(centre, shooter.me()))
const axis = norm(cross(norm(centre), losDir))
const left = add(centre, mul(axis, 10))
const right = add(centre, mul(axis, -10))
console.log(`bait shuttling 20 m across the sightline, ${dist(centre, nearest.pos).toFixed(0)} m from grunt ${nearest.id}; ` +
            `shooter ${dist(shooter.me(), centre).toFixed(0)} m from the shuttle, ${dist(camp, shooter.me()).toFixed(0)} m from camp`)

let kiting = true
const kiteLoop = (async () => {
  while (kiting) {
    await bait.walkTo(right, 2.5, 120)
    if (!kiting) break
    await bait.walkTo(left, 2.5, 120)
  }
})()

console.log(`camp grunts: ${gruntIds.length}`)

function gruntTable(label) {
  console.log(`${label} | shooter snaps=${shooter.snaps} closed=${shooter.closed} | bait snaps=${bait.snaps} closed=${bait.closed}`)
  for (const id of gruntIds) {
    const st = shooter.ents.get(id), lv = bait.ents.get(id)
    console.log(`  grunt ${id}: shooter ${st ? st.pos.map(x => x.toFixed(1)).join(',') : 'absent'} hp=${st?.health}` +
                ` | bait ${lv ? lv.pos.map(x => x.toFixed(1)).join(',') : 'absent'} hp=${lv?.health}` +
                ` | offset ${st && lv ? dist(st.pos, lv.pos).toFixed(2) : 'n/a'} m` +
                ` | from shooter ${lv ? dist(shooter.me(), lv.pos).toFixed(1) : 'n/a'} m`)
  }
}
gruntTable('before the volleys')
await sleep(3000)
gruntTable('3 s later')

/**
 * usable finds a grunt that both sessions can see alive, and whose stale and
 * live positions are further apart than its own hitbox — the condition that
 * makes the two aim points name different capsules.
 */
function usable() {
  let best = null
  const eye = add(shooter.me(), mul(norm(shooter.me()), 1.7))
  for (const id of gruntIds) {
    const live = bait.ents.get(id), seen = shooter.ents.get(id)
    if (!seen || !live || seen.health === 0 || live.health === 0) continue
    const shown = displayed(shooter, id)
    if (!shown) continue
    const stale = { pos: shown }
    const range = dist(shooter.me(), live.pos)
    // Too close and the grunt re-targets onto the shooter, turning its motion
    // radial. Too far and spread_base alone exceeds the hitbox: the cone is an
    // angle, 0.6 deg is 0.35 m at 33 m, and past that the weapon's own
    // dispersion decides the shot rather than the aim. t18 makes the same
    // trade the other way and reads 19/20 for it.
    if (range < AGGRO_RADIUS || range > MAX_SHOT_RANGE) continue
    // Only the offset ACROSS the ray moves a shot off the capsule; offset
    // along it slides the aim point up and down a line that still intersects.
    const ray = norm(sub(stale.pos, eye))
    const d = sub(live.pos, stale.pos)
    const lateral = Math.hypot(...sub(d, mul(ray, dot(d, ray))))
    if (lateral <= HITBOX_RADIUS) continue
    if (!best || lateral > best.off) {
      best = { id, off: lateral, stale: stale.pos.slice(), live: live.pos.slice() }
    }
  }
  return best
}

/**
 * shoot fires one round at `aim` and reports what the server did with it.
 * `fired` separates a shot the server dropped outright (cadence, empty
 * magazine, no history) from one it resolved and missed — without it a zero
 * reads as "rewind is broken" when the round was never spent.
 */
async function shoot(aim, victimId) {
  const mark = bait.events.length
  shooter.look = aimAt(shooter, aim)
  shooter.send(fire(shooter.seq++, shooter.look))
  await sleep(SHOT_SPACING_MS)
  const seen = bait.events.slice(mark)
  const hit = seen.find(e => e.ev === 3 && e.id === victimId && e.shooter === shooter.myId)
  return {
    fired: seen.some(e => e.ev === 2 && e.id === shooter.myId),
    hit: !!hit,
    point: hit?.point,
  }
}

// resolvedAt reports how far the server's hit point sits from each candidate
// body axis. It is the one direct read of where the rewind landed: much
// nearer the stale axis means the shooter's own frame was reconstructed,
// nearer live means the present was, and halfway means the rewind is short.
function resolvedAt(point, stale, live) {
  const axis = (p, base) => {
    const up = norm(base)
    const d = sub(p, base)
    return Math.hypot(...sub(d, mul(up, dot(d, up)))) // distance from the capsule's axis
  }
  return { toStale: axis(point, stale), toLive: axis(point, live) }
}

/** volley fires n shots at `mode` and returns [hits, offsets]. */
async function volley(mode, n) {
  let hits = 0, fired = 0
  const offsets = []
  for (let i = 0; i < n; i++) {
    const t = await shooter.wait(usable, 60000)
    if (!t) { console.log(`  shot ${i + 1}: no moving grunt within 60 s — giving up`); break }
    const r = await shoot(mode === 'stale' ? t.stale : t.live, t.id)
    offsets.push(t.off)
    if (r.hit) hits++
    if (r.fired) fired++
    let where = ''
    if (r.point) {
      const a = resolvedAt(r.point, t.stale, t.live)
      resolved.push(a)
      where = `  [resolved ${a.toStale.toFixed(2)} m from the stale axis, ${a.toLive.toFixed(2)} m from live]`
    }
    console.log(`  ${mode} shot ${String(i + 1).padStart(2)}: offset ${t.off.toFixed(2)} m, range ${dist(shooter.me(), t.live).toFixed(1)} m` +
                ` -> ${r.fired ? (r.hit ? 'HIT' : 'miss') : 'NOT FIRED'}${where}`)
  }
  console.log(`  ${mode}: ${hits} hits, ${fired}/${offsets.length} shots accepted by the server`)
  return [hits, offsets]
}

// --- control: can this shooter hit anything from here? ---------------------
// A zero in both volleys is only evidence about rewind if a shot from this
// position lands at all. Range, spread, terrain and the camp walls all sit
// between the two, and each of them fails the same silent way.
console.log('\n=== control: shots at a near-stationary grunt, aimed live ===')
let controlHits = 0
for (let i = 0; i < 3; i++) {
  const still = gruntIds
    .map(id => ({ id, st: shooter.ents.get(id), lv: bait.ents.get(id) }))
    .filter(g => g.st && g.lv && g.lv.health > 0)
    .sort((a, b) => dist(a.st.pos, a.lv.pos) - dist(b.st.pos, b.lv.pos))[0]
  if (!still) break
  const r = await shoot(still.lv.pos, still.id)
  if (r.hit) controlHits++
  console.log(`  control shot ${i + 1}: offset ${dist(still.st.pos, still.lv.pos).toFixed(2)} m,` +
              ` range ${dist(shooter.me(), still.lv.pos).toFixed(1)} m -> ${r.fired ? (r.hit ? 'HIT' : 'miss') : 'NOT FIRED'}`)
}
console.log(`  control: ${controlHits}/3`)

// --- the two volleys -------------------------------------------------------
console.log('\n=== aiming where THIS client sees the grunt (stale) ===')
const [staleHits, staleOffsets] = await volley('stale', SHOTS)

console.log('\n=== aiming where the grunt actually is (live) ===')
const [liveHits, liveOffsets] = await volley('live', SHOTS)

kiting = false
await kiteLoop.catch(() => {})
clearInterval(shooter.heartbeat); clearInterval(bait.heartbeat)

const offsets = [...staleOffsets, ...liveOffsets]
const meanOffset = offsets.reduce((a, b) => a + b, 0) / (offsets.length || 1)
console.log(`\nstale-to-live offset ACROSS the ray: mean ${meanOffset.toFixed(2)} m, min ${Math.min(...offsets).toFixed(2)} m over ${offsets.length} shots (hitbox radius ${HITBOX_RADIUS} m)`)

// Run validity first. A red criterion is only worth reading if the run could
// have gone green: something had to be hittable from here, and the two aim
// points had to name different capsules.
check('control: a shot from this position lands at all', controlHits > 0, `${controlHits}/3`)
check('the two aim points were further apart ACROSS the ray than the hitbox',
      offsets.length > 0 && Math.min(...offsets) > HITBOX_RADIUS,
      `min ${offsets.length ? Math.min(...offsets).toFixed(2) : 'n/a'} m`)

// The criterion itself: a player shoots what their screen shows them.
check(`C14: ${SHOTS} shots aimed where the client SEES the target register`,
      staleHits === SHOTS && staleOffsets.length === SHOTS, `${staleHits}/${SHOTS}`)

if (resolved.length) {
  const mean = (f) => resolved.reduce((a, r) => a + f(r), 0) / resolved.length
  console.log(`\nwhere the server resolved, over ${resolved.length} landed shots:` +
              ` ${mean(r => r.toStale).toFixed(2)} m from the stale axis,` +
              ` ${mean(r => r.toLive).toFixed(2)} m from the live axis` +
              ` (hitbox radius ${HITBOX_RADIUS} m)`)
}
console.log(`\ndiagnosis: aiming at the target's PRESENT position instead scored ${liveHits}/${liveOffsets.length}.`)
if (staleHits > liveHits) {
  console.log(
    '  Rewind is pointed correctly: what the client saw lands, and the position no client\n' +
    '  can know does not.')
} else if (liveHits > staleHits) {
  console.log(
    '  Rewind is pointed at the wrong instant — the server is resolving nearer to the\n' +
    '  target\'s present than to the frame the client fired at. Check that rewindTicks\n' +
    '  (server/internal/server/client.go) still carries all three terms: staleness, RTT/2\n' +
    '  and interp_delay (GDD "Lag compensation").')
} else {
  console.log(
    '  Both aim points scored the same, so this run says nothing about rewind. The two\n' +
    '  were far enough apart across the ray, so suspect the weapon: at these ranges\n' +
    '  spread_base is a large fraction of the hitbox and dispersion, not aim, is\n' +
    '  deciding the shots.')
}

proxy.kill()
shooter.ws.close(); bait.ws.close()
const bad = checks.filter(([, ok]) => !ok).length
console.log(`\nOVERALL: ${bad ? `FAIL (${bad}/${checks.length})` : `PASS (${checks.length} checks)`}`)
process.exit(bad ? 1 : 0)
