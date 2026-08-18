# Game design document — Space Adventure

Status: **v0 seed.** Owner: `game` agent. This file is the design contract:
implementers build from it; when it and code disagree, **this file wins**.

## Vision

A low-poly MMO played entirely in first person, where you crew vehicles with
other players — fly a ship between star systems, land on fantasy planets,
climb out and explore on foot, drive a rover across the surface, then load
back up and leave. Space is the connective tissue; planets are the
destination; **the vehicle is the party bus that carries your friends between
the two.** Charm comes from style (low-poly, flat-shaded, bold color) and feel
(responsive movement, smooth multiplayer) — not from detail.

## Pillars

1. **Feel first** — movement is responsive and weighty, on foot and in the
   pilot seat alike; latency is hidden, never felt.
2. **One continuous first person** — you are always a body behind one pair of
   eyes. Walking, flying a ship, driving a rover: same camera, different
   controls. There is no third-person mode and no orbit cam to fall back on,
   so cockpits, interiors, and hands are part of the game, not scenery.
3. **Vehicles are shared spaces** — a ship is a place several players occupy
   together, not a single-player mount. One flies, the rest are aboard, alive
   and present in the same moving room.
4. **Legible low-poly** — every shape reads at a glance; silhouette over
   detail.
5. **Shared world** — other players are the content: visible, present,
   persistent.
6. **Small worlds, deep pockets** — one system at a time, fully built, not
   half-finished infinity.

## Core loop (target)

The full loop, all of it first person:

1. **Board** — walk up to a ship on foot, enter it, take the pilot seat or any
   passenger seat. Other players board the same ship and ride along.
2. **Fly** — the pilot flies from the cockpit in first person; passengers are
   aboard the moving ship, free to move around its interior, look out, or
   swap seats.
3. **Land** — approach a planet and set down. The transition from space to
   surface happens with the crew inside; nobody is teleported or
   loading-screened apart from their friends.
4. **Disembark and explore** — leave the ship on foot, on a fantasy planet,
   in first person. Gather, quest, fight, find things.
5. **Drive** — surface vehicles (rover and friends) work exactly like the
   ship: board, one drives from the driver's seat, others ride.
6. **Load back up and leave** — return to the ship, everyone boards, take off,
   back to space. Repeat, somewhere else.

Every stage is multiplayer: other players are visible throughout, and the
crew you brought stays with you across all of it.

**Where the milestones cut this loop.** M1 builds step 4 alone: a body, on
foot, on a planet, with other players. Vehicles (steps 1–2, 5) arrive at M2,
and space with its landing transition (steps 3, 6) at M3. The loop is built
from the inside out, because the body is the thing every other step hands
control back to.

## M1 scope (on foot, first person, on a planet)

**In scope**

- **One small round world** — a walkable asteroid/mini-planet you can circle on
  foot, with low-poly terrain, sky, and scattered props. No space, no orbit,
  no other locations. Small and round is the point: the horizon sits ~23 m
  away, curvature is visible from the ground, and a lap takes a couple of
  minutes, so the demo world teaches you the shape of the game in one session.
- **You are a body.** The entity a connection controls is a player character
  with a position on the ground — not a vehicle. This is the permanent shape
  of the game (pillar 2); nothing later has to undo it.
- First-person on-foot movement over a curved surface: walk, sprint, jump,
  look, and terrain that pushes back — slopes you can climb, slopes you slide
  off, steps you walk up. "Down" is toward the planet centre everywhere, so
  walking far enough brings you back where you started with no edge, no wall,
  and no wrap seam.
- **First-person camera at eye height on the body.** No chase cam, no orbit
  cam, no toggle — not even as a debug affordance. Per pillar 2 this is the
  only camera the game ever has, so M1 builds it and never builds the other
  one.
- **You have a body.** Look down and you see your own torso, legs and hands —
  the same `char.player` model everyone else sees, with only the head hidden.
  Not a floating camera, and not a pair of disembodied viewmodel arms. Pillar 2
  says you are always a body behind one pair of eyes; this is where that stops
  being a slogan. See "First-person body" below.
