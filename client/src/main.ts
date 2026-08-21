/**
 * Client bootstrap.
 *
 * Two authority sources, one pipeline:
 *   live  — NetClient (WebSocket, frozen wire protocol)
 *   mock  — MockServer (offline fake authority, ?mock)
 * Both feed the same Predictor (50 ms fixed steps, replay reconciliation)
 * and the same World (radial-up first-person scene). The render loop
 * interpolates predicted state at 60 fps and never blocks on I/O.
 *
 * PROTOCOL: the client must not simulate before terrain arrives — ticking
 * starts only in onTerrain.
 */
import * as THREE from 'three'
import { parseConfig } from './config.js'
import { TICK_DT, decodeTerrain, spawnState, vec } from './sim/index.js'
import type { Terrain, Vec3 } from './sim/index.js'
import { Predictor } from './net/predictor.js'
import { NetClient } from './net/netClient.js'
import { PROTOCOL_VERSION } from './net/protocol.js'
import type { Snapshot } from './net/protocol.js'
import { MockServer, MOCK_SEED } from './mock/mock.js'
import { World } from './scene/world.js'
import { Hud } from './hud/hud.js'
import { Controls } from './input/controls.js'
import { AssetLib } from './scene/assets.js'
import { makeRigFromGltf, rigIsComplete } from './scene/character.js'

const TICK_MS = TICK_DT * 1000
const MAX_FRAME_MS = 250 // clamp: tab switches must not spiral
// Frame-loop scratch: the render pass must not allocate (GC spikes show up
// as frame jitter). Allocations at 20 Hz tick or event rate are fine.
const scratchUp: Vec3 = { x: 0, y: 0, z: 0 }
const scratchPos: Vec3 = { x: 0, y: 0, z: 0 }

const cfg = parseConfig(location.search)

// --------------------------------------------------------------------- DOM
const appEl = document.getElementById('app') as HTMLDivElement
const hudEl = document.getElementById('hud') as HTMLDivElement
const statusEl = document.getElementById('status') as HTMLDivElement
const hintEl = document.getElementById('hint') as HTMLDivElement
const tagsEl = document.getElementById('tags') as HTMLDivElement

// Backing store at 1:1 CSS pixels. The QA-gate box rasterizes through a
// virtualized iGPU (ANGLE/D3D11 over WSL2): a >1.0 device-pixel ratio
// multiplies fragment + present cost with no visible gain on a flat-shaded
// low-poly scene (no AA, no text in the GL canvas), and it is what pushed
// full-viewport frames past the 16.7 ms budget intermittently.
const renderer = new THREE.WebGLRenderer({ antialias: false })
renderer.setPixelRatio(1)
renderer.setSize(window.innerWidth, window.innerHeight)
appEl.appendChild(renderer.domElement)
const world = new World(tagsEl)
const hud = new Hud(hudEl)
const controls = new Controls(renderer.domElement)
const predictor = new Predictor()

let myId = 0
let worldSeed = MOCK_SEED
let simReady = false
let mock: MockServer | null = null
let net: NetClient | null = null
let seq = 0
let accMs = 0
let prevNow = performance.now()
let statusTimer = 0
/** Loaded `char.player` scene; every body gets its own clone as a rig. */
let playerScene: THREE.Object3D | null = null

// ?corrupt dev override (ROADMAP criterion 3): every 5 s the rendered local
// position is forced 4 m off the server truth for 1 s, then the next
// snapshot's replay correction visibly snaps it back.
let corruptNext = 0
let corruptUntil = 0
let corruptSide: Vec3 = { x: 0, y: 0, z: 0 }
const CORRUPT_DIST = 4

const remoteIds = new Set<number>()
const hudData = { speed: null as number | null, nearest: null as number | null, conn: '' }

function flashStatus(text: string): void {
  statusEl.textContent = text
  window.clearTimeout(statusTimer)
  statusTimer = window.setTimeout(() => {
    statusEl.textContent = ''
  }, 4000)
}

