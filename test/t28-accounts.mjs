#!/usr/bin/env node
/**
 * Phase 16 acceptance, live: C153, C154, C156, C157, C159.
 *
 * Register → POST /api/game-login (the launcher's sign-in: a session in the
 * body, sent back as `Authorization: Bearer`) → GET /api/characters is []
 * (game-login mints nothing; a new account has no character) → POST
 * /api/characters makes the first one (char.player, a per-run
 * name) → JOIN THE GAME with its token under a different hello name:
 * the self spawn carries the ROW's name, no NUL → the account page shows the
 * same player and credits → a second game-login still lists ONE row → wrong
 * password / unknown email are one 401 → the login bucket 429s a burst →
 * POST /api/characters: a second character (char.ubc.f), the name rule,
 * case-insensitive uniqueness, five per account → C159: JOIN as it while the
 * first is in the world: its self spawn data is `name \0 char.ubc.f`, the
 * other client sees the same bytes, and it sees the first as a bare name → a
 * password change kills the bearer (401) but the character token still joins →
 * logout ends a bearer → the Phase 7 link-code / redeem / import routes are
 * 404 → delete the account: its characters go with it.
 *
 * Two run modes (GDD "The machines"):
 *   default      a server that seats guests (kind, SA_GUESTS=1): a made-up
 *                token joins, and a deleted character's token joins again
 *                as a FRESH guest with the start credits.
 *   SA_STRICT=1  a server with a store and SA_GUESTS unset (production's
 *                rule): a made-up token's hello is closed 1008 before any
 *                spawn, and a deleted character's token is refused too.
 *
 * Run: node test/t28-accounts.mjs   (needs a deployed stack with a store)
 *   SA_SERVER_URL / SA_SITE_URL override the wire and site origins.
 *   e.g. SA_SITE_URL=http://localhost:18080 SA_SERVER_URL=ws://localhost:18080/ws
 */
const SITE = process.env.SA_SITE_URL ?? 'http://192.168.1.163'
const WS = process.env.SA_SERVER_URL ?? 'ws://192.168.1.163/ws'
const STRICT = process.env.SA_STRICT === '1'

