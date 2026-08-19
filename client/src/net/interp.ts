/**
 * Remote entity interpolation (ARCHITECTURE "Network model"): remotes are
 * rendered ~100 ms in the past, interpolated between received snapshots,
 * with short extrapolation (capped at 150 ms) when the buffer drains.
 *
 * Remote players are interpolated, never replayed — replay is only for the
 * local player against its own acked inputs.
 */
import type { Vec3 } from '../sim/index.js'
import { vec } from '../sim/index.js'
import type { Quat } from '../util/quat.js'
import { quatSlerp } from '../util/quat.js'

const BUFFER_MS = 100
const MAX_EXTRAP_MS = 150
const MAX_SAMPLES = 64

export interface RemoteSample {
  tick: number
  pos: Vec3
  quat: Quat
  vel: Vec3
  /** Local receive time, ms (performance.now base). */
  recvMs: number
}

export interface RemoteRender {
  pos: Vec3
  quat: Quat
  vel: Vec3
  /** How far past the newest sample we extrapolated (ms); 0 = buffered. */
  extrapolatedMs: number
}

export class InterpBuffer {
  private buf: RemoteSample[] = []

  push(s: RemoteSample): void {
    this.buf.push(s)
    if (this.buf.length > MAX_SAMPLES) this.buf.shift()
  }

  clear(): void {
    this.buf.length = 0
  }

  get size(): number {
    return this.buf.length
  }

  /** Render state at `nowMs - BUFFER_MS`, or null before the first sample. */
  render(nowMs: number): RemoteRender | null {
    const n = this.buf.length
    if (n === 0) return null
    const t = nowMs - BUFFER_MS
    const last = this.buf[n - 1]
    if (t >= last.recvMs) {
      // Buffer drained: extrapolate with the last velocity, capped.
      const lead = Math.min(last.recvMs + MAX_EXTRAP_MS - t, MAX_EXTRAP_MS)
      if (lead <= 0) return { pos: last.pos, quat: last.quat, vel: last.vel, extrapolatedMs: 0 }
      const dt = lead / 1000
      return {
        pos: vec.add(last.pos, vec.scale(last.vel, dt)),
        quat: last.quat,
        vel: last.vel,
        extrapolatedMs: lead,
      }
    }
    if (t < this.buf[0].recvMs) {
      return { pos: this.buf[0].pos, quat: this.buf[0].quat, vel: this.buf[0].vel, extrapolatedMs: 0 }
    }
    // Find the pair bracketing t (walk from the back — it is almost always
    // the newest pair).
    for (let i = n - 1; i >= 1; i--) {
      const b = this.buf[i]
      const a = this.buf[i - 1]
      if (t < a.recvMs) continue
      const span = b.recvMs - a.recvMs
      const k = span > 0 ? (t - a.recvMs) / span : 1
      return {
        pos: vec.lerp(a.pos, b.pos, k),
        quat: quatSlerp(a.quat, b.quat, k),
        vel: vec.lerp(a.vel, b.vel, k),
        extrapolatedMs: 0,
      }
    }
    return { pos: last.pos, quat: last.quat, vel: last.vel, extrapolatedMs: 0 }
  }
}