- Multiple players on the same surface simultaneously, seeing each other move;
  server-authoritative at 20 Hz.
- Minimal HUD: speed, distance to nearest player, connection state.
- **Nametags** over remote characters — every player is the same low-poly body,
  so without them the world reads as "a player" rather than "Steve". Plain
  text, fading out past ~40 m (beyond that the character is a dot on a 23 m
  horizon anyway), never drawn for your own body.

**Parked (explicitly out of M1 scope)**

- Vehicles of every kind: ships, rovers, boarding, seats, passengers,
  piloting, control handoff (M2).
- Space, flight, orbit, landing and takeoff transitions (M3).
- Player-vs-player collision — in M1 players pass through each other.
- Combat, weapons, damage, health.
- Inventory, gathering, quests, NPCs.
- Economy, accounts, persistence, chat.

## M1 on-foot movement (spec for `netcode` + `frontend`)

- Controls: WASD — move (local to facing); mouse — look; Shift — sprint;
  Space — jump.
- Units: 1 unit = 1 m. Positions are world-space Cartesian with the planet
  centre at the origin.
- **There is no global "up".** Local up is `normalize(pos)` — the radial
  direction from the planet centre. Gravity, the ground test, slope, jump, the
  camera's up vector and the body's tangent plane are all defined against it,
  and every one of them changes as you walk. This is the single fact that
  makes a round world different from a flat one; anything in the
  implementation that hardcodes `+Y` is a bug that only shows up on the far
  side of the planet.
- **No world edge.** A sphere has no boundary, so M1 has no position clamp, no
  invisible wall, and no wrap seam — walking in a straight line returns you to
  where you began. (This deletes a whole class of edge-case bug that a flat
  patch would have needed rules for.)
- **Terrain is a cube-sphere heightfield, and the server owns it.** The server
  generates six `face_grid × face_grid` arrays of surface radii from
  `world_seed` and sends them once on join (PROTOCOL `terrain`). Both ends
  collide against those same arrays — no mesh collision, no collision library,
  and no cross-language noise function that has to agree to the last bit.
  A cube-sphere is used rather than a latitude/longitude grid because it has no
  pole singularity and no bunching, at the cost of six faces and a seam rule.
- Look is **client-authoritative and never predicted**: the client sends its
  view direction as an absolute unit vector `look_dir` in world space. The
  server accepts it (clamping how close it may come to local up or down) and
  derives body facing by projecting it onto the tangent plane. Aim must be
  instant, and a corrected view is motion sickness. Only position and velocity
  are predicted and replayed.
  - A world-space vector is used instead of a `yaw`/`pitch` pair because on a
    sphere there is no global reference frame for yaw to be measured against —
    any choice has a singularity somewhere on the surface. A direction vector
    needs no frame, stays absolute and idempotent (latest wins, matching the
    rest of the input design), and costs 4 bytes more.
- Spawn: on the surface above `spawn_dir`, standing, zero velocity, facing an
  arbitrary tangent direction.

### First-person body

The local player renders the same character model as everyone else, seen from
inside. That is cheap to say and has four consequences that are not:

- **Hide the head, not the body.** The camera sits at the `eye` node, which is
  inside the skull — so the model carries a separate `head` node that is hidden
  for the local player only. Everything else renders. Relying on the near clip
  plane to slice the head away instead produces a visible cross-section
  whenever you look down or a wall gets close.
- **Near clip plane at 0.05 m** (far plane 500 m — nothing is visible past the
  23 m horizon but the sky and the tall landmarks). A default 0.1 m near plane
  slices through your own chest when you look straight down.
- **The body is seen from above and inside**, which is the same problem
  vehicle interiors have: a shell modelled only for outside viewing looks
  hollow and wrong from the eye point. Torso and legs need to read from a
  steep angle at half a metre.
- **The body stays upright when you look down.** Only the camera pitches;
  pitch is not applied to the body. This is already why remote head pitch is
  not transmitted, and it is what makes looking at your own feet work.

