#!/usr/bin/env node
/**
 * t44 — Phase 23 placeable stations, over the wire (docs/GDD.md "Placeable
 * stations", ROADMAP "Phase 23").
 *
 * Self-contained: the test BUILDS the server and runs it itself, because the
 * point is persistence across a restart.
 *   server 1  DATABASE_URL=sqlite://<tmp>/t44.db, no SA_GUESTS (accounts
 *             only), SA_START seeds every new character's bag with plates +
 *             parts (the bare-harness way, Phase 22 — there is no give cmd).
 *   server 2  the SAME database after a SIGTERM: structures + chest survive.
 *   server 3  SA_GUESTS=1 on its own db, SA_START hands a workbench: a guest
 *             is refused `no_account` at place.
 *
 * Steps (one PASS/FAIL line each, exit 1 at the FIRST fail):
 *  1 build, start, register accounts A + B and a character each, join both.
 *  2 A crafts a workbench by hand (the real recipe, 20 s). A structure is
 *    stack_max 1 and a bag keeps one stack per item, so A carries ONE bench
 *    at a time: Construction 3 (the chest's level, 174 XP) is three benches
 *    at 60 XP, each placed before the next is made.
 *  3 A places it: ok, `structure` id; both sockets see a type-9 spawn with
 *    data `structure.bench\0<A>`.
 *  4 B walks to it and crafts a bench-tier recipe (tool.drill) with `npc` =
 *    the structure id, while A makes bench 2 by hand; A places bench 2 and
 *    makes bench 3 (kept in the bag) → Construction 3.
 *  5 A crafts a chest on bench 1, places it, puts 3 plates in; B's
 *    chest_take is `not_yours`; A invites B, B accepts, B takes 2: ok.
 *  6 refusals: bench 3 on the chest's spot `no_room`; B pick_up A's bench
 *    `not_yours`; A pick_up the non-empty chest `not_empty`; B place with no
 *    structure `not_owned`, a plate `unknown_item`; A places bench 3 (4);
 *    cap: chest 2 made + placed (5), chest 3 made, placing it `too_many`
 *    (T44_SKIP_CAP=1 skips the cap — ~30 s of chest crafting).
 *  7 SIGTERM, restart on the same db, rejoin: A's structures respawn with the
 *    same data; A peeks the chest (chest_take item "" qty 0): still 1 plate;
 *    B's peek is `not_yours` (the party died with the sessions); A takes it.
 *  8 A pick_up the bench: `{seconds: 1.5}`, then pick_up_end ok with the bench
 *    in the bag; B sees the despawn.
 *  9 guest server: `place` → `no_account`.
 *
 * Run (from the repo root):  node test/t44-structures.mjs
 *   SA_SERVER_BIN=<path> skips the build (uses that binary).
 * Budget: about three minutes (≈115 s of it crafting channels).
 */
import { spawn, execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { EVENT, OP, TYPE_STRUCTURE, decodeCraftEnd, decodePickUpEnd } from './lib/wire.mjs'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const EV_PARTY_UPDATE = 0x0009, EV_PARTY_INVITED = 0x000b
const START = 'credits=0,mat.plate.steel=40,mat.parts=50'
const SKIP_CAP = process.env.T44_SKIP_CAP === '1'

// ------------------------------------------------------------------ codec
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
  dv.setUint16(20, mask, true); dv.setUint16(22, seq & 0xffff, true)
  return frame(0x0003, b)
}
function cmd (seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(8 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq & 0xffff, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l) }
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
const scale = (a, k) => a.map((x) => x * k)
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
const dist = (a, b) => Math.hypot(...sub(a, b))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const tangent = (d, up) => { const k = dot(d, up); return [d[0] - up[0] * k, d[1] - up[1] * k, d[2] - up[2] * k] }
const wait = async (fn, ms = 6000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(25) } return null }
const count = (stacks, item) => (stacks ?? []).filter((s) => s.item === item).reduce((n, s) => n + s.qty, 0)
const J = (x) => JSON.stringify(x)

