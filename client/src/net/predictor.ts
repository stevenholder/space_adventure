/**
 * Local player prediction + replay reconciliation (ARCHITECTURE "Network
 * model").
 *
 * The client steps the local entity at a fixed 50 ms (the server tick)
 * with the current command state and sends that command to the server as
 * an `input` (one per tick, each stamped with a fresh u16 seq). When a
 * snapshot arrives with `ack_seq`:
 *
 *   1. snap state to the server entity (pos/vel/quat, grounded inferred),
 *   2. drop buffered inputs with seq ≤ ack_seq (wrap-safe),
 *   3. re-simulate the remaining buffered inputs, one per tick.
 *
 * No lerping toward server state — that rubber-bands by
 * `velocity × latency`. Replay re-runs the steps the server already ran,
 * so the correction is only the residual (packet reordering, sim drift),
 * not the whole predicted distance.
 *
 * The server applies the *latest* input each tick (PROTOCOL: current
 * command state, constant between inputs), so one input per tick makes
 * the buffered seq↔tick mapping exact.
 *
 * The render loop reads `renderState()`, which interpolates between the
 * last two predicted states (50 ms behind the latest), giving 60 fps
 * smoothness on top of a 20 Hz sim. Look is never part of this: the
 * camera applies it instantly.
 */
import type { Input, State, Terrain, Vec3 } from '../sim/index.js'
import {
  RULES,
  TICK_DT,
  inferGrounded,
  sanitizeLook,
  spawnLook,
  step,
  vec,
} from '../sim/index.js'
import { seqNewer, type EntityState } from './protocol.js'

const HISTORY_LEN = 16
const INPUT_BUFFER = 256

interface HistoryEntry {
  timeMs: number
  pos: Vec3
  facing: Vec3
}

export class Predictor {
  private state: State | null = null
  private terrain: Terrain | null = null
  private inputs = new Map<number, Input>()
  private history: HistoryEntry[] = []
  /** Seq of the last snapshot we reconciled against (-1 = never). */
  lastAck = -1
  /** The last applied input's look (GDD "Input" fallback), per tick. */
  private lastLook: Vec3 | null = null
  /** Ticks stepped since start — debug/HUD use. */
  tick = 0

  get hasState(): boolean {
    return this.state !== null && this.terrain !== null
  }

  get stateRef(): State | null {
    return this.state
  }

  /**
   * Seed the local state (spawn). Until a snapshot reconciles, prediction
   * runs from here; after that, snapshots own the truth.
   */
  seed(state: State, terrain: Terrain, nowMs: number): void {
    this.state = state
    this.terrain = terrain
    this.lastLook = null
    this.history = [{ timeMs: nowMs, pos: vec.copy(state.pos), facing: vec.copy(state.facing) }]
    this.inputs.clear()
    this.tick = 0
  }

  /**
   * One fixed prediction step with `input`. Records the input under
   * `seq` (when ≥ 0, i.e. it went over the wire) and pushes a history
   * entry. Returns the new state.
   */
  predict(input: Input, seq: number, nowMs: number): State {
    const s = this.state
    const t = this.terrain
    if (!s || !t) throw new Error('predict before seed')
    if (seq >= 0) {
      this.inputs.set(seq, input)
      if (this.inputs.size > INPUT_BUFFER) this.trimInputs()
    }
    const prevLook = this.lastLook ?? spawnLook()
    this.state = step(s, input, t, TICK_DT, prevLook)
    this.lastLook = sanitizeLook(input, prevLook)
    this.tick++
    this.history.push({
      timeMs: nowMs,
      pos: vec.copy(this.state.pos),
      facing: vec.copy(this.state.facing),
    })
    if (this.history.length > HISTORY_LEN) this.history.shift()
    return this.state
  }