**Animation: procedural, no rig.** The character is a handful of separately
named nodes (`torso`, `arm.l`, `arm.r`, `leg.l`, `leg.r`) that the client
rotates in code — a sine-driven walk cycle whose frequency and amplitude follow
horizontal speed, arms counter-swinging to legs, everything settling to rest
when stopped. No skeleton, no skinning, no animation clips, no `.glb`
animation import.

A static body seen from the inside while sprinting looks broken, so *some*
motion is mandatory; a rig and clip pipeline is a large amount of machinery for
a low-poly figure whose joints are hidden by flat shading. Procedural motion is
roughly thirty lines and looks right at this fidelity. Revisit when characters
need to do something more expressive than walk.

**No head bob in M1.** Camera bob coupled to a walk cycle is a known nausea
source and this is a first-person game on a world with a 23 m horizon and a
constantly rotating up vector — there is enough vestibular novelty already.
Add it later behind a setting if the walk feels weightless.

Terrain sampling (binding on both ends — this is the shared function
everything else calls):

1. Given a direction `d` (normalized world position), pick the face by the
   largest-magnitude component of `d`; face order is `+X, −X, +Y, −Y, +Z, −Z`.
2. Divide the other two components by that largest magnitude to get face
   coordinates `(u, v)` in [−1, 1]. Per-face axis assignment must match the
   `terrain` message layout exactly — this is where a seam bug comes from.
3. Map `(u, v)` to grid coordinates and **bilinearly** sample the radius.
4. Across a face boundary the shared edge samples are duplicated in both
   faces and must hold identical values, so a body walking over a seam sees a
   continuous surface. Generating from 3D noise over the direction vector
   (below) satisfies this **for free** — the shared edge is the same direction,
   so it evaluates to the same radius. Any generator that works in per-face 2D
   instead has to reconcile twelve edges by hand; don't.
5. Surface normal is not radial on sloped ground: compute it by finite
   differences of the sampled radius around the point. Slope is the angle
   between that normal and local up.

Rule table (implementers treat as spec — every value is named):

| name | value | unit | note |
|------|-------|------|------|
| `eye_height` | 1.7 | m | camera offset above foot position |
| `capsule_radius` | 0.4 | m | body radius (terrain + props only in M1) |
| `capsule_height` | 1.8 | m | body height |
| `walk_speed` | 4.5 | m/s | target ground speed |
| `sprint_speed` | 7.5 | m/s | target speed with the sprint bit held |
| `accel_ground` | 50 | m/s² | reaches walk speed in ~0.09 s — arcade snap |
| `friction_ground` | 8 | 1/s | `v *= exp(-friction·dt)` with no move input (stop in ~0.3 s) |
| `accel_air` | 8 | m/s² | limited air control; no friction while airborne |
| `gravity` | 9.8 | m/s² | toward the planet centre. A real 150 m asteroid pulls ~0.0001 g; that is ignored on purpose, because the game is about walking, not floating. Lower it for a floatier feel — it is a one-value knob. |
| `jump_speed` | 4.5 | m/s | initial upward velocity → ~1.03 m apex |
| `terminal_speed` | 60 | m/s | downward speed clamp |
| `max_step` | 0.3 | m | height walked up without jumping |
| `max_slope` | 50 | deg | steeper ground is not walkable — you slide |
| `ground_snap` | 0.15 | m | stay glued to ground walking downhill |
| `look_clamp` | 1 | deg | `look_dir` may not come within this angle of local up or down |
| `planet_radius` | 150 | m | nominal surface radius — 942 m circumference, ~2 min sprint lap, ~3.5 min walk |
| `radius_min` | 124 | m | wire-encoding floor — deepest crater floor in a valley, with headroom |
| `radius_max` | 190 | m | wire-encoding ceiling — highest peak on the tallest ridge, with headroom |
| `face_grid` | 65 | samples | per cube face, per axis; 6 faces → 25,350 samples, ~3.3 m apart at the surface |
| `spawn_dir` | (0, 1, 0) | unit | spawn on the surface along this direction |
| `tick_hz` | 20 | Hz | server tick (matches PROTOCOL) |

