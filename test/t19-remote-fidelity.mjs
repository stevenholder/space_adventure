#!/usr/bin/env node
/**
 * C16 — remote fidelity: what a SECOND client sees of a shooter.
 *
 * The criterion has three clauses. Two are implemented and checked here; the
 * third is not implemented at all, and this script says so rather than going
 * green on two out of three:
 *
 *   1. "sees the shooter's equipped weapon"  -- NOT ON THE WIRE. The 54-byte
 *      entity row has no weapon field, a player's `spawn` payload is just the
 *      name, and `shot_fired` carries origin/dir/dist and no item id. Server
 *      side, `client.EquippedWeapon` exists only to notice a re-equip and
 *      reset the magazine (server.go:614) -- it is never encoded. So no client
 *      can render another player's weapon, and the TS client does not try.
 *   2. "aim pitch within 1 deg"              -- checked, via pitch_q.
 *   3. "one shot_fired per shot, ordered"    -- checked.
 *
 * pitch_q is asin(up . look) quantised to [-127,127] over +-90 deg
 * (server/internal/server/client.go), so one step is 0.709 deg and the
 * quantisation alone can account for +-0.355 deg of the 1 deg budget.
 *
 * Run: node test/t19-remote-fidelity.mjs   (needs `make up`)
 */
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

async function session(name) {
  const s = { myId: 0, ents: new Map(), spawns: new Map(), results: [], events: [], defs: null, seq: 1 }
  const ws = new WebSocket('ws://127.0.0.1:18080/ws'); ws.binaryType = 'arraybuffer'
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
      for (let i = 0; i < n; i++) { const o = 8 + i * 54
        s.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o+4,true), pv.getFloat32(o+8,true), pv.getFloat32(o+12,true)],
          health: pv.getUint16(o+50,true), pitchQ: pv.getInt8(o+53),
        }) }
    }
    else if (t === 0x000f) s.results.push({ seq: pv.getUint16(0,true), op: pv.getUint16(2,true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
    else if (t === 0x0007) s.events.push({ id: pv.getUint32(0,true), ev: pv.getUint16(4,true), at: Date.now() })
  })
  s.ws = ws
  s.wait = async (fn, ms = 8000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
  s.me = () => s.ents.get(s.myId).pos
  s.send = (f) => ws.send(f)
  s.call = async (op, body) => { const q = s.seq++; ws.send(cmd(q, op, body)); return await s.wait(() => s.results.find(r => r.seq === q)) }
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

const checks = []
const check = (name, ok, detail = '') => { checks.push([name, ok]); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`) }

const shooter = await session('shooter')
const observer = await session('observer')
console.log(`shooter id=${shooter.myId}, observer id=${observer.myId}`)

// The observer must actually have the shooter in its snapshot.
const seen = await observer.wait(() => observer.ents.has(shooter.myId), 6000)
check('observer sees the shooter as an entity', !!seen)

// --- clause 2: aim pitch within 1 deg --------------------------------------
// Build a look direction with a known angle off the local tangent plane, then
// read it back from the row the OBSERVER received.
const up = norm(shooter.me())
const fwdRaw = [up[1], up[2], up[0]] // any vector not parallel to up
const fwd = norm(sub(fwdRaw, up.map(x => x * dot(fwdRaw, up))))
let worstPitchErr = 0
for (const deg of [0, 20, -20, 45]) {
  const r = deg * Math.PI / 180
  const look = norm(fwd.map((x, i) => x * Math.cos(r) + up[i] * Math.sin(r)))
  for (let i = 0; i < 4; i++) { shooter.send(input(0, 0, look, 0, shooter.seq++)); await sleep(60) }
  await sleep(250)
  const q = observer.ents.get(shooter.myId)?.pitchQ
  const seenDeg = (q / 127) * 90
  const err = Math.abs(seenDeg - deg)
  worstPitchErr = Math.max(worstPitchErr, err)
  console.log(`  aimed ${String(deg).padStart(3)} deg -> pitch_q ${String(q).padStart(4)} = ${seenDeg.toFixed(2)} deg  (err ${err.toFixed(2)})`)
}
check('remote aim pitch within 1 deg', worstPitchErr <= 1.0, `worst ${worstPitchErr.toFixed(2)} deg`)

// --- clause 3: one shot_fired per shot, ordered ----------------------------
const npcId = [...shooter.spawns].find(([, v]) => v.type === 3 && v.data === 'npc.quartermaster')?.[0]
await shooter.walkTo(shooter.ents.get(npcId).pos, 2.4, 120)
await shooter.call(0x0002, { npc: npcId, item: 'weapon.pulse', qty: 1 })
await shooter.call(0x0003, { slot: 'primary', item: 'weapon.pulse' })

const N = 8
observer.events.length = 0
const level = norm(sub(fwd, norm(shooter.me()).map(x => x * dot(fwd, norm(shooter.me())))))
shooter.send(input(0, 0, level, 0, shooter.seq++)); await sleep(150)
for (let i = 0; i < N; i++) { shooter.send(fire(shooter.seq++, level)); await sleep(520) }
await sleep(600)

const shots = observer.events.filter(e => e.ev === 2 && e.id === shooter.myId)
check(`observer receives one shot_fired per shot`, shots.length === N, `${shots.length}/${N}`)
const ordered = shots.every((e, i) => i === 0 || e.at >= shots[i-1].at)
check('shot_fired events arrive in order', ordered)

// --- clause 1: equipped weapon --------------------------------------------
// Not a check that can pass: there is no field for it anywhere on the wire.
console.log('')
check('observer can see the shooter\'s equipped weapon', false,
      'NOT IMPLEMENTED — no weapon id in the entity row, in `spawn`, or in `shot_fired`. ' +
      'Needs a protocol decision, not a code fix.')

const bad = checks.filter(([, ok]) => !ok).length
console.log(`\nOVERALL: ${bad ? `FAIL (${bad}/${checks.length})` : `PASS (${checks.length} checks)`}`)
process.exit(bad ? 1 : 0)
