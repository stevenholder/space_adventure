#!/usr/bin/env node
/**
 * C39 — pilot prediction under latency: through the 100 ms round-trip
 * proxy (test/lib/proxy.mjs, 50 ms/direction), a scripted pilot run keeps
 * p95 |predicted − authoritative| under 0.5 m with no snap-back.
 *
 * This harness owns the setup: own a ship on a fresh token (direct
 * connection). Phase 24 took ship.v1 out of Vex's stock (the dock builds
 * it), so the server's SA_START must seed `ship.v1=1`: the join sync
 * spawns the owner's ship on the pad. Then hand the
 * token and ship id to SimDump --pilot, which joins THROUGH THE PROXY
 * (the owner's ship follows the token), walks to the pad, takes seat 1,
 * and flies the measurement run with ShipPredictor. The predictor under
 * test is the client's own (Game/Core/ShipPrediction.cs), same as C6
 * tests the body's.
 *
 * Run: node test/t27-pilot-latency.mjs   (needs dotnet and a server whose
 *   SA_START includes ship.v1=1, e.g. SA_GUESTS=1
 *   SA_START="credits=3000,ammo.cell=120,ship.v1=1"; SA_PORT=1809x for a
 *   bare one, default 18080)
 */
import { spawn, execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const PROXY_PORT = 19180
const TARGET_HOST = '127.0.0.1'
const TARGET_PORT = Number(process.env.SA_PORT ?? 18080)
const EVIDENCE = path.join(root, 'test/out/t27-pilot-latency.json')

const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
const frame = (type, body) => { const o = u8(2 + body.length); new DataView(o.buffer).setUint16(0, type, true); o.set(body, 2); return o }
function hello(name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(2 + 4 + n.length + 4 + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
function input(v, mask, seq) {
  const b = u8(25), dv = new DataView(b.buffer)
  for (let i = 0; i < 5; i++) dv.setFloat32(i * 4, v[i] ?? 0, true)
  dv.setUint16(20, mask, true); dv.setUint16(22, seq, true)
  return frame(0x0003, b)
}
function cmd(seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(8 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ---- setup: own a ship on a fresh token (direct, no proxy) -----------------
const token = `t27-pilot-${Date.now()}`
const ws = new WebSocket(`ws://${TARGET_HOST}:${TARGET_PORT}/ws`)
ws.binaryType = 'arraybuffer'
const st = { id: 0, ents: new Map(), spawns: new Map(), results: [], seq: 1 }
// The setup joins under SimDump's name: a storeless guest's vehicles are
// keyed `session:<name>`, so any other name here makes SimDump's join own
// (and spawn) a SECOND seeded ship beside the flown one — the take-off then
// brushes it and C39 counts snap-backs.
ws.addEventListener('open', () => ws.send(hello('c39-pilot', token)))
ws.addEventListener('message', (ev) => {
  const dv = new DataView(ev.data), t = dv.getUint16(0, true), pv = new DataView(ev.data, 2)
  if (t === 0x0002) st.id = pv.getUint32(8, true)
  else if (t === 0x0005) {
    const p = new Uint8Array(ev.data, 2)
    st.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10)) })
  } else if (t === 0x000f) st.results.push({ seq: pv.getUint16(0, true), status: pv.getUint8(4) })
  else if (t === 0x0004) {
    const n = pv.getUint16(6, true)
    for (let i = 0; i < n; i++) {
      const o = 8 + i * 54
      st.ents.set(pv.getUint32(o, true), [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)])
    }
  }
})
const wait = async (fn, ms = 5000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }

await wait(() => st.id && st.ents.size > 1)
// The seeded ship spawns on the tick after the join burst; world ids only
// grow, so the newest type-2 id is this fresh token's.
await sleep(1000)
const shipIds = () => [...st.spawns].filter(([, v]) => v.type === 2).map(([id]) => id)
const shipId = await wait(() => shipIds().length ? Math.max(...shipIds()) : 0, 3000)
if (!shipId) { console.log('FAIL: no owned ship spawned (does SA_START include ship.v1=1?)'); process.exit(1) }
console.log(`setup: token ${token}, ship ${shipId}`)
ws.close()
await sleep(300)

// ---- the measurement: SimDump --pilot through the proxy --------------------
const proxy = spawn(process.execPath,
  [path.join(root, 'test/lib/proxy.mjs'), String(PROXY_PORT), TARGET_HOST, String(TARGET_PORT)],
  { stdio: ['ignore', 'inherit', 'inherit'] })
await sleep(500)

let code = 1
try {
  execFileSync('dotnet',
    ['run', '--project', 'client/simdump', '--nologo', '--',
      '--pilot', `ws://127.0.0.1:${PROXY_PORT}/ws`, '--ship', String(shipId),
      '--token', token, '--evidence', EVIDENCE],
    { cwd: root, stdio: 'inherit' })
  code = 0
} catch (e) {
  code = e.status ?? 1
} finally {
  proxy.kill()
}
if (code === 0) console.log('evidence:', readFileSync(EVIDENCE, 'utf8').trim())
process.exit(code)
