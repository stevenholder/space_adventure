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

// Strafe handedness — the TS half of server/internal/sim TestStrafeDirection.
//
// This axis had no coverage on either end, which is how right = up × facing
// (the player's LEFT in a right-handed frame) shipped through all of M1 with
// A and D swapped: both sims agreed, so the conformance diff passed, and the
// C5 and C10 routes are pure forward motion with move_x == 0 throughout.
// Replay only converges while the two sims match, so this must be asserted on
// both ends, not just in Go.
//
// GDD "M1 on-foot movement" → "Axis mapping": move_x is +right, along facing × up.
{
  let s2 = spawnState(terrain)
  const up0 = vec.norm(s2.pos)
  const facing0 = vec.copy(s2.facing)
  const trueRight = vec.norm(vec.cross(facing0, up0)) // right-handed: right = forward × up
  const start = vec.copy(s2.pos)
  const strafe = { moveX: 1, moveY: 0, lookDir: facing0, actionMask: 0 }
  for (let i = 0; i < 20; i++) s2 = step(s2, strafe, terrain, TICK_DT)

  const d = vec.sub(s2.pos, start)
  const along = vec.dot(d, trueRight)
  const lateral = vec.len(vec.sub(d, vec.scale(trueRight, along)))
  console.log(JSON.stringify({ strafeAlongRight: along, offAxis: lateral }))
  if (!(along > 0)) {
    console.error(
      `FAIL: move_x=+1 displaced ${along.toFixed(3)} m along the player's right; want positive ` +
        `(A and D inverted — check the cross-product order in the wish vector)`,
    )
    process.exit(1)
  }
  if (lateral > 0.2) {
    console.error(`FAIL: strafe drifted ${lateral.toFixed(3)} m off the right axis; want purely lateral`)
    process.exit(1)
  }
}

console.log('sim smoke OK')
