/**
 * Remote entity interpolation (ARCHITECTURE "Network model"): remotes are
 * rendered ~100 ms in the past, interpolated between received snapshots,
 * with short extrapolation (capped at 150 ms) when the buffer drains.
 *
 * Remote players are interpolated, never replayed — replay is only for the
 * local player against its own acked inputs.
 */
import type { Vec3 } from '../sim/index.js'
import type { Quat } from '../util/quat.js'
import { quatSlerpInto } from '../util/quat.js'

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

  /**
   * Render state at `nowMs - BUFFER_MS`, written into `out` and returned
   * (no allocation — called per remote per frame from the render loop).
   * False before the first sample.
   */
  renderInto(nowMs: number, out: RemoteRender): boolean {
    const n = this.buf.length
    if (n === 0) return false
    const t = nowMs - BUFFER_MS
    const last = this.buf[n - 1]
    if (t >= last.recvMs) {
      // Buffer drained: extrapolate with the last velocity, capped.
      const lead = Math.min(last.recvMs + MAX_EXTRAP_MS - t, MAX_EXTRAP_MS)
      if (lead <= 0) {
        this.sampleInto(last, out)
        out.extrapolatedMs = 0
        return true
      }
      const dt = lead / 1000
      out.pos.x = last.pos.x + last.vel.x * dt
      out.pos.y = last.pos.y + last.vel.y * dt
      out.pos.z = last.pos.z + last.vel.z * dt
      out.quat.x = last.quat.x
      out.quat.y = last.quat.y
      out.quat.z = last.quat.z
      out.quat.w = last.quat.w
      out.vel.x = last.vel.x
      out.vel.y = last.vel.y
      out.vel.z = last.vel.z
      out.extrapolatedMs = lead
      return true
    }
    const first = this.buf[0]
    if (t < first.recvMs) {
      this.sampleInto(first, out)
      out.extrapolatedMs = 0
      return true
    }
    // Find the pair bracketing t (walk from the back — it is almost always
    // the newest pair).
    for (let i = n - 1; i >= 1; i--) {
      const b = this.buf[i]
      const a = this.buf[i - 1]
      if (t < a.recvMs) continue
      const span = b.recvMs - a.recvMs
      const k = span > 0 ? (t - a.recvMs) / span : 1
      out.pos.x = a.pos.x + (b.pos.x - a.pos.x) * k
      out.pos.y = a.pos.y + (b.pos.y - a.pos.y) * k
      out.pos.z = a.pos.z + (b.pos.z - a.pos.z) * k
      quatSlerpInto(a.quat, b.quat, k, out.quat)
      out.vel.x = a.vel.x + (b.vel.x - a.vel.x) * k
      out.vel.y = a.vel.y + (b.vel.y - a.vel.y) * k
      out.vel.z = a.vel.z + (b.vel.z - a.vel.z) * k
      out.extrapolatedMs = 0
      return true
    }
    this.sampleInto(last, out)
    out.extrapolatedMs = 0
    return true
  }

  private sampleInto(s: RemoteSample, out: RemoteRender): void {
    out.pos.x = s.pos.x
    out.pos.y = s.pos.y
    out.pos.z = s.pos.z
    out.quat.x = s.quat.x
    out.quat.y = s.quat.y
    out.quat.z = s.quat.z
    out.quat.w = s.quat.w
    out.vel.x = s.vel.x
    out.vel.y = s.vel.y
    out.vel.z = s.vel.z
  }
}
