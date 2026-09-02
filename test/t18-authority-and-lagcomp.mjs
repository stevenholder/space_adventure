#!/usr/bin/env node
/**
 * C12 (server authority over currency), C14 (hit registration under latency)
 * and C18's snapshot budget — live, at the wire level.
 *
 * These three criteria were defined in Phase 2 and never verified live: the
 * brief that was supposed to record C11-C18 in docs/QA-STATUS.md was never
 * written, so a third of the C43 gate had no evidence behind it. The Go suite
 * covers the rules (TestBuyRefusalReasons, TestResolveShot, history bounds);
 * nothing walked a player up to a target and counted holes.
 *
 * Two facts about this world shape the script, and both were found by writing
 * it rather than by reading the criteria:
 *
 *   - The magazine is 30 rounds and `reload` is a routed stub that reports an
 *     empty magazine rather than fabricating ammo, so C14's 20 aimed + 20 wide
 *     shots cannot come from one player. Each half gets its own session.
 *   - A target has 100 health and the pulse does 25, so it DIES every fourth
 *     hit and is gone for 3 s. Twenty shots at "a static target" is five
 *     bursts of four with a respawn wait between, not a twenty-round burst.
 *
 * Run: node test/t18-authority-and-lagcomp.mjs   (needs `make up`)
 */
import { spawn } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
const norm = (v) => { const l = Math.hypot(...v); return v.map(x => x / l) }
const sub = (a, b) => [a[0]-b[0], a[1]-b[1], a[2]-b[2]]
const dot = (a, b) => a[0]*b[0] + a[1]*b[1] + a[2]*b[2]

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

/** One connected player, with the wire state the checks below read. */
async function session(name, port) {
  const s = {
    myId: 0, ents: new Map(), spawns: new Map(), results: [], events: [],
    defs: null, seq: 1, rowBytes: [],
  }
  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`); ws.binaryType = 'arraybuffer'
  s.ws = ws
  const token = `${name}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const p = new Uint8Array(ev.data, 2), pv = new DataView(ev.data, 2)
    if (t === 0x0002) s.myId = pv.getUint32(8, true)
    else if (t === 0x0010) s.defs = JSON.parse(dec.decode(p.subarray(4)))
    else if (t === 0x0005) s.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10)) })
    else if (t === 0x0004) {
      const n = pv.getUint16(6, true)
      // C18: the snapshot body is 8 bytes of header then n fixed-width rows.
      if (n > 0) s.rowBytes.push((p.length - 8) / n)
      for (let i = 0; i < n; i++) { const o = 8 + i * 54
        s.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o+4,true), pv.getFloat32(o+8,true), pv.getFloat32(o+12,true)],
          health: pv.getUint16(o+50,true), flags: pv.getUint8(o+52), pitchQ: pv.getInt8(o+53),
        }) }
    }
    else if (t === 0x000f) s.results.push({ seq: pv.getUint16(0,true), op: pv.getUint16(2,true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    else if (t === 0x0007) s.events.push({ id: pv.getUint32(0,true), ev: pv.getUint16(4,true), at: Date.now() })
  })
  s.wait = async (fn, ms = 8000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
  s.me = () => s.ents.get(s.myId).pos
  s.send = (f) => ws.send(f)
  /** Send `op` and wait for the cmd_result carrying that seq. */
  s.call = async (op, body) => {
    const q = s.seq++
    ws.send(cmd(q, op, body))
    return await s.wait(() => s.results.find(r => r.seq === q))
  }
  /** Walk toward `dst` until within `stop` m, or `steps` ticks run out. */
  s.walkTo = async (dst, stop, steps = 500) => {
    for (let i = 0; i < steps; i++) {
      const d = sub(dst, s.me()); if (Math.hypot(...d) <= stop) break
      const up = norm(s.me()); const lk = norm(sub(d, up.map(x => x * dot(d, up))))
      ws.send(input(0, 1, lk, 0x0001, s.seq++)); await sleep(50)
    }
    ws.send(input(0, 0, norm(sub(dst, s.me())), 0, s.seq++)); await sleep(150)
    return Math.hypot(...sub(dst, s.me()))
  }
  await s.wait(() => s.myId && s.defs && s.ents.size > 1)
  return s
}

/** Buy and equip the pulse rifle. Returns the cmd_result of the purchase. */
async function armed(s) {
  const npcId = [...s.spawns].find(([, v]) => v.type === 3 && v.data === 'npc.quartermaster')?.[0]
  if (!npcId) throw new Error('no quartermaster in spawns')
  await s.walkTo(s.ents.get(npcId).pos, 2.4, 120)
  const buy = await s.call(0x0002, { npc: npcId, item: 'weapon.pulse', qty: 1 })
  await s.call(0x0003, { slot: 'primary', item: 'weapon.pulse' })
  return { npcId, buy }
}

/** Aim from the shooter's eye at a point `offset` m to the side of the target. */
function aimAt(s, tPos, offset) {
  const up = norm(s.me())
  const eye = s.me().map((x, i) => x + up[i] * 1.7)
  const centre = tPos.map((x, i) => x + norm(tPos)[i] * 0.9)
  if (!offset) return norm(sub(centre, eye))
  const fwd = norm(sub(centre, eye))
  const side = norm([fwd[1]*up[2]-fwd[2]*up[1], fwd[2]*up[0]-fwd[0]*up[2], fwd[0]*up[1]-fwd[1]*up[0]])
  return norm(sub(centre.map((x, i) => x + side[i] * offset), eye))
}

const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok, detail]); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }

