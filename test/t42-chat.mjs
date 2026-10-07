#!/usr/bin/env node
/**
 * t42 — world chat over the wire (docs/GDD.md "Chat (Phase 20)";
 * PROTOCOL.md cmd `0x0016` `chat`, event `0x0011` `chat`; ROADMAP C174/C175).
 *
 * Two guests, A and B, on one server:
 *   - A says "over here": A and B each get exactly one `chat` event naming A
 *     (entity_id = A, data = A's name NUL "over here"), and A's event lands
 *     BEFORE A's cmd_result, which is status 0 `{}`;
 *   - B says "a\u0007b": the control character is stripped, both see "ab";
 *   - A says "   ": status 3 `{"reason":"empty"}`, no event;
 *   - A says 201 bytes: status 2, no event;
 *   - with A's bucket full, four lines back to back: 0,0,0,4, and B sees
 *     exactly three events;
 *   - a body without `text` (`{"nope":1}`): status 2;
 *   - and no `chat` event arrives for anything else during the run.
 *
 * The chat bucket (burst 3, 1 per s) is checked BEFORE the body, so a refused
 * line still spends a token: the waits between groups keep A's bucket where
 * each step needs it.
 *
 * Run: SA_SERVER_URL=ws://localhost:18086/ws node test/t42-chat.mjs
 *      (needs a live server built with Phase 20; default ws://127.0.0.1:18080/ws)
 */
import { MSG, EVENT, OP, frame, encodeCmd, decodeCmdResult, decodeEvent, decodeHelloAck, decodeChat } from './lib/wire.mjs'

const URL = process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws'
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const wait = async (fn, ms = 4000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = fn(); if (v) return v; await sleep(20) } return null }

function hello (name, token) {
  const n = Buffer.from(name, 'utf8'), t = Buffer.from(token, 'utf8')
  const b = Buffer.alloc(2 + 4 + n.length + 4 + t.length)
  b.writeUInt16LE(2, 0); b.writeUInt32LE(n.length, 2); n.copy(b, 6)
  b.writeUInt32LE(t.length, 6 + n.length); t.copy(b, 10 + n.length)
  return frame(MSG.HELLO, b)
}

function connect (name) {
  const ws = new WebSocket(URL)
  ws.binaryType = 'arraybuffer'
  // log: every chat event and cmd_result in arrival order (the event-before-result check needs it).
  const c = { ws, name, id: 0, seq: 0, log: [], closed: false }
  ws.addEventListener('open', () => ws.send(hello(name, `chat-${name}-${Date.now()}`)))
  ws.addEventListener('close', () => { c.closed = true })
  ws.addEventListener('message', (ev) => {
    const buf = Buffer.from(ev.data), t = buf.readUInt16LE(0), p = buf.subarray(2)
    if (t === MSG.HELLO_ACK) c.id = decodeHelloAck(p).entityId
    else if (t === MSG.CMD_RESULT) c.log.push({ kind: 'result', ...decodeCmdResult(p) })
    else if (t === MSG.EVENT) {
      const e = decodeEvent(p)
      if (e.eventId === EVENT.CHAT) c.log.push({ kind: 'chat', entityId: e.entityId, ...decodeChat(e.data) })
    }
  })
  return c
}
const chats = (c) => c.log.filter((x) => x.kind === 'chat')
const resultOf = (c, seq) => c.log.find((x) => x.kind === 'result' && x.opcode === OP.CHAT && x.seq === seq)
function say (c, body) {
  const s = ++c.seq
  c.ws.send(frame(MSG.CMD, encodeCmd(s, OP.CHAT, body)))
  return s
}
async function call (c, body) {
  const s = say(c, body)
  return wait(() => resultOf(c, s))
}