  /**
   * Replay reconciliation. `entity` is the local player's row in the
   * snapshot; `ackSeq` the seq the server last applied. Stale snapshots
   * (ack ≤ lastAck) are ignored.
   */
  reconcile(entity: EntityState, ackSeq: number, nowMs: number): boolean {
    const t = this.terrain
    if (!t || !this.state) return false
    if (this.lastAck >= 0 && !seqNewer(ackSeq, this.lastAck)) return false
    this.lastAck = ackSeq

    const pos: Vec3 = { x: entity.pos[0], y: entity.pos[1], z: entity.pos[2] }
    const vel: Vec3 = { x: entity.vel[0], y: entity.vel[1], z: entity.vel[2] }
    const up = vec.norm(pos)
    // GDD: the owning client takes the server pos/vel and re-derives facing
    // from its OWN (client-authoritative) look — never the server quat for
    // its own body. The current look is the latest sent input's look.
    const lastInput = this.latestInput()
    const prevLook = this.lastLook ?? spawnLook()
    const look = lastInput ? sanitizeLook(lastInput, prevLook) : vec.copy(prevLook)
    const tang = vec.sub(look, vec.scale(up, vec.dot(look, up)))
    const facing =
      vec.len(tang) >= RULES.facingHold ? vec.norm(tang) : vec.copy(this.state.facing)
    this.state = {
      pos,
      vel,
      facing,
      grounded: inferGrounded(pos, t),
    }

    // Re-simulate every buffered input newer than the ack, threading the
    // last-applied look through (GDD "Input" degenerate-look fallback).
    const pending = [...this.inputs.entries()].filter(([s]) => seqNewer(s, ackSeq))
    pending.sort((a, b) => (seqNewer(a[0], b[0]) ? 1 : -1))
    let curLook = this.lastLook ?? spawnLook()
    for (const [s, input] of pending) {
      this.state = step(this.state, input, t, TICK_DT, curLook)
      curLook = sanitizeLook(input, curLook)
      this.inputs.delete(s)
    }
    this.lastLook = curLook
    this.tick += pending.length
    this.history = [
      { timeMs: nowMs, pos: vec.copy(this.state.pos), facing: vec.copy(this.state.facing) },
    ]
    return true
  }

  /**
   * Render state interpolated one tick behind the latest prediction:
   * lerp(history[n-1], history[n]) at the render clock. Look is NOT
   * included — the camera applies it instantly.
   */
  renderState(nowMs: number): { pos: Vec3; facing: Vec3 } | null {
    const s = this.state
    if (!s) return null
    const h = this.history
    if (h.length === 0) return { pos: vec.copy(s.pos), facing: vec.copy(s.facing) }
    const last = h[h.length - 1]
    if (h.length === 1) return { pos: vec.copy(last.pos), facing: vec.copy(last.facing) }
    const prev = h[h.length - 2]
    const denom = last.timeMs - prev.timeMs
    const t = denom > 0 ? (nowMs - TICK_DT * 1000 - prev.timeMs) / denom : 1
    const k = t < 0 ? 0 : t > 1 ? 1 : t
    return {
      pos: vec.lerp(prev.pos, last.pos, k),
      facing: vec.norm(vec.add(vec.scale(prev.facing, 1 - k), vec.scale(last.facing, k))),
    }
  }

  /** Drop all prediction state (connection loss, respawn). */
  reset(): void {
    this.state = null
    this.inputs.clear()
    this.history = []
    this.lastAck = -1
    this.lastLook = null
    this.tick = 0
  }

  /** The newest buffered input (the client's current command state). */
  private latestInput(): Input | null {
    let latest: Input | null = null
    let latestSeq = -1
    for (const [seq, input] of this.inputs) {
      if (latest === null || seqNewer(seq, latestSeq)) {
        latest = input
        latestSeq = seq
      }
    }
    return latest
  }

  private trimInputs(): void {
    if (this.inputs.size <= INPUT_BUFFER) return
    const keys = [...this.inputs.keys()].sort((a, b) => (seqNewer(a, b) ? -1 : 1))
    for (let i = keys.length - 1; i >= INPUT_BUFFER; i--) {
      this.inputs.delete(keys[i])
    }
  }
}