function connText(): string {
  if (cfg.mock) return 'mock'
  if (!net) return 'connecting…'
  switch (net.state) {
    case 'open':
      return net.rtt > 0 ? `online ${Math.round(net.rtt)} ms` : 'online'
    case 'reconnecting':
      return net.reconnectLabel
    case 'connecting':
      return 'connecting…'
    default:
      return 'disconnected'
  }
}

function hudPush(force = false): void {
  const st = predictor.stateRef
  hudData.speed = st ? vec.len(st.vel) : null
  hudData.nearest = st ? world.nearestDist(st.pos) : null
  hudData.conn = connText()
  if (force) hud.flush(hudData)
  else hud.update(hudData)
}

controls.onLock = (locked) => {
  hintEl.style.display = locked ? 'none' : ''
}
renderer.domElement.addEventListener('click', () => {
  if (!controls.isLocked) controls.requestLock()
})
window.addEventListener('resize', () => {
  renderer.setSize(window.innerWidth, window.innerHeight)
  world.setAspect(window.innerWidth, window.innerHeight)
})

// ------------------------------------------------------------ authority
function processSnapshot(snap: Snapshot, nowMs: number): void {
  for (const e of snap.entities) {
    if (e.id === myId) predictor.reconcile(e, snap.ackSeq, nowMs)
    else world.feedRemote(e.id, e, snap.tick, nowMs)
  }
}

function onSpawn(id: number, name: string): void {
  // Own entity: the predictor + local body are its entry. The server
  // re-anchors it via its snapshot rows (and the fresh SPAWN on join);
  // upserting it as a remote would duplicate the body.
  if (id === myId) return
  world.upsertRemote(id, name)
  remoteIds.add(id)
  // Late joiner (asset already loaded): give it the real model.
  if (playerScene) world.setRemoteRig(id, makeRigFromGltf(playerScene.clone(true)))
}

function onDespawn(id: number): void {
  world.removeRemote(id)
  remoteIds.delete(id)
}

/** Shared: terrain arrived (PROTOCOL: only now may we simulate). */
function onTerrain(t: Terrain): void {
  if (simReady) return
  world.addTerrain(t, worldSeed)
  const spawn = spawnState(t)
  predictor.seed(spawn, t, performance.now())
  controls.reset(spawn.facing)
  controls.setWorldUp(vec.norm(spawn.pos))
  simReady = true
  hudPush(true)
}

/**
 * Connection re-established after a loss (NetClient.onReconnect): the
 * fresh join handshake (hello_ack, terrain, complete spawn list) is the
 * complete truth, so drop everything the old connection believed.
 * Inputs held during the disconnect are dropped — the server spawns the
 * new entity at the spawn point and the first snapshot reconciles.
 */
function onResync(): void {
  world.clearRemotes()
  remoteIds.clear()
  const t = world.terrainRef
  if (!t) return // terrain never arrived; the fresh handshake seeds it
  predictor.reset()
  // reset() clears lastAck: the new connection's low ack seqs would
  // else compare wraparound-stale against the old one and every
  // snapshot would be rejected. Re-seed at spawn (where the server
  // places the new entity); the first snapshot owns the truth.
  predictor.seed(spawnState(t), t, performance.now())
}

function startMock(): void {
  mock = new MockServer()
  myId = mock.helloAck().entityId
  for (const sp of mock.spawnMessages()) onSpawn(sp.entityId, sp.name)
  flashStatus('mock authority — no server')
  onTerrain(mock.terrain)
}

function startLive(): void {
  net = new NetClient({
    onState: (_s, detail) => {
      flashStatus(detail)
      hudPush(true)
    },
    onHelloAck: (ack) => {
      if (ack.serverVer !== PROTOCOL_VERSION) {
        net?.close(`protocol version ${ack.serverVer} ≠ ${PROTOCOL_VERSION}`)
        return
      }
      myId = ack.entityId
      worldSeed = ack.worldSeed
      hudPush(true)
    },
    onTerrain: (wire) => {
      onTerrain(decodeTerrain(wire.faceGrid, wire.radiusMin, wire.radiusMax, wire.radii))
    },
    onSnapshot: (snap) => processSnapshot(snap, performance.now()),
    onSpawn: (sp) => onSpawn(sp.entityId, sp.name),
    onDespawn: (id) => onDespawn(id),
    onReconnect: () => onResync(),
    onPong: () => hudPush(true),
  })
  net.connect(cfg.wsUrl, cfg.name)
}

