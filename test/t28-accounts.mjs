#!/usr/bin/env node
/**
 * Phase 7 acceptance, live: C54–C59.
 *
 * Register → me → mint a link code → redeem it (the game client's
 * exchange) → JOIN THE GAME with the issued token and buy nothing, just
 * exist → the account page shows that same player (C56 cross-checked
 * against the wire, not the database) → import a legacy guest token →
 * change password (other session dies) → delete account → the issued
 * token joins as a FRESH GUEST afterwards (the old player is gone; the
 * hello path itself stays open by the coexistence rule).
 *
 * Run: node test/t28-accounts.mjs   (needs a deployed stack with a store)
 *   SA_SERVER_URL / SA_SITE_URL override the wire and site origins.
 */
const SITE = process.env.SA_SITE_URL ?? 'http://192.168.1.163'
const WS = process.env.SA_SERVER_URL ?? 'ws://192.168.1.163/ws'

const enc = new TextEncoder()
const u8 = (n) => new Uint8Array(n)
const frame = (t, b) => { const o = u8(2 + b.length); new DataView(o.buffer).setUint16(0, t, true); o.set(b, 2); return o }
function hello (name, token) {
  const n = enc.encode(name), t = enc.encode(token)
  const b = u8(10 + n.length + t.length), dv = new DataView(b.buffer)
  dv.setUint16(0, 2, true); dv.setUint32(2, n.length, true); b.set(n, 6)
  dv.setUint32(6 + n.length, t.length, true); b.set(t, 10 + n.length)
  return frame(0x0001, b)
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const checks = []
const check = (name, ok, detail = '') => {
  checks.push([name, ok])
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? '  ' + detail : ''}`)
}

// A jarred fetch client (Node fetch has no cookie jar; the jar is a Map).
function client () {
  const jar = new Map()
  return async (path, { method = 'GET', body, csrf = true } = {}) => {
    const headers = { 'Content-Type': 'application/json' }
    if (csrf && method === 'POST') headers['X-Requested-With'] = 't28'
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

// Join the game with a token; resolve {id} once hello_ack arrives, then close.
function joinGame (token, name = 't28') {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(WS)
    ws.binaryType = 'arraybuffer'
    const timer = setTimeout(() => { ws.close(); reject(new Error('join timeout')) }, 8000)
    ws.addEventListener('open', () => ws.send(hello(name, token)))
    ws.addEventListener('message', (ev) => {
      const t = new DataView(ev.data).getUint16(0, true)
      if (t === 0x0002) {
        const id = new DataView(ev.data, 2).getUint32(8, true)
        clearTimeout(timer)
        // Stay a beat so the autosave sees us, then leave.
        setTimeout(() => { ws.close(); resolve(id) }, 600)
      }
    })
    ws.addEventListener('error', () => { clearTimeout(timer); reject(new Error('ws error')) })
  })
}

const email = `t28-${Date.now()}@example.com`
const a = client()

// --- C54: register, session, wrong password, rate limit ---------------------
let r = await a('/api/register', { method: 'POST', body: { email, password: 'orbital-insertion' } })
check('C54 register + auto-login', r.status === 200, `status ${r.status}`)
r = await a('/api/me')
check('C54 session works', r.status === 200)
const b = client()
r = await b('/api/login', { method: 'POST', body: { email, password: 'wrong-password' } })
check('C54 wrong password refused', r.status === 401, `status ${r.status}`)
r = await b('/api/login', { method: 'POST', body: { email, password: 'orbital-insertion' } })
check('C54 second session logs in', r.status === 200)
r = await a('/api/register', { method: 'POST', body: { email, password: 'whatever12' }, csrf: false })
check('C54 CSRF header enforced', r.status === 403, `status ${r.status}`)

// --- C55: link code → token → play ------------------------------------------
r = await a('/api/link-code', { method: 'POST' })
const { code } = await r.json()
check('C55 code minted', typeof code === 'string' && code.length === 8, code)
r = await a('/api/redeem', { method: 'POST', body: { code } })
const { token } = await r.json()
check('C55 code redeemed for a token', typeof token === 'string' && token.length === 32)
r = await a('/api/redeem', { method: 'POST', body: { code } })
check('C55 code is single-use', r.status === 401, `status ${r.status}`)

const entityId = await joinGame(token)
check('C55 the issued token joins the game', entityId > 0, `entity ${entityId}`)
await sleep(1500) // let the save land

// Redeeming a fresh code returns the SAME player.
r = await a('/api/link-code', { method: 'POST' })
const mint2 = await r.json()
r = await a('/api/redeem', { method: 'POST', body: { code: mint2.code } })
const red2 = await r.json()
check('C55 one player per account', red2.token === token)

// --- C56: the account page tells the truth ----------------------------------
r = await a('/api/me')
const me = await r.json()
check('C56 the pilot appears on the account', me.players?.length === 1,
  `players ${me.players?.length}`)
check('C56 start credits shown truthfully', me.players?.[0]?.credits === 1000,
  `credits ${me.players?.[0]?.credits}`)

// --- C59: legacy import ------------------------------------------------------
// An account can hold ONE player, so import onto a second fresh account.
const legacyToken = `t28legacy${Date.now()}`
await joinGame(legacyToken, 'legacy')
await sleep(1500)
const c = client()
const email2 = `t28b-${Date.now()}@example.com`
await c('/api/register', { method: 'POST', body: { email: email2, password: 'orbital-insertion' } })
r = await c('/api/import-token', { method: 'POST', body: { token: legacyToken } })
check('C59 legacy token imported', r.status === 200, `status ${r.status}`)
r = await c('/api/me')
const me2 = await r.json()
check('C59 imported progress appears', me2.players?.length === 1)
r = await c('/api/import-token', { method: 'POST', body: { token } })
check('C59 owned token refuses re-import', r.status === 409, `status ${r.status}`)

// --- C58: public pages -------------------------------------------------------
r = await fetch(SITE + '/api/stats')
const stats = await r.json()
check('C58 public stats serve', r.status === 200 && stats.players >= 2,
  `players ${stats.players}, online ${stats.online}`)
r = await fetch(SITE + '/')
const html = await r.text()
check('C58 landing page serves', r.status === 200 && html.includes('SPACE ADVENTURE'))
r = await fetch(SITE + '/api/me')
check('C58 anonymous me refused', r.status === 401)

// --- C57: lifecycle ----------------------------------------------------------
r = await a('/api/password', { method: 'POST', body: { old: 'orbital-insertion', new: 'retrograde-burn' } })
check('C57 password change accepted', r.status === 200)
r = await b('/api/me')
check('C57 other session invalidated', r.status === 401, `status ${r.status}`)
r = await a('/api/me')
check('C57 changing session survives', r.status === 200)

const before = await (await fetch(SITE + '/api/stats')).json()
r = await a('/api/delete', { method: 'POST', body: { password: 'retrograde-burn' } })
check('C57 delete with password', r.status === 200)
r = await a('/api/me')
check('C57 session gone after delete', r.status === 401)
const after = await (await fetch(SITE + '/api/stats')).json()
check('C57 the owned player went with the account', after.players === before.players - 1,
  `players ${before.players} -> ${after.players}`)

// Coexistence: the orphaned token still JOINS — as a brand-new guest.
const rejoinId = await joinGame(token, 'fresh-guest')
check('C59 hello stays open after account deletion', rejoinId > 0, `entity ${rejoinId}`)

const fails = checks.filter(([, ok]) => !ok)
console.log(fails.length ? `\nOVERALL: FAIL (${fails.map(([n]) => n).join(', ')})` : `\nOVERALL: PASS (${checks.length} checks)`)
process.exit(fails.length ? 1 : 0)