// ------------------------------------------------------------------ checks
class Fail extends Error {}
let passed = 0
const check = (name, ok, detail = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
  if (!ok) throw new Fail(name)
  passed++
}
/** A cmd result must be ok; status 1 means the server predates the opcode. */
const okResult = (name, r) => {
  const why = !r ? 'no cmd_result within 6 s'
    : r.status === 1 ? `status 1 unknown opcode 0x${r.op.toString(16)} — the server has no Phase 23 ops`
      : `status ${r.status} ${J(r.body)}`
  check(name, r?.status === 0, r?.status === 0 ? '' : why)
  return r.body
}
const refused = (name, r, reason, status = 3) => {
  const ok = r?.status === status && (status !== 3 || r.body?.reason === reason)
  check(name, ok, ok ? '' : `want status ${status}${status === 3 ? ' ' + reason : ''}, got ${r ? `status ${r.status} ${J(r.body)}` : 'nothing'}`)
}

// ------------------------------------------------------------------ server process
const tmp = mkdtempSync(join(tmpdir(), 't44-'))
const procs = new Set()
async function freePort () {
  return new Promise((res, rej) => {
    const s = createServer(); s.unref(); s.on('error', rej)
    s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => res(p)) })
  })
}
function build () {
  if (process.env.SA_SERVER_BIN) return process.env.SA_SERVER_BIN
  const bin = join(tmp, 'sa-server')
  execFileSync('go', ['build', '-o', bin, './cmd/server'], { cwd: join(ROOT, 'server'), stdio: 'inherit' })
  return bin
}
async function startServer (bin, port, env, tag) {
  const p = spawn(bin, ['-listen', `127.0.0.1:${port}`, '-metrics', ''], {
    env: { ...process.env, SA_GUESTS: '', SA_START: '', ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  const log = []
  const keep = (b) => { for (const l of b.toString().split('\n')) if (l) log.push(l); if (log.length > 400) log.splice(0, log.length - 400) }
  p.stdout.on('data', keep); p.stderr.on('data', keep)
  p.exited = new Promise((r) => p.on('exit', (code, sig) => r({ code, sig })))
  p.log = log; p.tag = tag
  procs.add(p)
  const t0 = Date.now()
  while (Date.now() - t0 < 20000) {
    if (p.exitCode !== null) break
    try { const r = await fetch(`http://127.0.0.1:${port}/healthz`); if (r.ok) return p } catch {}
    await sleep(150)
  }
  console.log(`--- ${tag} log tail:\n${log.slice(-30).join('\n')}`)
  throw new Fail(`${tag} did not come up on :${port}`)
}
async function stopServer (p) {
  if (!p || p.exitCode !== null) { procs.delete(p); return }
  p.kill('SIGTERM')
  const r = await Promise.race([p.exited, sleep(10000).then(() => null)])
  if (!r) { p.kill('SIGKILL'); await p.exited }
  procs.delete(p)
}

// ------------------------------------------------------------------ http
const nonce = Date.now().toString(36).slice(-5)
async function api (base, path, { method = 'GET', body, bearer, ip = '198.18.44.1' } = {}) {
  const headers = { 'Content-Type': 'application/json', 'CF-Connecting-IP': ip }
  if (method !== 'GET') headers['X-Requested-With'] = 't44'
  if (bearer) headers.Authorization = `Bearer ${bearer}`
  const r = await fetch(base + path, { method, headers, body: body ? J(body) : undefined })
  const text = await r.text()
  let json = null; try { json = JSON.parse(text) } catch {}
  return { status: r.status, json, text }
}
async function makeCharacter (base, tag, ip) {
  const email = `t44-${tag}-${nonce}@example.com`, password = 'placeable-stations'
  let r = await api(base, '/api/register', { method: 'POST', body: { email, password }, ip })
  check(`register account ${tag}`, r.status === 200, r.status === 200 ? '' : `status ${r.status} ${r.text.slice(0, 120)}`)
  r = await api(base, '/api/game-login', { method: 'POST', body: { email, password }, ip })
  check(`game-login ${tag}`, r.status === 200 && r.json?.session, r.status === 200 ? '' : `status ${r.status} ${r.text.slice(0, 120)}`)
  const name = `t44${tag}-${nonce}`
  r = await api(base, '/api/characters', { method: 'POST', body: { name, body: 'char.player' }, bearer: r.json.session, ip })
  check(`create character ${name}`, r.status === 200 && r.json?.token, r.status === 200 ? '' : `status ${r.status} ${r.text.slice(0, 120)}`)
  return { name: r.json.name, token: r.json.token }
}

// ------------------------------------------------------------------ ws client
const socks = new Set()
function connect (url, name, token) {
  const ws = new WebSocket(url)
  ws.binaryType = 'arraybuffer'
  const c = { ws, name, id: 0, ents: new Map(), spawns: new Map(), despawned: [], results: [], xp: [], crafts: [], pickups: [], party: [], invites: [], defs: null, seq: 1, iseq: 1, closed: null }
  socks.add(c)
  ws.addEventListener('open', () => ws.send(hello(name, token)))
  // The server drops a socket silent for 10 s; channels here are longer.
  const keep = setInterval(() => { if (ws.readyState === 1) { const b = u8(4); new DataView(b.buffer).setUint32(0, Date.now() >>> 0, true); ws.send(frame(0x0008, b)) } }, 2000)
  ws.addEventListener('close', (e) => { clearInterval(keep); c.closed = e.code || 1 })
  ws.addEventListener('error', () => {})
  ws.addEventListener('message', (ev) => {
    const dv = new DataView(ev.data), t = dv.getUint16(0, true)
    const pv = new DataView(ev.data, 2), p = new Uint8Array(ev.data, 2)
    if (t === 0x0002) c.id = pv.getUint32(8, true)
    else if (t === 0x0010) c.defs = JSON.parse(dec.decode(p.subarray(4, 4 + pv.getUint32(0, true))))
    else if (t === 0x0005) c.spawns.set(pv.getUint32(0, true), { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10, 10 + pv.getUint32(6, true))) })
    else if (t === 0x0006) { const id = pv.getUint32(0, true); c.spawns.delete(id); c.despawned.push(id) } else if (t === 0x0004) {
      const n = pv.getUint16(6, true)
      for (let i = 0; i < n; i++) {
        const o = 8 + i * 54
        c.ents.set(pv.getUint32(o, true), {
          pos: [pv.getFloat32(o + 4, true), pv.getFloat32(o + 8, true), pv.getFloat32(o + 12, true)],
          quat: [pv.getFloat32(o + 16, true), pv.getFloat32(o + 20, true), pv.getFloat32(o + 24, true), pv.getFloat32(o + 28, true)],
          health: pv.getUint16(o + 50, true),
        })
      }
    } else if (t === 0x0007) {
      const id = pv.getUint16(4, true), data = Buffer.from(p.subarray(10, 10 + pv.getUint32(6, true))), at = Date.now()
      if (id === EVENT.SKILL_XP) c.xp.push({ ...JSON.parse(data.toString('utf8')), at })
      else if (id === EVENT.CRAFT_END) c.crafts.push({ ...decodeCraftEnd(data), at })
      else if (id === EVENT.PICK_UP_END) c.pickups.push({ ...decodePickUpEnd(data), at })
      else if (id === EV_PARTY_UPDATE) c.party.push(JSON.parse(data.toString('utf8')))
      else if (id === EV_PARTY_INVITED) c.invites.push(JSON.parse(data.toString('utf8')))
    } else if (t === 0x000f) {
      const raw = dec.decode(p.subarray(9, 9 + pv.getUint32(5, true)))
      let body = {}; try { body = raw ? JSON.parse(raw) : {} } catch { body = { raw } }
      c.results.push({ seq: pv.getUint16(0, true), op: pv.getUint16(2, true), status: pv.getUint8(4), body })
    }
  })
  c.me = () => c.ents.get(c.id)
  c.call = async (op, body, ms = 6000) => { const seq = c.seq++ & 0xffff; c.ws.send(cmd(seq, op, body)); return wait(() => c.results.find((r) => r.seq === seq && r.op === op), ms) }
  c.send = (v, mask = 0) => c.ws.send(input(v, mask, c.iseq++))
  c.structs = () => [...c.spawns].filter(([, v]) => v.type === TYPE_STRUCTURE).map(([id, v]) => ({ id, ...v }))
  c.close = () => { try { c.ws.close() } catch {} socks.delete(c) }
  return c
}
async function joinGame (url, who, token) {
  const c = connect(url, who.name ?? who, token)
  const ok = await wait(() => c.closed || (c.id && c.me() && c.defs ? c : null), 15000)
  check(`${c.name} joins`, ok === c, ok === c ? `entity ${c.id}` : `close ${c.closed ?? 'timeout'}`)
  await sleep(400)
  return c
}

/** Sprint along the surface toward a point, jumping when wedged (t36's walker). */
async function walkTo (c, target, close, timeoutMs = 30000) {
  const at = typeof target === 'function' ? target : () => target
  const t0 = Date.now()
  let lastPos = c.me().pos.slice(), lastMove = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const d = sub(at(), c.me().pos)
    if (Math.hypot(...d) <= close) break
    const up = norm(c.me().pos)
    if (dist(c.me().pos, lastPos) > 0.5) { lastPos = c.me().pos.slice(); lastMove = Date.now() }
    const lk = norm(tangent(d, up))
    c.send([0, 1, ...lk], Date.now() - lastMove > 1000 ? 0x0003 : 0x0001)
    await sleep(50)
  }
  for (let i = 0; i < 6; i++) { const lk = norm(tangent(sub(at(), c.me().pos), norm(c.me().pos))); c.send([0, 0, ...lk]); await sleep(60) }
  return dist(at(), c.me().pos)
}
/** Stand still looking at pos + up×aim from the eye (cmd.go inRangeAt). */
async function faceAt (c, pos, aim = 0.6) {
  for (let i = 0; i < 10; i++) {
    const up = norm(c.me().pos), tUp = norm(pos)
    const eye = add(c.me().pos, scale(up, 1.7))
    c.send([0, 0, ...norm(sub(add(pos, scale(tUp, aim)), eye))])
    await sleep(60)
  }
  await sleep(150)
}
/** Stand still looking level along a tangent direction (place uses the horizontal facing). */
async function faceDir (c, dir) {
  for (let i = 0; i < 8; i++) { c.send([0, 0, ...norm(tangent(dir, norm(c.me().pos)))]); await sleep(60) }
  await sleep(150)
}
async function approach (c, id, close = 2.2) {
  const at = () => c.ents.get(id)?.pos
  check(`${c.name} sees structure ${id} in the snapshot`, !!at(), '')
  const d = await walkTo(c, at, close)
  await faceAt(c, at(), 0.6)
  return d
}
/** One craft, waiting out every unit; returns the craft_end events. */
async function craft (c, npc, recipe, qty) {
  const from = c.crafts.length
  const r = await c.call(OP.CRAFT, { npc, recipe, qty })
  if (r?.status !== 0) return { r, ends: [] }
  const per = r.body.duration ?? 30
  await wait(() => { const e = c.crafts.slice(from); return e.length >= qty || e.some((x) => x.reason !== 'done') }, (per * qty + 5) * 1000)
  return { r, ends: c.crafts.slice(from) }
}
const bag = async (c) => (await c.call(OP.INVENTORY, {}))?.body?.inventory ?? []
/**
 * Walk a few metres to fresh ground and place `item`, turning to a new
 * heading on `no_room` (the pad has rocks, NPCs and a garage near spawn).
 * Returns the ok result body or throws with the last refusal.
 */