const enc = new TextEncoder(), dec = new TextDecoder()
const u8 = (n) => new Uint8Array(n)
const frame = (t, b) => { const o = u8(2 + b.length); new DataView(o.buffer).setUint16(0, t, true); o.set(b, 2); return o }
function hello (name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(10 + n.length + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
// cmd 0x000E: u16 seq | u16 op | u32 len | JSON (PROTOCOL "cmd"); op 4 = inventory.
function cmd (seq, op, body) {
  const d = enc.encode(JSON.stringify(body)), b = u8(8 + d.length), dv = new DataView(b.buffer)
  dv.setUint16(0, seq, true); dv.setUint16(2, op, true); dv.setUint32(4, d.length, true); b.set(d, 8)
  return frame(0x000e, b)
}
// A player spawn row's data: `name`, or `name \0 body` (GDD "Characters";
// the same split as lib/wire.mjs decodeSpawn).
const split = (data) => {
  const i = data?.indexOf('\0') ?? -1
  return i < 0 ? { name: data, body: 'char.player' } : { name: data.slice(0, i), body: data.slice(i + 1) }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const HEX32 = /^[0-9a-f]{32}$/

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}
const skip = (name) => console.log(`SKIP ${name}`)

// A fake client address per role. web.go keys the login bucket on
// CF-Connecting-IP first (webClientIP, the gatekeeper's rule), so each role
// gets its own bucket and the burst check below cannot starve the others.
const run = Date.now()
const ip = (n) => `198.18.${(run >> 8) & 0xff}.${(run + n) & 0xff}`

// A jarred fetch client (Node fetch has no cookie jar; the jar is a Map).
// `bearer` is the launcher's way in; `addr` the CF-Connecting-IP it claims.
function client ({ bearer, addr = ip(0) } = {}) {
  const jar = new Map()
  return async (path, { method = 'GET', body, csrf = true } = {}) => {
    const headers = { 'Content-Type': 'application/json', 'CF-Connecting-IP': addr }
    if (csrf && method === 'POST') headers['X-Requested-With'] = 't28'
    if (bearer) headers.Authorization = `Bearer ${bearer}`
    if (jar.size) headers.Cookie = [...jar.entries()].map(([k, v]) => `${k}=${v}`).join('; ')
    const r = await fetch(SITE + path, { method, headers, body: body ? JSON.stringify(body) : undefined })
    for (const sc of r.headers.getSetCookie?.() ?? []) {
      const [pair] = sc.split(';')
      const [k, v] = pair.split('=')
      if (v === '' || sc.includes('Max-Age=-1')) jar.delete(k)
      else jar.set(k, v)
    }
    return r
  }
}
const json = async (r) => { try { return await r.json() } catch { return null } }
const text = async (r) => (await r.text()).trim()

// Join the game with a token. Resolves {id, data, credits, close, spawns}:
// id/data are the hello_ack entity and ITS spawn row's data (the self
// spawn), credits the inventory cmd's answer. A server that closes first
// resolves id 0 with the close code and how many spawns preceded it.
function joinGame (token, name = 't28') {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(WS)
    ws.binaryType = 'arraybuffer'
    const out = { id: 0, data: null, credits: null, close: null, spawns: 0 }
    let done = false
    const finish = () => { if (!done) { done = true; clearTimeout(timer); resolve(out) } }
    const timer = setTimeout(() => { ws.close(); reject(new Error('join timeout')) }, 8000)
    ws.addEventListener('open', () => ws.send(hello(name, token)))
    ws.addEventListener('message', (ev) => {
      const dv = new DataView(ev.data), t = dv.getUint16(0, true), p = new Uint8Array(ev.data, 2)
      if (t === 0x0002) out.id = dv.getUint32(2 + 8, true)
      else if (t === 0x0005) {
        out.spawns++
        if (out.id && dv.getUint32(2, true) === out.id && out.data === null) {
          out.data = dec.decode(p.subarray(10, 10 + dv.getUint32(2 + 6, true)))
          ws.send(cmd(1, 0x0004, {}))
        }
      } else if (t === 0x000f && dv.getUint16(2 + 2, true) === 0x0004) {
        out.credits = JSON.parse(dec.decode(p.subarray(9)) || '{}').credits ?? null
        // Stay a beat so the autosave sees us, then leave.
        setTimeout(() => { ws.close(); finish() }, 600)
      }
    })
    ws.addEventListener('close', (ev) => {
      if (out.close === null) out.close = ev.code
      if (!out.data) finish() // closed before the self spawn: a refusal
    })
    ws.addEventListener('error', () => {}) // the close event carries the code
  })
}

// Stay in the game: like joinGame, but the socket is kept open and every
// spawn row is collected (entity id → raw data) until close() is called.
// Resolves once the self spawn arrives (or the server closes first).
function openGame (token, name = 't28') {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(WS)
    ws.binaryType = 'arraybuffer'
    const out = { id: 0, data: null, close: null, rows: new Map(), leave: () => ws.close() }
    let done = false
    const finish = () => { if (!done) { done = true; clearTimeout(timer); resolve(out) } }
    const timer = setTimeout(() => { ws.close(); reject(new Error('join timeout')) }, 8000)
    ws.addEventListener('open', () => ws.send(hello(name, token)))
    ws.addEventListener('message', (ev) => {
      const dv = new DataView(ev.data), t = dv.getUint16(0, true), p = new Uint8Array(ev.data, 2)
      if (t === 0x0002) out.id = dv.getUint32(2 + 8, true)
      else if (t === 0x0005) {
        const id = dv.getUint32(2, true)
        const data = dec.decode(p.subarray(10, 10 + dv.getUint32(2 + 6, true)))
        out.rows.set(id, data)
        if (out.id && id === out.id && out.data === null) { out.data = data; finish() }
      }
    })
    ws.addEventListener('close', (ev) => { if (out.close === null) out.close = ev.code; finish() })
    ws.addEventListener('error', () => {})
  })
}
// Wait (up to ms) until a socket from openGame has seen a row for id.
async function rowFor (g, id, ms = 3000) {
  for (let t = 0; t < ms && !g.rows.has(id); t += 50) await sleep(50)
  return g.rows.get(id) ?? null
}