// ----------------------------------------------------------------- ticks
/** One fixed 50 ms step: sample input, predict, hand to the authority. */
function tickStep(nowMs: number): void {
  const input = controls.input()
  if (mock) {
    const s = seq++ & 0xffff
    predictor.predict(input, s, nowMs)
    const snap = mock.tick(input, s)
    if (snap) processSnapshot(snap, nowMs)
  } else if (net) {
    // The wire seq and the replay-buffer seq MUST be the same number.
    const s = net.sendInput(input)
    predictor.predict(input, s, nowMs)
    net.updateHeartbeat(nowMs)
  }
}

// --------------------------------------------------------------- render
function frame(now: number): void {
  requestAnimationFrame(frame)
  const elapsed = Math.min(now - prevNow, MAX_FRAME_MS)
  prevNow = now
  const frameDt = elapsed / 1000

  if (simReady) {
    accMs += elapsed
    while (accMs >= TICK_MS) {
      accMs -= TICK_MS
      tickStep(now)
    }
  }

  const rs = predictor.renderState(now)
  if (rs) {
    // Radial up, normalized once and reused for the look frame, body and
    // camera (the render pass allocates nothing).
    const px = rs.pos.x
    const py = rs.pos.y
    const pz = rs.pos.z
    const il = 1 / Math.hypot(px, py, pz)
    scratchUp.x = px * il
    scratchUp.y = py * il
    scratchUp.z = pz * il
    controls.setWorldUp(scratchUp)

    // Dev override: force a bad local position, briefly.
    let pos = rs.pos
    let vel = predictor.stateRef ? predictor.stateRef.vel : null
    if (cfg.corrupt) {
      if (now >= corruptNext) {
        corruptNext = now + 5000
        corruptUntil = now + 1000
        const up = scratchUp
        corruptSide = vec.norm(vec.cross(up, { x: 1, y: 0, z: 0 }))
      }
      if (now < corruptUntil) {
        pos = vec.add(pos, vec.scale(corruptSide, CORRUPT_DIST))
        if (vel) vel = vec.add(vel, vec.scale(corruptSide, 10))
      }
    }

    world.setLocal(pos, rs.facing, controls.lookDir)
    hudData.speed = vel ? vec.len(vel) : null
  }

  world.frameRemotes(now, frameDt, rs ? rs.pos : scratchPos)
  hudData.nearest = rs ? world.nearestDist(rs.pos) : null
  hudData.conn = connText()
  hud.update(hudData)

  renderer.render(world.scene, world.camera)
}

// ---------------------------------------------------------------- assets
const lib = new AssetLib()
void lib
  .loadManifest()
  .then(async () => {
    // char.player: swap the local body (and remotes) for the real model.
    const player = await lib.loadGltf('char.player')
    if (player) {
      playerScene = player
      // Each body needs its own node hierarchy: a THREE object has a
      // single parent, so the cached scene cannot be shared. Geometry and
      // materials stay shared (GPU buffers), only transforms clone.
      const rig = makeRigFromGltf(player.clone(true))
      if (rig.eye || rigIsComplete(rig)) {
        world.setLocalRig(rig)
        for (const id of remoteIds) {
          world.setRemoteRig(id, makeRigFromGltf(player.clone(true)))
        }
      }
    }
    // Rock props: swap geometry only; the seeded scatter placement is final.
    const rocks: THREE.BufferGeometry[] = []
    for (const id of ['prop.rock.a', 'prop.rock.b', 'prop.rock.c']) {
      const obj = await lib.loadGltf(id)
      const mesh = AssetLib.firstGeometry(obj)
      if (mesh) rocks.push((mesh as THREE.Mesh).geometry)
    }
    if (rocks.length === 3) world.setRockGeoms(rocks)
  })
  .catch(() => {
    /* asset failures never block: placeholder path is the design */
  })

// ------------------------------------------------------------------ start
world.setAspect(window.innerWidth, window.innerHeight)
hudPush(true)
if (cfg.mock) startMock()
else startLive()
requestAnimationFrame(frame)
