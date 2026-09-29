#!/usr/bin/env node
/**
 * Phase 3 acceptance — C19..C25 (docs/ROADMAP.md).
 *
 * The rule-level halves of C20/C21/C23 are covered deterministically in Go
 * (internal/ai/melee_test.go, internal/sim/projectile_test.go, loot_test.go).
 * What those cannot see is the behaviour through the wire with a real client
 * attached, which is what this measures.
 *
 * Run: node test/t17-phase3-qa.mjs   (needs `make up`)
 */
import { readFileSync, writeFileSync } from 'node:fs'

const enc = new TextEncoder(), dec = new TextDecoder(), u8 = (n) => new Uint8Array(n)
const frame = (t, b) => { const o = u8(2 + b.length); new DataView(o.buffer).setUint16(0, t, true); o.set(b, 2); return o }
function hello(name, tok) {
  const n = enc.encode(name), t = enc.encode(tok)
  const b = u8(2 + 4 + n.length + 4 + t.length), d = new DataView(b.buffer)
  d.setUint16(0, 2, true); d.setUint32(2, n.length, true); b.set(n, 6)
  d.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length); return frame(1, b)
}
function input(mx, my, lk, mask, seq) {
  const b = u8(24), d = new DataView(b.buffer)
  d.setFloat32(0, mx, true); d.setFloat32(4, my, true)
  d.setFloat32(8, lk[0], true); d.setFloat32(12, lk[1], true); d.setFloat32(16, lk[2], true)
  d.setUint16(20, mask, true); d.setUint16(22, seq, true); return frame(3, b)
}
const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const dist = (a, b) => Math.hypot(...sub(a, b))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

function connect(name, token) {
  const ws = new WebSocket('ws://127.0.0.1:18080/ws')
  ws.binaryType = 'arraybuffer'
  const c = { ws, id: 0, ents: new Map(), spawns: new Map(), events: [], bytes: 0, snaps: 0, seq: 1, despawns: [], selfDeaths: 0 }
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  ws.addEventListener('message', (ev) => {
    c.bytes += ev.data.byteLength
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const pv = new DataView(ev.data, 2), p = new Uint8Array(ev.data, 2)
    if (t === 2) c.id = pv.getUint32(8, true)
    else if (t === 5) c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), def: dec.decode(p.subarray(10)) })
    else if (t === 6) c.despawns.push(pv.getUint32(0, true))
    else if (t === 4) {
      c.snaps++
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) {
        const o = 8 + i * 54
        c.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
          health: pv.getUint16(o + 50, true), flags: pv.getUint8(o + 52), tick: pv.getUint32(0, true),
        })
      }
    } else if (t === 7) {
      const id = pv.getUint32(0, true), evId = pv.getUint16(4, true)
      c.events.push({ id, ev: evId, at: Date.now() })
      // Latch our own deaths. Sampling the dead flag at one instant misses a
      // death that happens between the fight loop ending and the check
      // running — which reported died=false for a player sitting at the spawn
      // point with full health, i.e. one who had plainly just died.
      if (evId === 4 && id === c.id) c.selfDeaths++
    }
  })
  c.send = (mx, my, lk, mask = 0) => ws.send(input(mx, my, lk, mask, c.seq++))
  // Heartbeat. The server drops a connection silent for 10 s (PROTOCOL), and
  // this harness has stretches — the leash trace waits up to 60 s — where it
  // sends no input at all. Without this the socket dies mid-run and every
  // later check reads stale entity data: the first version reported 0 hit
  // events, a 0.0 s respawn and 0 Hz, all of which were just a dead socket.
  // A ping is the right keepalive here: an input would fight the movement
  // loops for "latest input wins".
  c.beat = setInterval(() => {
    if (ws.readyState !== 1) return
    const b = u8(6), d = new DataView(b.buffer)
    d.setUint16(0, 8, true); d.setUint32(2, Date.now() >>> 0, true)
    ws.send(b)
  }, 2000)
  return c
}
const wait = async (f, ms = 8000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = f(); if (v) return v; await sleep(25) } return null }
const tangentTo = (from, to) => { const u = norm(from); const d = sub(to, from); const p = d[0]*u[0]+d[1]*u[1]+d[2]*u[2]; return norm(sub(d, u.map(x => x*p))) }

async function walkRoute(c, waypoints, stopAt, stopFn) {
  for (const wp of waypoints) {
    const legT0 = Date.now()
    while (Date.now() - legT0 < 25000) {
      const self = c.ents.get(c.id)
      if (!self) { await sleep(50); continue }
      const me = self.pos
      if (stopFn && stopFn()) return
      if (dist(wp, me) <= 6) break
      c.send(0, 1, tangentTo(me, wp), 0x0001)
      await sleep(50)
    }
    if (stopAt && stopAt()) return
  }
}

