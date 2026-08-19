/**
 * JSONL trajectory dump — the shared conformance entry point
 * (ARCHITECTURE "Client": "the Go server exposes it as a subcommand, the
 * client sim as a Node entry point"). Both sims take the same input script
 * and emit the same output shape, so the conformance test is a file diff.
 * Pinned contract (agreed with the Go server):
 *
 * Input script (JSONL):
 *   line 1:  initial state, exactly the GDD State table
 *             { "pos": [x,y,z], "vel": [x,y,z], "grounded": bool,
 *               "facing": [x,y,z] }
 *             plus optional "look": [x,y,z] = the "last applied input's
 *             look" for tick 1 (defaults to facing).
 *   lines 2..N: one input per tick (GDD Input fields; N−1 ticks run)
 *             { "move_x": f, "move_y": f, "look_dir": [x,y,z],
 *               "action_mask": u16 }
 *
 * Output (JSONL): one line per stepped tick, tick 1-based:
 *   { "tick": n, "pos": [x,y,z], "vel": [x,y,z], "grounded": bool }
 *
 * Terrain: the server owns terrain generation, so the field is passed as a
 * separate JSON file (not in the script):
 *   { "face_grid": 65, "radius_min": 124, "radius_max": 190,
 *     "radii": [25350 numbers, wire order face·grid²+row·grid+col] }
 * Without --world, a flat 150 m sphere is used — fine for a quick check;
 * the real conformance run passes the server's generated field to both
 * sims:  server terrain --seed 1337 > world.json
 *
 *   npm run sim:dump -- <script.jsonl> <out.jsonl> [--world world.json]
 */
import { readFileSync, writeFileSync } from 'node:fs'
import {
  RULES,
  TICK_DT,
  decodeTerrain,
  makeTerrain,
  sanitizeLook,
  step,
  vec,
  type Input,
  type State,
  type Terrain,
  type Vec3,
} from '../src/sim/index.js'

function arg(name: string): string | undefined {
  const i = process.argv.indexOf(name)
  return i >= 0 ? process.argv[i + 1] : undefined
}

const scriptPath = arg('--script') ?? process.argv[2]
const outPath = arg('--out') ?? process.argv[3]
if (!scriptPath || !outPath) {
  console.error('usage: sim:dump <script.jsonl> <out.jsonl> [--world world.json]   (also: --script X --out Y)')
  process.exit(2)
}

let terrain: Terrain
const worldPath = arg('--world')
if (worldPath) {
  const field = JSON.parse(readFileSync(worldPath, 'utf8')) as {
    face_grid: number
    radius_min: number
    radius_max: number
    radii: number[]
  }
  const raw = new Uint16Array(field.radii)
  terrain = decodeTerrain(field.face_grid, field.radius_min, field.radius_max, raw)
} else {
  terrain = makeTerrain(RULES.faceGrid, RULES.radiusMin, RULES.radiusMax, () => RULES.planetRadius)
}

const lines = readFileSync(scriptPath, 'utf8')
  .split('\n')
  .map((l) => l.trim())
  .filter((l) => l.length > 0)
if (lines.length === 0) {
  console.error('empty script')
  process.exit(2)
}

const v3 = (a: [number, number, number]): Vec3 => ({ x: a[0], y: a[1], z: a[2] })
const start = JSON.parse(lines[0]) as {
  pos: [number, number, number]
  vel: [number, number, number]
  grounded: boolean
  facing: [number, number, number]
  look?: [number, number, number]
}
if (start.vel == null || start.facing == null || typeof start.grounded !== 'boolean') {
  console.error('script line 1 must be the full GDD State: pos, vel, grounded, facing')
  process.exit(2)
}

const inputs: Input[] = []
for (let i = 1; i < lines.length; i++) {
  const line = JSON.parse(lines[i]) as {
    move_x: number
    move_y: number
    look_dir: [number, number, number]
    action_mask: number
  }
  inputs.push({
    moveX: line.move_x,
    moveY: line.move_y,
    lookDir: v3(line.look_dir),
    actionMask: line.action_mask,
  })
}

let state: State = {
  pos: v3(start.pos),
  vel: v3(start.vel),
  grounded: start.grounded,
  facing: v3(start.facing),
}

// "Last applied input's look" (GDD "Input") — line 1's look, defaulting to
// facing, then the previous tick's sanitized input look.
let lastLook: Vec3 = start.look ? v3(start.look) : vec.copy(state.facing)

const out: string[] = []
for (let i = 0; i < inputs.length; i++) {
  state = step(state, inputs[i], terrain, TICK_DT, lastLook)
  lastLook = sanitizeLook(inputs[i], lastLook)
  out.push(
    JSON.stringify({
      tick: i + 1,
      pos: [state.pos.x, state.pos.y, state.pos.z],
      vel: [state.vel.x, state.vel.y, state.vel.z],
      grounded: state.grounded,
    }),
  )
}

writeFileSync(outPath, out.join('\n') + (out.length > 0 ? '\n' : ''))
console.log(`dumped ${out.length} ticks -> ${outPath}`)

