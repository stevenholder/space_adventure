/**
 * WebSocket transport for the frozen wire protocol.
 *
 * The render loop never blocks on this: every network operation is an
 * async callback, and the only synchronous work is parsing inbound frames
 * (the biggest is the one-shot ~51 KB terrain field).
 *
 * Outbound: hello on open, input (one per local tick while simulating),
 * ping every 2 s when otherwise silent (PROTOCOL heartbeat).
 * On unexpected close: auto-reconnect with exponential backoff and a
 * fresh Hello; the server's join handshake is the full resync. Only an
 * explicit close() stops the retries.
 */
import {
  MAX_MESSAGE_SIZE,
  MSG,
  PROTOCOL_VERSION,
  ProtocolError,
  decodeDespawn,
  decodeEvent,
  decodeHelloAck,
  decodePong,
  decodeSnapshot,
  decodeSpawn,
  decodeTerrain,
  encodeHello,
  encodeInput,
  encodePing,
  unframe,
  type EventMsg,
  type HelloAck,
  type Snapshot,
  type SpawnMsg,
  type TerrainWire,
} from './protocol.js'
import { getToken } from './identity.js'
import type { Vec3 } from '../sim/index.js'

export type NetState = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'closed'

export interface InputCmd {
  moveX: number
  moveY: number
  lookDir: Vec3
  actionMask: number
}

export interface NetEvents {
  onState?: (state: NetState, detail: string) => void
  /**
   * The connection re-established after an unexpected close and a fresh
   * Hello is about to be sent. The join handshake that follows
   * (hello_ack, terrain, complete spawn list) is the complete truth:
   * the caller must clear stale remote state and reset local prediction
   * before any of it is processed.
   */
  onReconnect?: () => void
  onHelloAck?: (ack: HelloAck) => void
  onTerrain?: (wire: TerrainWire) => void
  onSnapshot?: (snap: Snapshot) => void
  onSpawn?: (spawn: SpawnMsg) => void
  onDespawn?: (entityId: number) => void
  onEvent?: (event: EventMsg) => void
  onPong?: (tsMs: number, rttMs: number) => void
}

export class NetClient {
  private ws: WebSocket | null = null
  private events: NetEvents
  private netState: NetState = 'idle'
  private lastSentAt = 0
  /** RTT of the last answered ping, ms. -1 until the first pong. */
  rtt = -1
  /** u16 input seq, incremented on every input message sent. */
  seq = 0

  // --- reconnect (M1) ------------------------------------------------
  // Unexpected close → exponential backoff (500 ms × 2, cap 30 s, small
  // jitter), then a fresh Hello with the same name; the server's join
  // handshake (hello_ack, terrain, complete spawn list) is the full
  // resync. An intentional close() never reconnects.
  private url = ''
  private name = ''
  private stopped = false
  private timer: number | null = null
  /** Reconnect attempts since the last open (drives the backoff). */
  attempts = 0
  /** Delay of the scheduled retry in ms (0 when none pending). */
  nextRetryMs = 0

  constructor(events: NetEvents) {
    this.events = events
  }

  get state(): NetState {
    return this.netState
  }

  get isOpen(): boolean {
    return this.netState === 'open'
  }

  connect(url: string, name: string): void {
    this.teardown()
    this.url = url
    this.name = name
    this.stopped = false
    this.attempts = 0
    this.nextRetryMs = 0
    this.openSocket()
  }

  /** Open one socket — first try or a backoff retry — and wire its handlers. */
  private openSocket(): void {
    const retrying = this.netState === 'reconnecting'
    if (!retrying) this.netState = 'connecting'
    this.nextRetryMs = 0
    this.events.onState?.(this.netState, retrying ? this.reconnectLabel : `connecting ${this.url}`)
    const ws = new WebSocket(this.url)
    ws.binaryType = 'arraybuffer'
    this.ws = ws

    ws.onopen = () => {
      if (this.ws !== ws) return
      const wasReconnecting = this.netState === 'reconnecting'
      this.attempts = 0
      this.nextRetryMs = 0
      this.seq = 0 // per-connection counter (PROTOCOL "seq")
      this.netState = 'open'
      this.lastSentAt = performance.now()
      if (wasReconnecting) this.events.onReconnect?.()
      // getToken() returns "" if localStorage is unavailable, which the
      // server treats as absent: an ephemeral session that is not saved
      // (docs/PROTOCOL.md, "Identity token").
      this.sendRaw(encodeHello(PROTOCOL_VERSION, this.name, getToken()))
      this.events.onState?.('open', 'connected')
    }

    ws.onmessage = (ev: MessageEvent) => {
      if (this.ws !== ws) return
      this.handleMessage(ev.data as ArrayBuffer)
    }

    ws.onerror = () => {
      if (this.ws !== ws) return
      // onclose follows and owns the state transition + backoff.
      this.events.onState?.(
        this.netState,
        this.netState === 'reconnecting' ? this.reconnectLabel : `${this.url} — connection error`,
      )
    }

    ws.onclose = (ev: CloseEvent) => {
      if (this.ws !== ws) return
      this.ws = null
      if (this.stopped) {
        this.netState = 'closed'
        this.events.onState?.('closed', `closed ${ev.code}${ev.reason ? `: ${ev.reason}` : ''}`)
        return
      }
      // Unexpected loss (or a failed first try): back off, then retry
      // with a fresh Hello. The join handshake on success is the full
      // resync (hello_ack, terrain, complete spawn list).
      this.netState = 'reconnecting'
      this.attempts += 1
      this.nextRetryMs = this.backoffMs(this.attempts)
      this.timer = window.setTimeout(() => {
        this.timer = null
        this.openSocket()
      }, this.nextRetryMs)
      this.events.onState?.('reconnecting', this.reconnectLabel)
    }
  }

