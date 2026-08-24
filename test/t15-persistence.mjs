#!/usr/bin/env node
/**
 * C11 — persistence across a reconnect.
 *
 * Buy with a token, disconnect, reconnect with the SAME token, and check the
 * purchase survived; then confirm a FRESH token starts clean. Both halves
 * matter: restoring everyone's progress is useless if a new player inherits
 * it, and a "clean start" that also wipes returning players is worse.
 *
 * This could not pass until the server actually opened a store — New() built
 * one field for it and cmd/server never filled it, so every session was
 * ephemeral while looking entirely healthy.
 *
 * Run: node test/t15-persistence.mjs   (needs `make up` with DATABASE_URL set)
 */
const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
const frame = (t, b) => { const o = u8(2 + b.length); new DataView(o.buffer).setUint16(0, t, true); o.set(b, 2); return o }
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
const norm = (v) => { const l = Math.hypot(...v); return v.map(x => x / l) }
const sub = (a, b) => [a[0]-b[0], a[1]-b[1], a[2]-b[2]]
const sleep = (ms) => new Promise(r => setTimeout(r, ms))

async function session(token, doBuy) {
  const ws = new WebSocket('ws://127.0.0.1:18080/ws'); ws.binaryType = 'arraybuffer'
  let myId = 0; const ents = new Map(), spawns = new Map(), results = []
  ws.addEventListener('open', () => ws.send(hello('persist', token)))
  ws.addEventListener('message', (ev) => {
    const pv = new DataView(ev.data, 2), t = new DataView(ev.data).getUint16(0, true)
    const p = new Uint8Array(ev.data, 2)
    if (t === 0x0002) myId = pv.getUint32(8, true)
    else if (t === 0x0005) spawns.set(pv.getUint32(0, true), pv.getUint16(4, true))
    else if (t === 0x0004) { const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) { const o = 8 + i * 54
        ents.set(pv.getUint32(o, true), [pv.getFloat32(o+4,true), pv.getFloat32(o+8,true), pv.getFloat32(o+12,true)]) } }
    else if (t === 0x000f) results.push({ op: pv.getUint16(2,true), status: pv.getUint8(4), body: JSON.parse(dec.decode(p.subarray(9)) || '{}') })
  })
  const wait = async (fn, ms=4000) => { const t0=Date.now(); while(Date.now()-t0<ms){const v=fn(); if(v) return v; await sleep(25)} return null }
  await wait(() => myId && ents.size > 1)
  const npcId = [...spawns].find(([, v]) => v === 3)?.[0]
  let seq = 1
  if (doBuy) {
    const npcPos = ents.get(npcId), me = () => ents.get(myId)
    for (let i = 0; i < 80; i++) {
      const d = sub(npcPos, me()); if (Math.hypot(...d) <= 2.4) break
      const u = norm(me()); const lk = norm(sub(d, u.map(x => x*(d[0]*u[0]+d[1]*u[1]+d[2]*u[2]))))
      ws.send(input(0, 1, lk, 0, seq++)); await sleep(50)
    }
    const u = norm(ents.get(myId)), d = sub(npcPos, ents.get(myId))
    const lk = norm(sub(d, u.map(x => x*(d[0]*u[0]+d[1]*u[1]+d[2]*u[2]))))
    ws.send(input(0, 0, lk, 0, seq++)); await sleep(120)
    ws.send(cmd(seq++, 0x0002, { npc: npcId, item: 'weapon.pulse', qty: 1 }))
    await wait(() => results.find(r => r.op === 2))
    ws.send(cmd(seq++, 0x0003, { slot: 'primary', item: 'weapon.pulse' }))
    await wait(() => results.find(r => r.op === 3))
  }
  ws.send(cmd(seq++, 0x0004, {}))
  const inv = await wait(() => results.find(r => r.op === 4))
  await sleep(200); ws.close(); await sleep(600) // let the server save on leave
  return inv?.body
}

const tok = 'persist-e2e-' + Date.now()
const a = await session(tok, true)
console.log(`buy session : credits=${a.credits} equipped=${JSON.stringify(a.equipped)} items=${a.inventory.length}`)
const b = await session(tok, false)
console.log(`reconnect   : credits=${b.credits} equipped=${JSON.stringify(b.equipped)} items=${b.inventory.length}`)
const c = await session('fresh-token-' + Date.now(), false)
console.log(`fresh token : credits=${c.credits} equipped=${JSON.stringify(c.equipped)} items=${c.inventory.length}`)
const restored = b.credits === a.credits && JSON.stringify(b.equipped) === JSON.stringify(a.equipped)
const cleanStart = c.credits === 1000 && Object.keys(c.equipped ?? {}).length === 0
console.log(restored ? 'PASS restored across reconnect' : 'FAIL not restored')
console.log(cleanStart ? 'PASS fresh token starts clean' : 'FAIL fresh token not clean')
process.exit(restored && cleanStart ? 0 : 1)
