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

This section is a **drop-in spec**: the server tick step and the client
prediction step are the same function, and an implementer can code either
from this section alone. Every number is in the rule table; every rule is a
formula or a named reference to one. There are no prose-only rules in this
section.

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
  A cube-sphere is used rather than a latitude/longitude grid because it has
  no pole singularity and no bunching, at the cost of six faces and a seam rule.
- Look is **client-authoritative and never predicted**: the client sends its
  view direction as an absolute unit vector `look_dir` in world space. Both
  ends apply the same `clampLook` — the client to its own camera (it must not
  be able to view the poles the server would reject), the server to derive
  body facing — by projecting the clamped vector onto the tangent plane. Aim
  must be instant, and a corrected view is motion sickness. Only position and
  velocity are predicted and replayed.
  - A world-space vector is used instead of a `yaw`/`pitch` pair because on a
    sphere there is no global reference frame for yaw to be measured against —
    any choice has a singularity somewhere on the surface. A direction vector
    needs no frame, stays absolute and idempotent (latest wins, matching the
    rest of the input design), and costs 4 bytes more.

### State

The complete sim state of a body — both ends keep this and nothing else:

| field | type | meaning |
|---|---|---|
| `pos` | vec3, f64 | foot position, world space; `\|pos\|` is the radius from the planet centre |
| `vel` | vec3, f64 | foot velocity, world space |
| `grounded` | bool | in contact with the surface **and** the slope at the contact point ≤ `max_slope` (set by the terrain resolution). This exact boolean is the `grounded` field of the trajectory dump |
| `facing` | vec3, f64 | body facing, unit vector in the local tangent plane; carried so the near-vertical facing-hold (integrator step 2) has a value to keep. Set at spawn; not part of the trajectory dump |

Derived per step (never stored):

```
up         ← normalize(pos)
in_contact ← |pos| − radius(terrain, up) ≤ ground_snap
mode       ← GROUND  if grounded
             SLIDE   if in_contact
             AIR     otherwise
```

`grounded` ⟹ `in_contact` always (the flag is only set while the body is on
the surface), so the three modes partition the state: **GROUND** = walkable
contact, **SLIDE** = contact on ground steeper than `max_slope`, **AIR** =
not in contact.

### Input

The input for a tick is the latest `input` frame received (server) or the
local command state (client) — current state, latest wins (PROTOCOL).
Sanitize before use:

| field | sanitize | default when missing |
|---|---|---|
| `move_x`, `move_y` | each clamped to [−1, 1]; the pair clamped to unit length (a diagonal must not be 1.41× faster); non-finite → 0 | 0 |
| `look_dir` | normalized, then `clampLook` (below); non-finite or shorter than `eps_degen` → the last applied input's look | the spawn look |
| `action_mask` | `0x0001` sprint, `0x0002` jump; other bits ignored | 0 |

- **Axis mapping:** `move_y` is forward (along the facing), `move_x` is right
  (along `up × facing`) — W = +y, D = +x, S is exactly −forward.
- **Silent/missing input:** the server holds the last input it received on
  every tick (PROTOCOL "latest arrival wins"). If it has never received an
  input frame, the defaults row above applies: the body stands still, with
  facing derived from the spawn look. A dropped client keeps walking until
  the 10 s heartbeat timeout despawns it — acceptable, and simpler than a special case.
- **"Previous look" / "previous facing"** — both mean the last applied
  input's look: the server's last received `input` frame (PROTOCOL: the input
  is constant between inputs), or the client's previous-tick local command
  state. "Previous facing" is the facing step 2 would derive from it. Before
  any input, both are the spawn look (see "Spawn").

### Spawn

```
spawn(terrain):
  up       ← spawn_dir
  pos      ← up · radius(terrain, up)         # feet, on the surface along spawn_dir
  vel      ← 0
  grounded ← slopeOK(terrain, up)             # true by the terrain contract (flat spawn disc)
  look     ← normalize((1,0,0) − up·dot((1,0,0), up))     # tangent projection of world +X
  if |look| < eps_degen:
      look ← normalize((0,0,1) − up·dot((0,0,1), up))
  facing   ← normalize(look − up·dot(look, up))    # tangent of the spawn look; the held value from here on
```

With `spawn_dir` (0, 1, 0) this is `pos = (0, radius, 0)` with the initial
look along world +X. The `look` is the connection's initial `look_dir` — the
"last applied input's look" of "Input" until the client sends its first
input, and body facing is initialized from it as in step 2 (then carried
as state). Deterministic: two servers with the same seed and input stream
spawn identically. All players spawn at the same point; bodies pass through
each other (no player-vs-player collision in M1), so overlap is acceptable
and there is no spawn spread in M1.

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

