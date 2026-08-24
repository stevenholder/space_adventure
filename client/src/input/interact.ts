/**
 * Look-at interaction targeting (GDD "Interaction").
 *
 * Pure function over plain data — no DOM, no Three.js — so it is
 * unit-testable without a renderer; the render loop calls it every frame
 * with the current eye transform and the entity list, and the HUD renders
 * whatever verb comes back.
 *
 * A candidate qualifies when it is within `INTERACT_DIST` of the eye AND
 * inside the `INTERACT_CONE_DEG` look cone. Among qualifying candidates the
 * LARGEST dot product wins — the thing the player is most directly looking
 * at, not the nearest. Nearest picks the wrong target when two NPCs stand
 * together, the common case at a shop (GDD "Interaction").
 *
 * The server re-validates distance and cone on the `cmd` (GDD, W2-14); this
 * is a UI affordance only.
 */
import type { Vec3 } from '../sim/index.js'
import { vec } from '../sim/index.js'

/** GDD "Interaction" rule table. */
export const INTERACT_DIST = 3.0 // m
export const INTERACT_CONE_DEG = 20 // deg, half-angle
const COS_INTERACT_CONE = Math.cos((INTERACT_CONE_DEG * Math.PI) / 180)

/** An entity whose def carries a `verb` (`"Talk"`, `"Pick up"`, `"Board"`). */
/**
 * Eye height above the feet (GDD "First-person body").
 *
 * Candidates are entity ORIGINS — a standing character's feet. The GDD's
 * interaction rule is against the target's EYE, and the difference decides the
 * check: from a 1.7 m eye at 2 m away, the vector to another character's feet
 * points ~40 degrees down, outside the 20 degree cone. Testing feet meant the
 * prompt never appeared for someone standing in front of you, and the server
 * refused with out_of_range if you sent it anyway.
 */
const EYE_HEIGHT = 1.7

export interface Interactable {
  entityId: number
  pos: Vec3
  verb: string
}

/**
 * The interactable the player is most directly looking at, or null when
 * nothing in `candidates` is both in range and in cone.
 */
export function pickInteractable(
  eye: Vec3,
  lookDir: Vec3,
  candidates: Interactable[],
): Interactable | null {
  let best: Interactable | null = null
  let bestDot = -Infinity
  for (const c of candidates) {
    // Raise the candidate to its own eye height along ITS radial up — the
    // GDD checks against the target's eye, and on a round world "up" differs
    // per entity. Must match the server's inRange or the prompt appears for
    // interactions the server then refuses.
    const targetUp = vec.norm(c.pos)
    const targetEye = vec.add(c.pos, vec.scale(targetUp, EYE_HEIGHT))
    const toTarget = vec.sub(targetEye, eye)
    const dist = vec.len(toTarget)
    // Degenerate (co-located with the eye) is not a valid look direction;
    // never a real candidate.
    if (dist > INTERACT_DIST || dist < 1e-9) continue
    const dir = vec.scale(toTarget, 1 / dist)
    const dot = vec.dot(lookDir, dir)
    if (dot < COS_INTERACT_CONE) continue
    if (dot > bestDot) {
      bestDot = dot
      best = c
    }
  }
  return best
}