const email = `t28-${run}@example.com`
// The first character's name: per run (names are unique across accounts and
// kind's database outlives a run) and inside the 3-16 name rule, which the
// email's local part (17 characters) is not.
const local = `t28-${run.toString(36)}`
const a = client()

// --- 1. C153/C157: register → game-login → bearer → no character → make one
let r = await a('/api/register', { method: 'POST', body: { email, password: 'orbital-insertion' } })
check('C153 register', r.status === 200, `status ${r.status}`)
r = await a('/api/game-login', { method: 'POST', body: { email, password: 'orbital-insertion' } })
const gl = await json(r)
check('C153 game-login returns a session and name', r.status === 200 && HEX32.test(gl?.session ?? '') && gl?.name === email,
  `status ${r.status}, name ${gl?.name}`)
check('C153 game-login sets no cookie', (r.headers.getSetCookie?.() ?? []).length === 0)
const g = client({ bearer: gl?.session })
r = await g('/api/me')
check('C153 bearer session accepted by /api/me', r.status === 200, `status ${r.status}`)
r = await g('/api/characters')
let chars = await json(r)
check('C157 a new account has no character (game-login mints none)', r.status === 200 && Array.isArray(chars) && chars.length === 0,
  `status ${r.status}, ${JSON.stringify(chars)}`)
r = await g('/api/characters', { method: 'POST', body: { name: local, body: 'char.player' } })
const first = await json(r)
check('C157 create the first character', r.status === 200 && first?.name === local && first?.body === 'char.player' &&
  HEX32.test(first?.token ?? ''), `status ${r.status}, ${JSON.stringify(first && [first.name, first.body])}`)

// --- 2. C154: the token joins under the ROW's name ------------------------
const j1 = await joinGame(first?.token ?? '', 'ignored-name')
check('C154 character joins under the row name, not hello.name (char.player: no NUL)', j1.id > 0 && j1.data === local,
  `entity ${j1.id}, data ${JSON.stringify(j1.data)}, close ${j1.close}`)
await sleep(1500) // let the save land

// --- 3. C156 (web): the account page tells the truth; game-login is idempotent
r = await g('/api/me')
const me = await json(r)
check('C156 the account page lists the same character', me?.players?.length === 1 &&
  me.players[0].name === first?.name && me.players[0].credits === first?.credits && j1.credits === first?.credits,
  `players ${JSON.stringify(me?.players?.map((p) => [p.name, p.credits]))}, wire credits ${j1.credits}`)
r = await a('/api/game-login', { method: 'POST', body: { email, password: 'orbital-insertion' } })
const gl2 = await json(r)
r = await client({ bearer: gl2?.session })('/api/characters')
chars = await json(r)
check('C156 a second game-login: the list is still one row', r.status === 200 && chars?.length === 1 && chars[0].token === first?.token,
  `rows ${chars?.length}`)

// --- 4. C153: one 401 body; the bucket ------------------------------------
const b = client({ addr: ip(1) })
r = await b('/api/game-login', { method: 'POST', body: { email, password: 'wrong-password' } })
const wrongBody = await text(r)
const wrongStatus = r.status
r = await b('/api/game-login', { method: 'POST', body: { email: `nobody-${run}@example.com`, password: 'orbital-insertion' } })
const unknownBody = await text(r)
check('C153 wrong password and unknown email: 401, one body', wrongStatus === 401 && r.status === 401 && wrongBody === unknownBody,
  `${wrongStatus} ${JSON.stringify(wrongBody)} / ${r.status} ${JSON.stringify(unknownBody)}`)
r = await a('/api/game-login', { method: 'POST', body: { email, password: 'orbital-insertion' }, csrf: false })
check('C153 game-login needs X-Requested-With', r.status === 403, `status ${r.status}`)
{
  const burst = client({ addr: ip(2) })
  const codes = []
  for (let i = 0; i < 8; i++) {
    codes.push((await burst('/api/game-login', { method: 'POST', body: { email, password: 'wrong-password' } })).status)
  }
  check('C153 the login bucket 429s a game-login burst', codes[0] === 401 && codes.includes(429), codes.join(' '))
}