Consequences of `planet_radius` worth knowing before tuning it: the horizon
sits `sqrt(2 · planet_radius · eye_height)` ≈ **23 m** away, other players
disappear over it at that range, and the ground visibly falls away in every
direction. That is the demo's whole charm — and also why this world cannot
double as a realistic planet later. It is a practice world, sized for
practicing.

Edge cases:

- **Diagonal input is normalized.** `(move_x, move_y)` is clamped to unit
  length before use, or diagonal movement is 1.41× faster than forward — the
  oldest bug in first-person movement.
- **Sprint applies in every direction**, not forward only. Simpler to
  implement, simpler to predict, and nobody has ever enjoyed the alternative.
- **Jump requires being grounded** — no double jump, no air jump. Coyote time
  is a feel-pass question, not an M1 rule.
- **Slopes steeper than `max_slope`:** no ground friction and no walk
  acceleration; gravity applies along the slope, so you slide down. The body
  is not "grounded" for jump purposes while sliding.
- **`max_step`:** ground within `max_step` above the body's feet is walked up
  without leaving the ground. Above that, it is a wall.
- **Falling through terrain:** after integrating position, if `|pos|` is below
  the sampled surface radius for that direction, snap it out to the surface and
  zero the radial component of velocity. This is a correctness backstop, not a
  movement rule — it must never be the mechanism that normal walking relies on.
- **Crossing a cube-face seam must be invisible.** This is the one bug a round
  world adds that a flat patch does not have, and it will not appear in a test
  that walks in a small circle near the spawn point. Any test of movement must
  cross at least one seam.
- **Velocity is re-projected as you walk.** Moving across a curved surface
  continuously rotates local up, so tangential velocity must be re-projected
  into the new tangent plane each step or the body slowly acquires a radial
  component and drifts off the ground.
- **Poles do not exist and must not be introduced.** No `atan2`-based
  latitude/longitude anywhere in movement, orientation, or camera code. The
  cube-sphere has no singularity; adding a spherical-coordinate helper puts one
  back.
- **Input missing (client silent):** server holds last input state; heartbeat
  timeout despawns (PROTOCOL semantics). Note this means a dropped client keeps
  walking — acceptable for the ~10 s until timeout, and simpler than a
  special case.
- **HUD "distance to nearest player" with nobody else in the world:** show
  `—`, not `0` and not `∞`.
- Client prediction uses the identical rule table and integrator — divergence
  is corrected by **replay from `ack_seq`**, not blending (ARCHITECTURE
  "Network model").

Integrator (binding on both implementations — replay only converges if the
step structure matches):

1. Step at fixed `dt = 1/tick_hz` (50 ms). Never a variable frame delta.
2. Semi-implicit Euler, in this order per step:
   compute `up = normalize(pos)` → accept `look_dir` from input, clamping it to
   `look_clamp` from up/down, and derive body facing by projecting it onto the
   tangent plane → build the wish direction from normalized `(move_x, move_y)`
   in that tangent frame → if grounded, apply `accel_ground` toward
   `wish · target_speed` and `friction_ground` when there is no input; else
   apply `accel_air` → apply `gravity` along `−up` and clamp the radial speed
   to `terminal_speed` when airborne → apply `jump_speed` along `+up` if
   grounded and the jump bit is set → integrate position → resolve against the
   terrain radius (snap within `max_step`/`ground_snap`, set grounded, zero
   radial velocity on landing) → re-project velocity into the tangent plane at
   the new position.
   There is no world-edge clamp: the world has no edge.
3. `f64` internally on both ends, `f32` only on the wire. Bit-identical
   determinism is explicitly **not** required — the server is authoritative and
   the per-step rounding difference is far below the 5% conformance tolerance.
   Chasing cross-language float parity is not an M1 problem.

## M1 terrain generation (spec for `netcode`)

The asteroid is generated procedurally from `world_seed`: valleys, mountain
ranges, flat plains, and scattered craters, with loose rocks as props on top.