function headings (c) {
  const up = norm(c.me().pos)
  const e0 = cross([0, 1, 0], up)
  const east = norm(Math.hypot(...e0) > 1e-3 ? e0 : cross([1, 0, 0], up))
  const north = cross(up, east)
  return [east, north, scale(east, -1), scale(north, -1)]
}
async function placeSomewhere (c, item, tag, firstStep = 6) {
  const heads = headings(c)
  let last = null
  for (let i = 0; i < 10; i++) {
    const h = heads[i % 4]
    const step = i === 0 ? firstStep : 4 + 2 * Math.floor(i / 4)
    if (step > 0) await walkTo(c, add(c.me().pos, scale(h, step)), 0.8, 8000)
    await faceDir(c, heads[(i + 1) % 4])
    last = await c.call(OP.PLACE, { item })
    if (last?.status === 1) okResult(`${tag}: place`, last) // fails with the unknown-op message
    if (last?.status === 0) return last.body
    if (!(last?.status === 3 && last.body?.reason === 'no_room')) break
  }
  okResult(`${tag}: place ${item}`, last)
}
const structOwnerData = (item, owner) => `${item}\0${owner}`

// ================================================================== run
const servers = { main: null, guest: null }
let A, B
let exitCode = 0
const T0 = Date.now()
try {
  // ---- 1. build, start, accounts, join
  const bin = build()
  check('server builds', true, bin)
  const port = await freePort(), dbUrl = `sqlite://${join(tmp, 't44.db')}`
  const env1 = { DATABASE_URL: dbUrl, SA_START: START }
  servers.main = await startServer(bin, port, env1, 'server 1')
  check('server 1 up (store, accounts only)', true, `:${port}`)
  const SITE = `http://127.0.0.1:${port}`, WS = `ws://127.0.0.1:${port}/ws`
  const charA = await makeCharacter(SITE, 'a', '198.18.44.1')
  const charB = await makeCharacter(SITE, 'b', '198.18.44.2')
  A = await joinGame(WS, charA, charA.token)
  B = await joinGame(WS, charB, charB.token)
  const items = A.defs.items ?? {}
  check('defs carry the three structure items', ['structure.bench', 'structure.forge', 'structure.chest'].every((id) => items[id]?.kind === 'structure'),
    J(['structure.bench', 'structure.forge', 'structure.chest'].map((id) => items[id]?.kind)))
  check('defs: bench station/verb, chest verb', items['structure.bench']?.station === 'bench' && items['structure.bench']?.verb === 'Use' && items['structure.chest']?.verb === 'Open' && !items['structure.chest']?.station,
    J([items['structure.bench'], items['structure.chest']].map((i) => i && { station: i.station, verb: i.verb })))
  const R = Object.fromEntries((A.defs.recipes ?? []).map((r) => [r.id, r]))
  const benchR = R['recipe.structure.bench'], chestR = R['recipe.structure.chest']
  check('defs: bench recipe hand/construction 1, chest bench/construction 3', benchR?.station === 'hand' && benchR.skill === 'construction' && benchR.level === 1 && chestR?.station === 'bench' && chestR.level === 3,
    J([benchR && [benchR.station, benchR.skill, benchR.level, benchR.seconds], chestR && [chestR.station, chestR.level, chestR.seconds]]))
  const bagA0 = await bag(A)
  check('A starts with the SA_START plates and parts', count(bagA0, 'mat.plate.steel') === 40 && count(bagA0, 'mat.parts') === 50, J(bagA0))

  // ---- 2. a bench by hand. A structure is stack_max 1 and a bag holds ONE
  // stack per item, so the bag carries one bench at a time: Construction 3
  // (174 XP) takes three benches at 60 XP, each placed before the next.
  let t = Date.now()
  const b1 = await craft(A, 0, benchR.id, 1)
  okResult('A crafts structure.bench by hand', b1.r)
  check('the bench lands', b1.ends[0]?.reason === 'done' && b1.ends[0].item === 'structure.bench',
    `${J(b1.ends)} in ${((Date.now() - t) / 1000).toFixed(1)} s (recipe ${benchR.seconds} s)`)

  // ---- 3. place the bench
  const placed = await placeSomewhere(A, 'structure.bench', 'A bench')
  const benchId = placed?.structure
  check('place result carries structure id + inventory', benchId > 0 && Array.isArray(placed.inventory) && count(placed.inventory, 'structure.bench') === 0, J(placed))
  const want = structOwnerData('structure.bench', charA.name)
  const sa = await wait(() => A.spawns.get(benchId)), sb = await wait(() => B.spawns.get(benchId))
  check('A sees the bench spawn: type 9, data item\\0owner', sa?.type === TYPE_STRUCTURE && sa.data === want, J(sa))
  check('B sees the same spawn', sb?.type === TYPE_STRUCTURE && sb.data === want, J(sb))
  const benchPos = (await wait(() => A.ents.get(benchId)))?.pos
  check('the bench is in the snapshot ~2 m ahead of A', benchPos && dist(benchPos, A.me().pos) < 3.2, benchPos ? `${dist(benchPos, A.me().pos).toFixed(2)} m` : 'absent')

  // ---- 4. B crafts on A's bench (while A makes bench 2 in the hands)
  await approach(B, benchId)
  const drillR = R['recipe.tool.drill']
  t = Date.now()
  const [bc, b2] = await Promise.all([craft(B, benchId, drillR.id, 1), craft(A, 0, benchR.id, 1)])
  okResult('B crafts tool.drill on A\'s bench (npc = structure id)', bc.r)
  check('B\'s drill lands', bc.ends[0]?.reason === 'done' && bc.ends[0].item === 'tool.drill', `${J(bc.ends)} ${((Date.now() - t) / 1000).toFixed(1)} s`)
  okResult('A crafts bench 2', b2.r)
  check('bench 2 lands', b2.ends[0]?.reason === 'done', J(b2.ends))
  const p2 = await placeSomewhere(A, 'structure.bench', 'A bench 2', 4)
  check('A places bench 2', p2.structure > 0, `${p2.structure}`)
  const b3 = await craft(A, 0, benchR.id, 1)
  okResult('A crafts bench 3', b3.r)
  check('bench 3 lands (kept in the bag)', b3.ends[0]?.reason === 'done', J(b3.ends))
  await sleep(1200) // skill_xp batches once a second
  const lvl = A.xp.filter((x) => x.skill === 'construction').at(-1)
  check('Construction reaches the chest\'s level 3', (lvl?.level ?? 1) >= chestR.level, J(lvl))

  // ---- 5. chest, put, not_yours, party, take
  const craftChest = async (tag) => {
    await approach(A, benchId)
    const r = await craft(A, benchId, chestR.id, 1)
    okResult(`A crafts ${tag} on its own bench`, r.r)
    check(`${tag} lands`, r.ends[0]?.reason === 'done' && r.ends[0].item === 'structure.chest', J(r.ends))
  }
  await craftChest('chest 1')
  const cp = await placeSomewhere(A, 'structure.chest', 'A chest', 4)
  const chestId = cp.structure
  const sc = await wait(() => B.spawns.get(chestId))
  check('B sees the chest spawn', sc?.type === TYPE_STRUCTURE && sc.data === structOwnerData('structure.chest', charA.name), J(sc))
  const chestPos = (await wait(() => A.ents.get(chestId)))?.pos
  const chestSpot = A.me().pos.slice()
  await faceAt(A, chestPos)
  const put = okResult('A chest_put 3 plates', await A.call(OP.CHEST_PUT, { structure: chestId, item: 'mat.plate.steel', qty: 3 }))
  check('put result: inventory + chest', Array.isArray(put.inventory) && count(put.chest, 'mat.plate.steel') === 3, J(put.chest))
  await approach(B, chestId)
  refused('B chest_take before the party is not_yours', await B.call(OP.CHEST_TAKE, { structure: chestId, item: 'mat.plate.steel', qty: 2 }), 'not_yours')
  okResult('A invites B to the party', await A.call(OP.PARTY_INVITE, { target: B.id }))
  check('B gets the invite', !!(await wait(() => B.invites.at(-1))), '')
  okResult('B accepts', await B.call(OP.PARTY_RESPOND, { accept: true }))
  check('A sees a party of two', !!(await wait(() => A.party.find((u) => u.members?.length === 2))), J(A.party.at(-1)))
  const plB0 = count(await bag(B), 'mat.plate.steel')
  const take = okResult('B chest_take 2 plates in A\'s party', await B.call(OP.CHEST_TAKE, { structure: chestId, item: 'mat.plate.steel', qty: 2 }))
  check('B\'s bag has the plates, the chest keeps 1', count(take.inventory, 'mat.plate.steel') === plB0 + 2 && count(take.chest, 'mat.plate.steel') === 1,
    `bag ${plB0}→${count(take.inventory, 'mat.plate.steel')}, chest ${J(take.chest)}`)

  // ---- 6. refusals
  await walkTo(A, chestSpot, 0.4, 5000)
  await faceDir(A, tangent(sub(chestPos, A.me().pos), norm(A.me().pos)))
  refused('A places bench 3 on the chest\'s spot: no_room', await A.call(OP.PLACE, { item: 'structure.bench' }), 'no_room')
  await approach(B, benchId)
  refused('B pick_up on A\'s bench: not_yours', await B.call(OP.PICK_UP, { structure: benchId }), 'not_yours')
  await approach(A, chestId)
  refused('A pick_up on the non-empty chest: not_empty', await A.call(OP.PICK_UP, { structure: chestId }), 'not_empty')
  const bagB = await bag(B)
  check('B holds no structure', !bagB.some((s) => items[s.item]?.kind === 'structure'), J(bagB.map((s) => s.item)))
  refused('B place with none in the bag: not_owned', await B.call(OP.PLACE, { item: 'structure.bench' }), 'not_owned')
  refused('place a non-structure item: unknown_item', await B.call(OP.PLACE, { item: 'mat.plate.steel' }), 'unknown_item')
  const p3 = await placeSomewhere(A, 'structure.bench', 'A bench 3', 4)
  check('A places bench 3 elsewhere (4 placed)', p3.structure > 0, `${p3.structure}`)
  let mine = 4
  if (SKIP_CAP) console.log('SKIP the 5-structure cap (T44_SKIP_CAP=1)')
  else {
    await craftChest('chest 2')
    const p5 = await placeSomewhere(A, 'structure.chest', 'A chest 2', 4)
    mine++
    check('A places structure 5 of 5', p5.structure > 0, `${p5.structure}`)
    await craftChest('chest 3')
    const away = headings(A)[2]
    await walkTo(A, add(A.me().pos, scale(away, 5)), 0.8, 6000)
    await faceDir(A, away)
    refused('a 6th structure: too_many', await A.call(OP.PLACE, { item: 'structure.chest' }), 'too_many')
  }

  // ---- 7. restart on the same database
  const beforeA = A.structs().filter((s) => s.data.endsWith(`\0${charA.name}`)).map((s) => s.data).sort()
  check(`A sees ${mine} of its own structures before the restart`, beforeA.length === mine, J(beforeA))
  A.close(); B.close()
  await sleep(1500) // the disconnect saves land
  await stopServer(servers.main)
  check('server 1 stops on SIGTERM', true, '')
  servers.main = await startServer(bin, port, env1, 'server 2')
  check('server 2 up on the same database', true, dbUrl)
  A = await joinGame(WS, charA, charA.token)
  B = await joinGame(WS, charB, charB.token)
  await sleep(500)
  const afterA = A.structs().filter((s) => s.data.endsWith(`\0${charA.name}`)).map((s) => s.data).sort()
  check('A\'s structures respawn with the same item\\0owner data', J(afterA) === J(beforeA), `${J(afterA)} vs ${J(beforeA)}`)
  const afterB = B.structs().filter((s) => s.data.endsWith(`\0${charA.name}`)).map((s) => s.data).sort()
  check('B sees them too', J(afterB) === J(beforeA), J(afterB))
  const near = (c, item, pos) => c.structs().filter((s) => s.data === structOwnerData(item, charA.name) && c.ents.get(s.id))
    .sort((a, b) => dist(c.ents.get(a.id).pos, pos) - dist(c.ents.get(b.id).pos, pos))[0]?.id
  const chest2 = near(A, 'structure.chest', chestPos), bench2 = near(A, 'structure.bench', benchPos)
  check('the chest and the first bench are back where they were', chest2 && bench2 && dist(A.ents.get(chest2).pos, chestPos) < 0.05 && dist(A.ents.get(bench2).pos, benchPos) < 0.05,
    `chest ${chest2 && dist(A.ents.get(chest2).pos, chestPos).toFixed(3)} m, bench ${bench2 && dist(A.ents.get(bench2).pos, benchPos).toFixed(3)} m`)
  await approach(A, chest2)
  const peek = okResult('A peeks its chest (chest_take item "" qty 0)', await A.call(OP.CHEST_TAKE, { structure: chest2, item: '', qty: 0 }))
  check('the peek shows the 1 plate, nothing moved', count(peek.chest, 'mat.plate.steel') === 1 && (peek.chest ?? []).length === 1 && Array.isArray(peek.inventory), J(peek.chest))
  await approach(B, chest2)
  refused('B peeks after the restart (no party now): not_yours', await B.call(OP.CHEST_TAKE, { structure: chest2, item: '', qty: 0 }), 'not_yours')
  const last = okResult('A chest_take the plate left in the chest', await A.call(OP.CHEST_TAKE, { structure: chest2, item: 'mat.plate.steel', qty: 1 }))
  check('the chest held exactly 1 plate across the restart', (last.chest ?? []).length === 0, J(last.chest))

  // ---- 8. owner picks the bench up
  await approach(A, bench2)
  await approach(B, bench2, 2.6)
  await faceAt(A, A.ents.get(bench2).pos)
  const from = A.pickups.length
  const pu = okResult('A pick_up its bench', await A.call(OP.PICK_UP, { structure: bench2 }))
  check('pick_up result is {seconds: 1.5}', pu?.seconds === 1.5, J(pu))
  // stand still through the channel (moving > 0.5 m breaks it)
  const end = await wait(() => { A.send([0, 0, ...norm(tangent(sub(A.ents.get(bench2)?.pos ?? A.me().pos, A.me().pos), norm(A.me().pos)))]); return A.pickups.slice(from)[0] }, 5000)
  check('pick_up_end ok, the bench back in the bag', end?.ok === true && end.structure === bench2 && count(end.inventory, 'structure.bench') >= 1, J(end))
  check('B sees the bench despawn', !!(await wait(() => B.despawned.includes(bench2), 3000)), '')
  A.close(); B.close()

  // ---- 9. guest refusal
  const gport = await freePort()
  servers.guest = await startServer(bin, gport, { DATABASE_URL: `sqlite://${join(tmp, 'guest.db')}`, SA_GUESTS: '1', SA_START: 'structure.bench=1' }, 'server 3')
  const G = await joinGame(`ws://127.0.0.1:${gport}/ws`, 'guest44', '')
  const gb = await bag(G)
  check('the guest holds a workbench (SA_START)', count(gb, 'structure.bench') === 1, J(gb))
  refused('a guest place: no_account', await G.call(OP.PLACE, { item: 'structure.bench' }), 'no_account')
  G.close()
} catch (e) {
  exitCode = 1
  if (!(e instanceof Fail)) console.log(`FAIL harness error: ${e?.stack ?? e}`)
  for (const p of procs) if (p.log?.length) console.log(`--- ${p.tag} log tail:\n${p.log.slice(-25).join('\n')}`)
} finally {
  for (const c of socks) { try { c.ws.close() } catch {} }
  for (const p of [...procs]) await stopServer(p)
  rmSync(tmp, { recursive: true, force: true })
  console.log(`${exitCode ? 'FAILED' : 'OK'}: ${passed} checks passed in ${((Date.now() - T0) / 1000).toFixed(0)} s`)
  process.exit(exitCode)
}
