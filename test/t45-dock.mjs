#!/usr/bin/env node
/**
 * t45 — Phase 24 buildings and the dock, over the wire (docs/GDD.md
 * "Buildings and the dock", ROADMAP "Phase 24", PROTOCOL `deliver` 0x001A).
 *
 * Self-contained like t44: the test BUILDS the server and runs it itself,
 * because the half-built site and the owned rover must survive a restart.
 *   server 1  DATABASE_URL=sqlite://<tmp>/t45.db, accounts only, SA_START
 *             seeds EVERY new character (it is global) with a dock site, a
 *             chest, ammo (an item no bill wants) and 50 each of plates,
 *             wiring, iron + 4 cores.
 *   server 2  the SAME database after a SIGTERM.
 *
 * Why the seed is shaped this way (contract gaps, see the report):
 *  - a bag holds ONE stack per item, capped at stack_max (50 for a
 *    material), and SA_START grants through AddItem, so a character can
 *    never carry the bill's 60 plates: A brings 10, B (in A's party) 50.
 *  - `site.dock` is stack_max 1 and `place` checks `not_owned` before the
 *    one-dock rule, so `too_many` needs a SECOND site in A's bag while the
 *    first stands: B hands A its seeded site through A's chest (party put,
 *    owner take). That second site is the one A places after the demolish.
 *
 * Steps (one PASS/FAIL line each, exit 1 at the FIRST fail):
 *  1 build, start, register A + B, join both; defs carry site.dock (bill,
 *    footprint 6), structure.dock (station dock), vehicle.rover (kind
 *    vehicle) and the dock recipes; the bags hold the seed.
 *  2 A at Quartermaster Vex: shop_list stocks no ship.v1 (the dock makes it).
 *  3 A places its chest; deliver to the chest (not a site) is status 5.
 *  4 A places the site: ok, both sockets see a type-9 spawn
 *    `site.dock\0<A>`, the snapshot row's health is 0; a peek (qty 0) shows
 *    the full bill (every line), pct 0, nothing moved.
 *  5 B delivers before any party: `not_yours`. A delivers ammo: `not_wanted`.
 *  6 A delivers 10 plates: bill remaining plates 50, pct 9 (10/108 units),
 *    the row's health follows.
 *  7 A invites B; B delivers qty 60 plates → clamped to the 50 the line
 *    still wants (and B carries); B delivers 4 wiring; pct 59. A's plates
 *    are now `not_wanted` (the line is full).
 *  8 B puts its site into A's chest, A takes it; A's second place is
 *    `too_many` (one site-or-dock per character).
 *  9 A delivers wiring 20 + iron 20 (pct 96), then the 4 cores: the result
 *    is an all-zero bill at 100; despawn then spawn of the SAME id with
 *    `structure.dock\0<A>` on both sockets; A's skill_xp construction is
 *    Σ(value × qty)/2 of the bill (1580 → level 11); a deliver to the dock
 *    is status 5.
 * 10 A crafts recipe.vehicle.rover with npc = the dock id: craft_end done
 *    vehicle.rover within 35 s, the rover in the bag, a new type-5 spawn
 *    within 8 m of the dock; a second rover craft is refused `no_space`.
 * 11 B pick_up the dock: `not_yours`. A pick_up: {seconds: 3}, pick_up_end
 *    ok, B sees the despawn, A's bag is exactly what it was (nothing back).
 * 12 A places the second site (its one-dock slot is free again) and delivers
 *    54 of 108 units → pct 50.
 * 13 SIGTERM, restart on the same db, rejoin: the site is back
 *    (`site.dock\0<A>`), its row health 50 and a peek gives the same bill;
 *    A's rover is in the world again (one more type-5 than at first join).
 * 14 (beyond the contract) chest_take on the half-built site must not hand
 *    the delivered bill back.
 *
 * Run (from the repo root):  node test/t45-dock.mjs
 *   SA_SERVER_BIN=<path> skips the build (uses that binary).
 * Ports: the first free one in 18090–18099 (bare-server rule).
 * Budget: about two minutes (30 s of it the rover's channel).
 */
