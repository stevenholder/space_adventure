/**
 * Offline authority for `?mock` — no server needed.
 *
 * Fakes the wire contract exactly: same decoded message shapes the net
 * client emits (HelloAck / SpawnMsg / Snapshot / TerrainWire), same fixed
 * 50 ms tick, same sim (`sim/step.js`) as the Go server, same one-tick
 * input delay and snapshot latency as a real round trip. The replay
 * pipeline is therefore exercised for real: each snapshot acks the
 * previous tick's input, so the predictor drops one and re-simulates the
 * rest.
 *
 * Terrain is a small analytic cube-sphere: smooth mid-band undulation,
 * one crater (14 m), one spire (18 m, max slope ~40° — walkable, rocks
 * skip its steepest side), and a flat spawn plain around +Y. Radii are
 * quantized through the same u16 decode the net path uses, so the client
 * sees exactly what the server would send.
 *
 * Two remote "players" walk latitude circles at different heights and
 * speeds; their entities are synthesized per tick in the GDD wire frame
 * (+Z forward), so remotes exercise interpolation, orientation, and the
 * art-frame flip without a second client.
 */
import type { HelloAck, SpawnMsg, Snapshot, EntityState, TerrainWire } from '../net/protocol.js'
import { PROTOCOL_VERSION } from '../net/protocol.js'
import type { Input, State, Terrain, Vec3 } from '../sim/index.js'
import {
  RULES,
  TICK_DT,
  decodeTerrain,
  latticeDir,
  sampleRadius,
  sanitizeLook,
  spawnLook,
  spawnState,
  step,
  vec,
} from '../sim/index.js'
import { quatFromBasis } from '../util/quat.js'

export const MOCK_SEED = 1337
const MOCK_ENTITY_ID = 1

export const MOCK_WALKERS = [
  { id: 2, name: 'Vega', lat: 0.35, speed: 4.0, phase0: 0.0 },
  { id: 3, name: 'Orion', lat: -0.5, speed: 3.0, phase0: Math.PI * 0.7 },
]

function angDist(a: Vec3, b: Vec3): number {
  const d = vec.dot(a, b)
  return Math.acos(Math.min(1, Math.max(-1, d)))
}

/** Analytic radius at a unit direction (pre-quantization). */
function mockRadiusAt(d: Vec3): number {
  let r = 150
  // Mid-band undulation: wavelengths 5–7 rad, amplitude 4–9 m.
  r += 9 * Math.sin(4.1 * d.x + 1.7) * Math.cos(3.3 * d.y - 0.8)
  r += 6 * Math.sin(5.2 * d.z + 0.4) * Math.cos(4.7 * d.x + 2.1)
  r += 4 * Math.sin(3.1 * (d.x + d.y + d.z))
  // The Great Crater (basin) and The Spire (ridge) — both off the spawn face.
  const craterDir = vec.norm({ x: 0.3, y: 0.55, z: 0.78 })
  const spireDir = vec.norm({ x: -0.4, y: 0.3, z: -0.86 })
  r -= 14 * Math.exp(-Math.pow(angDist(d, craterDir) / 0.18, 2))
  r += 18 * Math.exp(-Math.pow(angDist(d, spireDir) / 0.12, 2))
  // Flatten the spawn plain: full flatten within 0.12 rad of +Y.
  const ang = angDist(d, { x: 0, y: 1, z: 0 })
  return r + (150 - r) * Math.min(1, Math.max(0, (ang - 0.12) / 0.1))
}

/** Raw radii in wire order (face · g² + row · g + col), quantized to u16. */
function mockRadiiWire(): Uint16Array {
  const g = RULES.faceGrid
  const out = new Uint16Array(6 * g * g)
  const span = RULES.radiusMax - RULES.radiusMin
  for (let f = 0; f < 6; f++) {
    for (let j = 0; j < g; j++) {
      for (let i = 0; i < g; i++) {
        const d = latticeDir(f, i, j, g)
        const r = mockRadiusAt(d)
        const q = Math.round(((r - RULES.radiusMin) / span) * 65535)
        out[f * g * g + j * g + i] = Math.min(65535, Math.max(0, q))
      }
    }
  }
  return out
}

interface Walker {
  id: number
  name: string
  lat: number
  speed: number
  phase: number
  omega: number
}

export class MockServer {
  /** Quantized terrain in wire order (what a real server would send). */
  readonly wire: TerrainWire
  /** Decoded form — the mock sim runs against exactly this. */
  readonly terrain: Terrain