// --- 5. C157: characters ---------------------------------------------------
// Names are unique across EVERY account, and kind's database outlives a
// crashed run, so the second character is "Kade" plus a per-run tag.
const tag = [0, 1, 2, 3].map((i) => String.fromCharCode(97 + Math.floor(run / 26 ** i) % 26)).join('')
const kade = `Kade ${tag}`
const create = (name, body) => g('/api/characters', { method: 'POST', body: { name, body } })
r = await create(kade, 'char.ubc.f')
const k = await json(r)
check('C157 create a second character', r.status === 200 && k?.name === kade && k?.body === 'char.ubc.f' &&
  HEX32.test(k?.token ?? '') && k.token !== first?.token, `status ${r.status}, ${JSON.stringify(k && [k.name, k.body])}`)
r = await g('/api/characters')
chars = await json(r)
check('C157 list is both, oldest first', chars?.length === 2 && chars[0].token === first?.token && chars[1].token === k?.token,
  JSON.stringify(chars?.map((c) => c.name)))
r = await create(kade.toLowerCase(), 'char.player')
check('C157 case-variant name is taken (409)', r.status === 409 && (await text(r)) === 'name taken', `status ${r.status}`)
for (const [bad, body, want] of [['K', 'char.player', 'bad name'], ['Kade!', 'char.player', 'bad name'], [`Nope ${tag}`, 'char.nope', 'bad body']]) {
  r = await create(bad, body)
  const t = await text(r)
  check(`C157 ${JSON.stringify(bad)} / ${body} refused 400 ${want}`, r.status === 400 && t === want, `status ${r.status} ${JSON.stringify(t)}`)
}
const extra = []
for (const [i, body] of [[3, 'char.player.f'], [4, 'char.ubc'], [5, 'char.player']]) {
  r = await create(`${tag} pilot ${i}`, body)
  extra.push(r.status)
}
check('C157 up to five characters', extra.every((s) => s === 200), extra.join(' '))
r = await create(`${tag} pilot 6`, 'char.player')
check('C157 the sixth is refused (409 character limit)', r.status === 409 && (await text(r)) === 'character limit', `status ${r.status}`)
r = await g('/api/me')
const me5 = await json(r)
check('C157 the account page shows the same rows', me5?.players?.length === 5 &&
  me5.players.map((p) => p.name + '|' + p.body).join(',') === (await json(await g('/api/characters')))?.map((c) => c.name + '|' + c.body).join(','),
  `players ${me5?.players?.length}`)
// C159: the first character stays in the world while Kade joins.
const want = `${kade}\0char.ubc.f`
const o1 = await openGame(first?.token ?? '', 'observer')
check('C159 the char.player row is its bare name (no NUL)', o1.id > 0 && o1.data === local && !o1.data.includes('\0') &&
  split(o1.data).body === 'char.player', `entity ${o1.id}, data ${JSON.stringify(o1.data)}, close ${o1.close}`)
const o2 = await openGame(k?.token ?? '', 'not-kade')
const ks = split(o2.data)
check('C159 join as char.ubc.f: self spawn data is name \\0 body', o2.id > 0 && o2.data === want,
  `entity ${o2.id}, data ${JSON.stringify(o2.data)}, close ${o2.close}`)
check('C159 the split gives name and body', ks.name === kade && ks.body === 'char.ubc.f', JSON.stringify(ks))
const seen = await rowFor(o1, o2.id)
check('C159 the other client sees the same bytes for Kade', seen === want, `row ${JSON.stringify(seen)}`)
const back = await rowFor(o2, o1.id)
check('C159 Kade sees the char.player row as a bare name', back === local, `row ${JSON.stringify(back)}`)
o2.leave(); o1.leave()
await sleep(1000)