**The algorithm below is advisory; the constraints are binding.** Because the
server generates the field once and ships it over the wire, no client ever
re-derives it — so the noise functions do not have to match anything, and
swapping in a different noise implementation is not a protocol change. What
must hold is the *character* of the result: the constraints in "Must hold" are
the actual contract, and `qa` checks those, not the recipe.

Evaluate per sample direction `d` (a unit vector), producing a radius. Work in
**3D noise over `d`**, never in per-face 2D — that is what makes the surface
continuous across the cube seams without any special-casing.

```
r(d) = planet_radius
     + base(d)                      // large-scale: lowlands vs highlands
     + ridges(d) · highland_mask(d) // mountain ranges, only in highlands
     + detail(d)                    // small-scale roughness
     + craters(d)                   // subtractive bowls + raised rims
```

1. **Base relief** — fractal noise, `base_octaves` at `base_freq`, amplitude
   `base_amp`. Produces the continent-scale split between low plains and high
   ground.
2. **Highland mask** — smoothstep of the base relief around
   `highland_threshold`. Above it, mountains grow; below it, relief is damped
   by `plain_damp` to make genuinely flat plains. Without this mask the whole
   asteroid becomes uniformly lumpy and reads as noise rather than terrain.
3. **Mountains** — ridged fractal noise (`1 − |noise|`, `ridge_octaves` at
   `ridge_freq`, amplitude `ridge_amp`), multiplied by the highland mask so
   peaks form connected ranges with valleys between them rather than isolated
   spikes.
4. **Detail** — low-amplitude high-frequency noise so nothing reads as a
   smooth CG surface. Keep it under `detail_amp`; at 3.3 m sample spacing,
   anything finer is invisible anyway.
5. **Craters** — choose `crater_count` centres on the sphere by random
   rejection sampling with a `crater_min_sep` spacing rule, each with a
   diameter drawn from `crater_diam`. Apply a bowl-and-rim profile as a
   function of angular distance `t` (normalised so `t = 1` is the rim):
   - `t < 1` — bowl: depth `crater_depth_ratio · diameter`, deepest at the
     centre, easing to zero at the rim
   - `t ≈ 1` — raised rim of `crater_rim_ratio · diameter`
   - `t > 1` — ejecta easing to zero by `t = 1.4`

   Craters are applied **after** mountains, so one can cut into a slope and
   look like it landed there.
6. **Landmarks** — six unique placed features (below). Applied with `max()`
   for anything that rises and `min()` for anything excavated, **not** by
   addition: a landmark states its own absolute radius, so it never stacks with
   noise amplitude and never threatens the radius budget.
7. **Spawn plain** — force flat ground within `spawn_flat_radius` of
   `spawn_dir`, blending out over `spawn_flat_blend`. Applied last so nothing
   overrides it. Spawning on a 45° mountainside is a bad first five seconds,
   and this is a demo world whose first five seconds matter.

### Landmarks

Procedural noise makes terrain *varied*; it does not make it
*distinguishable*. Two noise valleys look alike, so you cannot navigate by
them. These six features are placed rather than sampled, and each is unique in
silhouette so "I'm north of the Spire" is a thing a player can say.

**A landmark must clear the horizon to do its job.** With a 23 m horizon,
anything at ground level is invisible from 30 m away — so landmarks are
defined by height first and shape second. An object rising `H` above nominal
radius is visible from an angular distance of
`acos(R/(R+H)) + acos(R/(R+eye_height))`:

| landmark | peak/floor radius | rise | visible from | shape |
|---|---|---|---|---|
| **The Spire** | 188 m | +38 | 120 m | narrow steep pinnacle, base ⌀ 30 m; the tallest thing on the asteroid and deliberately unclimbable |
| **Twin Peaks** | 180 / 176 m | +30 | 110 m | two peaks 45 m apart, reads as a pair from every angle |
| **The Mesa** | 172 m top | +22 | 99 m | flat top ⌀ 40 m, steep sides, one walkable ramp — a summit worth standing on |
| **The Great Crater** | rim 170 m, floor 132 m | +20 | 96 m | ⌀ 120 m, far larger than any scattered crater; the rim reads as a wall from outside and a ring horizon from inside |
| **The Notch** | walls 170 m, floor 138 m | +20 | 96 m | a 60 m canyon cut clean through a ridge — a walkable pass, and a gap in the skyline from far off |
| **Home Beacon** | 165 m | +15 | 87 m | slender pillar, base ⌀ 12 m, 50 m from spawn centre — the "you are home" marker |

