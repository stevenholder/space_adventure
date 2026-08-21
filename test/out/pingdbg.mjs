// scratch: frame-level capture to debug game ping/pong
import { WSClient } from '../lib/ws.mjs'
import { MSG, encodeHello, frame } from '../lib/wire.mjs'

const ws = new WSClient('127.0.0.1', 18080, '/ws')
const log = []
ws.onMessage = (op, payload, recvNs) => {
  const g = payload.readUInt16LE(0)
  log.push(`op=${op} game=${g.toString(16)} len=${payload.length} recv=${Number(recvNs / 1000000n)}`)
}
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(1, 'qa-pingdbg')))
await new Promise((r) => setTimeout(r, 800))
const ts = Date.now()
log.push(`-- sent game PING ts=${ts}`)
ws.sendBinary(frame(MSG.PING, ((b) => (b.writeUInt32LE(ts & 0xffffffff, 0), b))(Buffer.alloc(4))))
await new Promise((r) => setTimeout(r, 700))
log.push(`-- ws ping sent`)
ws.sendPing(Date.now())
await new Promise((r) => setTimeout(r, 700))
ws.sendClose()
for (const l of log) console.log(l)