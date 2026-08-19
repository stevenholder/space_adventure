/**
 * sim — pure movement + terrain core.
 *
 * Import surface for the renderer (src/), the Node tools (tools/), and the
 * headless conformance path. No DOM, no Three.js: enforced by
 * tsconfig.sim.json (lib ES2022 only), run by `npm run build`.
 */
export type { Input, State, Terrain, Vec3 } from './types.js'
export { ACTION, RULES, SPAWN_DIR, TICK_DT, clamp, lerp, vec } from './types.js'
export {
  FACE_COUNT,
  curvature,
  decodeTerrain,
  faceUV,
  latticeDir,
  makeTerrain,
  pickFace,
  sampleRadius,
  slopeAngle,
  slopeOK,
  surfaceNormal,
} from './terrain.js'
export {
  clampLook,
  inferGrounded,
  sanitizeLook,
  sanitizeMove,
  spawnLook,
  spawnState,
  step,
} from './step.js'
export { hash3, mulberry32 } from './rng.js'
