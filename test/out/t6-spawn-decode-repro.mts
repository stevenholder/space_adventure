/**
 * C6/C7 product-bug repro: decode a LIVE spawn frame (nginx /ws, :3000)
 * with the shipped client's decodeSpawn (client/src/net/protocol.ts).
 *
 * PROTOCOL.md:33 — spawn payload: u32 entity_id | u16 entity_type |
 * u32 data_len | bytes  =>  data_len at payload offset 6.
 * Client decodeSpawn reads dv.getUint32(8) — offset 8 (name bytes).
 */
import { MSG, encodeHello, frame } from '../lib/wire.mjs'
import { WSClient } from '../lib/ws.mjs'
import { PROTOCOL_VERSION, decodeSpawn } from '../../client/src/net/protocol.js'

const NAME = 'Player-2f0c' // 11 chars, like the page default Player-<4hex>
const ws = new WSClient('127.0.0.1', 3000, '/ws')
let hex = ''
let body: Uint8Array | null = null
ws.onMessage = (op, payload) => {
  const t = payload.readUInt16LE(0)
  if (t === MSG.SPAWN && body === null) {
    for (let i = 0; i < payload.length; i++) hex += payload[i].toString(16).padStart(2, '0')
    body = payload.subarray(2)
  }
}
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, NAME)))
for (let i = 0; i < 200 && body === null; i++) await new Promise((r) => setTimeout(r, 50))
if (body === null) {
  console.error(JSON.stringify({ error: 'no spawn frame received' }))
  process.exit(1)
}
const b = body
let clientDecode: string
try {
  clientDecode = `OK: ${JSON.stringify(decodeSpawn(b))}`
} catch (e) {
  clientDecode = `THROWS: ${String(e)}`
}
const dv = new DataView(b.buffer, b.byteOffset, b.byteLength)
console.log(
  JSON.stringify(
    {
      frameHex: hex,
      layout: 'u32 entity_id | u16 entity_type | u32 data_len@6 | bytes@10',
      dataLenAtOffset6: dv.getUint32(6, true),
      dataLenReadAtOffset8: dv.getUint32(8, true),
      clientDecodeSpawn: clientDecode,
    },
    null,
    1,
  ),
)
ws.sendClose()
process.exit(0)