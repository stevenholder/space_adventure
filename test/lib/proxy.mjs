/**
 * C6 latency proxy — raw-TCP WebSocket pass-through with 50 ms delay per
 * direction (100 ms RTT). QA harness for criterion 6 (docs/ROADMAP.md):
 * prediction quality under injected network latency.
 *
 *   node test/lib/proxy.mjs <listenPort> <targetHost> <targetPort> [delayMs]
 *
 * delayMs is the ONE-WAY injection and defaults to 50 (100 ms RTT, C6's
 * figure). t21 raises it: lag compensation rewinds by RTT/2, so the rewind
 * window only exceeds an NPC's own hitbox once the RTT is a few hundred ms.
 *
 * Transparency contract (locked by the C6 method):
 *   - byte-identical frame forwarding: each WebSocket frame is passed
 *     through as raw bytes, mask included — no mutation, no re-encoding;
 *   - per-direction FIFO: frames are delayed 50 ms each and written in
 *     arrival order — no reordering beyond the delay;
 *   - handshake bytes (HTTP 101 exchange) are piped without delay;
 *   - every frame type (binary game frames, ping/pong, close) is
 *     passed through opaquely.
 *
 * Node built-ins only (net). No npm dependencies.
 */
import net from 'node:net'

const listenPort = Number(process.argv[2])
const targetHost = process.argv[3]
const targetPort = Number(process.argv[4])
const delayArg = process.argv[5] === undefined ? 50 : Number(process.argv[5])
if (!listenPort || !targetHost || !targetPort || !Number.isFinite(delayArg) || delayArg < 0) {
  console.error('usage: node test/lib/proxy.mjs <listenPort> <targetHost> <targetPort> [delayMs]')
  process.exit(2)
}
const DELAY_MS = delayArg // one-way injection; RTT is twice this

const server = net.createServer((cli) => {
  const t0 = Date.now()
  const tgt = net.connect({ host: targetHost, port: targetPort })
  const stats = { c2t: { frames: 0, bytes: 0 }, t2c: { frames: 0, bytes: 0 } }
  const pending = [] // {timer, dir} — cleared on teardown
  let tornDown = false

  /** Per-direction pipeline: handshake pipe, then frame-delay. */
  function pipeline(src, dst, dir, onHandshakeDone) {
    let mode = 'handshake'
    let acc = Buffer.alloc(0)
    src.on('data', (chunk) => {
      if (tornDown) return
      if (mode === 'handshake') {
        acc = Buffer.concat([acc, chunk])
        const idx = acc.indexOf('\r\n\r\n')
        if (idx < 0) return // wait for the full HTTP exchange
        const head = acc.slice(0, idx + 4)
        const tail = acc.slice(idx + 4)
        acc = Buffer.alloc(0)
        mode = 'frame'
        if (head.length) dst.write(head) // handshake: no delay
        onHandshakeDone?.()
        if (tail.length) frameMode(tail)
        return
      }
      frameMode(chunk)
    })
    src.on('error', () => teardown())
    src.on('close', () => teardown())

    function frameMode(chunk) {
      acc = Buffer.concat([acc, chunk])
      for (;;) {
        if (tornDown) return
        const r = parseFrame(acc)
        if (!r) return
        acc = r.rest
        stats[dir].frames++
        stats[dir].bytes += r.frame.length
        const timer = setTimeout(() => {
          if (!tornDown) dst.write(r.frame)
        }, DELAY_MS)
        pending.push({ timer, dir })
      }
    }
  }

  function teardown() {
    if (tornDown) return
    tornDown = true
    for (const p of pending) clearTimeout(p.timer)
    pending.length = 0
    cli.destroy()
    tgt.destroy()
    console.log(
      `conn closed after ${Date.now() - t0} ms: c2t ${stats.c2t.frames} frames/${stats.c2t.bytes} B, ` +
        `t2c ${stats.t2c.frames} frames/${stats.t2c.bytes} B (delay ${DELAY_MS} ms/dir)`,
    )
  }

  tgt.on('error', () => teardown())
  tgt.on('close', () => teardown())
  pipeline(cli, tgt, 'c2t')
  pipeline(tgt, cli, 't2c')
})

/**
 * Parse one complete WebSocket frame from the head of `buf` — raw bytes,
 * mask key included, payload un-decoded. Returns {frame, rest} or null.
 * RFC 6455 framing; control frames carry len <= 125.
 */
function parseFrame(buf) {
  if (buf.length < 2) return null
  const b1 = buf[1]
  const masked = (b1 & 0x80) !== 0
  let len = b1 & 0x7f
  let off = 2
  if (len === 126) {
    if (buf.length < 4) return null
    len = buf.readUInt16BE(2)
    off = 4
  } else if (len === 127) {
    if (buf.length < 10) return null
    const hi = buf.readUInt32BE(2)
    const lo = buf.readUInt32BE(6)
    len = hi * 0x100000000 + lo
    off = 10
  }
  if (masked) {
    if (buf.length < off + 4) return null
    off += 4
  }
  if (buf.length < off + len) return null
  return { frame: buf.slice(0, off + len), rest: buf.slice(off + len) }
}

server.on('error', (e) => {
  console.error(`proxy listen error: ${e.message}`)
  process.exit(1)
})
server.listen(listenPort, '127.0.0.1', () => {
  console.log(`proxy ready on :${listenPort} -> ${targetHost}:${targetPort} (delay ${DELAY_MS} ms/direction)`)
})