Placement: the six sit on the cube-face directions (`±X, ±Y, ±Z`), with `+Y`
reserved for the spawn plain and its Home Beacon just outside the flat disc.
Seeded jitter up to 15° varies each world; a minimum separation of 60° is
enforced so jitter cannot bunch two together and open a hole opposite them.
Scattered craters reject positions overlapping a landmark footprint.

Coverage, measured over the surface: **66% of the asteroid can see at least one
landmark**, and the octahedral spread wastes almost nothing (the sum of the
individual visibility caps is 66.5%, so overlap is ~0). The remaining third
navigates by ordinary terrain — ridges, the crater rim, the shape of a valley.
Raising coverage means taller landmarks, and `radius_max` 190 m is the ceiling
on that, or more of them. 66% is a deliberate number, not a leftover: a world
where a landmark is visible from *everywhere* has no sense of place.

Parameters (tune freely — none of these is a wire contract):

| name | value | unit | note |
|------|-------|------|------|
| `base_freq` | 1.5 | cycles/sphere | large-scale relief |
| `base_octaves` | 4 | | |
| `base_amp` | 14 | m | lowland/highland spread |
| `highland_threshold` | 0.15 | — | base value above which mountains grow |
| `plain_damp` | 0.35 | — | relief multiplier below the threshold → flat plains |
| `ridge_freq` | 3.0 | cycles/sphere | mountain range spacing |
| `ridge_octaves` | 5 | | |
| `ridge_amp` | 20 | m | peak height above base, before masking |
| `detail_freq` | 12 | cycles/sphere | |
| `detail_amp` | 1.2 | m | surface roughness |
| `crater_count` | 6–12 | | across the whole asteroid |
| `crater_diam` | 20–60 | m | see the resolution floor below |
| `crater_depth_ratio` | 0.15 | × diameter | 3–9 m deep — real craters run ~1:5 |
| `crater_rim_ratio` | 0.05 | × diameter | raised lip |
| `crater_min_sep` | 1.2 | × summed radii | keeps craters from merging into mush |
| `spawn_flat_radius` | 25 | m | forced flat disc at spawn |
| `spawn_flat_blend` | 15 | m | blend band outside it |
| `landmark_count` | 6 | | one per cube-face direction; see "Landmarks" |
| `landmark_jitter` | 15 | deg | seeded offset from the base direction |
| `landmark_min_sep` | 60 | deg | jitter may not bunch two together |
| `beacon_offset` | 50 | m | Home Beacon distance from spawn centre — clear of the flat disc and its blend band |

**Must hold** (binding — `qa` verifies these against the generated field, not
against the recipe):

- **≥ 70% of surface samples are walkable** (slope below `max_slope`). The
  unwalkable remainder should be mountains, deliberately — a world you mostly
  slide down is not a walking demo.
- **The spawn disc is flat to within ±0.5 m** and walkable in every direction
  out of it.
- **All radii land inside `[radius_min, radius_max]`.** Clamp as a safety net,
  but a generator that clamps often produces flat-topped mesas and wants its
  amplitudes lowered instead. The bounds are set from the amplitude budget with
  headroom, so clamping should be rare:

  | | worst case | |
  |---|---|---|
  | low | `150 − base_amp 14 − detail 1.2 − deepest crater 9` | = 125.8 m, inside `radius_min` 124 |
  | high | `150 + base_amp 14 + ridge_amp 20 + detail 1.2 + tallest rim 3` | = 188.2 m, inside `radius_max` 190 |

  **Re-run this arithmetic whenever an amplitude changes.** Widening
  `ridge_amp` without widening `radius_max` silently flattens every peak, and
  it looks like a noise bug rather than an encoding one.
