/**
 * Minimal WebSocket client over raw TCP — independent of the product code.
 *
 * Written against RFC 6455 so the QA harness speaks the transport itself
 * (the game wire codec lives in wire.mjs). Raw TCP also lets tests destroy
 * the socket without a close handshake (criterion 4: connection loss).
 *
 * Node built-ins only (net, crypto) — no npm dependencies.
 */
import net from 'node:net'
import crypto from 'node:crypto'

const OPCODE = { CONT: 0x0, TEXT: 0x1, BINARY: 0x2, CLOSE: 0x8, PING: 0x9, PONG: 0xa }

export class WSClient {
  /**
   * @param {string} host
   * @param {number} port
   * @param {string} path  e.g. '/ws'
   */
  constructor(host, port, path = '/ws') {
    this.host = host
    this.port = port
    this.path = path
    this.socket = null
    this.buffer = Buffer.alloc(0)
    this.closed = false
    this.closeInfo = null
    /** @type {Array<{type:number, payload:Buffer, recvNs:bigint}>} */
    this.inbox = []
    this.onMessage = null // (type: number, payload: Buffer, recvNs: bigint) => void
    this.onClose = null // (info) => void
    this.onOpen = null
  }

  connect() {
    return new Promise((resolve, reject) => {
      const sock = net.connect({ host: this.host, port: this.port })
      this.socket = sock
      const key = crypto.randomBytes(16).toString('base64')
      const req =
        `GET ${this.path} HTTP/1.1\r\n` +
        `Host: ${this.host}:${this.port}\r\n` +
        'Upgrade: websocket\r\n' +
        'Connection: Upgrade\r\n' +
        `Sec-WebSocket-Key: ${key}\r\n` +
        'Sec-WebSocket-Version: 13\r\n' +
        '\r\n'
      const deadline = setTimeout(() => {
        reject(new Error('WS connect timeout (10 s)'))
        sock.destroy()
      }, 10_000)
      let headerDone = false
      const onData = (chunk) => {
        if (!headerDone) {
          const buf = Buffer.concat([this.buffer, chunk])
          const idx = buf.indexOf('\r\n\r\n')
          if (idx < 0) {
            this.buffer = buf
            return
          }
          const head = buf.slice(0, idx).toString('latin1')
          if (!/^HTTP\/1\.1 101 /m.test(head) && !/^HTTP\/1\.1 \d{3} /m.test(head)) {
            clearTimeout(deadline)
            sock.destroy()
            reject(new Error(`WS handshake rejected: ${head.split('\r\n')[0]}`))
            return
          }
          if (!/101 /.test(head.split('\r\n')[0])) {
            clearTimeout(deadline)
            sock.destroy()
            reject(new Error(`WS handshake rejected: ${head.split('\r\n')[0]}`))
            return
          }
          headerDone = true
          this.buffer = buf.slice(idx + 4)
          clearTimeout(deadline)
          sock.removeListener('data', onData)
          sock.on('data', (c) => this._feed(c))
          sock.on('error', (e) => this._fail(e))
          sock.on('close', () => this._fail(new Error('TCP closed')))
          if (this.onOpen) this.onOpen()
          resolve()
          // process bytes that arrived with the handshake tail
          if (this.buffer.length > 0) this._feed(this.buffer), (this.buffer = Buffer.alloc(0))
        }
      }
      sock.on('data', onData)
      sock.on('error', (e) => {
        clearTimeout(deadline)
        reject(e)
      })
      sock.write(req)
    })
  }

  _fail(err) {
    if (this.closed) return
    this.closed = true
    this.closeInfo = { reason: String(err && err.message || err), atNs: BigInt(Date.now()) * 1000000n }
    if (this.onClose) this.onClose(this.closeInfo)
  }

  _feed(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk])
    // decode as many complete frames as possible
    for (;;) {
      const fr = this._tryDecode()
      if (!fr) return
      this.buffer = fr.rest
      if (fr.opcode === OPCODE.CLOSE) {
        this.closed = true
        this.closeInfo = { reason: 'close frame', code: fr.payload.length >= 2 ? fr.payload.readUInt16BE(0) : 0, atNs: fr.recvNs }
        if (this.onClose) this.onClose(this.closeInfo)
        return
      }
      if (fr.opcode === OPCODE.PING) {
        this._sendFrame(OPCODE.PONG, fr.payload)
        continue
      }
      if (fr.opcode === OPCODE.PONG) continue
      this.inbox.push({ opcode: fr.opcode, payload: fr.payload, recvNs: fr.recvNs })
      if (this.onMessage) this.onMessage(fr.opcode, fr.payload, fr.recvNs)
    }
  }

  /** Decode one frame from this.buffer; returns {opcode, payload, rest, recvNs} or null. */
  _tryDecode() {
    const b = this.buffer
    if (b.length < 2) return null
    const b0 = b[0]
    const opcode = b0 & 0x0f
    const masked = (b[1] & 0x80) !== 0
    let len = b[1] & 0x7f
    let off = 2
    if (len === 126) {
      if (b.length < 4) return null
      len = b.readUInt16BE(2)
      off = 4
    } else if (len === 127) {
      if (b.length < 10) return null
      const hi = b.readUInt32BE(2)
      const lo = b.readUInt32BE(6)
      len = hi * 0x100000000 + lo
      off = 10
    }
    let maskKey = null
    if (masked) {
      if (b.length < off + 4) return null
      maskKey = b.slice(off, off + 4)
      off += 4
    }
    if (b.length < off + len) return null
    let payload = b.slice(off, off + len)
    if (masked) {
      payload = Buffer.from(payload)
      for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i & 3]
    }
    return { opcode, payload, rest: b.slice(off + len), recvNs: process.hrtime.bigint() }
  }

  _sendFrame(opcode, payload) {
    if (!this.socket || this.socket.destroyed) return
    const len = payload.length
    let header
    if (len < 126) {
      header = Buffer.alloc(2)
      header[1] = len
    } else if (len < 65536) {
      header = Buffer.alloc(4)
      header[1] = 126
      header.writeUInt16BE(len, 2)
    } else {
      header = Buffer.alloc(10)
      header[1] = 127
      header.writeBigUInt64BE(BigInt(len), 2)
    }
    header[0] = 0x80 | opcode
    header[1] |= 0x80 // client MUST mask
    const maskKey = crypto.randomBytes(4)
    const masked = Buffer.from(payload)
    for (let i = 0; i < masked.length; i++) masked[i] ^= maskKey[i & 3]
    this.socket.write(Buffer.concat([header, maskKey, masked]))
  }

  /** Send a binary message (one game frame). */
  sendBinary(buf) {
    this._sendFrame(OPCODE.BINARY, buf)
  }

  sendPing(tsMs) {
    const p = Buffer.alloc(4)
    p.writeUInt32LE(tsMs >>> 0, 0)
    this._sendFrame(OPCODE.PING, p)
  }

  sendClose(code = 1000) {
    const p = Buffer.alloc(2)
    p.writeUInt16BE(code, 0)
    this._sendFrame(OPCODE.CLOSE, p)
  }

  /**
   * Simulate a hard connection loss: kill TCP with no close frame.
   * The server only learns of it when the read deadline (10 s) expires.
   */
  kill() {
    if (this.socket && !this.socket.destroyed) this.socket.destroy()
    if (!this.closed) {
      this.closed = true
      this.closeInfo = { reason: 'killed by test', atNs: process.hrtime.bigint() }
    }
  }
}
