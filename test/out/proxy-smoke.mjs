// scratch: proxy transparency + RTT smoke (not part of the suite)
import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { WSClient } from '../lib/ws.mjs'
import { MSG, encodeHello, decodeHelloAck, frame } from '../lib/wire.mjs'

const LPORT = 28091
const proxy = spawn('node', ['test/lib/proxy.mjs', String(LPORT), '127.0.0.1', '18080'], { stdio: ['ignore', 'pipe', 'pipe'] })
let pout = ''
proxy.stdout.on('data', (d) => (pout += d))
proxy.stderr.on('data', (d) => (pout += d))
await new Promise((res, rej) => {
  const iv = setInterval(() => {
    if (pout.includes('proxy ready')) { clearInterval(iv); res() }
  }, 20)
  setTimeout(() => rej(new Error('proxy not ready: ' + pout)), 5000)
})

const sha16 = (b) => createHash('sha256').update(b).digest('hex').slice(0, 16)
const pingTs = (b) => b.readUInt32LE(0)

async function probe(port, name) {
  const ws = new WSClient('127.0.0.1', port, '/ws')
  const got = { hello: null, terrain: null, pong: null }
  let pingSendNs = null
  ws.onMessage = (type, payload, recvNs) => {
    const g = payload.readUInt16LE(0) // game type; ws opcode is 0x2 for all binary
    const p = payload.subarray(2) // game frame = u16 type + payload
    if (g === MSG.HELLO_ACK && !got.hello) got.hello = decodeHelloAck(p)
    else if (g === MSG.TERRAIN && !got.terrain) got.terrain = p
    else if (g === MSG.PONG && pingSendNs !== null && !got.pong) got.pong = { recvNs, ts: pingTs(p) }
  }
  await ws.connect()
  ws.sendBinary(frame(MSG.HELLO, encodeHello(1, name)))
  for (let i = 0; i < 300 && (!got.hello || !got.terrain); i++) await new Promise((r) => setTimeout(r, 10))
  // 5 game pings, 150 ms apart, for an RTT sample
  const rtts = []
  for (let i = 0; i < 5; i++) {
    const ts = Date.now() & 0xffffffff
    pingSendNs = process.hrtime.bigint()
    ws.sendBinary(frame(MSG.PING, ((b) => (b.writeUInt32LE(ts & 0xffffffff, 0), b))(Buffer.alloc(4))))
    for (let k = 0; k < 300 && (!got.pong || got.pong.ts !== ts); k++) await new Promise((r) => setTimeout(r, 10))
    if (got.pong && got.pong.ts === ts) rtts.push(Number(got.pong.recvNs - pingSendNs) / 1e6)
    got.pong = null
    await new Promise((r) => setTimeout(r, 100))
  }
  ws.sendClose()
  return { hello: got.hello, terrainSha: got.terrain ? sha16(got.terrain) : null, rttMs: rtts }
}

const proxied = await probe(LPORT, 'qa-proxy-smoke')
const direct = await probe(18080, 'qa-proxy-smoke-direct')
proxy.kill('SIGTERM')
const med = (a) => [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)]
console.log(JSON.stringify({
  proxied: { hello: proxied.hello, terrainSha: proxied.terrainSha, rttMedianMs: med(proxied.rttMs), rtt: proxied.rttMs },
  direct: { hello: direct.hello, terrainSha: direct.terrainSha, rttMedianMs: med(direct.rttMs) },
  sameTerrain: proxied.terrainSha === direct.terrainSha,
}, null, 1))
console.error(pout)