- **All six landmarks are present, distinct, and ≥ `landmark_min_sep` apart**,
  and ≥ 60% of sampled surface points can see at least one of them. Their
  peak/floor radii are absolute, so they are also the easiest thing to check
  against the radius bounds.
- **The Home Beacon does not intrude on the spawn flat disc**, and the spawn
  plain stays flat after landmarks are applied.
- **No feature smaller than ~7 m** (two samples at 3.3 m spacing) is
  representable. A 5 m crater simply does not exist in this grid — small
  surface interest has to come from props, not from terrain. This is the
  constraint most likely to be discovered the hard way.
- **Overhangs are impossible** by construction: a radius field has one height
  per direction. Arches and caves are not a tuning problem, they are a
  different data structure.

### Rocks and surface props (spec for `frontend`)

Loose rocks are **props, not terrain** — scattered client-side, seated on the
sampled surface, oriented radially, and drawn from `world_seed` so every client
scatters them identically. They cost the server nothing and the wire nothing.

| name | value | unit | note |
|------|-------|------|------|
| `rock_count` | ~400 | | over the whole asteroid |
| `rock_size` | 0.3–1.5 | m | see the collision constraint |
| `rock_variants` | 3 | | `prop.rock.a/b/c` — one silhouette at 400 scales reads as a repeating texture |
| `rock_slope_max` | 35 | deg | do not scatter onto near-cliff faces |

- Props have **no collision in M1**, so nothing may be big enough that walking
  through it is jarring — hence the 1.5 m cap. Boulders you can climb on need
  prop collision, and that means the server owns their placement instead.
  A later milestone, not a tuning value.
- Denser scatter inside crater floors and along ridgelines reads as debris
  and gives the eye something to judge distance by on a world whose horizon is
  23 m away.

## Vehicles and crew (design direction — M2, not yet a spec)

Not buildable yet and deliberately not numbered here. It is written down
because it constrains decisions M1 makes *now*, and rewriting those later is
far more expensive than reading this paragraph.

- **A vehicle is a place, not a mount.** It is a world entity with an interior
  and a set of seats. It exists whether or not anyone is in it, and it keeps
  its position and velocity when empty.
- **Seats, and exactly one control seat.** A vehicle has one pilot/driver seat
  and N passenger seats. Only the occupant of the control seat produces input
  that moves the vehicle; passengers produce no vehicle input at all. This is
  server-enforced, never a client-side UI lock.
- **Seat claims are server-authoritative.** Two players reaching for the same
  seat is a race the server resolves; the loser is told no. Boarding is
  proximity + interact, never teleport.
- **Passengers are attached, not co-simulated.** A player aboard a moving
  vehicle has a position *relative to the vehicle interior*; the server
  composes that with the vehicle transform. Sending passengers as free-floating
  world positions would make walking around a moving ship a jitter nightmare.
- **Control handoff is a normal event.** The pilot standing up, disconnecting,
  or dying leaves the vehicle unpiloted and coasting (space) or stopping
  (ground). Another player can take the seat. Nobody is trapped and nothing is
  destroyed by a disconnect.
- **The camera never changes mode.** First person on foot, first person in the
  pilot seat, first person in a passenger seat. Different control mapping, same
  rig — the mount point moves from the body's eye height to a `seat.*` node in
  the vehicle model.

Because M1 makes the player a body rather than a vehicle, M2 **adds** the
vehicle entity and the occupancy relation between the two; it does not have to
unpick an identity. `netcode` should still treat "the entity whose movement
this connection's input drives" as a lookup rather than a fixed field, since
that is exactly what taking a pilot seat repoints.

Known contract changes M2 will force (**not** made now, since a
`docs/PROTOCOL.md` change must land on both ends in one milestone):

- `snapshot` entities need an optional parent/attachment reference and a
  local-space transform for anything riding a vehicle.
- New message types for board / take seat / leave seat, with server refusal as
  a first-class reply.