  /**
   * Send one input command. Returns the seq it was stamped with (callers
   * record it in the prediction ring buffer), or -1 when not open — the
   * caller keeps simulating and reconciles when traffic resumes.
   */
  sendInput(cmd: InputCmd): number {
    if (!this.isOpen) return -1
    const s = this.seq
    if (this.sendRaw(encodeInput(cmd.moveX, cmd.moveY, [cmd.lookDir.x, cmd.lookDir.y, cmd.lookDir.z], cmd.actionMask, s))) {
      this.seq = (this.seq + 1) & 0xffff
      return s
    }
    return -1
  }

  /** Heartbeat per PROTOCOL: ping if nothing was sent in the last 2 s. */
  updateHeartbeat(nowMs: number): void {
    if (!this.isOpen || nowMs - this.lastSentAt < 2000) return
    if (this.sendRaw(encodePing(nowMs))) {
      this.lastSentAt = nowMs
    }
  }

  close(reason = 'user'): void {
    this.stopped = true
    this.cancelTimer()
    const ws = this.ws
    this.ws = null
    this.netState = 'closed'
    if (ws && ws.readyState <= WebSocket.OPEN) {
      ws.onopen = null
      ws.onmessage = null
      ws.onerror = null
      ws.onclose = null
      ws.close(1000, reason.slice(0, 120))
    }
    this.events.onState?.('closed', reason)
  }

  private teardown(): void {
    this.cancelTimer()
    const ws = this.ws
    this.ws = null
    this.netState = 'idle'
    if (ws) {
      ws.onopen = null
      ws.onmessage = null
      ws.onerror = null
      ws.onclose = null
      if (ws.readyState <= WebSocket.OPEN) ws.close(1000, 'reconnect')
    }
  }

  /**
   * HUD text for the reconnecting state: attempt count plus the delay of
   * the next scheduled retry.
   */
  get reconnectLabel(): string {
    let label = this.attempts > 0 ? `reconnecting… attempt ${this.attempts}` : 'reconnecting…'
    if (this.nextRetryMs > 0) label += `, next in ${(this.nextRetryMs / 1000).toFixed(1)} s`
    return label
  }

  /** Exponential backoff: 500 ms × 2^(attempt−1), cap 30 s, ±20% jitter. */
  private backoffMs(attempt: number): number {
    const base = Math.min(30_000, 500 * 2 ** (attempt - 1))
    return Math.round(base * (0.8 + 0.4 * Math.random()))
  }

  private cancelTimer(): void {
    if (this.timer !== null) {
      window.clearTimeout(this.timer)
      this.timer = null
    }
  }

  private sendRaw(bytes: Uint8Array<ArrayBuffer>): boolean {
    const ws = this.ws
    if (!ws || ws.readyState !== WebSocket.OPEN) return false
    if (bytes.length > MAX_MESSAGE_SIZE) return false
    ws.send(bytes)
    this.lastSentAt = performance.now()
    return true
  }

  private handleMessage(buf: ArrayBuffer): void {
    let type: number
    let payload: Uint8Array
    try {
      const f = unframe(buf)
      type = f.type
      payload = f.payload
    } catch (e) {
      if (e instanceof ProtocolError) {
        this.close(`protocol error: ${e.message}`)
      }
      return
    }
    switch (type) {
      case MSG.hello_ack:
        this.events.onHelloAck?.(decodeHelloAck(payload))
        break
      case MSG.terrain:
        this.events.onTerrain?.(decodeTerrain(payload))
        break
      case MSG.snapshot:
        this.events.onSnapshot?.(decodeSnapshot(payload))
        break
      case MSG.spawn:
        this.events.onSpawn?.(decodeSpawn(payload))
        break
      case MSG.despawn:
        this.events.onDespawn?.(decodeDespawn(payload))
        break
      case MSG.event:
        this.events.onEvent?.(decodeEvent(payload))
        break
      case MSG.pong: {
        const ts = decodePong(payload)
        this.rtt = performance.now() - ts
        this.events.onPong?.(ts, this.rtt)
        break
      }
      default:
        // Unknown message type: ignore, never throw into the event loop.
        break
    }
  }
}
