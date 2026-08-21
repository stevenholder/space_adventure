/** Raw node WS probe against the nginx /ws path (port-forward :3000). */
import { MSG, encodeHello, frame } from '../lib/wire.mjs'
import { WSClient } from '../lib/ws.mjs'
import { PROTOCOL_VERSION } from '../../client/src/net/protocol.js'

const PORT = Number(process.argv[2] ?? 3000)
const ws = new WSClient('127.0.0.1', PORT, '/ws')
const types: number[] = []
ws.onMessage = (op, p) => {
  types.push(p.readUInt16LE(0))
  if (types.length >= 4) {
    console.log(JSON.stringify({ port: PORT, types }))
    ws.sendClose()
    process.exit(0)
  }
}
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'qa-raw')))
const timer = setTimeout(() => {
  console.error(JSON.stringify({ port: PORT, error: 'timeout', types }))
  process.exit(1)
}, 8000)
ws.onClose = (code: number, reason: string) => {
  clearTimeout(timer)
  console.error(JSON.stringify({ port: PORT, error: `closed ${code} ${reason}`, types }))
  process.exit(1)
}