### Terrain sampling (binding on both ends — the shared function everything else calls)

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
5. The surface normal is not radial on sloped ground. Compute it by finite
   differences of the sampled radius — this exact formula, so both ends get
   the same normal from the same field:

   ```
   surfaceNormal(terrain, up):
     k  ← (1,0,0)  if |dot(up, (1,0,0))| ≤ 0.9  else  (0,1,0)
     e1 ← normalize(k − up·dot(k, up))
     e2 ← up × e1
     P0 ← up · radius(terrain, up)
     d1 ← normalize(up + e1·normal_eps)
     d2 ← normalize(up + e2·normal_eps)
     P1 ← d1 · radius(terrain, d1)
     P2 ← d2 · radius(terrain, d2)
     n  ← cross(P1 − P0, P2 − P0)
     if dot(n, up) < 0: n ← −n
     return normalize(n)
   ```

   The fixed `k` and its fallback make the tangent basis deterministic and
   frame-free (no pole). **Slope** = the angle between `n` and local up:
   `acos(clamp(dot(n, up), −1, 1))`. `normal_eps` ≈ 5 m ≈ 1.5 grid cells, so
   the normal averages over the bilinear kinks instead of snapping at every
   grid line.

### Rule table

Implementers treat this as spec — every value is named, unit'd, and justified:

| name | value | unit | justification |
|------|-------|------|---------------|
| `tick_hz` | 20 | Hz | server **and** client-prediction tick (PROTOCOL); `dt = 1/tick_hz = 0.05 s`, fixed on both ends |
| `walk_speed` | 4.5 | m/s | brisk human walk; full 942 m lap in ~3.5 min |
| `sprint_speed` | 7.5 | m/s | ~30 km/h; full lap in ~2.1 min — the demo's "fast" |
| `accel_ground` | 50 | m/s² | `walk_speed/accel_ground` = 0.09 s from 0 to walk — input registers within a tenth of a second |
| `friction_ground` | 8 | 1/s | `v *= exp(−friction·dt)` with no move input: to 9 % of speed in 0.3 s — ~3× slower than the accel ramp, so stopping has weight |
| `accel_air` | 8 | m/s² | 1/6 of `accel_ground` — air control steers, momentum dominates; no friction while airborne |
| `gravity` | 9.8 | m/s² | 1 g toward the planet centre; jump apex 1.03 m, hang time 0.92 s — readable, not floaty. A real 150 m asteroid pulls ~0.0001 g; that is ignored on purpose, because the game is about walking, not floating. Lower it for a floatier feel — it is a one-value knob. |
| `jump_speed` | 4.5 | m/s | apex `v²/2g` = 1.03 m, hang time `2v/g` = 0.92 s — clears a `max_step` 0.3 m ledge with margin |
| `terminal_speed` | 60 | m/s | downward radial-speed clamp; the longest M1 fall (Great Crater rim→floor, 38 m) reaches `sqrt(2·9.8·38)` ≈ 27 m/s, so the clamp is a safety valve, not a feel knob |
| `max_step` | 0.3 | m | step height walked up per step; sprinting up a `max_slope` slope rises `sprint_speed·dt·sin 50°` ≈ 0.29 m per step — just under, so the steepest walkable slope is climbable at full sprint |
| `max_slope` | 50 | deg | walkability limit; steeper ground is slid (no friction, no walk accel), not climbed |
| `ground_snap` | 0.15 | m | glue-band margin on top of the per-step downhill drop `|vel|·dt·sinθ`, which the glue condition adds explicitly (`resolve`): the steepest walkable slope (50°) drops `4.5·0.05·sin 50°` ≈ 0.172 m/step at walk and 0.287 m at sprint — the band `0.15 + drop` covers both; on flat ground θ ≈ 0 so the band is exactly 0.15 m and cliff edges stay non-sticky (curvature drop ~0.5 mm → >300× margin, as before) |
| `look_clamp` | 1 | deg | keeps `look_dir` off the exact poles, where the tangent-plane projection that derives facing degenerates; ±89° pitch remains |
| `facing_hold` | 0.1 | — | below this tangent-projection magnitude the look is too near-vertical to define a stable azimuth, so facing holds its carried value (integrator step 2) — avoids body spin on mouse jitter |
| `normal_eps` | 2 | deg | finite-difference offset for the surface normal — ~5 m at the surface, ~1.5 grid cells, so the slope test is not kink-noisy |
| `wall_tol` | 1e-3 | m | bisection tolerance for wall contact — 1 mm, far below the 5 % conformance tolerance |
| `eps_degen` | 1e-6 | — | numeric-degeneracy threshold for direction normalization (unit-vector dot products) |
| `eye_height` | 1.7 | m | camera offset above the foot position (client only — the server sim does not use it) |
| `near_clip` | 0.05 | m | first-person near plane (client only); a default 0.1 m slices through the player's own chest when looking straight down — see "First-person body" |
| `far_clip` | 500 | m | first-person far plane (client only); the 23 m horizon bounds what is visible, so 500 m is skybox headroom, not view range |
| `capsule_radius` | 0.4 | m | body collision radius, declared now so M1 prop collision and M2 vehicle collision have a number; M1 terrain collision uses the foot point and must not use this |
| `capsule_height` | 1.8 | m | body height (client only — the M1 sim does not use it) |
| `planet_radius` | 150 | m | nominal surface radius — 942 m circumference, ~2 min sprint lap, ~3.5 min walk |
| `radius_min` | 124 | m | wire-encoding floor — deepest crater floor in a valley, with headroom |
| `radius_max` | 190 | m | wire-encoding ceiling — highest peak on the tallest ridge, with headroom |
| `face_grid` | 65 | samples | per cube face, per axis; 6 faces → 25,350 samples, ~3.3 m apart at the surface |
| `spawn_dir` | (0, 1, 0) | unit | spawn plain sits on the +Y cube face (reserved by the terrain spec); the flat disc is guaranteed by the terrain contract |