// --- latency proxy: 50 ms per direction, 100 ms RTT (test/lib/proxy.mjs) ----
const proxy = spawn('node', [path.join(root, 'test/lib/proxy.mjs'), '18081', '127.0.0.1', '18080'], { stdio: 'ignore' })
process.on('exit', () => proxy.kill())
await sleep(400)

// === C12 — server authority over currency ==================================
console.log('\n=== C12 server authority over currency ===')
const a = await session('authority', 18081)
const { npcId, buy } = await armed(a)
const creditsAfterBuy = buy?.body?.credits
console.log(`bought the pulse: credits=${creditsAfterBuy}`)

const tooDear = await a.call(0x0002, { npc: npcId, item: 'weapon.pulse', qty: 10 }) // 2500 > 750
check('unaffordable purchase refused', tooDear != null && tooDear.status !== 0,
      `status=${tooDear?.status} body=${JSON.stringify(tooDear?.body)}`)

const forged = await a.call(0x00ff, { npc: npcId, item: 'weapon.pulse', grant: true })
check('forged opcode refused', forged != null && forged.status !== 0, `status=${forged?.status}`)

const malformed = await a.call(0x0002, { npc: npcId, item: 'weapon.pulse', qty: 1, free: true })
check('malformed body refused (unknown field)', malformed != null && malformed.status !== 0, `status=${malformed?.status}`)

const probe = await a.call(0x0002, { npc: npcId, item: 'ammo.cell', qty: 1 }) // costs exactly 1
check('credits untouched by the refused commands', probe?.status === 0 && probe.body.credits === creditsAfterBuy - 1,
      `credits ${creditsAfterBuy} -> ${probe?.body?.credits} (expected ${creditsAfterBuy - 1})`)

// === C14 — hit registration under 100 ms injected latency ==================
console.log('\n=== C14 hit registration at 100 ms RTT ===')
const targetIds = [...a.spawns].filter(([, v]) => v.type === 4).map(([k]) => k)
const targetId = targetIds[0]
const tPos = a.ents.get(targetId).pos

// The range is a walled lane: side walls run its length and the targets stand
// 15-25 m in, so C14's "from 30 m" is a position OUTSIDE the mouth. A
// straight-line walker cannot get there -- it presses against a side wall and
// stops. Routes on this planet are solved, not assumed (see t16), and the
// router works over terrain, not colliders. So this measures at the firing
// line a walker can actually reach, and reports the distance rather than
// claiming the criterion's 30 m.
const range0 = await a.walkTo(tPos, 30, 700)
let range = range0
console.log(`shooter is ${range.toFixed(1)} m from target ${targetId}`)
await a.wait(() => a.ents.get(targetId)?.health === 100, 5000)

// Five bursts of four: the target dies on the fourth hit (100 hp / 25 dmg)
// and is gone for its 3.0 s respawn, so a twenty-round burst would be firing
// at an absent entity for most of its length.
let aimedHits = 0
for (let burst = 0; burst < 5; burst++) {
  await a.wait(() => a.ents.get(targetId)?.health > 0, 5000)
  for (let i = 0; i < 4; i++) {
    a.events.length = 0
    a.send(fire(a.seq++, aimAt(a, tPos, 0)))
    // 520 ms, not the 150 ms fire_interval: spread_per_shot is 0.35 deg and
    // spread_decay 3.0 deg/s, so firing at the minimum interval holds the cone
    // near 0.95 deg -- 0.68 m at this range, wider than the 0.45 m target. At
    // this spacing the cone decays back to the 0.6 deg base between shots.
    await sleep(520)
    if (a.events.some(e => e.ev === 3)) aimedHits++
  }
  await sleep(3300) // respawn 3.0 s
}
check('20 aimed shots all register', aimedHits === 20, `${aimedHits}/20 hits at ${range.toFixed(1)} m`)

// A second session: the magazine is 30 rounds and reload is a stub, so the
// wide half cannot share a player with the aimed half.
const b = await session('widemiss', 18081)
await armed(b)
const range2 = await b.walkTo(tPos, 30)
let wideHits = 0
for (let i = 0; i < 20; i++) {
  b.events.length = 0
  b.send(fire(b.seq++, aimAt(b, tPos, 1.0)))
  await sleep(520)
  if (b.events.some(e => e.ev === 3)) wideHits++
}
check('20 shots aimed 1 m wide register zero', wideHits === 0, `${wideHits}/20 hits at ${range2.toFixed(1)} m`)

// === C18 — snapshot budget =================================================
console.log('\n=== C18 snapshot budget ===')
const rows = [...a.rowBytes, ...b.rowBytes]
const worst = Math.max(...rows)
check('snapshot stays at 54 B/entity', worst <= 54, `worst ${worst} B/entity over ${rows.length} snapshots`)

proxy.kill()
const bad = checks.filter(([, ok]) => !ok).length
console.log(`\nOVERALL: ${bad ? `FAIL (${bad}/${checks.length})` : `PASS (${checks.length} checks)`}`)
process.exit(bad ? 1 : 0)