const results = []
const record = (id, name, ok, detail) => { results.push({ id, name, ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}\n     ${detail}`) }

const route = JSON.parse(readFileSync(new URL('./out/route-camp.json', import.meta.url), 'utf8')).waypoints
const A = connect('qa-a', 'qa-a-' + Date.now())
// Wait for our OWN row, not just for entities to exist: ents.size climbs as
// soon as the world's NPCs arrive, which happens before the player is in a
// snapshot.
await wait(() => A.id && A.ents.get(A.id) && A.spawns.size > 3)

const campNPCs = [...A.spawns].filter(([, v]) => v.type === 3 && v.def !== 'npc.quartermaster').map(([id]) => id)
const grunts = campNPCs.filter((id) => A.spawns.get(id).def === 'npc.grunt')
const gunners = campNPCs.filter((id) => A.spawns.get(id).def === 'npc.gunner')

// One approach. Death returns the player to spawn on its own, so the leash can
// be observed afterwards without a second traversal — the retreat-and-return
// version stalled 74.5 m short and turned two product checks into measurements
// of a player standing outside aggro range.
const watch = grunts[0] ?? campNPCs[0]
const post = [...A.ents.get(watch).pos]

await walkRoute(A, route, null, () => dist(A.ents.get(A.id).pos, A.ents.get(watch).pos) < 9)
const reEntry = dist(A.ents.get(A.id).pos, A.ents.get(watch).pos)
record('C19a', 'NPC notices a player inside its aggro radius and pursues',
  reEntry < 25 && dist(A.ents.get(watch).pos, post) > 1.0,
  `player closed to ${reEntry.toFixed(1)} m; NPC moved ${dist(A.ents.get(watch).pos, post).toFixed(1)} m off its post`)

// ---- C20/C21: damage through the wire, at cadence -------------------------
const hpBefore = A.ents.get(A.id).health
A.events.length = 0
const standT0 = Date.now()
while (Date.now() - standT0 < 12000 && (A.ents.get(A.id).flags & 0x04) === 0) {
  const me = A.ents.get(A.id).pos
  A.send(0, 0, tangentTo(me, A.ents.get(watch).pos))
  await sleep(50)
}
const hits = A.events.filter((e) => e.ev === 3).length
const died = A.selfDeaths > 0
const hpAfter = A.ents.get(A.id).health
record('C20/21', 'camp NPCs damage a player who stands in range',
  hits > 0 && (hpAfter < hpBefore || died),
  `${hits} hit events, health ${hpBefore} -> ${hpAfter}${died ? ' (died)' : ''} at ${reEntry.toFixed(1)} m`)

const sawProjectile = [...A.spawns.values()].some((v) => v.type === 7)
record('C21b', 'gunner projectiles are entities the client receives',
  sawProjectile, `projectile spawns seen: ${sawProjectile}`)

// ---- C22: death and respawn ----------------------------------------------
const deadSeen = A.selfDeaths > 0 || died || (A.ents.get(A.id).flags & 0x04) !== 0
const respawnT0 = Date.now()
const alive = await wait(() => A.ents.get(A.id).health >= 100 && (A.ents.get(A.id).flags & 0x04) === 0, 30000)
const respawnMs = Date.now() - respawnT0
const spawnDist = dist(A.ents.get(A.id).pos, [0, 150.0002, 0])
record('C22', 'player dies and respawns at the spawn point with full health',
  deadSeen && !!alive && spawnDist < 5,
  `died=${deadSeen} respawned=${!!alive} after ${(respawnMs / 1000).toFixed(1)} s, ${spawnDist.toFixed(2)} m from spawn`)

// ---- C19b: the leash, now that the player is back at spawn ----------------
let gap = dist(A.ents.get(watch).pos, post)
for (let i = 0; i < 30 && gap > 2.5; i++) { await sleep(1000); gap = dist(A.ents.get(watch).pos, post) }
record('C19b', 'NPC leashes back to its post and stays on the surface',
  gap <= 2.5 && Math.hypot(...A.ents.get(watch).pos) > 100,
  `returned to ${gap.toFixed(2)} m of post (limit 2.5), |pos|=${Math.hypot(...A.ents.get(watch).pos).toFixed(1)} m`)

// ---- C24: two clients agree -----------------------------------------------
const B = connect('qa-b', 'qa-b-' + Date.now())
await wait(() => B.id && B.ents.get(B.id) && B.spawns.size > 3)
await sleep(1500)
let worst = 0
for (const id of campNPCs) {
  const a = A.ents.get(id), b = B.ents.get(id)
  if (a && b) worst = Math.max(worst, dist(a.pos, b.pos))
}
record('C24', 'two clients see the same NPC positions',
  worst < 2.0, `worst disagreement across ${campNPCs.length} camp NPCs: ${worst.toFixed(3)} m (limit 2.0)`)

// ---- C25: budget ----------------------------------------------------------
A.bytes = 0; A.snaps = 0
const bwT0 = Date.now()
await sleep(5000)
const secs = (Date.now() - bwT0) / 1000
const kbs = A.bytes / 1024 / secs
record('C25a', 'per-client snapshot bandwidth under 100 KB/s',
  kbs < 100, `${kbs.toFixed(1)} KB/s over ${secs.toFixed(1)} s, ${A.ents.size} entities (interest culling NOT wired)`)
const hz = A.snaps / secs
record('C25b', 'server holds 20 Hz with the camp live',
  hz > 19 && hz < 21, `${hz.toFixed(2)} Hz`)

clearInterval(A.beat); if (typeof B !== 'undefined') clearInterval(B.beat)
writeFileSync(new URL('./out/t17-phase3-qa.json', import.meta.url), JSON.stringify(results, null, 1))
const bad = results.filter((r) => !r.ok).length
console.log(`\nOVERALL: ${bad ? `FAIL (${bad}/${results.length})` : `PASS (${results.length} checks)`}`)
process.exit(bad ? 1 : 0)