- `input` needs a mode, or a per-context meaning, so the same fields can drive
  a body on foot and a vehicle from its control seat.

## Flight model (parked — lands with M2/M3, kept because it is already spec'd)

Not M1. This was written as the M1 spec before the milestone order changed;
it is preserved intact rather than rewritten later. When vehicles land, this
becomes the ship's movement model, and the world/spawn rows get revisited in
whatever context it flies in (planet atmosphere at M2, open space at M3).

- Controls: W/S — thrust forward/back; mouse — yaw/pitch/roll rates;
  Shift — boost.
- Rotation: first-order response toward the input's target angular velocity
  (time constant τ).
- Translation: acceleration along ship forward; speed clamped; exponential
  decay when unthrottled (drift with a game-feel half-life).

| name | value | unit | note |
|------|-------|------|------|
| `accel` | 12 | u/s² | base forward acceleration |
| `accel_boost` | 24 | u/s² | with `action_mask` boost |
| `vmax` | 40 | u/s | speed clamp |
| `vmax_boost` | 80 | u/s | speed clamp while boosting |
| `damp` | 0.5 | 1/s | `v *= exp(-damp·dt)` when unthrottled (half-life ≈ 1.4 s) |
| `angvel_max` | 4 | rad/s | target angular velocity per axis |
| `angvel_tau` | 0.15 | s | first-order response time constant |

Edge cases already settled: backward thrust is `accel · 0.5` under the same
`vmax` clamp; boost with no thrust input does nothing (boost scales thrust, not
speed); the same fixed-step semi-implicit integrator and replay reconciliation
apply, with orientation normalized every step.

## Open questions (main thread decides)

M1:

- **Terrain parameters are first guesses.** The generator spec above is
  structured so the mix (plains vs ranges vs craters) is tunable without
  touching the wire or the client, but the actual values want a look-at-it
  pass. Expect `highland_threshold` and `ridge_amp` to move once someone walks
  around on it.
- **Is 65×65 per face enough?** At 3.3 m spacing the terrain cannot express
  anything under ~7 m, which is why rocks are props. If the surface reads as
  too smooth up close, `face_grid` can rise to 73 before the single-message
  budget breaks — beyond that, terrain has to be chunked.
- **Do six landmarks read as six?** They are distinct on paper; whether a
  player can tell the Mesa from the Great Crater rim at 90 m in flat shading is
  a look-at-it question. If they blur together, the fix is silhouette contrast
  (make the Spire thinner, the Mesa wider) before it is more landmarks.
- **Landmark coverage is 66%** by design. If navigating the remaining third
  turns out to be frustrating rather than atmospheric, the lever is height —
  and `radius_max` 190 m caps it, so a serious increase means raising the
  ceiling and re-running the radius budget.
- **Coyote time and jump buffering** — omitted from the M1 rules on purpose.
  Both are cheap and both are the difference between "responsive" and "this
  feels bad"; decide after the first feel pass, as a rule-table addendum.
- Concurrent player target for local dev: assume 10–50.

M2 and later:

- **Motion sickness in a hull-fixed cockpit at `angvel_max` 4 rad/s (229°/s).**
  A camera welded to a ship rolling that fast is a known nausea source. Options
  are damping the camera relative to the hull, capping roll rate, or a
  free-look head that decouples view from hull. Now an M2 question rather than
  an M1 one, but it can still force `angvel_max` down — a rule-table change.
- **Crew size per vehicle** — 4? 8? Drives ship interior scale, so `art` needs
  the answer before it models a real hull.
- **Do vehicle interiors stay walkable during flight, or lock to seats in
  transit?** Walkable is the vision; seat-locked is enormously cheaper and can
  ship first. M2 decision.
- **Landing: seamless or a transition?** Pillar 1 wants seamless; a short
  scripted descent is far cheaper and still keeps the crew together. M3
  decision, and it drives whether space and surface are one world or two.
- **Does an empty vehicle persist when its last occupant logs off?** Ties into
  persistence — parking a ship on a planet and finding it later is the
  motivating case.
- Ship customization: parked until the loop is closed.
