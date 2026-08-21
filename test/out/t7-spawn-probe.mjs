// QA probe: wire-level check of the spawn-broadcast path.
// A joins and holds; B joins after 3 s; A logs every SPAWN message it
// receives (entityId). If A never sees B's spawn, the running server is
// not broadcasting joins to existing clients.
import { WSClient } from '../lib/ws.mjs'
import { MSG, encodeHello, frame, decodeHelloAck, decodeSpawn } from '../lib/wire.mjs'
import { PROTOCOL_VERSION } from '../../client/src/net/protocol.js'

const HOST = '127.0.0.1'
const PORT = 18080

async function join(name) {
  const ws = new WSClient(HOST, PORT, '/ws')
  const log = []
  ws.onMessage = (_op, payload) => {
    const g = payload.readUInt16LE(0)
    if (g === MSG.HELLO_ACK) log.push(`hello_ack ${decodeHelloAck(payload.subarray(2)).entityId}`)
    else if (g === MSG.SPAWN) {
      const sp = decodeSpawn(payload.subarray(2))
      log.push(`SPAWN id=${sp.entityId} type=${sp.entityType}`)
    } else if (g === MSG.SNAPSHOT && !log.some((l) => l === 'snapshot(first)')) log.push('snapshot(first)')
  }
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, name)))
  return { ws, log }
}

const a = await join('qa-t7-a')
await new Promise((r) => setTimeout(r, 3000))
const aId = Number((a.log.find((l) => l.startsWith('hello_ack')) ?? '').split(' ')[1] ?? 0)
const b = await join('qa-t7-b')
await new Promise((r) => setTimeout(r, 5000))
console.log(`A joined (entity ${aId})`)
console.log('A log:', JSON.stringify(a.log))
console.log('B log:', JSON.stringify(b.log))
const bId = Number((b.log.find((l) => l.startsWith('hello_ack')) ?? '').split(' ')[1] ?? -1)
const sawB = a.log.some((l) => l === `SPAWN id=${bId} type=1`)
console.log(`VERDICT: A ${sawB ? 'RECEIVED' : 'DID NOT RECEIVE'} SPAWN for B (id ${bId}) -> server broadcast ${sawB ? 'OK' : 'BROKEN'}`)
a.ws.sendClose()
b.ws.sendClose()