Consequences of `planet_radius` worth knowing before tuning it: the horizon
sits `sqrt(2 · planet_radius · eye_height)` ≈ **23 m** away, other players
disappear over it at that range, and the ground visibly falls away in every
direction. That is the demo's whole charm — and also why this world cannot
double as a realistic planet later. It is a practice world, sized for
practicing.

### Integrator (binding per-tick step — both ends run this exact order)

The server tick step and the client prediction step are this one function.
Replay converges only if the step structure matches (ARCHITECTURE "Network
model"). Fixed `dt = 1/tick_hz` = 0.05 s, never a variable frame delta. `f64`
internally on both ends, `f32` only on the wire. Bit-identical determinism is
explicitly **not** required — the server is authoritative and the per-step
rounding difference is far below the 5 % conformance tolerance (ROADMAP
criterion 5). Chasing cross-language float parity is not an M1 problem.

```
step(state, input, terrain, dt):

  # 1. Sanitize input (see "Input")
  (move_x, move_y) ← clamped pair
  (sprint, jump)   ← mask bits
  look             ← input.look_dir, or the previous look if non-finite/degenerate

  # 2. Frame and facing — rotation is a direct rule, not a dynamic: facing is
  #    updated from the clamped look every tick, held when the look is too
  #    near-vertical to define a stable azimuth. No smoothing, no angular
  #    velocity, no lerp, on either end.
  up     ← normalize(pos)
  look   ← clampLook(look, up)
  tang   ← look − up·dot(look, up)                 # tangent projection of the look
  if |tang| ≥ facing_hold:                          # stable azimuth (≥ ~5.7° off vertical)
      facing ← normalize(tang)
  # else: facing holds its carried value — a near-vertical look has too short
  #       a tangent to define a stable azimuth, so recomputing would spin the
  #       body on mouse jitter (facing_hold = 0.1, rule table)
  right  ← up × facing

  # 3. Mode (from state carried out of the previous step)
  in_contact ← |pos| − radius(terrain, up) ≤ ground_snap
  mode ← GROUND if grounded, else SLIDE if in_contact, else AIR

  # 4. Acceleration — per mode
  w      ← (move_x, move_y)
  ŵ      ← facing·w.y + right·w.x
  speed  ← sprint_speed if sprint else walk_speed
  target ← (ŵ/|ŵ|)·speed  if |ŵ| > eps_degen  else  0    # binary: stick magnitude does not scale speed
  if mode = GROUND:
      if target ≠ 0: vel ← approach(vel, target, accel_ground·dt)
      else:          vel ← vel·exp(−friction_ground·dt)
  if mode = SLIDE:
      n ← surfaceNormal(terrain, up)
      vel ← vel + (−up·gravity − n·dot(−up·gravity, n))·dt   # gravity along the downslope tangent
  if mode = AIR:
      vr ← dot(vel, up)
      vt ← vel − up·vr
      if target ≠ 0: vt ← approach(vt, target, accel_air·dt)
      vel ← up·vr + vt − up·gravity·dt
      if dot(vel, up) < −terminal_speed:
          vel ← vel − up·(dot(vel, up) + terminal_speed)     # clamp downward radial speed only

  # 5. Jump — level-triggered, once per grounded contact
  if mode = GROUND and jump:
      vel ← vel + up·jump_speed

  # 6. Integrate (semi-implicit Euler: the velocity from 4–5 moves the body)
  pos_old ← pos
  pos     ← pos + vel·dt

  # 7. Terrain resolution — the single writer of grounded
  (pos, vel, grounded) ← resolve(pos_old, pos, vel, mode, terrain)
```

Helpers (both ends implement these exactly):

```
approach(v, target, maxΔ):         # constant acceleration toward target, no overshoot
  d ← target − v
  if |d| ≤ maxΔ: return target
  return v + (d/|d|)·maxΔ

clampLook(l, up):                  # push l to ≥ look_clamp from ±up, preserving azimuth
  l ← normalize(l)
  c ← dot(l, up)
  if c ≥ cos(look_clamp):
      t ← l − up·c
      if |t| < eps_degen: t ← previous facing        # l ≈ up exactly
      return up·cos(look_clamp) + (t/|t|)·sin(look_clamp)
  if c ≤ −cos(look_clamp):
      t ← l − up·c
      if |t| < eps_degen: t ← previous facing
      return −up·cos(look_clamp) + (t/|t|)·sin(look_clamp)
  return l

slopeOK(terrain, up):              # is ground at up walkable?
  return acos(clamp(dot(surfaceNormal(terrain, up), up), −1, 1)) ≤ max_slope

wallSlide(p_old, p_new, terrain):  # p_old on/above surface, p_new below it by > max_step
  f(t) ← |p_old + t·(p_new − p_old)| − radius(terrain, normalize(p_old + t·(p_new − p_old)))
  lo, hi ← 0, 1                    # f(lo) ≥ 0, f(hi) < 0
  while hi − lo > wall_tol:
      mid ← (lo + hi)/2
      if f(mid) ≥ 0: lo ← mid else: hi ← mid
  return p_old + lo·(p_new − p_old)
```

The bisection runs a fixed number of iterations on both ends, so the two
results differ only by float rounding. The search segment is ≤ ~1 m (max
per-step displacement) and the terrain varies on a ≥ 3.3 m scale, so `f` has
a single crossing in practice.

```
resolve(p_old, p_new, vel, mode, terrain):
  up ← normalize(p_new)
  h  ← |p_new| − radius(terrain, up)
  if h < 0:                                        # feet below the surface
      if −h ≤ max_step:                            # step-up, landing, or the normal curvature fit
          p_new ← up·radius(terrain, up)
      else:                                        # wall: the step is too high to climb
          p_new ← wallSlide(p_old, p_new, terrain)
          up ← normalize(p_new)
      vel ← vel − up·dot(vel, up)                  # zero radial velocity
      grounded ← slopeOK(terrain, up)              # in contact; walkable only if the slope allows
  else if mode = AIR:
      grounded ← false                             # still above the surface
  else:                                            # was GROUND or SLIDE
      θ_contact ← acos(clamp(dot(surfaceNormal(terrain, up), up), −1, 1))  # same slope measure as slopeOK
      slope_drop ← |vel|·dt·max(0, sin θ_contact)  # per-step downhill drop on that slope
      if h ≤ ground_snap + slope_drop:             # glue band; flat ground θ ≈ 0 → exactly ground_snap
          p_new ← up·radius(terrain, up)
          vel ← vel − up·dot(vel, up)
          grounded ← slopeOK(terrain, up)
      else:
          grounded ← false                         # ran off an edge, or jumped — airborne
  return (p_new, vel, grounded)
```

Invariants, checked after every step:

- **Never below the surface:** `|pos| ≥ radius(terrain, normalize(pos))`. The
  terrain resolution is the *only* position correction in M1 — there is no
  world-edge clamp (a sphere has no edge) and no altitude clamp (the maximum
  altitude in M1 is the jump apex, ~1.03 m).
- **Tangent velocity in contact:** whenever the body is in contact after a
  step, `dot(vel, up_new) = 0` at the new position. The radial zeroing above
  *is* the velocity re-projection, done at the new position, never the old
  one, so walking across a curved surface cannot accumulate radial drift. On
  flat ground the per-step correction is the curvature fit (~0.2 mm at walk
  speed); a body correcting by centimetres every step on flat ground is a bug
  (velocity not staying tangent), not the model.
- **Gravity is not applied in GROUND mode** — the ground's normal force
  cancels it. Gravity acts in SLIDE mode (downslope tangent) and AIR mode
  (radial) only. This is what keeps normal walking from relying on the
  below-surface snap as its floor.
- **A jump always leaves the glue band on its first tick.** A jump tick adds
  `jump_speed·dt` = 0.225 m of rise; while descending at speed v on slope θ,
  the post-jump height is `jump_speed·dt + v·dt·sinθ` while the glue band is
  `ground_snap + v·dt·sinθ`, so `h − band = jump_speed·dt − ground_snap` =
  0.075 m > 0 for all v and θ — the speed/slope terms cancel, so a jump is
  never re-glued, on any slope at any speed. Combined with the edge case
  "jump requires `mode = GROUND`", this bounds the re-jump cadence of a held
  jump bit to the hang time (0.92 s): the bit cannot re-fire until the body
  lands and `grounded` returns. The margin is exactly `jump_speed·dt −
  ground_snap`; re-check it if either changes.

**Snapshot encoding (per PROTOCOL `entity`):** `pos` and `vel` as stored,
plus the `quat` of the body's orientation:

```
quat = rotation taking the entity's local frame (+X right, +Y up, +Z forward)
       to (right = up_new × facing, up_new, facing)
```

so a remote body stands on its own local ground facing its own facing. The
character model is authored in that local frame. Head pitch is not
transmitted (PROTOCOL) — remote characters look level along their own
horizon. The owning client ignores the server's `pos`/`vel`/`quat` for its
own body except at the reconciliation snap, where it takes the server
`pos`/`vel`, re-integrates the un-acked inputs, and re-derives facing from
its own (client-authoritative) look.

### Edge cases (all resolved)

- **Diagonal input** is normalized in input sanitization — or diagonal
  movement is 1.41× faster than forward, the oldest bug in first-person
  movement.
- **Sprint applies in every direction** — forward, backward and strafe all
  at the same `speed`. Simpler to implement, simpler to predict, and nobody
  has ever enjoyed the alternative.
- **Backward movement is full speed.** S alone is exactly `−facing·speed`;
  there is no backward penalty (the uniform-speed rule above covers it, and
  a penalty would be an untestable feel knob).
- **Sprint with no move input does nothing.** The sprint bit selects the
  target speed; it adds no acceleration of its own. With no wish direction
  the target is zero and the body decelerates through `friction_ground`.
- **Jump requires `mode = GROUND`** — no double jump, no air jump, no slide
  jump. It is level-triggered: holding the bit re-jumps on every landing
  (bunny hop). The client may debounce the bit before sending if that is not
  wanted; the server rule is level-triggered either way. Coyote time and
  jump buffering are parked (open questions), not M1 rules.
- **Slopes steeper than `max_slope` are slid, not climbed:** `mode = SLIDE` —
  no walk acceleration, no friction, no jump, and gravity along the
  downslope tangent, so the body accelerates down the slope. The body is in
  contact but not `grounded`. When the slope below becomes walkable,
  `grounded` flips back and the slide velocity carries into normal walking
  (friction then decays it).
- **`max_step`:** ground within `max_step` above the feet, per step, is
  walked up without leaving the ground. Above that, it is a wall —
  `wallSlide` stops the body at the first contact and cancels the radial
  velocity, so the body slides along the face instead of passing through it.
- **Landing discards radial kinetic energy** — radial velocity zeroed,
  tangential kept. No bounce, no damage (health is parked). A fall at
  `terminal_speed` stops dead at the surface: safety-valve behaviour, not a
  feel.
- **Falling through terrain** is the resolution's `h < 0` branch — the
  mm-scale curvature fit in normal walking, and the cm–m correction only when
  a step, landing or wall is actually hit. The invariant above is the test:
  a body correcting by centimetres every step on flat ground is a bug, not
  the model.
- **Position clamping: none.** No world-edge clamp (a sphere has no edge), no
  altitude clamp. The terrain resolution is the only position correction, and
  the never-below-surface invariant holds after every step.
- **Crossing a cube-face seam must be invisible.** This is the one bug a
  round world adds that a flat patch does not have, and it will not appear in
  a test that walks in a small circle near the spawn point. Any test of
  movement must cross at least one seam.
- **Poles do not exist and must not be introduced.** No `atan2`-based
  latitude/longitude anywhere in movement, orientation, or camera code. The
  cube-sphere has no singularity; adding a spherical-coordinate helper puts
  one back.
- **Input missing (client silent):** the server holds the last input (see
  "Input"); the no-input default is stand-still; a dropped client keeps
  walking until the 10 s heartbeat timeout despawns it.
- **Non-finite input** (NaN/Inf on the wire) sanitizes to defaults, so one
  broken or malicious client cannot poison world state or snapshots.
- **HUD "distance to nearest player" with nobody else in the world:** show
  `—`, not `0` and not `∞` (client rule).
- **Client prediction** uses the identical rule table and this exact
  integrator — divergence is corrected by **replay from `ack_seq`**, not
  blending (ARCHITECTURE "Network model").

### Tuning targets (advisory — the rule table is the contract; this is what it buys)

**Feel: weighty + responsive.** The body answers input within a tenth of a
second but momentum is visible — the asymmetry *is* the feel:

- 0 → walk in 0.09 s (`accel_ground` 50): a direction change registers
  instantly, not after a ramp.
- A full sprint reversal takes 0.3 s (`2·sprint_speed/accel_ground`):
  readable momentum, not inertia.
- Stop in ~0.3 s (`friction_ground` 8): deceleration is ~3× slower than
  acceleration — stopping has weight.
- Jump apex 1.03 m, hang time 0.92 s: a readable hop, not a float.
- Slides above `max_slope` are frictionless: a slide-off is a commitment, not
  a shuffle.

**The ~100 ms interpolation budget.** Remote players are interpolated two
ticks (100 ms) behind the server (ARCHITECTURE "Network model"); the model
must stay smooth through that buffer:

- Max per-tick displacement is `sprint_speed·dt` = 0.375 m, so two buffered
  snapshots are ≤ 0.75 m apart — linear interpolation is smooth over that.
- Worst-case error of linearly interpolating a quadratic (max acceleration)
  across the full buffer is `accel_ground·T²/8` = 50·0.1²/8 ≈ 0.06 m — a
  quarter of the 0.25 m p95 prediction-error budget (ROADMAP criterion 6).
- Local prediction reconciles by replay, so its steady-state error is ~0
  while the two rule tables agree; the budget binds on remote interpolation
  and on transients after a reconciliation snap.
- Any tuning change that raises `accel_ground`, `sprint_speed`, or
  `jump_speed`, or changes `ground_snap`, must re-check this arithmetic. The
  budget is the constraint, not the table.
- The terrain-resolution glue-band arithmetic (per-step downhill drop
  `|vel|·dt·sinθ` vs the `ground_snap + slope_drop` band, and the jump margin
  `jump_speed·dt − ground_snap`) must be re-checked if `sprint_speed`,
  `walk_speed`, `max_slope`, or `ground_snap` changes.

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

## Vehicles and crew (M2 spec)

M2 adds one ship to the M1 world and the crew relation between a body and a
seat. The design direction this section replaces is kept intact as
principles below; everything after the principles is a rule — numbered,
unit'd, and binding on both ends the same way the on-foot table is. The ship
is modeled ahead of this milestone (`art` `ship.v1`, `ships/v1.glb`, built
against a frozen seat contract), so the spec adopts the asset's conventions
(crew of three, seat nodes at the eye points, origin at ground level) rather
than re-deriving them.

**Principles (the M1 design direction, kept as-is):**

- **A vehicle is a place, not a mount.** It is a world entity with an
  interior and a set of seats. It exists whether or not anyone is in it, and
  it keeps its position and velocity when empty.
- **Seats, and exactly one control seat.** A vehicle has one pilot seat and
  N passenger seats. Only the occupant of the control seat produces input
  that moves the vehicle; passengers produce no vehicle input at all. This
  is server-enforced, never a client-side UI lock.
- **Seat claims are server-authoritative.** Two players reaching for the
  same seat is a race the server resolves; the loser is told no. Boarding is
  proximity + interact, never teleport.
- **Passengers are attached, not co-simulated.** A player aboard a moving
  vehicle has a position *relative to the vehicle interior*; the server
  composes that with the vehicle transform.
- **Control handoff is a normal event.** The pilot standing up,
  disconnecting, or dying leaves the vehicle unpiloted and coasting or
  stopping. Another player can take the seat. Nobody is trapped and nothing
  is destroyed by a disconnect.
- **The camera never changes mode.** First person on foot, first person in
  the pilot seat, first person in a passenger seat. Different control
  mapping, same rig — the mount point moves.

The connection drives "the entity whose movement this connection's input
drives" as a lookup, not a fixed field (ARCHITECTURE "Server") — taking the
pilot seat is what repoints it.

### Scope

- One ship per world: entity type `0x0002`, manifest id `ship.v1`. It exists
  whether or not anyone is in it and never despawns in M2 (empty-vehicle
  persistence is M5).
- Boarding, seats, one control seat, two passengers, control handoff, and
  the "Flight model" below — flown on the M1 planet and set back down.
  **No space, no orbital transition** (M3). The protocol's `parent_id`/
  `seat` fields already support more ships and seats; M2 ships one.
- The interior is **seat-locked in M2** (walkable interiors parked for
  M3+).

### Ship entity and local frame

- Ship state: `pos` (vec3, f64), `quat` (unit, f64), `vel` (vec3, f64),
  `ω` angular velocity (vec3, f64, carried), `grounded` (bool). Same world
  space as a body; the `quat` carries the full attitude — no `facing` (a
  body's facing is one axis; a ship's is all three). `ω` and `grounded` are
  carried state, **not on the wire** (like a body's `facing`): the client's
  predictor carries its own copies and reconciliation (pos/quat/vel snap +
  replay) reconverges them — `angvel_tau` 0.15 s means an ω skew decays
  within a few ticks.
- **Local frame: origin at ground level** — the hull's flat bottom / gear
  contact point, the ship's "feet" — with **+X right, +Y up, +Z forward**
  (right-handed; the same +Z-forward wire convention as bodies, so the
  art's −Z-forward model uses the existing 180°-Y flip). The hull
  (`ship.v1`) is ≈ 7.1 m long (z ∈ [−3.55, +3.55]), ≈ 6.3 m wingspan
  (x ∈ [−3.15, +3.15]), ≈ 2.5 m tall (y ∈ [0, 2.5]), open-canopy cockpit.
- **Collision is at the origin point, exactly like a body's foot point**
  (M1 rule: terrain collision uses the foot point). The hull's volume is
  visual-only: a diving ship's nose can clip terrain visually before the
  origin registers contact — accepted in M2 (a hull-shape collision is a
  post-M2 candidate if the clipping reads as a problem).

### Ship spawn

Deterministic from the world seed, fixed per world:

```
p0      ← spawn_pos = spawn_dir · radius(terrain, spawn_dir)   # the spawn point (0, 1, 0)
bearing ← the spawn facing (tangent projection of world +X at p0 — GDD "Spawn" look)
dir     ← normalize(p0 + bearing · 15)                          # 15 m from the spawn plain
if slope(terrain, dir) > max_slope:
    dir ← normalize(p0 + bearing · (15 + 10·k)), k = 1..7       # first walkable candidate, else the last tried
pos     ← dir · radius(terrain, dir)                            # origin on the surface
up      ← dir
forward ← normalize(bearing − up · dot(bearing, up))
quat    ← basis with +X = up × forward, +Y = up, +Z = forward
vel     ← 0, ω ← 0, grounded ← slopeOK(terrain, dir)
```

15 m sits inside the 25 m spawn flat disc, so the ship is on
contract-guaranteed flat ground **and** visible from spawn — the horizon is
23.7 m, and a ship at 40 m is over it, which kills the milestone's hook
(walk to the ship, board). The ship faces the spawn (bow-on, along the
bearing). Two servers with the same seed park the same ship.

### Seats and occupancy

The seat table is in the ship's local frame. Both ends use these numbers
(the server composes from the table; the client's camera mounts agree); the
`ship.v1` glb's `seat.*` nodes are the same points in the art frame — the
180°-Y flip of this table, (x, y, z) → (−x, y, −z):

| seat | role | `seat_pos` (body origin / feet, m) | `seat_eye` (camera, m) |
|------|------|------------------------------------|------------------------|
| 1 | pilot — the only control seat | (0.0, 1.44, +1.90) | (0.0, 2.21, +1.90) |
| 2 | passenger | (+0.35, 1.44, +0.75) | (+0.35, 2.21, +0.75) |
| 3 | passenger | (−0.35, 1.44, +0.75) | (−0.35, 2.21, +0.75) |

- **`crew_size` 3** (1 pilot + 2 passengers) — decided against what `art`
  built (a bench layout that grows to 4; the wire `seat` field is u16, so a
  bigger crew later is a rule-table change, not a wire change).
- **`board_dist` 8 m** — the requesting body must be this far from the
  ship's origin (server-measured at the tick the request is processed).
- **`disembark_local` (4.0, 0.0, 1.5)** m, ship frame — bow-side, 4.3 m from
  the origin: outside the hull (wingtip at 3.15 m) and inside `board_dist`,
  so a disembarked player can re-board.
- **Boarding** is proximity + interact (E within `board_dist`), never a
  teleport: the player walks up and requests a specific empty seat (the
  `board` message). Server-authoritative, first request processed wins; the
  loser is told no (`seat_result` 1); out of range → 2.
- **Disembarking** is always available (E) at any time — including
  mid-flight at `vmax` and while the ship is airborne. The body is placed
  with the M1 `spawn` placement rule at `ship_pos + rotate(ship_quat,
  disembark_local)` projected radially onto the surface (`pos =
  normalize(p) · radius(terrain, normalize(p))`): on the ground, `vel = 0`,
  `facing` = the ship's forward projected to the local tangent plane (M1's
  spawn-facing fallback when degenerate). The body's pre-boarding state is
  **not** restored. A non-seated disembark request → result 3.
- **A seated body is attached, not co-simulated.** Per tick the server
  composes the snapshot transform: `pos = ship.pos + rotate(ship.quat,
  seat_pos[seat])`, `quat = ship.quat`, `vel = ship.vel`, `grounded =
  ship.grounded`. The body integrator does not step a seated body.
- **A seated body is not rendered** (client rule, binding): a standing
  character at a seat clips the hull (the head pokes through the canopy),
  and a seated pose is a post-M2 art item. The snapshot transform remains
  authoritative data (HUD, seat occupancy, the conformance criteria) — the
  client simply draws no character mesh and no nametag for an entity with
  `parent_id ≠ 0`.
- **One control seat, server-enforced.** Only the occupant of seat 1
  produces input that moves the ship. A passenger's input is read as an
  on-foot frame and its movement ignored — the body is composed from the
  ship. Never a client UI lock.
- **Control handoff is a normal event.** The pilot's connection dies (or the
  pilot disembarks) → the seat frees and the ship runs the same step with
  zero input: `ω` decays (τ 0.15 s), `damp` decays `vel` (half-life 1.4 s),
  gravity descends it, origin-point collision stops it on the terrain.
  Another player can take the seat. Nobody is trapped; nothing is destroyed
  by a disconnect.
- **No body-vs-ship collision in M2** (M1 has no body-vs-body collision): a
  player walking around the parked ship can visually clip the hull until
  they board. Accepted for M2; a post-M2 candidate.

### The flight model — M2 context

The base model is the "Flight model" section below, unchanged (first-order
rotation toward the input's target angular velocity, thrust along ship
forward with the `vmax` clamps, `damp` when unthrottled, the same
fixed-step semi-implicit integrator and replay). M2 adds the context it
flies in:

| name | value | unit | justification |
|------|-------|------|---------------|
| `angvel_max_roll` | 2.0 | rad/s | roll capped below `angvel_max` 4.0 (yaw/pitch): the open question — a welded camera rolling at 229°/s is a known nausea source; 114°/s is still lively (a `vmax` lap ≈ 24 s). The pilot keeps the welded camera (pillar 2, one rig); the free-look head goes to the passengers instead. Resolves the open question as a rule-table change, per its own option |
| `k_rate` | 0.0022 | rad/px | mouse-to-rate scale, the M1 `LOOK_SENS` value: a mouse at ≈1800 px/s reaches the yaw/pitch cap, so the mouse feels like M1's look |

The M1 `gravity` row (9.8 m/s²) applies along local −up while the ship is
airborne — same world, one gravity. No `terminal_speed` row: the `vmax`
clamp (40 m/s) caps the descent too.

**Input mapping (M2, pilot seat).** The base section's "mouse —
yaw/pitch/roll rates" assumes a 3-axis mouse; this project's control device
is a 2D mouse, so M2 pins the mapping (PROTOCOL v2, mode 1):

- W/S → `thrust` ∈ [−1, +1] (forward positive; the base model's 0.5×
  backward factor applies server-side)
- mouse X per second since the last input frame · `k_rate` → `yaw_rate`
  target; sign: rightward drag (dx > 0) → negative rate (positive is a left
  turn — right-hand rule about local +Y)
- mouse Y per second · `k_rate` → `pitch_rate` target; sign: upward drag
  (dy < 0) → positive rate (nose up about local +X)
- A/D → `roll` ∈ {+1, 0, −1} (A = roll left, D = roll right); the server's
  target is `roll · angvel_max_roll`
- Shift → `action_mask` bit `0x0004` boost (scales thrust, not speed — the
  base rule)

The client sends the **target rates as values** — current command state,
latest wins, the M1 input semantics — so replay re-integrates the buffered
rates deterministically. The pilot's camera is hull-fixed (below): the mouse
steers the ship, not the view.

**The step, both ends (Go and TypeScript mirror; `ω` carried):**

```
stepShip(s, input, terrain, dt):     # input = (thrust, roll, yaw_rate, pitch_rate, boost), sanitized
  up   ← normalize(s.pos)
  # 1. Rotation — first-order toward the target, ship frame
  ω_t  ← (pitch_rate, yaw_rate, roll · angvel_max_roll)      # about local +X, +Y, +Z
  ω    ← ω_t + (s.ω − ω_t) · e^(−dt / angvel_tau)
  q    ← normalize(s.quat ⊗ axisAngle(rotate(s.quat, ω) · dt))   # post-multiply: rotate about local axes
  # 2. Translation — thrust along ship forward (semi-implicit, base model)
  fwd  ← rotate(s.quat, (0, 0, 1))
  a    ← fwd · (boost ? accel_boost : accel) · (thrust > 0 ? thrust : 0.5 · thrust)
  v    ← s.vel + a · dt
  if thrust = 0:   v ← v · e^(−damp · dt)
  if ‖v‖ > (boost ? vmax_boost : vmax):  v ← v · ((boost ? vmax_boost : vmax) / ‖v‖)
  # 3. M2 context — gravity (airborne only), integrate
  if not in_contact(s.pos, terrain):  v ← v − up · gravity · dt
  pos  ← s.pos + v · dt
  # 4. Origin-point collision (terrain), exactly like a body's foot point
  dir  ← normalize(pos)
  R    ← radius(terrain, dir)
  if ‖pos‖ < R:
      pos ← dir · R
      vr  ← dot(v, dir)
      if vr < 0:  v ← v − dir · vr        # kill inward radial velocity
  in_contact ← ‖pos‖ ≤ R + ground_snap    # the M1 margin row, reused
  return (pos, q, v, ω, in_contact)
```

Zero input (no pilot) runs the same step — the "unpiloted ship coasts and
stops" behaviour falls out of the base model's `damp` + τ + gravity, no
special case.

### Camera and rig (client, binding)

The camera never changes mode: one first-person rig, the mount moves
(pillar 2; ARCHITECTURE). The cockpit is a real modeled space in `ship.v1`
(open canopy, double-sided hull, floor, dash, lit console, seats) — no hull
culling, no windshield trick: the local seated player sees the interior and,
through the canopy, the world.

- **On foot:** unchanged from M1 (eye height above the feet, radial up, free
  look clamped 1° off ±up).
- **Pilot (seat 1):** hull-fixed. The camera mounts at the ship's
  `seat.pilot` node (`seat_eye[1]` of the table); orientation = the ship's
  attitude (up = ship up, forward = ship +Z — the node carries no rotation,
  so the mount inherits the ship frame). The pilot's mouse never moves the
  camera.
- **Passenger (seats 2–3):** the seat's eye, free look. The camera mounts at
  the `seat.passenger.i` node; orientation = the client's `look_dir`,
  clamped 1° off ±(ship up) — the M1 clamp applied to the camera's up, which
  for a seated player is the ship's up, not radial.
- **Prediction:** the pilot predicts the ship with `stepShip` (the client
  has the same rules; conformance criterion 6 keeps Go and TS in step) and
  reconciles by replay exactly like the M1 body (snap to the snapshot's
  pos/quat/vel, re-run the buffered inputs). Every other client interpolates
  the ship from snapshots (~100 ms buffer) like a remote entity; a local
  passenger rides the interpolated ship — no prediction for a seated body.
- **HUD (DOM; the diegetic cockpit HUD stays a later option):** ship speed
  while seated (|ship vel|), "press E to board <name>" on foot within
  `board_dist`, the local role (pilot / passenger).

## Flight model (lands with M2)

Written before the milestone order changed and preserved intact: this is
the ship's movement model, used as-is by "Vehicles and crew (M2 spec)".
The M2 context it flies in (gravity, origin-point terrain collision, the
roll cap, the pilot input mapping) is pinned in that section; open space at
M3 revisits the world/spawn rows, not this model.

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
- **Bunny hop (level-triggered jump).** The server rule is
  level-triggered: holding the jump bit re-jumps on every landing. The
  client may debounce the bit before sending, so the *player-facing*
  behaviour depends on a client choice, not the server rule. M1 decision:
  allow the auto-rejump (free vertical mobility, simpler client), or require
  client-side edge-triggering (a hop is one press)? Either is implementable
  from the spec as written.
- Concurrent player target for local dev: assume 10–50.

M2 (decided at wave 0, 2026-08-22 — see "Vehicles and crew (M2 spec)"):

- **Motion sickness in a hull-fixed cockpit.** Decided: the pilot keeps the
  welded camera (no camera damping, no free-look head — pillar 2, one rig)
  and roll is capped at `angvel_max_roll` 2.0 rad/s against `angvel_max`
  4.0 for yaw/pitch; the free-look head goes to the passengers instead
  (their camera is decoupled from the hull).
- **Crew size per vehicle** — decided: 3 (1 pilot + 2 passengers), against
  what `art` built (`ship.v1`, a bench layout that grows to 4); the wire
  `seat` field is u16, so a bigger crew later is a rule-table change, not a
  protocol change.
- **Walkable interiors during flight** — decided: seat-locked in M2
  (enormously cheaper; the vision stays parked for M3+).
- **Landing: seamless or a transition?** Pillar 1 wants seamless; a short
  scripted descent is far cheaper and still keeps the crew together. M3
  decision, and it drives whether space and surface are one world or two.
- **Does an empty vehicle persist when its last occupant logs off?** Ties into
  persistence — parking a ship on a planet and finding it later is the
  motivating case.
- Ship customization: parked until the loop is closed.