// --- 6. C156/C157: a password change kills the bearer, not the token -------
const c = client({ addr: ip(3) })
r = await c('/api/login', { method: 'POST', body: { email, password: 'orbital-insertion' } })
check('C156 site login (cookie)', r.status === 200, `status ${r.status}`)
r = await c('/api/password', { method: 'POST', body: { old: 'orbital-insertion', new: 'retrograde-burn' } })
check('C156 password change accepted', r.status === 200, `status ${r.status}`)
r = await g('/api/characters')
check('C156 the old bearer is dead after a password change', r.status === 401, `status ${r.status}`)
const j3 = await joinGame(first?.token ?? '', 'still-me')
check('C157 the character token still joins after the change', j3.id > 0 && j3.data === local,
  `entity ${j3.id}, data ${JSON.stringify(j3.data)}, close ${j3.close}`)
r = await c('/api/game-login', { method: 'POST', body: { email, password: 'retrograde-burn' } })
const h = client({ bearer: (await json(r))?.session, addr: ip(3) })
r = await h('/api/logout', { method: 'POST' })
const outStatus = r.status
r = await h('/api/characters')
check('C153 logout ends a bearer session', outStatus === 200 && r.status === 401, `logout ${outStatus}, then ${r.status}`)

// --- 7. C153: the side doors are gone --------------------------------------
for (const path of ['/api/link-code', '/api/redeem', '/api/import-token']) {
  const get = await c(path)
  const post = await c(path, { method: 'POST', body: { code: 'ABCDEFGH', token: 'x' } })
  check(`C153 ${path} is 404`, get.status === 404 && post.status === 404, `GET ${get.status}, POST ${post.status}`)
}

// --- 8. C154: the machines -------------------------------------------------
let guestCredits = null
if (STRICT) {
  const s1 = await joinGame(`t28madeup${run}`, 'nobody')
  check('C154 strict: a made-up token is closed 1008 before any spawn', s1.id === 0 && s1.close === 1008 && s1.spawns === 0,
    `entity ${s1.id}, close ${s1.close}, spawns ${s1.spawns}`)
  const s2 = await joinGame(`t28guestrow${run}`, 'guest')
  check('C154 strict: a guest token is refused too', s2.id === 0 && s2.close === 1008 && s2.spawns === 0,
    `entity ${s2.id}, close ${s2.close}, spawns ${s2.spawns}`)
} else {
  const gj = await joinGame(`t28madeup${run}`, 'a-guest')
  guestCredits = gj.credits
  check('C154 guests: a made-up token joins under its hello name', gj.id > 0 && gj.data === 'a-guest',
    `entity ${gj.id}, data ${JSON.stringify(gj.data)}, credits ${gj.credits}`)
  skip('strict-mode checks (SA_STRICT unset)')
}

// --- 9. delete: the characters go with the account --------------------------
await sleep(500) // the guest join above is saved by the server AFTER its socket closes; let it land
const before = await json(await fetch(SITE + '/api/stats'))
r = await c('/api/delete', { method: 'POST', body: { password: 'retrograde-burn' } })
check('C157 delete with password', r.status === 200, `status ${r.status}`)
r = await c('/api/me')
check('C157 session gone after delete', r.status === 401, `status ${r.status}`)
const after = await json(await fetch(SITE + '/api/stats'))
check('C157 all five characters went with the account', after?.players === before?.players - 5,
  `players ${before?.players} -> ${after?.players}`)
if (STRICT) {
  for (const [who, tok] of [['first', first?.token], ['second', k?.token]]) {
    const d = await joinGame(tok ?? '', 'orphan')
    check(`C154 strict: the deleted ${who} character's token is refused 1008`, d.id === 0 && d.close === 1008 && d.spawns === 0,
      `entity ${d.id}, close ${d.close}, spawns ${d.spawns}`)
  }
} else {
  const d = await joinGame(first?.token ?? '', 'fresh-guest')
  check('C154 guests: the deleted token joins as a FRESH guest', d.id > 0 && d.data === 'fresh-guest' && d.credits === guestCredits,
    `entity ${d.id}, data ${JSON.stringify(d.data)}, credits ${d.credits} (a new guest: ${guestCredits})`)
}

const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks${STRICT ? ', strict' : ''})`)
process.exit(fails.length ? 1 : 0)