  private state: State
  private lastInput: Input
  private lastLook: Vec3 | null = null
  private pendingSeq = -1
  private pendingInput: Input | null = null
  private ackSeq = 0xffff
  private tickCount = 0
  private outbox: Snapshot[] = []
  private walkers: Walker[]

  constructor() {
    this.wire = {
      faceGrid: RULES.faceGrid,
      radiusMin: RULES.radiusMin,
      radiusMax: RULES.radiusMax,
      radii: mockRadiiWire(),
    }
    this.terrain = decodeTerrain(this.wire.faceGrid, this.wire.radiusMin, this.wire.radiusMax, this.wire.radii)
    this.state = spawnState(this.terrain)
    this.lastInput = { moveX: 0, moveY: 0, lookDir: spawnLook(), actionMask: 0 }
    this.walkers = MOCK_WALKERS.map((w) => ({
      id: w.id,
      name: w.name,
      lat: w.lat,
      speed: w.speed,
      phase: w.phase0,
      // Angular rate so the walker's ground speed ≈ `speed` m/s at r ≈ 150.
      omega: w.speed / (150 * Math.cos(w.lat)),
    }))
  }

  helloAck(): HelloAck {
    return {
      serverVer: PROTOCOL_VERSION,
      tickHz: RULES.tickHz,
      worldSeed: MOCK_SEED,
      entityId: MOCK_ENTITY_ID,
    }
  }

  spawnMessages(): SpawnMsg[] {
    return MOCK_WALKERS.map((w) => ({ entityId: w.id, entityType: 1, name: w.name }))
  }

  /**
   * Advance one mock tick. `clientInput`/`seq` is the command the client
   * "sent" this tick (delivered to the sim one tick late, like a real RTT
   * of one). Returns the snapshot that "arrives" this tick (the previous
   * tick's state, one tick of delivery latency) or null.
   */
  tick(clientInput: Input, seq: number): Snapshot | null {
    const out = this.outbox.shift() ?? null

    // The command from the previous tick arrives now.
    if (this.pendingInput !== null) {
      this.lastInput = this.pendingInput
      this.lastLook = sanitizeLook(this.lastInput, this.lastLook ?? spawnLook())
      this.ackSeq = this.pendingSeq
    }
    this.pendingSeq = seq
    this.pendingInput = clientInput

    // Server advances one tick with the latest input it holds.
    this.state = step(this.state, this.lastInput, this.terrain, TICK_DT, this.lastLook ?? spawnLook())
    this.tickCount++

    for (const w of this.walkers) w.phase += w.omega * TICK_DT

    this.outbox.push(this.makeSnapshot())
    if (this.outbox.length > 8) this.outbox.shift()
    return out
  }

  private makeSnapshot(): Snapshot {
    const entities: EntityState[] = [
      {
        id: MOCK_ENTITY_ID,
        pos: [this.state.pos.x, this.state.pos.y, this.state.pos.z],
        quat: quatToTuple(quatFromBasis(vec.norm(this.state.pos), this.state.facing)),
        vel: [this.state.vel.x, this.state.vel.y, this.state.vel.z],
      },
      ...this.walkers.map((w) => this.walkerEntity(w)),
    ]
    return { tick: this.tickCount, ackSeq: this.ackSeq, entities }
  }

  private walkerEntity(w: Walker): EntityState {
    const eps = 1e-4
    const d = walkerDir(w.lat, w.phase)
    const r = sampleRadius(this.terrain, d)
    const d2 = walkerDir(w.lat, w.phase + eps)
    const r2 = sampleRadius(this.terrain, d2)
    const pos = vec.scale(d, r)
    const pos2 = vec.scale(d2, r2)
    const vel = vec.scale(vec.sub(pos2, pos), 1 / eps)
    // Facing = velocity projected tangent to the surface.
    let facing = vec.sub(vel, vec.scale(d, vec.dot(vel, d)))
    if (vec.len(facing) < 1e-9) facing = vec.cross(d, { x: 1, y: 0, z: 0 })
    facing = vec.norm(facing)
    return {
      id: w.id,
      pos: [pos.x, pos.y, pos.z],
      quat: quatToTuple(quatFromBasis(d, facing)),
      vel: [vel.x, vel.y, vel.z],
    }
  }
}

function quatToTuple(q: { x: number; y: number; z: number; w: number }): [number, number, number, number] {
  return [q.x, q.y, q.z, q.w]
}

function walkerDir(lat: number, phase: number): Vec3 {
  return vec.norm({
    x: Math.cos(lat) * Math.cos(phase),
    y: Math.sin(lat),
    z: Math.cos(lat) * Math.sin(phase),
  })
}
