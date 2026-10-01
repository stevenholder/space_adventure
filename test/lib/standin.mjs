// A second body for a screenshot: hello, walk forward for a second (spawn is
// where the rig stands), turn round, then idle -- sending input the whole time,
// because the server drops a client that goes quiet.
//   node test/lib/standin.mjs <secs> <port> [walk] &  then  godot ... -uiFace player
// `walk`: goes 4.5 m out, then paces side to side across the rig's view, for a gait shot.
import { WSClient } from './ws.mjs'
import { MSG, PROTOCOL_VERSION, encodeHello, encodeInput, frame } from './wire.mjs'
const secs = Number(process.argv[2] ?? 40)
const walk = process.argv[4] === 'walk'
const ws = new WSClient('127.0.0.1', Number(process.argv[3] ?? 18080))
ws.onMessage = () => {}
ws.onClose = (i) => { console.error('closed:', i.reason); process.exit(0) }
await ws.connect()
ws.sendBinary(frame(MSG.HELLO, encodeHello(PROTOCOL_VERSION, 'standin')))
console.log('standin joined')
let seq = 1
const t0 = Date.now()
// walk forward for the first second so it is not standing inside the rig at spawn
setInterval(() => {
  const t = (Date.now() - t0) / 1000
  const side = Math.floor(t) % 2 === 0 ? 1 : -1   // 1 s out, then 1 s left, 1 s right, ...
  const look = walk ? (t < 1 ? [0, 0, 1] : [side, 0, 0]) : t < 1 ? [0, 0, 1] : [0, 0, -1]
  ws.sendBinary(frame(MSG.INPUT, encodeInput(0, walk || t < 1 ? 1 : 0, look, 0, seq++)))
}, 50)
setTimeout(() => process.exit(0), secs * 1000)
