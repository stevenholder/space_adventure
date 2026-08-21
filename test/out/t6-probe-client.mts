/**
 * C6 gap-report live probe: scripted client that sprints for 12 s so the
 * deployed browser client (test observer) must render it — a visible,
 * moving nametag — IF and ONLY IF live snapshots are consumed
 * (onSnapshot wired -> world.feedRemote). With the deployed bundle
 * (onSnapshot x1 in the bundle = dispatch site only), the tag stays
 * display:none for the whole window.
 *
 * Evidence artifact (read-only libs: test/lib/wire.mjs, test/lib/ws.mjs).
 */
import { MSG, decodeHelloAck, encodeHello, encodeInput, frame } from '../lib/wire.mjs'
import { WSClient } from '../lib/ws.mjs'
import { PROTOCOL_VERSION } from '../../client/src/net/protocol.js'
import { spawnLook } from '../../client/src/sim/index.js'

const NAME = 'qa-c6-probe'
const N_TICKS = 240 // 12 s of sprint
const ws = new WSClient('127.0.0.1', 18080, '/ws')
let entityId: number | null = null
let terrain = false
let snaps = 0
ws.onMessage = (_op, payload) => {
  const g = payload.readUInt16LE(0)
  const p = payload.subarray(2)
  if (g === MSG.HELLO_ACK) entityId = decodeHelloAck(p).entityId
  else if (g === MSG.TERRAIN) terrain = true
  else if (g === MSG.SNAPSHOT) snaps++
}
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, NAME)))
const { promise: handshakeWait, resolve: handshakeDone } = Promise.withResolvers<void>()
setTimeout(handshakeDone, 1500)
await handshakeWait
if (!terrain || entityId === null) {
  console.error(JSON.stringify({ error: 'handshake/terrain timeout', entityId, terrain }))
  process.exit(1)
}
const look = spawnLook()
let k = 0
const iv = setInterval(() => {
  ws.sendBinary(frame(MSG.INPUT, encodeInput(0, 1, [look.x, look.y, look.z], 0x0001, k++)))
  if (k >= N_TICKS) {
    clearInterval(iv)
    ws.sendClose()
    console.log(JSON.stringify({ probe: NAME, entity: entityId, ticks: N_TICKS, seconds: 12, snapshots: snaps }))
    process.exit(0)
  }
}, 50)
setTimeout(() => {
  console.error(JSON.stringify({ error: 'probe timeout', entityId, terrain, snaps, sent: k }))
  process.exit(1)
}, 25000)