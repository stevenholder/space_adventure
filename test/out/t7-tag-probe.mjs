// QA probe: join as one client, hold ~15 s so the live browser page can
// display the nametag, then leave. Used to test the SPAWN-broadcast ->
// nametag path on the deployed client bundle.
import { WSClient } from '../lib/ws.mjs'
import { MSG, encodeHello, frame, decodeHelloAck } from '../lib/wire.mjs'
import { PROTOCOL_VERSION } from '../../client/src/net/protocol.js'

const HOST = '127.0.0.1'
const PORT = 18080
const name = process.argv[2] ?? 'qa-t7-x1'
const holdMs = Number(process.argv[3] ?? 15000)

const ws = new WSClient(HOST, PORT, '/ws')
ws.onMessage = (_op, payload) => {
  const g = payload.readUInt16LE(0)
  if (g === MSG.HELLO_ACK) {
    const h = decodeHelloAck(payload.subarray(2))
    console.log(`HELLO_ACK entityId=${h.entityId} wallMs=${Date.now()}`)
  }
}
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, name)))
console.log(`joined as ${name}, holding ${holdMs} ms ...`)
await new Promise((r) => setTimeout(r, holdMs))
ws.sendClose()
console.log('bye')