const results = []
function check (name, ok, detail = '') {
  results.push(ok)
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ` — ${detail}` : ''}`)
}

const A = connect('Talker'), B = connect('Listener')
await wait(() => A.id && B.id, 5000)
check('both guests seated', A.id > 0 && B.id > 0 && A.id !== B.id, `A=${A.id} B=${B.id}`)
await sleep(300) // let the joins settle; nothing below should be a chat event yet
check('no chat event at join', chats(A).length === 0 && chats(B).length === 0)

// --- 1. A: "over here" ------------------------------------------------------
let a0 = chats(A).length, b0 = chats(B).length
const s1 = say(A, { text: 'over here' })
const r1 = await wait(() => resultOf(A, s1))
await sleep(300)
const aNew = chats(A).slice(a0), bNew = chats(B).slice(b0)
check('A\'s line: cmd_result 0 {}', r1?.status === 0 && r1.raw === '{}', `status=${r1?.status} body=${r1?.raw}`)
check('A sees exactly one chat event', aNew.length === 1, `got ${aNew.length}`)
check('B sees exactly one chat event', bNew.length === 1, `got ${bNew.length}`)
const good1 = (e) => e && e.entityId === A.id && e.name === 'Talker' && e.text === 'over here'
check('A\'s event: entity_id = A, name Talker, text "over here"', good1(aNew[0]), JSON.stringify(aNew[0]))
check('B\'s event: entity_id = A, name Talker, text "over here"', good1(bNew[0]), JSON.stringify(bNew[0]))
const iEv = A.log.findIndex((x) => x.kind === 'chat' && x.text === 'over here')
const iRes = A.log.findIndex((x) => x.kind === 'result' && x.seq === s1)
check('the speaker\'s event lands before its cmd_result', iEv >= 0 && iRes >= 0 && iEv < iRes, `event@${iEv} result@${iRes}`)

// --- 2. B: "a\u0007b" -> "ab" -----------------------------------------------
a0 = chats(A).length; b0 = chats(B).length
const r2 = await call(B, { text: 'a\u0007b' })
await sleep(300)
const a2 = chats(A).slice(a0), b2 = chats(B).slice(b0)
check('B\'s line: cmd_result 0', r2?.status === 0, `status=${r2?.status}`)
check('control char stripped: both see one event "ab" from B',
  a2.length === 1 && b2.length === 1 && [a2[0], b2[0]].every((e) => e.entityId === B.id && e.name === 'Listener' && e.text === 'ab'),
  `A=${JSON.stringify(a2)} B=${JSON.stringify(b2)}`)

// --- 3. A: "   " -> status 3 empty ------------------------------------------
a0 = chats(A).length; b0 = chats(B).length
const r3 = await call(A, { text: '   ' })
await sleep(300)
check('blank line: status 3 {"reason":"empty"}', r3?.status === 3 && r3.body?.reason === 'empty', `status=${r3?.status} body=${r3?.raw}`)
check('blank line: no event on either', chats(A).length === a0 && chats(B).length === b0)

// --- 4. A: 201 bytes -> status 2 --------------------------------------------
await sleep(1100)
a0 = chats(A).length; b0 = chats(B).length
const r4 = await call(A, { text: 'x'.repeat(201) })
await sleep(300)
check('201-byte line: status 2', r4?.status === 2, `status=${r4?.status}`)
check('201-byte line: no event on either', chats(A).length === a0 && chats(B).length === b0)

// --- 5. burst: four lines back to back -> 0,0,0,4 ---------------------------
await sleep(2100) // A spent 3 tokens above (1 + 1 + 1, refills ~1/s): 2.1 s more fills the bucket
a0 = chats(A).length; b0 = chats(B).length
const burst = ['one', 'two', 'three', 'four'].map((t) => say(A, { text: t }))
await wait(() => burst.every((s) => resultOf(A, s)))
await sleep(400)
const statuses = burst.map((s) => resultOf(A, s)?.status)
const bBurst = chats(B).slice(b0)
check('burst of four: statuses 0,0,0,4', statuses.join(',') === '0,0,0,4', statuses.join(','))
check('burst of four: B sees exactly three events', bBurst.length === 3 && bBurst.map((e) => e.text).join(',') === 'one,two,three',
  bBurst.map((e) => e.text).join(','))
check('burst of four: A sees exactly three events', chats(A).length - a0 === 3, `got ${chats(A).length - a0}`)

// --- 6. a body without text -> status 2 -------------------------------------
await sleep(1100)
a0 = chats(A).length; b0 = chats(B).length
const r6 = await call(A, { nope: 1 })
await sleep(300)
check('{"nope":1}: status 2', r6?.status === 2, `status=${r6?.status}`)
check('{"nope":1}: no event on either', chats(A).length === a0 && chats(B).length === b0)

// --- the whole run ----------------------------------------------------------
// accepted lines: "over here", "ab", one, two, three = 5 per socket.
check('no stray chat events over the run (5 each)', chats(A).length === 5 && chats(B).length === 5, `A=${chats(A).length} B=${chats(B).length}`)
check('neither socket was closed', !A.closed && !B.closed)

const bad = results.filter((ok) => !ok).length
console.log(bad ? `OVERALL: FAIL (${bad}/${results.length})` : `OVERALL: PASS (${results.length} checks)`)
A.ws.close(); B.ws.close()
process.exit(bad ? 1 : 0)
