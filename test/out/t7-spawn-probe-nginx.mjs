// QA probe: spawn broadcast through the nginx same-origin /ws path
// (port 3000) — the page's path — vs the direct server path (18080).
import { WSClient } from '../lib/ws.mjs'
import { MSG, encodeHello, frame, decodeHelloAck, decodeSpawn } from '../lib/wire.mjs'
import { PROTOCOL_VERSION } from '../../client/src/net/protocol.js'

async function join(name, port) {
  const ws = new WSClient('127.0.0.1', port, '/ws')
  const log = []
  ws.onMessage = (_op, payload) => {
    const g = payload.readUInt16LE(0)
    if (g === MSG.HELLO_ACK) log.push(`hello_ack ${decodeHelloAck(payload.subarray(2)).entityId}`)
    else if (g === MSG.SPAWN) {
      const sp = decodeSpawn(payload.subarray(2))
      log.push(`SPAWN id=${sp.entityId}`)
    }
  }
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, name)))
  return { ws, log }
}

const a = await join('qa-t7-nginx-a', 3000) // page's path (nginx /ws)
await new Promise((r) => setTimeout(r, 3000))
const b = await join('qa-t7-direct-b', 18080) // direct server
await new Promise((r) => setTimeout(r, 5000))
const aId = Number((a.log.find((l) => l.startsWith('hello_ack')) ?? '').split(' ')[1] ?? 0)
const bId = Number((b.log.find((l) => l.startsWith('hello_ack')) ?? '').split(' ')[1] ?? -1)
const sawB = a.log.includes(`SPAWN id=${bId}`)
console.log(`A(via nginx) entity ${aId} log:`, JSON.stringify(a.log))
console.log(`B(direct) entity ${bId} log:`, JSON.stringify(b.log))
console.log(`VERDICT: A(via nginx) ${sawB ? 'RECEIVED' : 'DID NOT RECEIVE'} SPAWN for B -> nginx /ws path ${sawB ? 'OK' : 'DROPS SPAWN FRAMES'}`)
a.ws.sendClose()
b.ws.sendClose()