import { spawn, execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { EVENT, OP, TYPE_STRUCTURE, TYPE_VEHICLE, TYPE_NPC, decodeCraftEnd, decodePickUpEnd, decodeDeliverResult } from './lib/wire.mjs'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const EV_PARTY_UPDATE = 0x0009, EV_PARTY_INVITED = 0x000b
const START = 'credits=0,site.dock=1,structure.chest=1,ammo.cell=10,mat.plate.steel=50,mat.wiring=50,mat.ingot.iron=50,mat.core=4'
const BILL = { 'mat.plate.steel': 60, 'mat.wiring': 24, 'mat.ingot.iron': 20, 'mat.core': 4 }
const UNITS = Object.values(BILL).reduce((a, b) => a + b, 0) // 108
const pctOf = (delivered) => Math.floor((delivered * 100) / UNITS)

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
const tmp = mkdtempSync(join(tmpdir(), 't45-'))
const procs = new Set()
/** The first free port in 18090–18099 (bare servers never touch :18080). */
async function freePort (skip = []) {
  const tryPort = (p) => new Promise((res) => {
    const s = createServer(); s.unref(); s.on('error', () => res(false))
    s.listen(p, '127.0.0.1', () => s.close(() => res(true)))
  })
  for (let p = 18090; p <= 18099; p++) if (!skip.includes(p) && await tryPort(p)) return p
  throw new Fail('no free port in 18090–18099')
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
async function api (base, path, { method = 'GET', body, bearer, ip = '198.18.45.1' } = {}) {
  const headers = { 'Content-Type': 'application/json', 'CF-Connecting-IP': ip }
  if (method !== 'GET') headers['X-Requested-With'] = 't45'
  if (bearer) headers.Authorization = `Bearer ${bearer}`
  const r = await fetch(base + path, { method, headers, body: body ? J(body) : undefined })
  const text = await r.text()
  let json = null; try { json = JSON.parse(text) } catch {}
  return { status: r.status, json, text }
}
async function makeCharacter (base, tag, ip) {
  const email = `t45-${tag}-${nonce}@example.com`, password = 'dock-and-rover'
  let r = await api(base, '/api/register', { method: 'POST', body: { email, password }, ip })
  check(`register account ${tag}`, r.status === 200, r.status === 200 ? '' : `status ${r.status} ${r.text.slice(0, 120)}`)
  r = await api(base, '/api/game-login', { method: 'POST', body: { email, password }, ip })
  check(`game-login ${tag}`, r.status === 200 && r.json?.session, r.status === 200 ? '' : `status ${r.status} ${r.text.slice(0, 120)}`)
  const name = `t45${tag}-${nonce}`
  r = await api(base, '/api/characters', { method: 'POST', body: { name, body: 'char.player' }, bearer: r.json.session, ip })
  check(`create character ${name}`, r.status === 200 && r.json?.token, r.status === 200 ? '' : `status ${r.status} ${r.text.slice(0, 120)}`)
  return { name: r.json.name, token: r.json.token }
}

// ------------------------------------------------------------------ ws client
const socks = new Set()
function connect (url, name, token) {
  const ws = new WebSocket(url)
  ws.binaryType = 'arraybuffer'
  const c = { ws, name, id: 0, ents: new Map(), spawns: new Map(), despawned: [], results: [], xp: [], crafts: [], pickups: [], party: [], invites: [], order: [], defs: null, seq: 1, iseq: 1, closed: null }
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
    else if (t === 0x0005) { const id = pv.getUint32(0, true), sp = { type: pv.getUint16(4, true), data: dec.decode(p.subarray(10, 10 + pv.getUint32(6, true))) }; c.spawns.set(id, sp); c.order.push({ k: 'spawn', id, ...sp }) }
    else if (t === 0x0006) { const id = pv.getUint32(0, true); c.spawns.delete(id); c.despawned.push(id); c.order.push({ k: 'despawn', id }) } else if (t === 0x0004) {
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
  c.ofType = (type) => [...c.spawns].filter(([, v]) => v.type === type).map(([id, v]) => ({ id, ...v }))
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

/** A deliver: walk/face is the caller's; returns the raw result. */
const deliver = (c, structure, item, qty) => c.call(OP.DELIVER, { structure, item, qty })
const billJ = (bill) => J(Object.fromEntries((bill ?? []).map((l) => [l.item, l.qty])))
/** Every bill line present, each REMAINING count as expected. */
const billIs = (bill, want) => Array.isArray(bill) && bill.length === Object.keys(BILL).length &&
  Object.keys(BILL).every((k) => bill.filter((l) => l.item === k).length === 1 && bill.find((l) => l.item === k).qty === want[k])
const remaining = (delivered) => Object.fromEntries(Object.entries(BILL).map(([k, n]) => [k, n - (delivered[k] ?? 0)]))
const bagKey = (inv) => J([...(inv ?? [])].map((s) => `${s.item}=${s.qty}`).sort())
const rowHealth = async (c, id, want, ms = 3000) => { const h = await wait(() => c.ents.get(id)?.health === want, ms); return h ? want : c.ents.get(id)?.health }

// ================================================================== run
const servers = { main: null }
let A, B
let exitCode = 0
const T0 = Date.now()
try {
  // ---- 1. build, start, accounts, join, defs
  const bin = build()
  check('server builds', true, bin)
  const port = await freePort(), dbUrl = `sqlite://${join(tmp, 't45.db')}`
  const env1 = { DATABASE_URL: dbUrl, SA_START: START }
  servers.main = await startServer(bin, port, env1, 'server 1')
  check('server 1 up (store, accounts only)', true, `:${port}`)
  const SITE = `http://127.0.0.1:${port}`, WS = `ws://127.0.0.1:${port}/ws`
  const charA = await makeCharacter(SITE, 'a', '198.18.45.1')
  const charB = await makeCharacter(SITE, 'b', '198.18.45.2')
  A = await joinGame(WS, charA, charA.token)
  B = await joinGame(WS, charB, charB.token)
  const items = A.defs.items ?? {}
  const site = items['site.dock'], dockI = items['structure.dock'], roverI = items['vehicle.rover']
  check('defs: site.dock is a structure with the 6 m footprint and the four-line bill',
    site?.kind === 'structure' && site.site?.becomes === 'structure.dock' && site.site?.footprint === 6 &&
    billIs(site.site?.bill, BILL), J(site && { kind: site.kind, site: site.site }))
  check('defs: structure.dock is station dock, both demolish; vehicle.rover is kind vehicle',
    dockI?.kind === 'structure' && dockI.station === 'dock' && dockI.demolish === true && site?.demolish === true && roverI?.kind === 'vehicle' && roverI.stack_max === 1,
    J([dockI && { station: dockI.station, demolish: dockI.demolish }, site?.demolish, roverI && { kind: roverI.kind, stack_max: roverI.stack_max }]))
  const R = Object.fromEntries((A.defs.recipes ?? []).map((r) => [r.id, r]))
  const roverR = R['recipe.vehicle.rover'], shipR = R['recipe.ship.v1'], siteR = R['recipe.site.dock']
  check('defs: rover + ship recipes at the dock, the site at the bench (construction 10/20/15)',
    roverR?.station === 'dock' && roverR.skill === 'construction' && roverR.level === 10 &&
    shipR?.station === 'dock' && shipR.level === 20 && siteR?.station === 'bench' && siteR.level === 15,
    J([roverR, shipR, siteR].map((r) => r && [r.id, r.station, r.skill, r.level, r.seconds])))
  const bagA0 = await bag(A)
  check('A starts with the seed (site, chest, 50 plates/wiring/iron, 4 cores)',
    count(bagA0, 'site.dock') === 1 && count(bagA0, 'structure.chest') === 1 && count(bagA0, 'mat.plate.steel') === 50 &&
    count(bagA0, 'mat.wiring') === 50 && count(bagA0, 'mat.ingot.iron') === 50 && count(bagA0, 'mat.core') === 4, bagKey(bagA0))
  const xpValue = Object.entries(BILL).reduce((n, [k, q]) => n + (items[k]?.value ?? NaN) * q, 0)
  const wantXP = Math.floor(xpValue / 2)
  check('defs carry the bill items\' values (the completion XP is Σ value×qty / 2)', Number.isFinite(wantXP), `${wantXP}`)
  const roversAtJoin = A.ofType(TYPE_VEHICLE).map((v) => v.id)
  check('the world rovers before anyone owns one', true, `${roversAtJoin.length} type-5 spawns`)

  // ---- 2. no ship at Vex
  const vex = [...A.spawns].find(([, v]) => v.type === TYPE_NPC && v.data === 'npc.quartermaster')?.[0]
  check('Quartermaster Vex is in the spawns', !!vex, '')
  const dVex = await walkTo(A, () => A.ents.get(vex).pos, 1.8, 20000)
  await faceAt(A, A.ents.get(vex).pos, 1.7)
  const shop = okResult(`A shop_list at Vex (${dVex.toFixed(2)} m)`, await A.call(OP.SHOP_LIST, { npc: vex }))
  check('Vex stocks no ship.v1 (the dock builds it now)', Array.isArray(shop.stock) && shop.stock.length > 0 && !shop.stock.some((s) => s.item === 'ship.v1'), J(shop.stock?.map((s) => s.item)))

  // ---- 3. the chest, and a deliver to a non-site
  const cp = await placeSomewhere(A, 'structure.chest', 'A chest', 8)
  const chestId = cp.structure
  check('A places its chest', chestId > 0, `${chestId}`)
  await approach(A, chestId)
  const notSite = await deliver(A, chestId, 'mat.plate.steel', 1)
  check('deliver to the chest (not a site): status 5', notSite?.status === 5, notSite ? `status ${notSite.status} ${J(notSite.body)}` : 'nothing')
  if (notSite?.status === 1) throw new Fail('the server has no 0x001A deliver')

  // ---- 4. the site
  const sp = await placeSomewhere(A, 'site.dock', 'A site', 7)
  const siteId = sp?.structure
  check('place site.dock: ok, structure id, the site left the bag', siteId > 0 && count(sp.inventory, 'site.dock') === 0, J(sp))
  const wantSite = structOwnerData('site.dock', charA.name)
  const ssa = await wait(() => A.spawns.get(siteId)), ssb = await wait(() => B.spawns.get(siteId))
  check('A sees the site spawn: type 9, data site.dock\\0<A>', ssa?.type === TYPE_STRUCTURE && ssa.data === wantSite, J(ssa))
  check('B sees the same spawn', ssb?.type === TYPE_STRUCTURE && ssb.data === wantSite, J(ssb))
  const sitePos = (await wait(() => A.ents.get(siteId)))?.pos
  check('the site is in the snapshot ~2 m ahead of A', sitePos && dist(sitePos, A.me().pos) < 3.2, sitePos ? `${dist(sitePos, A.me().pos).toFixed(2)} m` : 'absent')
  check('the site row\'s health is 0 (pct)', (await rowHealth(A, siteId, 0)) === 0, `${A.ents.get(siteId)?.health}`)
  await faceAt(A, sitePos)
  const bagBeforePeek = await bag(A)
  const pk = decodeDeliverResult(okResult('A peeks the site (deliver qty 0)', await deliver(A, siteId, 'mat.plate.steel', 0)))
  check('peek: the full bill, every line, pct 0, nothing moved', billIs(pk.bill, BILL) && pk.pct === 0 && bagKey(pk.inventory) === bagKey(bagBeforePeek),
    `${billJ(pk.bill)} pct ${pk.pct}`)

  // ---- 5. refusals
  await approach(B, siteId)
  refused('B delivers before any party: not_yours', await deliver(B, siteId, 'mat.plate.steel', 5), 'not_yours')
  refused('A delivers ammo (not in the bill): not_wanted', await deliver(A, siteId, 'ammo.cell', 5), 'not_wanted')

  // ---- 6. A's 10 plates
  const delivered = {}
  const give = async (c, item, qty, expectMoved, tag) => {
    const before = count(await bag(c), item)
    const r = decodeDeliverResult(okResult(tag, await deliver(c, siteId, item, qty)))
    delivered[item] = (delivered[item] ?? 0) + expectMoved
    const units = Object.values(delivered).reduce((a, b) => a + b, 0)
    check(`${tag}: ${expectMoved} moved, bill remaining + pct ${pctOf(units)}`,
      billIs(r.bill, remaining(delivered)) && r.pct === pctOf(units) && count(r.inventory, item) === before - expectMoved,
      `bill ${billJ(r.bill)} pct ${r.pct} bag ${item} ${before}→${count(r.inventory, item)}`)
    return { r, units }
  }
  let g = await give(A, 'mat.plate.steel', 10, 10, 'A delivers 10 plates')
  check('the site row\'s health follows the pct', (await rowHealth(A, siteId, pctOf(g.units))) === pctOf(g.units), `${A.ents.get(siteId)?.health}`)

  // ---- 7. party: B delivers
  okResult('A invites B to the party', await A.call(OP.PARTY_INVITE, { target: B.id }))
  check('B gets the invite', !!(await wait(() => B.invites.at(-1))), '')
  okResult('B accepts', await B.call(OP.PARTY_RESPOND, { accept: true }))
  check('A sees a party of two', !!(await wait(() => A.party.find((u) => u.members?.length === 2))), J(A.party.at(-1)))
  await faceAt(B, sitePos)
  await give(B, 'mat.plate.steel', 60, 50, 'B (party) delivers qty 60 plates — clamped to the 50 the line wants')
  g = await give(B, 'mat.wiring', 4, 4, 'B delivers 4 wiring')
  check('B\'s row health reads the same pct', (await rowHealth(B, siteId, pctOf(g.units))) === pctOf(g.units), `${B.ents.get(siteId)?.health}`)
  await faceAt(A, sitePos)
  refused('A delivers plates to the full line: not_wanted', await deliver(A, siteId, 'mat.plate.steel', 5), 'not_wanted')

  // ---- 8. one site-or-dock per character
  await approach(B, chestId)
  okResult('B (party) puts its seeded site into A\'s chest', await B.call(OP.CHEST_PUT, { structure: chestId, item: 'site.dock', qty: 1 }))
  await approach(A, chestId)
  const tk = okResult('A takes the second site out', await A.call(OP.CHEST_TAKE, { structure: chestId, item: 'site.dock', qty: 1 }))
  check('A now carries a second site.dock', count(tk.inventory, 'site.dock') === 1, bagKey(tk.inventory))
  let second = null
  {
    const heads = headings(A)
    for (let i = 0; i < 8; i++) {
      await walkTo(A, add(A.me().pos, scale(heads[i % 4], 4 + 2 * Math.floor(i / 4))), 0.8, 8000)
      await faceDir(A, heads[(i + 1) % 4])
      second = await A.call(OP.PLACE, { item: 'site.dock' })
      if (!(second?.status === 3 && second.body?.reason === 'no_room')) break
    }
  }
  refused('A places a second site while one stands: too_many', second, 'too_many')

  // ---- 9. finish the bill
  await approach(A, siteId)
  await give(A, 'mat.wiring', 20, 20, 'A delivers 20 wiring')
  g = await give(A, 'mat.ingot.iron', 20, 20, 'A delivers 20 iron')
  check('row health 96 before the last line', (await rowHealth(A, siteId, pctOf(g.units))) === pctOf(g.units), `${A.ents.get(siteId)?.health}`)
  const ordA = A.order.length, ordB = B.order.length, xp0 = A.xp.length, tLast = Date.now()
  const fin = decodeDeliverResult(okResult('A delivers the 4 cores (the last unit)', await deliver(A, siteId, 'mat.core', 4)))
  check('the last result: an all-zero bill at 100', billIs(fin.bill, remaining(BILL)) && fin.pct === 100 && count(fin.inventory, 'mat.core') === 0, `${billJ(fin.bill)} pct ${fin.pct}`)
  const wantDock = structOwnerData('structure.dock', charA.name)
  const swapped = (c, from) => {
    const ev = c.order.slice(from).filter((e) => e.id === siteId)
    const di = ev.findIndex((e) => e.k === 'despawn')
    return di >= 0 && ev.slice(di + 1).some((e) => e.k === 'spawn' && e.type === TYPE_STRUCTURE && e.data === wantDock) ? ev : null
  }
  check('A sees despawn then spawn of the SAME id as structure.dock\\0<A>', !!(await wait(() => swapped(A, ordA), 3000)), J(A.order.slice(ordA).filter((e) => e.id === siteId)))
  check('B sees the same swap', !!(await wait(() => swapped(B, ordB), 3000)), J(B.order.slice(ordB).filter((e) => e.id === siteId)))
  const dockId = siteId
  const xp = await wait(() => A.xp.slice(xp0).find((x) => x.skill === 'construction'), 3000)
  check(`A's construction XP is the bill's Σ value×qty / 2 (${wantXP}) → level ≥ ${roverR.level}`, xp?.xp === wantXP && xp.level >= roverR.level,
    `${J(xp)} ${xp ? ((xp.at - tLast) / 1000).toFixed(1) + ' s after' : ''}`)
  check('B got no construction XP for the completion', !B.xp.some((x) => x.skill === 'construction'), J(B.xp))
  const dockPos = A.ents.get(dockId)?.pos
  check('the dock stands where the site stood', dockPos && dist(dockPos, sitePos) < 0.05, dockPos ? `${dist(dockPos, sitePos).toFixed(3)} m` : 'absent')
  const onDock = await deliver(A, dockId, 'mat.core', 1)
  check('deliver to the finished dock: status 5', onDock?.status === 5, onDock ? `status ${onDock.status} ${J(onDock.body)}` : 'nothing')

  // ---- 10. a rover at the dock
  await faceAt(A, dockPos)
  const roversBefore = new Set(A.ofType(TYPE_VEHICLE).map((v) => v.id))
  const tc = Date.now()
  const rc = await craft(A, dockId, roverR.id, 1)
  okResult('A crafts recipe.vehicle.rover with npc = the dock id', rc.r)
  check('craft_end done vehicle.rover within 35 s', rc.ends[0]?.reason === 'done' && rc.ends[0].item === 'vehicle.rover' && Date.now() - tc < 35000,
    `${J(rc.ends)} in ${((Date.now() - tc) / 1000).toFixed(1)} s (recipe ${roverR.seconds} s)`)
  const bagR = await bag(A)
  check('the rover is in A\'s bag', count(bagR, 'vehicle.rover') === 1, bagKey(bagR))
  const newRover = await wait(() => A.ofType(TYPE_VEHICLE).find((v) => !roversBefore.has(v.id) && A.ents.get(v.id)), 4000)
  const roverPos = newRover && A.ents.get(newRover.id).pos
  check('a new type-5 vehicle spawns within 8 m of the dock', !!newRover && dist(roverPos, dockPos) <= 8,
    newRover ? `id ${newRover.id} data ${J(newRover.data)} ${dist(roverPos, dockPos).toFixed(2)} m from the dock` : `none new (${J([...roversBefore])})`)
  check('B sees the rover too', !!(await wait(() => B.spawns.get(newRover.id)?.type === TYPE_VEHICLE, 3000)), '')
  refused('a second rover craft: no_space (one per owner)', await A.call(OP.CRAFT, { npc: dockId, recipe: roverR.id, qty: 1 }), 'no_space')

  // ---- 11. demolish
  await approach(B, dockId, 2.6)
  refused('B pick_up A\'s dock: not_yours', await B.call(OP.PICK_UP, { structure: dockId }), 'not_yours')
  await faceAt(A, dockPos)
  const bagD = await bag(A)
  const from = A.pickups.length, tD = Date.now()
  const pu = okResult('A pick_up its dock', await A.call(OP.PICK_UP, { structure: dockId }))
  check('pick_up result is {seconds: 3}', pu?.seconds === 3, J(pu))
  const end = await wait(() => { A.send([0, 0, ...norm(tangent(sub(dockPos, A.me().pos), norm(A.me().pos)))]); return A.pickups.slice(from)[0] }, 7000)
  check('pick_up_end ok after ~3 s', end?.ok === true && end.structure === dockId && Date.now() - tD >= 2800, `${J(end && { ...end, inventory: undefined })} ${((Date.now() - tD) / 1000).toFixed(1)} s`)
  check('nothing comes back: the bag is exactly what it was', bagKey(end?.inventory) === bagKey(bagD) && bagKey(await bag(A)) === bagKey(bagD), `${bagKey(bagD)} → ${bagKey(end?.inventory)}`)
  check('B sees the dock despawn', !!(await wait(() => B.despawned.includes(dockId), 3000)), '')
  check('the rover stays (owning the item is owning it)', !!A.spawns.get(newRover.id), '')

  // ---- 12. a new site, half built
  const sp2 = await placeSomewhere(A, 'site.dock', 'A site 2', 6)
  const site2 = sp2.structure
  check('A places its second site (the slot is free again)', site2 > 0 && site2 !== dockId, `${site2}`)
  const site2Pos = (await wait(() => A.ents.get(site2)))?.pos
  await faceAt(A, site2Pos)
  for (const k of Object.keys(delivered)) delete delivered[k]
  const give2 = async (item, qty) => {
    const r = decodeDeliverResult(okResult(`A delivers ${qty} ${item} to site 2`, await deliver(A, site2, item, qty)))
    delivered[item] = qty
    return r
  }
  await give2('mat.plate.steel', 20); await give2('mat.wiring', 22)
  const half = await give2('mat.ingot.iron', 12)
  check('site 2 is half built: 54 of 108 → pct 50', half.pct === 50 && billIs(half.bill, remaining(delivered)), `${billJ(half.bill)} pct ${half.pct}`)
  check('site 2 row health 50', (await rowHealth(A, site2, 50)) === 50, `${A.ents.get(site2)?.health}`)

  // ---- 13. restart
  A.close(); B.close()
  await sleep(1500) // the disconnect saves land
  await stopServer(servers.main)
  check('server 1 stops on SIGTERM', true, '')
  servers.main = await startServer(bin, port, env1, 'server 2')
  check('server 2 up on the same database', true, dbUrl)
  A = await joinGame(WS, charA, charA.token)
  await sleep(800)
  const back = await wait(() => A.structs().find((s) => s.data === wantSite && A.ents.get(s.id)), 3000)
  check('the half-built site is back: site.dock\\0<A>', !!back && dist(A.ents.get(back.id).pos, site2Pos) < 0.05,
    back ? `${dist(A.ents.get(back.id).pos, site2Pos).toFixed(3)} m` : J(A.structs().map((s) => s.data)))
  check('its row health is still 50', (await rowHealth(A, back.id, 50)) === 50, `${A.ents.get(back.id)?.health}`)
  check('only one site/dock of A\'s exists', A.structs().filter((s) => s.data === wantSite || s.data === wantDock).length === 1, J(A.structs().map((s) => s.data)))
  const rovers2 = await wait(() => { const n = A.ofType(TYPE_VEHICLE).length; return n === roversAtJoin.length + 1 ? n : null }, 4000)
  check('A\'s rover is back in the world (one more type-5 than at first join)', rovers2 === roversAtJoin.length + 1,
    `${A.ofType(TYPE_VEHICLE).length} vs ${roversAtJoin.length} at first join`)
  await approach(A, back.id)
  const pk2 = decodeDeliverResult(okResult('A peeks site 2 after the restart', await deliver(A, back.id, 'mat.core', 0)))
  check('the peek shows the same remaining bill at 50', pk2.pct === 50 && billIs(pk2.bill, remaining(delivered)), `${billJ(pk2.bill)} pct ${pk2.pct}`)

  // ---- 14. beyond the contract: a site's delivered stacks are not a chest
  const steal = await A.call(OP.CHEST_TAKE, { structure: back.id, item: 'mat.plate.steel', qty: 1 })
  check('chest_take on a site does not hand delivered plates back', steal?.status !== 0, steal ? `status ${steal.status} ${J(steal.body)}` : 'nothing')
  A.close()
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
