/**
 * WebSocket transport for the frozen wire protocol.
 *
 * The render loop never blocks on this: every network operation is an
 * async callback, and the only synchronous work is parsing inbound frames
 * (the biggest is the one-shot ~51 KB terrain field).
 *
 * Outbound: hello on open, input (one per local tick while simulating),
 * ping every 2 s when otherwise silent (PROTOCOL heartbeat).
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
import type { Vec3 } from '../sim/index.js'

export type NetState = 'idle' | 'connecting' | 'open' | 'closed'

export interface InputCmd {
  moveX: number
  moveY: number
  lookDir: Vec3
  actionMask: number
}

export interface NetEvents {
  onState?: (state: NetState, detail: string) => void
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
    this.netState = 'connecting'
    this.events.onState?.('connecting', url)
    const ws = new WebSocket(url)
    ws.binaryType = 'arraybuffer'
    this.ws = ws

    ws.onopen = () => {
      if (this.ws !== ws) return
      this.netState = 'open'
      this.lastSentAt = performance.now()
      this.sendRaw(encodeHello(PROTOCOL_VERSION, name))
      this.events.onState?.('open', url)
    }

    ws.onmessage = (ev: MessageEvent) => {
      if (this.ws !== ws) return
      this.handleMessage(ev.data as ArrayBuffer)
    }

    ws.onerror = () => {
      if (this.ws !== ws) return
      this.events.onState?.('connecting', `${url} — connection error`)
    }

    ws.onclose = (ev: CloseEvent) => {
      if (this.ws !== ws) return
      this.ws = null
      this.netState = 'closed'
      this.events.onState?.('closed', `closed ${ev.code}${ev.reason ? `: ${ev.reason}` : ''}`)
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
