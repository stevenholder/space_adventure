/**
 * Headless smoke test for the pure sim (ARCHITECTURE "Client", item 4).
 *
 * Runs under Node via tsx — no DOM, no browser. Fails the build the moment
 * something in src/sim reaches for a browser global.
 *
 *   npm run sim:smoke
 */
import { TICK_DT, RULES, makeTerrain, sampleRadius, spawnState, step, vec, type Input } from '../src/sim/index.js'

const terrain = makeTerrain(RULES.faceGrid, RULES.radiusMin, RULES.radiusMax, () => RULES.planetRadius)

let state = spawnState(terrain)
const input: Input = { moveX: 1, moveY: 0, lookDir: { x: 1, y: 0, z: 0 }, actionMask: 0 }

for (let i = 0; i < 20; i++) {
  state = step(state, input, terrain, TICK_DT)
}

const up = vec.norm(state.pos)
const onSurface = Math.abs(vec.len(state.pos) - sampleRadius(terrain, up)) < 1e-9
const speed = vec.len(state.vel)
const finite = Number.isFinite(state.pos.x + state.pos.y + state.pos.z + state.vel.x)
const atWalkSpeed = Math.abs(speed - RULES.walkSpeed) / RULES.walkSpeed < 0.05

console.log(
  JSON.stringify({
    tick: 20,
    pos: [state.pos.x, state.pos.y, state.pos.z],
    vel: [state.vel.x, state.vel.y, state.vel.z],
    grounded: state.grounded,
    onSurface,
    speed,
  }),
)

if (!finite || !state.grounded || !onSurface || !atWalkSpeed) {
  console.error(`FAIL: finite=${finite} grounded=${state.grounded} onSurface=${onSurface} speed=${speed.toFixed(3)}`)
  process.exit(1)
}
console.log('sim smoke OK')
