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
- Player-vs-player collision — in M1 players pass through each other. (Done 2026-10-02: "Full collision" under "Static colliders".)
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
  (along `facing × up`) — W = +y, D = +x, S is exactly −forward.
  - **The order of that cross product is load-bearing.** This world is
    right-handed, so `facing × up` is the player's right and `up × facing` is
    their left. Written the wrong way round, A and D are silently swapped:
    every other rule still holds, the two sims still agree, and the
    conformance test still passes — because no scripted route strafes. It is
    only visible to a human with their hands on the keys.
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
and there is no spawn spread in M1. Since full collision (2026-10-02) two
bodies on one spot push apart on the next tick, which is the spawn spread.

### First-person body

*Superseded in part by "First-person body, in the world (Phase 19)" below:
the local player draws torso and legs through the near-cut material; the
rest of this section's rules (head hidden, eye node, upright body, the
first-person arms) stand.*

The local player renders the same character model as everyone else, seen from
inside. That is cheap to say and has four consequences that are not:

- **Hide the head, not the body.** The camera sits at the `eye` node, which is
  inside the skull — so the model carries a separate `head` node that is hidden
  for the local player only. Relying on the near clip
  plane to slice the head away instead produces a visible cross-section
  whenever you look down or a wall gets close.
- **Near clip plane at 0.05 m** (far plane 500 m — nothing is visible past the
  23 m horizon but the sky and the tall landmarks). A default 0.1 m near plane
  slices through your own chest when you look straight down.
- **Looking down shows legs and feet, no torso** (2026-10-06). The local body
  draws only its `legs` mesh and what is worn in the `legs` and `feet` slots,
  so you can tell where you are standing; `torso`, `head`, `arms`, hair and
  every piece worn above the waist cast shadows only, so the ground shadow is
  still the whole figure. The drawn legs receive no shadows (the unseen
  torso's shadow lands right on the hips). A torso seen from half a metre above and inside
  read as a hollow shell, never as a chest. The arms you see are the separate
  first-person instance below.
- **The body stays upright when you look down.** Only the camera pitches;
  pitch is not applied to the body. This is already why remote head pitch is
  not transmitted, and it is what makes looking at your own feet work.

**Animation: skinned, clip-driven (since 2026-09-30).** The character is one
skinned body on an armature built by `art/tools/bpy/body.py`, in four meshes
(`torso`, `legs`, `arms`, `head`) with clips for the gaits, the armed gaits, death and
first person. Armor dresses the same skeleton (art/README.md).

**First-person arms.** What the eye sees of its own arms and weapon is a
SECOND instance of the same `char.player`, hung under the camera with its eye
on the camera, drawing only its `arms` mesh (its `torso`, `legs` and `head` are hidden) and the armor that covers it,
playing the `fp_*` clips, holding the real weapon in its own `hand.r` -- the
same model other players see. It draws with a depth-squeezed material so it is
never cut by the world or your own chest, and casts no shadow. The local
body's torso, head, arms, their armor and the held weapon are shadows-only: the ground
shadow holds the gun, the eye never sees two pairs of arms.

- Hold: the client frames the rifle by its rear sight (lower right of the
  view); empty hands frame by the right hand.
- Aim (right mouse, pointer captured): `fp_ads`, the rear sight on the eye
  line, FOV 60→45, sway and bob ×0.3.
- Recoil kicks the ARMS (back and muzzle-up) and plays `fp_fire`; the camera
  never kicks.
- Reload (R) plays `fp_reload`, stretched to the weapon's `reload_time`.
- Within 1.0 m of a collider or the ground along the view the rifle lowers
  (`fp_lower`), and comes back past 1.25 m.

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
  right  ← facing × up                               # right-handed: up × facing is LEFT

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
| `rock_size` | 0.6–1.5 | m | see the collision constraint; the floor rose from 0.3 on 2026-09-29 — the small end read as pebbles (pebbles proper are the client's cluster pass, 0.15–0.45 m, 0–3 per rock) |
| `rock_variants` | 3 | | `prop.rock.a/b/c` — one silhouette at 400 scales reads as a repeating texture |
| `rock_slope_max` | 35 | deg | do not scatter onto near-cliff faces |

- Props have **no collision in M1**, so nothing may be big enough that walking
  through it is jarring — hence the 1.5 m cap. Boulders you can climb on need
  prop collision, and that means the server owns their placement instead.
  (Superseded 2026-10-02: rocks and solid props collide -- "Full collision".)
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
  they board. Accepted for M2; a post-M2 candidate. (Done 2026-10-02: "Full
  collision".)

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
- mouse Y per second · `k_rate` → `pitch_rate` target; sign (corrected at
  Phase 5 implementation): a POSITIVE rate about local +X is nose DOWN by
  the right-hand rule (+Y rotates toward +Z), so upward drag (dy < 0) →
  **negative** rate. The draft claimed the opposite and the first flight
  script "climbed" by pitching 200° through the ground and out the far
  side of vertical
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
  q    ← normalize(s.quat ⊗ axisAngle(ω · dt))   # post-multiply: rotate about local axes
       # (corrected at Phase 5 implementation: the draft wrapped ω in
       #  rotate(s.quat, ·), a world-axis quat post-multiplied — which
       #  contradicts "rotate about local axes"; ω's components ARE the
       #  local axes' rates, so the local form is the one both sims build)
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

## Phase 5 — space regime, landing, ownership

The flight model above is used as written ("The flight model — M2 context"
pins the step, the input mapping and the camera; the phase number moved, the
rules did not). This section adds the three things Phase 5's criteria need
that no earlier phase specified: where space begins, what a landing is, and
who owns a ship. Same contract status: every number is in a rule table,
every rule is a formula or a named reference to one.

### Rule table

| name | value | unit | justification |
|------|-------|------|---------------|
| `space_radius` | 260 | m | the regime boundary, by RADIUS, not altitude — terrain tops out at 190 m, so the boundary clears every peak by ≥ 70 m and "altitude above ground" never flickers the regime over a mountain |
| `space_hyst` | 10 | m | hysteresis band: ENTER space at ‖pos‖ ≥ `space_radius + space_hyst/2` (265), LEAVE at ‖pos‖ ≤ `space_radius − space_hyst/2` (255). A hover exactly at the threshold changes regime at most once (C35) |
| `land_speed_max` | 8 | m/s | max inward radial speed at contact that settles; ~vmax/5 — a deliberate flare, not a formality |
| `bounce_k` | 0.3 | — | a hard landing reflects the radial velocity at this restitution instead of settling: the penalty is the bounce, tunnelling stays impossible either way (the origin-point clamp runs regardless) |
| `land_grip` | 3 | 1/s | tangential decay while grounded — a landed ship slides to a stop |
| `pad_dist` | 25 | m | the landing pad: `pad_dist` from spawn along the spawn bearing rotated −90° about up (the rover parks along +bearing; perpendicular keeps them apart), walkable-retry k = 1..7 at +10 m, exactly the rover's spawn scan |
| `ship_price` | 600 | cr | above the rifle (250) + typical camp loot: buying the ship is Phase 5's goal, not its opening move |
| `crew_size_ship` | 3 | — | pilot + 2 passengers, the "Seats and occupancy" table as written |

### Space regime

Carried state (`space bool`), like `grounded` — hysteresis needs memory, and
it is mirrored onto the wire as the `0x10` flags bit so the client renders
the regime it predicts. In space:

- **gravity off, drag off**: step 3's gravity term and step 2's
  `damp`-when-unthrottled both skip. An unthrottled ship in space coasts —
  Newton, not a half-life; stopping is reverse thrust.
- **everything else unchanged**: rotation model, thrust, the `vmax`/boost
  clamps (the clamp is what makes C36's "no drift off the world" cheap — a
  bounded speed cannot run away), the origin-point collision (vacuously, the
  terrain is 70 m below the boundary).

The transition changes NO state but the flag: position, velocity, attitude
and ω all carry through untouched, which is C35's "no discontinuity" by
construction rather than by tuning.

### Landing

At origin-point contact (step 4) with inward radial speed `vr`:

- `|vr| ≤ land_speed_max`: settle — kill the inward radial component
  (already the base rule), `grounded ← true`.
- `|vr| > land_speed_max`: bounce — `v ← v − dir·vr·(1 + bounce_k)`
  (the reflected radial at `bounce_k` restitution), `grounded` stays false.
- While grounded: tangential velocity decays `exp(−land_grip · dt)` and
  zeroes under `hold_speed` (the Phase 4 row, reused) — parked is parked,
  ships included. Thrust while grounded works (that is the takeoff).

### Ownership

A ship is an ITEM (`ship.v1`, kind `vehicle`, stack 1) sold by the
quartermaster at `ship_price`, so purchase, refusal codes, inventory and
persistence are all the Phase 2 machinery unchanged — ownership IS having
the item, and it survives reconnects because inventory already does.

- **On purchase**: the server spawns the buyer's ship on the pad
  (pad scan above; a slot within 6 m of an existing ship is skipped, so a
  second buyer's ship lands on the next walkable slot).
- **On join**: a player whose inventory holds `ship.v1` and whose ship is
  not in the world gets it spawned the same way. One ship per owner.
- The ship is a world entity like the rover: it persists for the server
  run, coasts unpiloted (zero-input step), and is never destroyed —
  `damageable: false`, same reasoning as the rover's def.

Spec for `netcode` + `frontend`, same contract status as the on-foot rules:
every number is in the rule table; every rule is a formula or a named
reference to one. There are no prose-only rules in this section. "Seats and
occupancy" applies to the rover **as written** (board/disembark semantics,
result codes, `board_dist`, composition, one control seat, handoff) — only
the seat table and `disembark_local` below are rover-specific.

The drive model is deliberately simpler than flight: no angular velocity
state, no boost, no first-order steering response. Yaw is applied directly
from the steer input, the wheels' job is done by a lateral-grip decay, and
the terrain does the suspension — the rover's origin follows the surface the
same way a body's foot point does. State is `pos/quat/vel` (f64) plus
carried `grounded`; nothing beyond the wire triplet plus one bool.

### Rule table

| name | value | unit | justification |
|------|-------|------|---------------|
| `accel_drive` | 8 | m/s² | 0 → `vmax_drive` in 2 s; brisk without out-accelerating the 20 Hz correction loop |
| `vmax_drive` | 16 | m/s | ~3× a sprint — walking should feel slow beside it, steering should still be possible at full speed (`steer_rate` gives a 13 m turn radius) |
| `steer_rate` | 1.2 | rad/s | full-lock U-turn in ~2.6 s; skid-steer — a stationary rover can turn in place |
| `grip` | 6 | 1/s | lateral velocity half-life ~0.12 s: a hard turn drifts for a beat, then bites |
| `damp_drive` | 0.8 | 1/s | coast half-life ~0.87 s — lifting throttle is a brake, there is no brake input |
| `hold_speed` | 0.1 | m/s | static friction: with no throttle, a tangential speed under this zeroes outright — parked is parked, an exponential decay alone never reaches zero and a parked rover would creep downhill forever |
| `drive_slope_max` | 40 | deg | throttle authority cutoff; less capable than feet (`max_slope` 50), so the last stretch of a climb is on foot |
| `rover_spawn_dist` | 20 | m | deterministic parked spawn along the spawn bearing (pseudocode below) |
| `crew_size_rover` | 2 | — | 1 driver + 1 passenger; the wire `seat` field is u16, more later is a table change |

Reused M1/M2 rows, by reference: `tick_hz`/`dt`, `gravity` (9.8, along local
−up, always), `ground_snap` (0.15 m), `eps_degen`, `board_dist` (8 m), and
the reverse rule from the flight model: negative throttle accelerates at
`accel_drive · 0.5` under the same clamp.

### `stepRover` (both sims, bit-for-bit intent — C30 diffs at ≤ 1e-6)

Input is sanitised first: `throttle`/`steer` clamped to [−1, 1], non-finite
→ 0. `up`, `h` (heading), `n` (ground normal) are unit vectors. Projection
fallback: wherever a projection is degenerate (`‖·‖ < eps_degen`), fall back
to `rotate(quat, +Y)` projected the same way, exactly as M1's spawn-facing
fallback.

```
1  up ← normalize(pos)
2  h  ← project rotate(quat, +Z) onto plane ⊥ up, normalized  (fallback above)
3  if grounded and steer ≠ 0:
       h ← rotate_about_axis(h, up, −steer · steer_rate · dt)
       (positive steer turns toward local +X, i.e. right; the sign is the
        right-hand rule about up)
4  if grounded and slope(terrain, up) ≤ drive_slope_max and throttle ≠ 0:
       a ← throttle > 0 ? accel_drive : accel_drive · 0.5
       vel ← vel + h · (throttle · a · dt)
5  vel ← vel − up · (gravity · dt)
6  if grounded:                       # grip and coast, tangent-frame split
       vr ← dot(vel, up);  vt ← vel − up·vr
       vf ← dot(vt, h);    vlat ← vt − h·vf
       vlat ← vlat · exp(−grip · dt)
       if throttle = 0:
           vf ← vf · exp(−damp_drive · dt)
           if vf² + ‖vlat‖² < hold_speed²: vf ← 0; vlat ← 0   # parked is parked
       vel ← up·vr + h·vf + vlat
7  vt ← vel − up·dot(vel, up)
   if ‖vt‖ > vmax_drive: vel ← vt · (vmax_drive/‖vt‖) + up·dot(vel, up)
8  pos ← pos + vel · dt
9  u' ← normalize(pos); R ← radius(terrain, u')
   if ‖pos‖ ≤ R + ground_snap:
       pos ← u' · R
       vr ← dot(vel, u'); if vr < 0: vel ← vel − u'·vr
       grounded ← true
   else: grounded ← false
10 n ← grounded ? surface_normal(terrain, u') : u'
   h' ← project h onto plane ⊥ n, normalized (fallback above)
   quat ← quat_from_basis(+X = n × h', +Y = n, +Z = h'), normalized
```

Steps 3, 4 and 6 are grounded-only: airborne, the rover is a ballistic
brick — no steering, no throttle, no grip, which is what makes crests feel
like crests. The one collision the rover has is step 9's origin point,
exactly like a body's foot point ("Vehicles and crew": hull volume is
visual-only, and there is no body-vs-vehicle collision). Superseded
2026-10-02: step 8b pushes the hull out of everything solid ("Full
collision").

### Rover seats

Rover local frame, +X right / +Y up / +Z forward, same conventions as the
ship's seat table. The glb's `seat.driver` / `seat.passenger.0` nodes are
these points in the art frame — the 180°-Y flip, (x, y, z) → (−x, y, −z).

| seat | role | `seat_pos` (body origin / feet, m) | `seat_eye` (camera, m) |
|------|------|------------------------------------|------------------------|
| 1 | driver — the only control seat | (−0.35, 0.95, +0.10) | (−0.35, 1.55, +0.10) |
| 2 | passenger | (+0.35, 0.95, −0.40) | (+0.35, 1.55, −0.40) |

`disembark_local` **(+2.0, 0.0, 0.0)** m, rover frame — right side, 2 m from
the origin: outside the hull, well inside `board_dist`, so a disembarked
driver can re-board. Placement follows the "Seats and occupancy" disembark
rule verbatim (radial projection onto the surface, `vel = 0`, facing = rover
forward projected to the tangent plane).

### Deterministic spawn

The ship's deterministic-spawn pseudocode ("Vehicles and crew", M2 spec)
applies with `rover_spawn_dist` in place of the ship's 15 m: a candidate
point `rover_spawn_dist` from spawn along the spawn bearing, walkable-retry
`k = 1..7` at +10 m steps, quat basis `+X = up × forward, +Y = up,
+Z = forward`. One rover, spawned at world build, entity type `0x0005`,
def `vehicle`, asset `vehicle.rover.v1`.

### Tuning targets (advisory — the rule table is the contract)

- Flat ground, full throttle: ~2 s to `vmax_drive`, top speed pins at 16 m/s.
- Full-speed full-lock: settles into a ~13 m-radius circle, drifting visibly
  for the first ~0.3 s of the turn.
- A 45° slope stalls the climb (throttle authority cut at 40°); gravity plus
  grip walks it back down without spinning.
- Lifting throttle at `vmax_drive` coasts to under 2 m/s in ~3 s.

## UI style guide — Scrapyard Comic (Phase 8)

The interface is a device the character also sees (the Borderlands rule),
drawn with comic-ink conviction over a scavenger world. Every screen is
reviewed against THIS section; a screen that needs a color or rule not
listed here adds it here first.

### Palette

| token | hex | used for |
|---|---|---|
| `ink` | `#10131A` | outlines, text on light, the notch cut |
| `slate` | `#1B2029` | panel background (at 92% opacity over the world) |
| `steel` | `#2A3140` | raised elements, input fields, bar troughs |
| `cream` | `#E8E2D0` | primary text |
| `dust` | `#9AA08E` | secondary text, disabled |
| `amber` | `#FFAE19` | THE accent: credits, highlights, active edges, crits |
| `danger` | `#FF4A3D` | health, damage numbers, destructive buttons |
| `shield` | `#3FC1FF` | shield/energy, info |
| `good` | `#7FD18A` | confirmations, gains |

Rarity ramp (border band on item cards, name tint in lists):
`common #B8B8A8` → `uncommon #4FD15C` → `rare #3FA9FF` →
`epic #B45CFF` → `legendary #FF9B1A`. Unknown rarity renders as common.

### Panel construction

- Background `slate` @ 92%, border **3 px `ink`**, and when the panel is
  active/focused an inner **1 px `amber`** edge.
- One corner (top-right by default) carries a **12 px notch cut** — the
  silhouette that says "this game" at a glance.
- Panels tilt TOWARD their screen edge (the comic lean): **−2° on the
  left side, +2° on the right, 0° for centered elements** like the
  compass strip. Text inside stays unskewed past ±4° reading sizes.
- Section headers: ALL CAPS, +8% letterspacing, `dust`, over a 2 px
  `ink` rule.

### Typography

Display face: one vendored OFL font (condensed, chunky — headers, big
numbers, damage popups). Body: the engine default sans. Sizes: body 14,
header 16, HUD numerals 22, damage numbers 18 (crit 26).

### Combat feedback

- **Damage numbers**: spawn at the hit's world point, drift up 0.8 m
  over 0.6 s while fading; `cream` normal, `amber` + size 26 crits;
  stacking hits offset horizontally so volleys read as counts.
- **Incoming damage**: a 500 ms `danger` arc at the screen edge in the
  attacker's direction (eight sectors is enough).
- **Hit marker**: a 120 ms four-tick cross at the reticle on a landed
  shot; `amber` when the target dies.

### HUD layout (the permanent cluster)

Bottom-left: health bar (trough `steel`, fill `danger`, numeral inside)
with the shield bar (`shield`) above it. Bottom-right: ammo as
mag/reserve split — mag in display type at 22, reserve smaller in
`dust` — with credits (`amber`) above. Top-center: the compass strip —
a bearing tape with `ink`-outlined markers (shop, rover, own ship, camp,
spawn). Prompts ("E · talk", notices) stay bottom-center. The flight
readout replaces the ammo cluster while seated in a ship.

### Display and settings (Phase 13)

The UI is laid out for **1920×1080** and the window scales it as one
piece (Godot `canvas_items` stretch, `expand` aspect): a 1280×720
window draws everything at two thirds, a 1440p window at 1.33×, a wider
aspect gains margin rather than distortion. Nothing overlaps because
the window shrank. Three display modes — **windowed**, **windowed
fullscreen** (borderless at the desktop size, the default) and
**fullscreen** (exclusive) — on a SETTINGS page off the Esc menu, with
a **UI scale** slider (0.75–1.5×, on top of the window scaling) and
**mouse sensitivity** (0.25–3×). All three persist in `user://sa.cfg`
`[settings]` and apply at boot; the rig and the self-test own their own
window and ignore the display mode.

### Launcher (Phase 15)

The game no longer opens full screen into an update. The same executable
boots into a **launcher**: one small window that owns the update, says
whether the server is up, and hands over to the game on PLAY. One binary,
one Velopack package; the update's restart lands back in the launcher.

**Window.** 560 × 360, centred, windowed, not resizable, title "Space
Adventure", the Scrapyard Comic palette on a plain dark panel. The game's
`canvas_items` stretch is OFF while the launcher shows (content scale
`Disabled`, 1:1 pixels) and restored, with the saved display mode from
Settings, when PLAY is pressed — the launcher never inherits the 1920×1080
canvas scaled down to a thumbnail, and the game never runs in the
launcher's window.

**The engine's window is born invisible.** Velopack runs the executable
twice around every update (`--veloapp-obsolete` on the old build,
`--veloapp-updated` on the new) and there is no switch to skip those
runs; each boots the engine far enough to show a window before any of our
code can exit. So `project.godot` creates the OS window as a 1×1 (Godot
floors it to 64×64) **borderless, per-pixel-transparent** window with a
zero-alpha splash, and `Boot` gives it borders, opacity, its real size and
the centre of the screen before the first frame of a real launch. A hook
run shows nothing; off-screen placement does not work because Godot
clamps the start position back onto a screen. Verified by screen capture
on Windows: the corner during a hook-shaped run shows only the desktop.

**Layout** (left to right, top to bottom):

| Region | Content |
|---|---|
| Header | `SPACE ADVENTURE`, the build label (`v1.0.41 · 3cbd797` / `dev · 3cbd797`) right-aligned |
| Account column (left, ~40 %) | reserved and empty in Phase 15; the sign-in column of "Accounts, launcher login and characters (Phase 16)" below |
| Status column (right) | the update line and its progress bar; the server line; `PLAY` (wide, primary) and `QUIT` (small) underneath |

**Update line, binding states** (the launcher model is a pure state
machine, testable in `-selftest`):

| State | Line | PLAY |
|---|---|---|
| `Checking` | `CHECKING FOR UPDATES…` | disabled |
| `Updating(p)` | `UPDATING TO v1.0.42 … 37 %` + bar | disabled |
| `Restarting` | `RESTARTING…` (Velopack swaps and relaunches into the launcher) | disabled |
| `UpToDate` | `UP TO DATE · v1.0.42` | enabled |
| `DevBuild` | `DEV BUILD · not installed, no update check` | enabled |
| `Offline(reason)` | `UPDATE CHECK FAILED · playing the installed build` (reason in the log) | enabled |

Rules: the check is given 5 s before it is declared `Offline` (an
unreachable GitHub must never keep anyone out of the game — Phase 6's
rule, kept); a download that fails mid-way is `Offline` too, not a
half-applied client; after the Velopack restart the launcher shows
`UpToDate` and waits for PLAY (no auto-launch); the in-session banner
("update ready, applies on exit", Phase 8/#48) is unchanged.

**Server line.** `SERVER · ONLINE · 3 PLAYING` from `GET <site>/api/stats`
(`online`, `players`) where `<site>` is the game URL's host over http(s)
(`wss://game.stevenholder.info/ws` → `https://game.stevenholder.info`,
`ws://127.0.0.1:18080/ws` → `http://127.0.0.1:18080`), polled every 10 s
while the launcher shows; `SERVER · UNREACHABLE` on any failure. PLAY is
never gated on it — a connect failure is reported by the game as today.

**PLAY.** Applies the saved display mode and UI scale, restores the canvas
stretch, hides the launcher, and connects exactly as `UpdateThenConnect`
did after its update step. **QUIT** quits.

**Who skips it.** Every rig and harness flow: `-uiShot`, `-quitAfter`,
`-selftest`, `-dumpNodes`, `-dumpSfx` (the existing `Rigged` set) and a
new `-play` flag for a human who wants straight in. `make godot-run` /
`godot-dev` still print `world ready` with no launcher in the way. For
photographs: `-uiShot … -uiLauncher <state>` forces the launcher into
one of the states above (with a fake version and a fake server line) so
each one has a shot in `test/out/ui/`.

### Accounts, launcher login and characters (Phase 16)

Phase 15 reserved the launcher's left column. This fills it, closes the
side doors, and puts characters between PLAY and the world. **The only
way into the world is an account, and an account plays one of its
characters.** Sign up and sign in happen in the launcher; PLAY is dark
until signed in; PLAY opens the game window on a character select, and
the world is joined as the chosen character. Phase 7's coexistence rule
(anonymous tokens join as guests), its link codes, the site's mint/import
page and the game's F1 link panel are removed — one identity, one door.

#### The door

`POST /api/game-login` `{email, password}` → `{session, name}`: verify
the password (the site login's argon2id path and dummy-hash timing, the
same per-IP bucket), then mint a session in `web_session` — the site's
30-day session, returned in the body instead of a cookie. Every account
route (`withAccount`) accepts it as `Authorization: Bearer <session>`
beside the cookie. `POST /api/register` is unchanged; the launcher's
CREATE ACCOUNT calls it, then `game-login` with the same fields. `POST
/api/logout` ends it. The client remembers `[identity] session` and
`[identity] email` and never the password; a player token is never
stored on disk — it is fetched per PLAY from the character list. A
password change on the site kills the session as it kills every other
(C57): the next PLAY on a remembered launcher is a 401, which is
`SIGNED OUT · sign in again`. `X-Requested-With` required on every
mutating route, as before.

#### Characters

A character is a `player` row owned by an account (`account_id` set),
with a **body** beside the name. Up to five per account. The row's token
is the character's credential on the wire — `hello` carries it unchanged
— and the server seats a token only when it owns such a row; any other
hello is closed `1008` (policy violation) before a spawn. A character
token's `hello.name` is ignored: the character's name and body are the
row's, not the client's. Phase 7's "one player per account" rule is gone
with its link codes; the player each account already has is its first
character (body `char.player`, name as it was).

- `GET /api/characters` → `[{token, name, body, credits, last_seen_ms}]`
  (newest last).
- `POST /api/characters` `{name, body}` → the same row shape. `name`:
  `SanitizeName`, then 3–16 characters of letters, digits, space, `-`,
  `'`; unique among characters, case-insensitive (a partial unique index
  on `lower(name)` where `account_id` is set). `body`: one of the four
  ids below. The new row is minted at a guest's start, as today. 400 bad
  name/body, 409 name taken or five characters already.
- Delete and rename: Deferred table.

**Bodies** — two models, each in two genders; a body id is a model plus
a gender, and armor is built for every one of them (`armor.py`
`BODIES`), so a suit fits whichever you wear:

| Model (picker) | Gender | id | source |
|---|---|---|---|
| `COLONIST` | M | `char.player` | human.py, as today |
| `COLONIST` | F | `char.player.f` | human.py variant, `gender` macro 0.0 |
| `VANGUARD` | M | `char.ubc` | Quaternius UBC Superhero_Male, as today |
| `VANGUARD` | F | `char.ubc.f` | Quaternius UBC Superhero_Female, the same recipe |

The player **spawn row** carries the body: `data` is the name, and a
player whose body is not `char.player` appends `\0` + the body id. A
reader that stops at the first NUL (or that never sees a non-default
body — every harness player) reads the name exactly as before; the Godot
client and `test/lib/wire.mjs` split it. The local body, its
first-person arms and every remote player attach the body the row names
instead of `char.player`.

#### Character select

PLAY opens the game window (saved display mode, canvas stretch) on the
**character select**, not the world: the world is neither built nor
joined until a character is chosen. It is the game's canvas (1920×1080
UI base, Scrapyard Comic), not the launcher's.

| Region | Content |
|---|---|
| Stage (right ~60 %) | the selected character's body on a plain dark floor under one key light, idle clip, turning slowly — drawn in the main viewport by the asset registry, no SubViewport (gl_compat draws those unlit) |
| List (left) | `CHARACTERS`; one row per character: name, model + gender, credits, `last played`; the selected row framed; `NEW CHARACTER` (dark when five exist); `PLAY` (primary, dark with nothing selected); `SIGN OUT` (small) |
| Create (replaces the list) | `NAME` field; `GENDER` toggle `M / F`; `MODEL` toggle `COLONIST / VANGUARD` — the stage swaps as you toggle; `CREATE` (primary, dark until the name passes the client-side length rule), `CANCEL`; the server's reason under the buttons on 400/409 |

Rules:

- Entering the screen is `GET /api/characters` with the session; 401 →
  back to the launcher window in `Failed(SIGNED OUT · sign in again)`
  with both keys cleared; any other failure → `COULD NOT LOAD CHARACTERS
  · retry` with a RETRY button. One character: it is pre-selected. None:
  the create form opens at once.
- PLAY frees the stage, builds the world and connects with the row's
  token, exactly today's `Connect` from there on. SIGN OUT is the
  launcher's: `POST /api/logout`, keys cleared, launcher window back.
- `SignedIn` in the launcher shows `SIGNED IN · name@host` (there is no
  character yet to name); the character's name is the nametag.
- Rig runs (`-token`, `-uiPlayAfter`) skip the screen and connect as
  today — the server they talk to seats them (storeless or `SA_GUESTS=1`).
  `-uiChars <select|create|empty|loading|failed>` photographs it with
  fake rows.
- A `1008` close before the first snapshot (the character was deleted
  under you — only the site's account delete can, today) returns to the
  character select, reloaded.

#### Account column in the launcher

(Left, ~40 %), a pure `Login` model beside the update model, testable in
`-selftest`:

| State | Column | PLAY |
|---|---|---|
| `SignedOut` | `ACCOUNT`; `EMAIL`, `PASSWORD` (masked); `SIGN IN` (primary), `CREATE ACCOUNT` (small); `Sign in to play` | **disabled** |
| `Busy(what)` | fields disabled, `SIGNING IN…` / `CREATING ACCOUNT…` | disabled |
| `SignedIn(email)` | `SIGNED IN · name@host` (shortened with `…` past the column); `SIGN OUT` | per the update state |
| `Failed(reason)` | the `SignedOut` column with the reason under the buttons: `WRONG EMAIL OR PASSWORD` (401), `EMAIL ALREADY REGISTERED` (409), `PASSWORD TOO SHORT` (400), `TOO MANY TRIES · wait a moment` (429), `SIGNED OUT · sign in again` (a 401 on PLAY), `SITE UNREACHABLE` (anything else, detail in the log) | disabled |

- **PLAY = signed in AND the update state allows it.** Both gates.
- **Remembered.** A successful sign-in or sign-up writes the session and
  the email; the next launch opens in `SignedIn` with no request. The
  session is checked by the character list on PLAY, not at launch.
- **Enter** in either field submits SIGN IN. Each request has the update
  check's 5 s; longer is `Failed(SITE UNREACHABLE)`.
- **Site URL** is the derived one (`Launcher.SiteUrl`).
- **In the game** F1 no longer opens anything; the game menu loses its
  ACCOUNT row; the HUD, reconnect and in-session banner are untouched.

For photographs: `-uiLogin <signedout|busy|signedin|failed>` beside
`-uiLauncher` forces the column into a state with a fake email.

#### The machines

A server **without a store** (`DATABASE_URL` unset — the bare dev server,
every session ephemeral) has no accounts to check and seats anyone, and a
server started with `SA_GUESTS=1` (the kind overlay) does too, so the
harness fleet (26 of 29 tests join with a made-up token) and the rigs
keep joining as before and the login bucket never meets a fleet.
Production sets neither.

### Faces and hair (Phase 17)

Phase 16's COLONIST bodies (MakeHuman via MPFB2, 5600 tris, a painted
hair cap) read a class below the VANGUARD bodies (Quaternius UBC, 9000
tris, real hair). This phase closes the gap without making the two the
same body, and gives every character a **hair** choice.

**The Colonist, v2** (`char.player`, `char.player.f`):

- Same ceiling as the Vanguard: ≤ 9000 tris for body + arms + head, the
  face taking most of the new budget — a modelled brow ridge, cheekbones,
  jaw and nose, eyelids, lips; MPFB's higher-resolution head where the
  decimator is told to spend there and save on the torso.
- A different build, deliberately: eyes at **1.65 m** (the Vanguard's at
  1.70), slimmer and less muscular (MakeHuman `muscle` ~0.35–0.45,
  `weight` ~0.45), narrower shoulders. The camera rides the model's `eye`
  node, so the player sees from 1.65; the server's shot origin stays the
  shared 1.70 — a 5 cm offset, accepted and noted here, not a per-body
  server table.
- **Eyebrows** are the Quaternius `Eyebrows_Regular` / `Eyebrows_Female`
  meshes, refitted to the MakeHuman brow and weighted to the head bone,
  shipped inside the body glb as part of `head` (the client hides `head`
  for the local player; helmets cover `head`/`hair` as before).
- Everything the client and armor.py rely on is unchanged: bone names,
  the three meshes, the clips, `eye` and `hand.*` mounts, the `@body`
  armor variants (rebuilt for the new shape).

**Hair** is its own asset family, worn like armor:

- `hair.<style>@<body>` glbs — the Quaternius styles (`buzzed`,
  `buzzed_female`, `buns`, `long`, `simple_parted`, `beard`, and whatever
  else the pack's glTF folder holds), each fitted to each of the four
  heads and weighted to its head bone, built by `art/tools/bpy/hair.py`
  the way armor.py builds `<piece>@<body>`. Plus `hair.none`.
- A character row carries `hair` (`hair.none` when omitted; migration 006;
  the create form itself starts on `hair.buzzed` so a player who ignores
  the row is not bald).
  `POST /api/characters {name, body, hair}`; `GET` returns it; 400 `bad
  hair` for an id not in the table. The create form gets a HAIR row that
  cycles the styles valid for the body (the stage swaps live); the list
  row's line stays `COLONIST · F`.
- On the wire hair is a **worn slot**: at spawn the server sends the same
  `worn` event armor uses, slot `hair`, item `hair.<style>` — no spawn-row
  change. The client attaches it like a worn piece (`@wearer` variant
  lookup already exists) and helmets hide it through the existing
  `covers: hair` rule. Harness: `t28` creates a character with hair and
  sees the worn frame on both sockets.

### Edit a character (Phase 18)

A character's name and hair can change after creation; its body cannot
(make a new one). A character can be deleted.

- `PATCH /api/characters/<token>` `{name?, hair?}` — the bearer's own
  row only (a token owned by another account, or none, is 404); the same
  rules as create: 400 `bad name` / `bad hair`, 409 `name taken`
  (case-insensitive, other characters; a character may be renamed to its
  own name). Returns the row. The nametag follows on the next join (the
  entity is named from the row at `hello`); a connected session keeps its
  old name until then — accepted.
- `DELETE /api/characters/<token>` — the bearer's own row only (else
  404). The row goes; a connected session of that character is kicked as
  an account delete kicks (`Server.Kick`, 1008, no final save). Deleting
  the last character leaves an empty list: the select opens on the create
  form. Nothing else is touched — the account's other characters stay.

**The select** gains EDIT on the selected row (beside PLAY's row, small,
steel): the create form opens in **edit mode** — NAME prefilled, the
GENDER and MODEL rows hidden (the body is fixed), the HAIR row live on
the stage, SAVE (primary, dark until something changed and the name
passes) instead of CREATE, CANCEL, and a small Danger DELETE at the
bottom. DELETE asks once, inline: `DELETE <NAME>? THIS CANNOT BE UNDONE`
with CONFIRM (Danger) / KEEP. After SAVE the list shows the new name and
the stage the new hair, that row selected; after CONFIRM the row is gone
and the next row (or the create form) is selected.

Rig: `-uiChars edit` (the form in edit mode on fake row 0),
`-uiChars delete` (the confirm shown).

### First-person body, in the world (Phase 19)

Three attempts at the look-down view (#74, #82, #83) each hid a symptom:
pull the body back, cap the collar, draw legs only through an open hip
ring. The last one let the player look through their own hip ring at the
inside of their thigh plates. This phase settles it the way shooters do:
a **third-person model (3PM)** for everyone else and for shadows, and a
**first-person body (1PM)** drawn for the local camera — but, unlike a
viewmodel, drawn **in the world**: real depth, real sun, occluded by a
crate, standing on the ground at your real feet. What the 1PM changes is
only how it is *cut*: per pixel, by distance from the eye, never by the
near plane.

**Meshes.** The body glb splits into five: `head`, `arms`, `chest`
(shoulders, upper chest, neck ring — from the mid-chest line up),
`torso` (mid-chest down to the hips, **capped at the top** with a suit
face), `legs` (hips to feet). Remote players draw all five; cut edges are
shared boundaries, so nothing shows. The mid-chest line sits about 0.40 m
below the eye (roughly the sternum's lower third), so that even leaning
down the cap stays outside the near cut below.

**The local player draws** `torso`, `legs`, and the worn pieces on the
`chest`, `back`, `legs` and `feet` slots; `head`, `arms`, `chest`, hair
and `hands`/arm-covering pieces are shadows-only (the ground shadow is the
whole figure; the first-person arms are the separate instance as today).
Everything the local player draws uses the **near-cut material**: the
first-person PBR shader without the depth squeeze, plus a per-fragment
discard of anything within `NearCut` (0.30 m) of the eye, dithered over
0.05 m so the edge dissolves rather than slicing. It receives shadows
(the hidden head's shadow on your chest is yours) and casts none.

**Why both the cap and the cut.** The cut alone would open a window into
the hollow torso; the cap alone would clip at the near plane when you
lean. Together: the cap is the solid thing you see when you look down,
and the cut dissolves a chest plate's collar or a pauldron before it
reaches the near plane.

**Camera.** The pitch lean stays (`Fps.PlaceCamera`): from 20° down to
straight down the eye moves up to 0.30 m forward and 0.16 m up (inside
the body's 0.35 m radius). From behind the cap the cap hides the legs;
from in front of it you see the plate's top edge and the boots at −70,
and the cap or plate with a knee and both feet at −89. Thighs show only
near straight down — the belly is wider than the eye is forward.

**Two things the build taught.** The torso and legs cast no shadow (their
dither would punch holes in it), so each drawn mesh gets a shadows-only
twin on the same skeleton: the ground shadow is still the whole figure.
And the shadows-only `chest` sits right over the cap, so the cap's
up-facing fragments floor their shadow term at `CapShadowFloor` 0.65 —
a world shadow darkens the cap less than the legs beside it; accepted.

**What you see.** At −45: nothing of you, as before. At −70: your chest
plate and thighs below it, feet on the ground, the ground shadow of the
whole figure, a rock's shadow across your shins if you stand in one.
Straight down: the cap, the thighs, the feet. Never a ring, a hole, a
shard or a body drawn over the world.

Rig: `-uiPitch <deg>` as today; `-rigWorn slot=asset,…` dresses the guest
body for the armored case (asset ids).

### Character panel and backpack (Phase 11.7)

WoW's paper doll in this book's ink. **C** opens the character: the
equipment slots in two columns of six around a live model of the
character (its own viewport, turning slowly, lit for the panel, never by
the planet's sun), then STATS, then SKILLS. **B** opens the backpack: a
5×4 grid of `inv_slots` cells holding everything carried and not worn.

- **Slot cell**: 60 px square, `steel` fill, 2 px `ink` frame when empty
  with the slot's name in `dust` (ACC, WEAPON, SIDEARM); when filled a
  **3 px frame in the item's rarity colour**, the icon, and a stack
  count bottom-right. Icons are `art/icons/<item id>.png`, rendered from
  the item's model at build (`gen_icons.py`); an item with no model
  shows its initials in display type on the tile.
- **Moving things**: drag a cell onto a slot it fits (the slot refuses
  the drop otherwise), drag a worn slot onto the bag to take it off;
  right-click equips (accessories to the first free accessory slot) or
  unequips. Every move is one `equip`; the result's map redraws both
  panels. Nothing is optimistic.
- **Tooltip** on hover: name in rarity colour; tier · kind · slot;
  weapon numbers (damage, rpm, rounds, range) or armor value; stack;
  the item's `desc` line; the move hint.
- **STATS** are the numbers the game computes, never invented ones:
  health, armor (sum of worn `armor.value`), damage (weapon × Marksmanship),
  fire rate, sprint (× Athletics), rover/ship bonus, loot rolls, buy
  prices, discovery range, credits, bag. **SKILLS**: the ten, with level.
- Panels stack. C and B are meant to be open together (that is what
  dragging between them is for): the character sits left of centre by
  default, the backpack right. No key closes another panel.
- **Escape** closes everything that is open (panels, map, shop). With
  nothing open it opens the game menu: RETURN TO GAME, ACCOUNT, QUIT
  GAME. Escape again returns. There is no separate "free the cursor"
  key: an open panel frees it, closing captures it.
- **Lists are cards**: a `steel` card with a 2 px `ink` frame and a
  colour band on the left (rarity for items, the mission type's colour,
  `amber` for anything that wants an answer), a leading tile or item
  slot, a title over a `dust` subline, and prices/progress/buttons on the
  right. The shop, journal and party use nothing else.
- **Every panel drags by its header** and stays where it was put, per
  panel, across sessions (`user://sa.cfg` `[panels]`); at least the
  header always stays on screen.
- Placeholder gear: the Scout set (helmet, suit, leggings, gloves,
  boots, pack) and the Rabbit's Foot at the quartermaster, so slots have
  something to hold before armor matters.

### Motion

Panels: 120 ms slide+fade in, none out (closing is instant — snappy
beats smooth). Damage numbers as above. Nothing else animates; restraint
IS the budget (C65).

## World art style guide — structures and points of interest (Phase 9)

Buildings tell you who built them before you read a single label. Every
structure kit piece and every POI layout is reviewed against THIS
section; a piece that needs a color, module or rule not listed here adds
it here first — the UI style guide's contract, extended to the world.

### The two builders

Everything standing on the planet was built by one of two hands, and the
difference must read at silhouette range:

| | **Scrapyard** (hostile) | **Colony** (civilised) |
|---|---|---|
| who | raiders, squatters, the camp | the quartermaster, spawn, future vendors |
| shapes | leaning, asymmetric, welded-on | level, modular, repeated |
| edges | jagged toplines, exposed frames | clean copings, closed corners |
| color | rust `#8C4A2F`, scorch `#3A2E28`, hazard `#D9A013` stripes | hull `#B8BDC4`, panel `#5E6A75`, trim `#2F6E8C` |
| light | flame-amber `#FFAE19` point glows | cool white `#CFE8F2` strips |
| tell | one thing is always CROOKED | one thing is always SYMMETRIC |

Shared planet palette stays beneath both. The ground is flat-shaded
per triangle from the sampled height, in bands: crater dust
`#857866`/rust dust `#755C4D` below the 124–190 m range's 0.30 mark,
sage `#6E8C5C`/moss `#4F6B4A` plains around the 150 m datum, ochre
`#99825A`/dun `#80705C` highlands from 0.50, pale `#BDBAB3`/frost
`#9EA1A8` peaks from 0.72; scree `#5C4D42` on anything steeper than
about half the walkable limit. A seeded 3D noise (~30 m patches, the
world seed, client-side only) picks between each band's two colours, a
per-triangle ±6 % value jitter keeps a plain faceted, and the map draws
the same palette (`TerrainMesh.Shade`). `ink #10131A` outlines on
everything built (the comic line weight the UI already committed to).
Flat-shaded, vertex colors only, no textures — color changes happen at
polygon edges, which is what keeps tri budgets honest. Rocks: the 400
contract placements each get a warm-to-cool tint and 0–3 seated
pebbles from a second seeded stream, so the same three models do not
read as the same three rocks.

### Silhouette and the 23 m horizon

Eye height on a 150 m planet sees the ground vanish at ~23 m. A POI
that cannot be seen over the curve does not exist as gameplay. So:

- **Every POI carries exactly one mast** — its tallest element, a
  distinct silhouette per POI kind (dish, chimney, antenna cluster,
  watchtower). Height sets discovery range:
  `visible ≈ 22.6 m + √(300·h)` → 6 m mast ≈ 65 m, 12 m ≈ 83 m,
  20 m ≈ 100 m. Standard POI mast: **10–14 m**. Nothing else at the
  POI exceeds half the mast — one skyline spike per site, so two POIs
  are never confused on the horizon.
- Masts glow: a 1-triangle emissive-colored tip in the faction light
  color. At night (most of the planet, most of the time) the tip IS the
  landmark.
- The compass strip names POIs the moment their mast would be visible,
  not before — discovery matches sight.

### The kit of parts (no more stretched boxes)

The camp's walls today are ONE unit box scaled to 32 m — bands smear,
corners interpenetrate, geometry clips. The kit replaces stretching
with **tiling**:

- **Module grid: 4 m.** Every kit piece occupies a whole number of 4 m
  cells in plan. A 32 m wall is 8 wall modules, not one stretched box.
- Pieces (per faction skin, same footprints): `wall4` (4×0.8×3 m),
  `corner` (post, 1.2×1.2×3.6 m — corners belong to posts, walls BUTT
  INTO them and never meet each other), `gate4` (wall with a 2.4 m
  opening), `tower` (2×2 cell, 8 m), `mast` (1 cell, 10–14 m), `hab`
  (2×2 cell closed hut, Colony), `shack` (2×2, leaning, Scrapyard),
  plus the existing props (barrels, crates, generator, dish, bones).
- **Ground skirt rule**: every piece's base extends 0.3 m below y=0 as
  a plinth wider than the piece — the flatten disc is never perfectly
  flat at the falloff band, and the skirt is what hides the seam. No
  piece may show its underside from any standing viewpoint.
- **Clipping rules**: pieces may only touch at grid faces; nothing
  interpenetrates. Decorative lean (Scrapyard) happens INSIDE a piece's
  own cell, never across a boundary. The verify gate checks kit GLBs
  fit their declared cell bounds.
- Budgets: wall/corner/gate ≤ 120 tris, tower/hab/shack ≤ 400, mast
  ≤ 200. A whole POI including props stays under 6,000.

Colliders remain the server's, authored per zone as today — the kit is
VISUAL. The mapping rule inverts though: the zone names kit placements
and the colliders are derived from the same layout data, so the wall
you see and the wall you hit cannot drift apart.

### POI anatomy (what the solver places)

Every POI template declares, in its local frame:

1. **The mast** (landmark, see above) — at the site's visual center.
2. **An approach** — one obvious opening facing the likeliest arrival
   bearing; the solver rotates the template so the gate faces the
   nearest travel corridor (spawn, road, or neighbouring POI).
3. **A cover ring** — waist-high (1.2 m) pieces between the opening and
   the core, spaced 3–6 m, so the fight has geometry: no naked charge,
   no safe snipe.
4. **The core** — what you came for: loot crates, a vendor, an
   objective marker. Never visible from outside the walls; the POI
   must be ENTERED.
5. **Dressing density**: 1 prop cluster per 8×8 m cell, minimum 3
   clusters — an empty compound reads as unfinished, not abandoned.

### Placement (the solver's contract)

The manual site-sweeps in camp.json/range.json become code:

- Clearance: a site must clear every world feature (spawn, zones,
  landmarks, craters, other POIs) by `flatten_radius + flatten_falloff
  + 10 m` — the camp.json lesson ("anything the terrain audit checks
  for has to be in the avoid set") as an algorithm.
- Slope: mean slope over the flatten disc under 12° before flattening.
- Spacing: POI masts at least 120 m apart along the surface, so at most
  one skyline spike per view.
- Determinism: solver runs from the world seed; same seed, same world.
  Output is committed zone JSON — reviewed, not runtime magic.

## Missions and parties (Phase 10)

The world got places worth walking to (Phase 9); missions are the reasons
to walk. Two shapes: PERSONAL missions everyone can hold at once, and
LIMITED missions — bounties — that exactly one party claims at a time.
Both are server-authoritative end to end: the client renders offers,
progress and rewards; it asserts none of them.

### Parties

- **Max 4, session-scoped, leaderless.** Any member may invite; anyone
  may leave; the party dissolves when one member remains. Nothing about
  a party persists — reconnecting means re-inviting, which at this scale
  is a feature, not a gap.
- **Forming**: two paths to the same cmd. Look at a player within
  interact range and press E ("invite to party"), or open the party
  panel (P) and invite from the nearby-player roster. The invitee gets
  a HUD toast and accepts or declines from the panel. One pending
  invite per player; a newer one replaces it.
- **Credit**: a qualifying action by ANY member progresses the mission
  for EVERY member who holds it, wherever they stand — that is what the
  formal party buys over proximity. Completion pays each holder the
  full reward; the economy eats the inflation in exchange for "playing
  together always feels good".

### Mission types

| type | objective | progress source | completes |
|---|---|---|---|
| `kill` | kill N hostiles (optionally of one archetype) | death events the server already emits | on the Nth kill |
| `scout` | visit a named POI | entering the POI's mast-discovery radius (the compass math, server-side) | on entry |
| `fetch` | hold N of an item collected from drops | loot pickups | at the board — turn-in CONSUMES the items |
| `bounty` | kill THE named NPC | that entity's death | on the kill |

Templates live in `server/data/missions.json` (id, type, params, credit
reward) — data, like items and npcs. Personal missions are repeatable
after completion (the board re-offers them); a cooldown is a template
field, default none.

**Sharing**: a member may push any held, active, non-bounty mission to
party members who lack it — from anywhere, no board needed; being in
the party is the authorisation. Recipients start at zero progress and
get the full template with the notification (their journal may never
have seen a board). Bounties are excluded: their membership follows
the party through the claim machine.

### The board

`npc.dispatcher` stands at the relay — the Colony POI's purpose. E to
talk, the same interaction cone and range as the quartermaster; the
journal-style board lists offered templates and the player's active
missions. Fetch missions turn in here. The quartermaster also offers
the starter kill mission, so the loop is teachable without leaving
spawn.

### Bounties — the limited missions

- **One live at a time.** The mission system spawns a named bounty NPC
  (`npc.warlord`: tougher, meaner, generous loot) at a solver-eligible
  POI, and BROADCASTS a `priority_offer` — every HUD raises the toast,
  the journal shows it, anyone may accept from anywhere. First
  `mission_accept` wins; everyone else is refused with `"claimed"`.
- **The claim belongs to the accepting player's party, and FOLLOWS
  it**: anyone who shares a party with an original claimant at the
  moment of the kill counts as a claimant — a member who joins after
  the accept completes the contract rather than stealing it, and the
  pay goes to the claimant party's current members, full reward each.
- **Release**: completion, explicit abandon by all claimants, every
  claimant disconnecting, or a **15-minute expiry** — then the warlord
  despawns and, after a ~2-minute cooldown, the system posts a fresh
  bounty (fresh NPC, possibly another POI). Nobody squats a bounty;
  content never wedges.
- The bounty NPC is a real entity in the world: non-claimants can see
  it, get shot by it, even kill it — but only claimants get the mission
  credit (the kill still releases the claim; the world is not obligated
  to be fair to snipers, and a stolen kill re-posts the bounty).

### State and authority

Per-player mission state (active missions + counts + completion
history) persists on the player row beside credits and inventory.
Parties and bounty claims are in-memory — they die with the server,
which at worst re-posts a bounty. Every transition is a cmd the server
validates or an event the server emits; the client's journal is a view.

## Skills (Phase 11 core, Phase 12 artisan)

RuneScape's shape: many individual skills, each trained BY DOING its
thing, each with its own exponential ladder to 99. No classes, no
points to allocate — what you did is what you are. Levels both scale
numbers (efficacy) and open doors (unlocks), some skills feed each
other (declared synergies), and the whole sheet is the character.

### The roster

Ten skills. Seven train in Phase 11 against verbs the game already
has; three were RESERVED (greyed at 1 on the panel) until Phase 12's
artisan loop, contracted below, gave them verbs.

| skill | trains by | efficacy (per level, linear to 99) | unlocks (data-driven) |
|---|---|---|---|
| **Marksmanship** | damage dealt (2 XP/point), kills (+40, warlord +400) | +0.4% weapon damage | future weapon tiers |
| **Athletics** | metres sprinted (1 XP/10 m), jumps landed (1 XP) | +0.15% sprint speed | — |
| **Driving** | metres driven as the driver (1 XP/10 m) | +0.3% rover accel & grip | future vehicle tiers |
| **Piloting** | metres flown as pilot (1 XP/10 m), clean landings (+50) | +0.3% ship handling | future ship tiers |
| **Scavenging** | loot pickups (10 XP × qty) | +0.3% extra-roll chance on loot tables | rare-drop table at 30 |
| **Commerce** | credits moved at shops (1 XP / 5 cr, buy or sell) | −0.2% buy prices (cap −19.6%) | future vendor stock tiers |
| **Recon** | first discovery of each POI (+250, permanent per player), scout missions (+100) | +0.5% compass discovery range | — |
| **Mining** | Phase 12: node yields (node `xp`) | −0.5% channel time | nodes by `level` |
| **Salvaging** | Phase 12: wreck yields (node `xp`) | −0.5% channel time | nodes by `level` |
| **Engineering** | Phase 12: crafts (recipe `xp`) | +0.3% extra-output chance | recipes by `level` |

### The curve — RuneScape's, exactly

`points(L) = floor( Σ_{l=1}^{L−1} (l + 300·2^(l/7)) / 4 )` — level 2 at
83 XP, 50 at 101,333, 99 at 13,034,431, and level 92 is the halfway
point of 99, as tradition demands. Award rates above are tuned so a
regular player's ceiling is around 50; 99 in anything is a monument.
The table is FROZEN — both ends compute it from the formula, and a
unit test pins the landmark values forever.

### Efficacy and the two sims (the load-bearing constraint)

Combat, prices and loot roll server-side only — free. **Movement
multipliers enter the simulation**, and the sim is mirrored (C30/C34
conformance at 1e-6), so they must be deterministic on BOTH ends: the
server computes the player's multipliers from their levels, ships them
in the skills sheet (and on every level-up event), and the client's
predictor applies the SAME numbers. Conformance suites gain cases at
non-unit multipliers. A fresh player's multipliers are exactly 1.0, so
every existing test and every existing player behaves identically —
skills change nothing until trained.

### Synergies — declared, visible

Data, not lore (`skills.json`): each synergy names source, target,
per-level bonus, and where it applies. Launch set:

- **Recon → Scavenging**: +0.2%/Recon-level extra loot yield inside a
  discovered POI's radius — knowing the ground pays.
- **Athletics → Driving**: +0.1%/Athletics-level rover grip — a fit
  body drives harder.
- **Scavenging → Commerce**: +0.1%/Scavenging-level better sell
  prices — knowing what junk is worth.
- **Engineering → Mining** (Phase 12): +0.1%/Engineering-level faster
  drill channels — a better hand with the drill.

The skills panel draws the arrows; training one visibly moves its
partner's tooltip.

### On the wire, on the sheet

XP awards BATCH server-side (flush ≤1/s per skill) into a `skill_xp`
event: `{skill, xp, level, next_at, leveled}`. `leveled: true` raises
the banner. The full sheet (XP per skill + derived multipliers +
discovered POIs) answers a `skills` cmd and persists beside inventory
(migration 004). The panel is **K**: ten rows, level, progress bar to
next, synergy arrows, multiplier tooltips. Reserved skills render
greyed.

### Phase 12 — the artisan loop (contract, wave 0, 2026-09-29)

The three reserved skills come alive. Mining drills ore out of nodes,
Salvaging cuts scrap out of wrecks, Engineering turns both into things
at a workbench, and `shop_sell` turns anything with a value into
credits. **Travel is the loop**: ore lies in the rocks around the pad,
wrecks lie inside the guarded scrapyard, the bench stands at the relay,
the buyer stands at the pad. Raw materials — and only raw materials —
spill where you die. Everything below is data; the verbs are
`shop_sell`, `gather`, `gather_cancel`, `craft` (PROTOCOL).

#### Nodes

A **node** is a static world entity (`entity_type` node, PROTOCOL) placed
by a zone file like an NPC (`{type:"node", def, pos, yaw}`), defined in
`server/data/nodes.json`. Its snapshot `health` IS its remaining yields:
`0` reads as depleted and the client draws it dark and shrunk. A node is
interactable with the verb its skill names (`Drill` / `Cut`), under the
ordinary interaction rules (`interact_dist`, `interact_cone`).

| field | meaning |
|---|---|
| `id` | `node.ore.iron`, `node.ore.copper`, `node.wreck` |
| `skill` | `mining` or `salvaging` — trained by, gated by |
| `level` | minimum level in `skill`; below it `gather` refuses `locked` |
| `tool` | item id that must sit in the `tool` equip slot; `tool.drill`, `tool.cutter`, `tool.drill.mk2` (mk2 satisfies any node asking for `tool.drill`) |
| `channel` | seconds the drill channel takes at level 1 |
| `yields` | yields before depletion (= spawn health) |
| `respawn` | seconds depleted → full |
| `loot` | loot table (`loot.json`) rolled once per yield |
| `xp` | XP in `skill` per yield |

| node | skill | level | tool | channel | yields | respawn | loot (guaranteed) | xp | where |
|---|---|---|---|---|---|---|---|---|---|
| `node.ore.iron` | mining | 1 | `tool.drill` | 3.0 s | 5 | 90 s | `mat.ore.iron` ×2 | 25 | spawn (×3) |
| `node.ore.copper` | mining | 10 | `tool.drill.mk2` | 4.0 s | 4 | 120 s | `mat.ore.copper` ×2 | 60 | spawn (×1) |
| `node.wreck` | salvaging | 1 | `tool.cutter` | 3.0 s | 4 | 120 s | `mat.scrap` ×3 | 30 | outpost (×2, inside the walls) |

#### The channel

`gather {node}` starts a **server-timed channel**; nothing is
predicted. Validation, in order: node exists (status 5) → in range and
cone (`out_of_range`) → not already channelling (`busy`) → alive → the
node's `tool` (or a tool that supersedes it) is in the `tool` slot
(`no_tool`) → `skill` level ≥ `level` (`locked`) → node `health > 0`
(`depleted`) → the yield would fit the bag (`no_space`, checked again at
the end). The result carries `{"node", "duration"}` — the channel
length in seconds AFTER efficacy — and the client draws a bar for
exactly that long. The channel ends when:

- **`duration` elapses** → the yield: the node's `loot` table rolls once,
  the items go straight into the bag (never on the ground — a mined ore
  is yours), node `health −= 1`, `xp` is awarded in `skill`, and a
  `gather_end` event (`reason: "done"`, with `item`/`qty`) is unicast.
  At `health 0` the node starts its `respawn` timer; at zero it is full
  again. Everyone sees the depletion through the snapshot.
- **The player moves** more than `gather_move_tol` from where the channel
  started, **takes damage**, **dies**, **disconnects**, or sends
  `gather_cancel {}` → `gather_end` with `reason` `moved` / `hit` / `died`
  / `cancel`; nothing is granted, the node is untouched. (Disconnect
  sends nothing.)
- Someone else depletes the node first → `reason: "depleted"`.

Two players may channel the same node; each yield is a separate `−1`.
Nothing in the sim reads gathering: no conformance case changes.

| param | value | note |
|---|---|---|
| `gather_move_tol` | 0.5 m | measured from the channel's start position |
| `gather_min_channel` | 1.0 s | floor after efficacy |

#### Efficacy, unlocks, synergy

| skill | trains by | efficacy (per level, linear) | unlocks (data) |
|---|---|---|---|
| **Mining** | yields (node `xp`) | −0.5% channel time (`gather_speed`) | nodes by `level` |
| **Salvaging** | yields (node `xp`) | −0.5% channel time (`gather_speed`) | nodes by `level` |
| **Engineering** | crafts (recipe `xp`) | +0.3% chance of one extra output unit (`craft_extra`) | recipes by `level`; `tool.drill.mk2` through `unlock_requirements` |

The roster rows above lose their italics; `skills.json` drops the
`reserved` flag on all three and gains their `efficacy` rows. Channel
time = `channel × (1 − bonus)`, floored at `gather_min_channel`.

One new synergy: **Engineering → Mining**, `gather_speed`
+0.1%/Engineering-level — a better hand with the drill. It appears on
the panel like the other three.

#### Materials, tools, and the bag

New item `kind` **`material`** (`mat.ore.iron`, `mat.ore.copper`,
`mat.scrap`): stackable, sellable, spills on death, crafts into things.
New item `kind` **`tool`** with `slot: "tool"` — the first residents of
that slot. Every item may carry a **`value`** (integer credits):
`shop_sell` pays `value × sell_rate`, and an item without `value`
cannot be sold (`unsellable`) — ships, mission rewards, and anything
the design does not want liquid simply have none.

| item | kind | slot | stack | value | price (quartermaster) | note |
|---|---|---|---|---|---|---|
| `tool.drill` | tool | tool | 1 | 40 | 120 | common; drills `node.ore.iron` |
| `tool.cutter` | tool | tool | 1 | 60 | 180 | common; cuts `node.wreck` |
| `tool.drill.mk2` | tool | tool | 1 | 150 | — (crafted) | uncommon; `supersedes: "tool.drill"` |
| `mat.ore.iron` | material | — | 50 | 6 | — | common |
| `mat.ore.copper` | material | — | 50 | 14 | — | uncommon |
| `mat.scrap` | material | — | 50 | 8 | — | common |
| `armor.plate.iron` | armor | chest | 1 | 90 | — (crafted) | rare, `armor 14` — beats the Scout Suit's 8 |

Existing items gain a `value` too: `weapon.pulse` 80, the Scout pieces
one third of their price rounded down, `charm.rabbit` 50. `ammo.cell`
and `ship.v1` get none — cells are made, not sold, and a ship is not
pocket change.

#### `shop_sell`

`shop_sell {npc, item, qty}` at any `shop` NPC (the quartermaster and
the dispatcher both buy; the bench does not). Validation: NPC is a shop
and in range → `qty` in 1..1000 (`bad_qty`) → item known → item has a
`value` (`unsellable`) → `qty` owned in the bag (`not_owned`) → the item
is not worn (`equipped`). Then, atomically: the stacks shrink, credits
grow by `floor(qty × value × sell_rate × (1 + sell_bonus))`, where
`sell_bonus` is the resolved Scavenging→Commerce synergy — the synergy
that has waited since Phase 11. Commerce trains on the credits moved
(1 XP / 5 cr, as buying). Result: `{"credits", "inventory"}`, the same
shape as `shop_buy`.

**Selling is a bag gesture, not a panel** (Phase 13, WoW's rule): with
a shop open, right-click on a bag stack sells one, Shift-right-click
the stack; the backpack's hint line says so while a shop is open. The
shop panel has two tabs, STOCK and BUYBACK. **Buyback**: every sale of
this connection goes on a list (newest last, twelve kept, gone at
logout — the shop has moved it on) as `{item, qty, price}` with the
credits the shop paid; `shop_list` carries the list, `shop_buyback
{npc, item}` returns the newest sale of that item whole for exactly
that price at any shop (`no_buyback` when none, else the buy
refusals), and the entry leaves the list only on success.

| param | value |
|---|---|
| `sell_rate` | 0.5 |

#### The workbench and recipes

The **workbench** is an NPC archetype (`npc.workbench`, `kind: "bench"`,
verb `Use`, asset `prop.bench`) placed at the relay beside the
dispatcher — shop-shaped, no health, no stock. It is the only place
`craft` works. Recipes live in `server/data/recipes.json` and ship in
`defs.recipes`:

| field | meaning |
|---|---|
| `id` | `recipe.cells`, `recipe.plate.iron`, `recipe.drill.mk2` |
| `level` | minimum Engineering level, else `locked` |
| `inputs` | `[{item, qty}]`, all consumed |
| `output` | `{item, qty}` |
| `xp` | Engineering XP per craft |

| recipe | level | inputs | output | xp |
|---|---|---|---|---|
| `recipe.cells` | 1 | `mat.ore.iron` ×2, `mat.scrap` ×1 | `ammo.cell` ×30 | 20 |
| `recipe.plate.iron` | 5 | `mat.ore.iron` ×6, `mat.scrap` ×3 | `armor.plate.iron` ×1 | 120 |
| `recipe.drill.mk2` | 10 | `mat.ore.iron` ×8, `mat.scrap` ×6, `tool.drill` ×1 | `tool.drill.mk2` ×1 | 300 |

(`recipe.drill.mk2` consumes the old drill; copper is what the mk2
mines, not what it costs.) `craft {npc, recipe, qty}` validates: bench
in range (`out_of_range`) → recipe known (`unknown_recipe`) → level
(`locked`) → every input × qty owned (`missing_materials`) → the output
fits after the inputs leave (`no_space`). Crafting is **instant** — the
travel to the bench is the time cost — and atomic per call; `qty` crafts
happen as one transaction or none. Each craft rolls `craft_extra` once
for one bonus output unit (stackable outputs only — a second chest
plate has nowhere to go). Result: `{"inventory", "crafted": {item,
qty}}`. Medkits, the scanner and weapon mods arrive with Phase 13's
`use` verb and `mod` slot.

#### Death spills the raw

Amends "Death and respawn": on death, every `material` stack leaves the
bag and becomes a loot drop where the body fell — one drop per stack,
ordinary `loot_dropped` events, ordinary walk-over pickup by **anyone**,
ordinary `loot_lifetime` (120 s; this phase makes the lifetime real —
drops have never expired). Equipment, credits, tools, ammo, everything
else stays. Run back and you have it; someone else ran first and they
have it. Scavenging XP applies to the re-pickup like any pickup.

#### Client

- Nodes render from `defs.nodes[].asset` (`prop.node.ore.iron`,
  `prop.node.ore.copper`, `prop.wreck`); `health 0` draws the same model
  at 0.6 scale in the depleted tint. Prompt: `E · drill` / `E · cut`;
  bench: `E · use`.
- A **channel bar** under the crosshair (`Styles.Progress`) fills over
  `duration`; `gather_end` clears it and, on `done`, drips the yield
  into the HUD like a pickup.
- **Bench panel** (modal, same framework as the shop): one card per
  recipe — icon, name, inputs with have/need in red when short, level
  requirement greyed when locked, CRAFT button; the bag's material
  counts along the top.
- **Shop panel** gains a SELL column: sellable stacks with unit price
  (synergy applied), click sells one, shift-click the stack.
- Skills panel: the three rows un-grey, Engineering→Mining arrow.
- Tool slot on the character doll shows the tool's icon like any slot.

### Phase 13 — use, modify, and the hotbar (contract, wave 0, 2026-09-30)

Three things Phase 12 left on the bench: a **`use` verb** for
consumables and gear abilities, **weapon mods** that make Engineering's
copper tiers worth reaching, and a **hotbar** across the bottom of the
screen that the player fills by dragging. The first ability is a
gadget, server-side and instant; abilities that enter the movement sim
(a hover on a pair of boots) ride the same framework later, when both
sims and the conformance suite are extended for them.

#### `use`

`use {item}` (PROTOCOL `0x0013`) does one of two things, decided by the
item's def:

- A **consumable** (`kind: "consumable"`, def carries `consumable`): one
  unit leaves the bag and its effect applies. Refused `not_owned` when
  none is carried.
- **Worn gear with an ability** (def carries `ability`): the ability
  fires. Refused `not_owned` unless the item sits in an equip slot.

Common rules, in order: item known (`unknown_item`) → item has a
`consumable` or `ability` block (`unusable`) → owned/worn as above →
alive (`dead`) → not on cooldown (`cooldown`, with `"ready_in"` seconds
in the refusal body) → effect-specific checks. Using anything ends a
running gather channel (`gather_end` `cancel`). Cooldowns are
per-connection, per item id, server-side; the result carries
`"cooldown"` seconds so the bar can draw the sweep without a clock of
its own. Result: `{"item", "effect": {…}, "cooldown": s}`; the `effect`
shape is the item's.

| item | kind | block | effect | cooldown | value | recipe (Eng level, inputs, xp) |
|---|---|---|---|---|---|---|
| `consumable.medkit` | consumable, stack 10, common | `consumable: {heal: 50, cooldown: 8}` | +50 health, capped at max; refused `no_effect` at full health; `effect: {"health": <after>}` | 8 s | 12 | 3: `mat.ore.iron` ×1, `mat.scrap` ×2 → ×2, xp 40; also stocked by the quartermaster at 30 cr — a shop sells bandages |
| `gadget.scanner` | gadget, slot `gadget`, stack 1, rare | `ability: {id: "scan", range: 120, cooldown: 30}` | every node and loot drop within `range` of the player: `effect: {"pings": [{"id", "def", "pos": [x,y,z], "health"}]}` — the compass shows them for `scan_show` seconds | 30 s | 120 | 6: `mat.ore.iron` ×4, `mat.scrap` ×4 → ×1, xp 150 |

| param | value |
|---|---|
| `scan_show` | 20 s (client) |

#### Weapon mods

A **mod** is an item of `kind: "mod"` with `slot: "mod"` — a new entry
in `equip_slots`, drawn on the character panel under WEAPON — whose def
carries `mod: {…}` deltas applied to whatever `primary` is worn. One
mod at a time; the mod stays worn when the rifle comes off and applies
to the next one. The server applies the deltas everywhere the weapon
table is read (`ResolveShot`, `reload`, the fire-interval check), the
client applies the same deltas to the DAMAGE / FIRE RATE / MAGAZINE /
RANGE stats and its magazine display. Deltas add; nothing multiplies.

| item | rarity | `mod` deltas | value | recipe (Eng level, inputs, xp) |
|---|---|---|---|---|
| `mod.barrel` | rare | `max_range +40`, `falloff_start +20`, `falloff_end +40` | 90 | 12: `mat.ore.copper` ×4, `mat.scrap` ×4, xp 250 |
| `mod.mag` | rare | `magazine +10` | 90 | 12: `mat.ore.copper` ×3, `mat.scrap` ×6, xp 250 |
| `mod.coil` | epic | `damage +5` | 160 | 15: `mat.ore.copper` ×6, `mat.ore.iron` ×4, xp 400 |

A worn rifle keeps its loaded rounds when a mag mod comes off: the
magazine is clamped to the new size on the next reload, never emptied.

#### The hotbar

Ten cells across the bottom centre of the HUD, one row, keys **1 2 3 4
5 Q E T Z X**. A second row on the same keys under **Shift** (`⇧1` …
`⇧X`, twenty slots) is built and saved but **switched off for now**
(`Hotbar.ShiftRow`): Shift is sprint, and a hotkey mid-sprint firing
the second row read as a misfire in play. **F interacts** and **R
reloads** (when a gun is worn); neither is on the bar. Shift is also sprint; a hotkey
pressed while sprinting fires the shift row, which is what a modifier
means. A slot holds a **reference**, not a thing:

| kind | what it points at | fires | drawn |
|---|---|---|---|
| `item` | a consumable item id | `use {item}` | icon, bag count bottom-right; greyed at 0 |
| `ability` | a worn item id whose def has `ability` | `use {item}` | icon; greyed when not worn |

Defaults on first run: every slot empty. Filling the bar: drag from the backpack (a consumable) or the
character panel (worn gear with an ability) onto a cell; drag cell to
cell moves; right-click clears; dropping onto an occupied cell replaces
(the old reference is dropped, never swapped — a bar is not a bag).
Layout persists in `user://sa.cfg` `[hotbar]` as twenty `kind:id`
strings — client state, never on the wire; a second machine starts
with the defaults.

A cell on cooldown draws a dark sweep draining over the `cooldown`
seconds the result carried, and a key press during it does nothing
locally (the server would refuse anyway). Keys fire with panels open
except while a text field has focus. The world prompt reads
`F · drill iron node`.

#### Client

- HUD: the bar, key labels, counts, cooldown sweeps, the shift row
  swap while Shift is held.
- Backpack: consumables draggable to the bar (the existing `ItemSlot`
  drag source with a new drop target); right-click USE on a consumable
  as a keyboard-free path.
- Character panel: MOD slot under WEAPON; DAMAGE / FIRE RATE /
  MAGAZINE / RANGE stats reflect the worn mod; a worn gadget draggable
  to the bar.
- Compass: scan pings as node/drop markers for `scan_show` seconds,
  one toast `scan: 3 nodes, 1 drop`.
- Bench: five new recipes on the same cards.

## Phase 2 — items, weapons, combat, interaction

Spec for `netcode` + `frontend`, same contract status as the on-foot rules
above: every number lives in a table, every rule is implementable from this
section alone. Phase 2's playable proof is buying a rifle from an NPC and
shooting a target range (`docs/ROADMAP.md`).

### Items and currency

- **Credits** are a single integer on the player row. Starting balance
  `start_credits`. There is no banking or trading; credits move through
  `shop_buy` and, from Phase 12, `shop_sell`.
- An **item id** is a string defined in `server/data/items.json` (`weapon.pulse`,
  `ammo.cell`). The client never invents one; it learns the table from `defs`.
- **Inventory** is a list of `{item, qty}` stacks on the player row, at most
  `inv_slots` stacks, each capped at that item's `stack_max`. A purchase that
  would exceed either is refused (`no_space`).
- **Equipment** is one map, `{slot: item}`. The slot set is data
  (`items.json` `equip_slots`, Phase 11.7): `head`, `chest`, `legs`,
  `hands`, `feet`, `back`, `accessory1`, `accessory2`, `primary`,
  `secondary`, `tool`, `gadget`, and from Phase 13 `mod` (a weapon mod,
  applied to whatever `primary` is worn). An item declares the slot it fits; an
  `accessory` item fits either accessory slot; one stack of one sits in
  one slot at a time. `equip` with an empty item clears a slot. `primary`
  and the worn slots (`head`, `chest`, `legs`, `hands`, `feet`, `back`) are
  visible on the body and on the wire to others (`equipped` and `worn`
  events, PROTOCOL.md); armor values of every worn item are summed
  (the character panel's ARMOR) and scale each incoming hit by
  100 / (100 + armor), rounded, never below 1 (`sim.Mitigate`): full Scout
  (23) takes ~19% off, full Bulwark (46) ~32%. Equipping an item you do not own is refused.
- Ammunition is an item like any other. The weapon's `magazine` is *not* in the
  inventory — it is loaded rounds, held per equipped weapon; `reload` moves
  rounds from the `ammo.cell` stack into the magazine.

| Param | Value | Note |
|---|---|---|
| `start_credits` | 1000 | new player row |
| `start_items` | `ammo.cell × 120` | new player row; the rifle is bought, not given |
| `inv_slots` | 20 | stacks, not items |
| `stack_max` (weapon) | 1 | weapons do not stack |
| `stack_max` (ammo) | 300 | |

### Weapons (rule table)

Phase 2 ships one weapon. It is **hitscan** — no projectile, no travel time, no
drop. NPC projectiles arrive in Phase 3 and are a separate entity type.

| Param | `weapon.pulse` | Unit | Note |
|---|---|---|---|
| `price` | 250 | credits | |
| `damage` | 25 | hp | before falloff |
| `fire_interval` | 0.15 | s | 400 rpm; server-enforced with 1 tick tolerance |
| `magazine` | 30 | rounds | |
| `reload_time` | 2.0 | s | cannot fire while reloading |
| `ammo_item` | `ammo.cell` | — | |
| `max_range` | 120 | m | beyond this the ray misses, full stop |
| `falloff_start` | 40 | m | full damage at or below |
| `falloff_end` | 120 | m | |
| `falloff_min` | 0.35 | × | damage multiplier at `falloff_end` |
| `spread_base` | 0.6 | deg | cone half-angle at rest |
| `spread_max` | 2.5 | deg | |
| `spread_per_shot` | 0.35 | deg | added per shot fired |
| `spread_decay` | 3.0 | deg/s | recovery toward `spread_base` |

**The armoury (2026-10-01).** Three more guns on the same schema, all
sold by the quartermaster and all firing `ammo.cell`:

| Param | `weapon.sidearm` | `weapon.smg` | `weapon.dmr` |
|---|---|---|---|
| name | Pocket Pulser | Scrap SMG | Longshot DMR |
| `price` | 120 | 220 | 480 |
| `damage` | 20 | 14 | 60 |
| `fire_interval` | 0.22 | 0.075 | 0.45 |
| `magazine` | 12 | 40 | 10 |
| `reload_time` | 1.4 | 1.8 | 2.6 |
| `max_range` | 60 | 70 | 250 |
| `falloff_start` / `end` / `min` | 20 / 60 / 0.3 | 15 / 60 / 0.3 | 120 / 250 / 0.6 |
| `spread_base` / `max` / `per_shot` / `decay` | 0.8 / 2.5 / 0.5 / 4 | 1.0 / 3.5 / 0.3 / 4 | 0.15 / 1.2 / 0.8 / 2 |
| `class` | `pistol` | — | — |

`weapon.class` (optional) is the hold family. It is empty for a long gun and
`pistol` for the sidearm, and it picks the body's `*_pistol` clips. The
client paces its own trigger by the held weapon's `fire_interval`.

Damage is `round(damage · falloff)`, with `falloff` linearly interpolated from
`1.0` at `falloff_start` to `falloff_min` at `falloff_end`, clamped at both ends.

**Spread is resolved server-side and is not predicted.** The client draws its
crosshair from the same numbers so the cone is honest, but the actual deviation
applied to `dir` is drawn from the server's per-connection RNG. A client-chosen
deviation is a client-chosen hit.

**No headshots in Phase 2.** One hitbox per entity (below). Split hitboxes and a
head multiplier land in Phase 3 with enemies worth aiming at — they change every
damage number, and doing that once, against real enemies, beats doing it twice.

### Melee (2026-10-03)

A body carries a gun in `primary` and a **hand weapon in `melee`** (the
equip slot `secondary` became `melee`; nothing had used it). **Tab** swaps
which is in the hand (`wield`, PROTOCOL `0x0015`); with no gun worn the
blade is in hand anyway. The left mouse button does whatever the hand holds:
shoot, or swing. No physics: a swing is an **area check** — every
damageable world entity whose capsule comes within `range` of the swinger's
chest (1.1 m up) and lies inside `arc` degrees of the facing, flattened onto
the ground, takes `damage` (`sim.InArc`). `arc` 360 hits everything around
you. Cadence is `interval`, server-enforced like `fire_interval`; no
ammunition, no rewind (targets at their current positions — at 2–3 m reach
the lag reads fine). Players are not hit (no PvP). Damage trains Athletics.

| item | name | hands | `damage` | `interval` s | `range` m | `arc` ° | price |
|---|---|---|---|---|---|---|---|
| `melee.dagger` | Shiv | 1 | 16 | 0.4 | 1.8 | 70 | 40 |
| `melee.sword` | Cutlass | 1 | 28 | 0.6 | 2.2 | 100 | 110 |
| `melee.axe` | Hatchet | 1 | 32 | 0.7 | 2.0 | 90 | 110 |
| `melee.greatsword` | Greatsword | 2 | 48 | 1.0 | 2.8 | 150 | 280 |
| `melee.greataxe` | Cleaver | 2 | 56 | 1.1 | 2.6 | 130 | 300 |
| `melee.hammer` | Maul | 2 | 42 | 1.3 | 2.6 | 360 | 420 |

The hold family is `_melee` (one hand) or `_melee2h`; the swing clip is
`attack` (`attack_spin` for a 360° weapon) with that suffix, `fp_attack…`
in first person. The weapon rides the fist: blade out of the thumb side
along the knuckle row, edge toward the knuckles (`human.py blade_frame`,
the client's `EntityViews.BladeFrame` — one rule, so the clip is the hold).

**NPCs.** An archetype names a `melee` item. A pure brawler (no
`projectile_speed`) keeps its own `attack_*` numbers and lands them on
everyone in the weapon's arc; a shooter with a blade (`npc.gunner`: dagger)
swaps to it when its target comes within the blade's reach + 1.5 m and back
when the target leaves, taking 0.5 s to swap, and swings for `melee_damage`
(else the weapon's). The swap is an `equipped` event. `npc.grunt` carries a
Hatchet, `npc.warlord` a Maul.

### Throwables (2026-10-03)

A consumable with `consumable.throw {damage, radius, speed}` is thrown by
`use` (the hotbar): it leaves the hand (1.5 m up, 0.5 m ahead) at `speed`
along the look, falls under gravity, and bursts on the first thing it
touches — a body, a wall, the ground — or after 8 s. The burst (`explosion`
event, PROTOCOL `0x0001`) deals `damage` at the centre falling linearly to
half at `radius` to every damageable world entity in reach (a player's
charge never hurts players). Kills pay missions and bounties like a shot.

| item | name | `damage` | `radius` m | `speed` m/s | price |
|---|---|---|---|---|---|
| `throw.fire` | Firebomb Flask | 40 | 4.0 | 16 | 20 |
| `throw.frag` | Frag Grenade | 60 | 5.0 | 18 | 30 |
| `throw.acid` | Acid Vial | 30 | 6.5 | 16 | 36 |

All cool 1.5 s and stack to 10. `consumable.potion.heal` (Healing Draught,
+30, 16 cr) uses the same flask art. Grunts drop flasks (15 %), gunners
grenades (12 %).

### Health and damage

- `health` is an integer, `0` = dead, maximum from the entity's def.
- **Hitbox: one vertical capsule per entity**, `hitbox_radius` around the
  segment from `pos` (feet) to `pos + up · hitbox_height`. Target dummies use
  the same capsule shape with their own numbers. `up` is the entity's own
  radial up, not the shooter's.
- Damage applies server-side only, on the tick the shot resolves, and emits a
  `hit` event; reaching 0 emits `death` and sets the `dead` flag.
- **Players cannot be damaged in Phase 2.** Only target dummies take damage.
  Player damage, death and respawn are Phase 3 — with them come the rules that
  make dying mean something, and shipping the mechanic before those rules exist
  just means building it twice.

| Param | Value | Unit | Note |
|---|---|---|---|
| `hitbox_radius` (player) | 0.35 | m | |
| `hitbox_height` (player) | 1.8 | m | feet to crown |
| `max_health` (player) | 100 | hp | |
| `max_health` (target) | 100 | hp | |
| `target_respawn` | 3.0 | s | full health, same position |
| `rewind_max` | 0.5 | s | hard clamp on lag-compensation rewind |
| `interp_delay` | 0.1 | s | how far behind the simulation clock a client renders remote entities |

**Lag compensation.** The server keeps `rewind_max` of position history per
entity at tick granularity, and resolves a shot against the world **as the
shooter's screen was showing it**:

```
rewind_ticks = staleness + L + interp_delay      clamped to [0, rewind_max]

  L         = the server's own smoothed RTT/2 for that connection
  staleness = how far back the tick that ran fire.seq is from now
              (zero for the ordinary shot; positive when the client
              pulled the trigger against an older input)
```

Walk one shot along a single timeline and the three terms are forced. The
client fires at `t` while displaying remotes at `t − interp_delay`. The shot
arrives at `t + L`. The instant to reconstruct is therefore `interp_delay + L`
behind arrival, plus whatever extra the client's own input lag added.

The shooter's eye position comes from that same history — never from the
client. Without rewind, a hit at 100 ms and 7.5 m/s misses by 0.4 m, which is
most of a body.

**`interp_delay` is a contract, and it binds the client.** A client MUST render
remote entities at `serverClock − interp_delay`, against a clock synchronised
to the server's — **not** at a fixed offset behind whenever a packet happened to
arrive locally. The two differ by a whole one-way trip, and the server rewinds
by the first, so a client that renders by the second misses everything that
moves. This is a rule about what a client draws, so no amount of server testing
catches a breach of it; the client owes it explicitly (ROADMAP U13).

Both halves were measured wrong at once, 2026-08-27: the server rewound `L`
alone, and the retired TS client rendered on local receive time. A player
shooting what their screen showed hit 0 of 8 moving targets, while aiming at
the target's live position — which no real client can know — hit 8 of 8
(`docs/QA-STATUS.md`, C14).

### Interaction

One mechanism, reused by shop NPCs now, loot in Phase 3, and vehicles in
Phase 4/5.

- An entity is **interactable** if its def carries a `verb` (`"Talk"`,
  `"Pick up"`, `"Board"`).
- A candidate must be within `interact_dist` of the player's eye **and** inside
  the look cone: `dot(look_dir, normalize(target_eye − eye)) ≥ cos(interact_cone)`.
- Among candidates, the one with the **largest** dot product wins — the thing
  you are most directly looking at, not the nearest. Nearest picks the wrong
  target when two NPCs stand together.
- The client shows a prompt with the verb; `E` sends the opcode that verb maps
  to. **The server re-validates distance and cone on the `cmd`** — the client
  check is a UI affordance and nothing more.

| Param | Value | Unit |
|---|---|---|
| `interact_dist` | 3.0 | m |
| `ui_close_dist` | 5.0 | m — an NPC's panel (shop, bench) closes when the player is further than this from the NPC (Phase 13) |
| `interact_cone` | 20 | deg (half-angle) |

### Shop NPCs

- Entity type `0x0003`, no AI, no movement, no health. Placed by a zone file,
  facing a fixed direction, standing on the terrain.
- Defined in `server/data/npcs.json`: display name, model asset id, `verb`, and
  a `stock` list of `{item, price}`. Prices live on the NPC, not the item, so
  the same item can cost different amounts in different places later.
- `shop_list` returns the stock. `shop_buy` validates, in this order: the NPC
  exists and is a shop; the player is in range and in cone; the item is in
  stock; the player can afford `price × qty`; there is inventory space. Then it
  debits and grants **atomically** — a purchase never half-applies.
- Stock is unlimited in Phase 2. Finite stock is a field in the same JSON when
  it is wanted; nothing else changes.

### Static colliders

Rocks stay client-scattered and non-collidable. **Anything the player must not
walk through is authored into a zone file** and shipped in the `colliders`
message (`docs/PROTOCOL.md`). (Rocks: superseded below.)

#### Full collision (2026-10-02)

Playtest: "the game should have full collision". Everything solid, on both
sides, through the existing sphere-vs-collider rule (`ResolveColliders` /
`nearest`) -- no new solver:

| Mover | Shape | Pushed out of |
|---|---|---|
| body (player, NPC) | its chest sphere (`body_radius`, `body_sphere_h`) | everything below |
| rover | 2 hull spheres r 0.80 at (0, 0.75, ±0.55) local, step 8b | everything below |
| ship | 3 hull spheres r 1.40 at (0, 1.30, 0/±2.40) local, step 4a | everything below |

Solid: the shipped colliders; **rocks** (each a sphere: centre 0.4 of its
height up, radius 0.45 of its narrower footprint) from the same seeded
scatter the client draws, run server-side on the wire-quantized field
(`sim/rocks.go`, held to the client's by `t38-rock-parity`); **solid props**
(barrel, barrels, generator, dish, loot crate -- model bounds as a box;
bones walk-through); and every **moving thing but yourself** at its
start-of-tick pose: on-foot players and live NPCs as body spheres, rovers
(half 0.85 × 0.55 × 1.12, centre 0.75 up) and ships (half 1.40 × 1.30 ×
3.60, centre 1.30 up) as boxes. Seated bodies are not obstacles. Order:
static, rocks, props, then moving bodies. The client predicts against the
same list built from what it draws. One-sided: nothing transfers momentum,
so a rover stops against a player rather than shoving them, and the player
is pushed out of the rover's box on their own step.

**Authoring frame.** Authoring boxes in world space on a sphere is not humanly
possible, so a zone file is written in a **local tangent frame** at the zone's
`origin_dir` and composed to world space by the server at load:

```
# zone frame, once per zone
up     ← normalize(origin_dir)
ref    ← (0,0,1)  unless |dot(ref, up)| > 0.999, then (1,0,0)
north  ← normalize(ref − up·dot(ref, up))
east   ← cross(up, north)
origin ← up · radius(terrain, up)

# per object, from its local p = (x, y, z)
dir      ← normalize(origin + east·p.x + north·p.z)      # walk out along the surface
world_pos ← dir · (radius(terrain, dir) + p.y)           # y is height ABOVE the ground

# re-derive the frame at the object's own position, so it stands on its own up
north_p  ← normalize(north − dir·dot(north, dir))
east_p   ← cross(dir, north_p)
world_quat ← quatFromBasis(east_p, dir, north_p) · local_quat
```

Local `+y` is up, `+x` is east, `+z` is north, and the frame is right-handed —
so a zone file reads like a flat level, which is the whole point.

Two details that are easy to get wrong and expensive to find later:

- **`y` is height above the ground, not height above the zone origin.** A zone
  40 m across on a 150 m planet drops ~5 m at its edge; authoring against the
  origin plane would bury the far wall.
- **Each object is oriented by its own local up**, re-derived at its own
  position, not by the zone origin's. Using the origin frame throughout leans
  the far wall by `40/150` ≈ 15°, which reads as broken from ten metres away.

**The site is flattened, not assumed flat.** A zone declares `flatten_radius`
and `flatten_falloff`; the terrain generator levels the radius field to the
zone origin's radius inside `flatten_radius` and blends out over
`flatten_falloff`, **before** the field is encoded for the wire. Both ends
therefore see the flattened terrain with no special case, and a retune of the
noise cannot put a wall halfway underground. Flattening only ever makes terrain
more walkable, so ROADMAP criterion 9 still holds.

**Player collision shape: one sphere at chest height**, radius `body_radius`
centred at `pos + up · body_sphere_h`. Not a capsule.

> `ponytail:` a chest sphere blocks walls and cannot be stepped over or crawled
> under, which is right for Phase 2's walls and posts. It cannot express a
> waist-high railing you vault or a low gap you crouch through. Upgrade to a
> swept capsule when a zone wants one — the resolve step is the only caller.

**Integrator addition.** The on-foot `step` gains one stage between the existing
terrain resolution and the end of the tick:

```
  # 7.  Terrain resolution (unchanged) — the single writer of grounded
  (pos, vel, grounded) ← resolve(pos_old, pos, vel, mode, terrain)

  # 8.  Static collider resolution
  c ← pos + up·body_sphere_h
  for each collider in colliders:                 # world order, as received
      (hit, n, depth) ← nearest(c, body_radius, collider)
      if hit:
          pos ← pos + n·depth                     # push out along the exit normal
          c   ← pos + up·body_sphere_h
          if dot(vel, n) < 0:
              vel ← vel − n·dot(vel, n)           # cancel velocity into the surface
          if dot(n, up) ≥ cos(max_slope):         # standing on top of it
              grounded ← true

  # 9.  Re-seat on the terrain if step 8 pushed the feet under it
  r ← radius(terrain, normalize(pos))
  if |pos| < r:
      pos ← normalize(pos)·r
      vel ← vel − up·min(dot(vel, up), 0)
```

Order is binding on both ends. `nearest` is sphere-vs-OBB (transform the sphere
centre into the collider's local frame, clamp to `±half`, transform back) and
sphere-vs-sphere; both are total and neither allocates.

> `ponytail:` one push-out pass, not an iterative solver. A player wedged into
> an interior corner of two colliders can resolve to a slightly wrong spot.
> Iterate the loop 2–3 times if a zone's geometry makes that visible — same
> code, one outer loop.

| Param | Value | Unit | Note |
|---|---|---|---|
| `body_radius` | 0.35 | m | collision sphere |
| `body_sphere_h` | 0.9 | m | chest height above the feet |
| `collider_max` | 1560 | — | one 64 KiB `colliders` message |

## Phase 3 — NPC combat at an encampment

Spec for `netcode` + `game`. Phase 3's playable proof is clearing a hostile
camp: enemies notice you, close or shoot, hurt you, and drop loot
(`docs/ROADMAP.md`).

### NPC archetypes (rule table)

Two archetypes. One closes, one keeps its distance — that contrast is the
whole fight, and a third variant adds nothing until these two feel right.

| Param | `npc.grunt` (melee) | `npc.gunner` (ranged) | Unit |
|---|---|---|---|
| `max_health` | 60 | 40 | hp |
| `move_speed` | 4.0 | 3.0 | m/s |
| `aggro_radius` | 22 | 30 | m |
| `leash_radius` | 45 | 45 | m — from its POST, not from the player |
| `attack_range` | 2.0 | 26 | m |
| `attack_damage` | 12 | 8 | hp |
| `attack_interval` | 1.2 | 1.6 | s |
| `attack_windup` | 0.35 | 0.25 | s — telegraph before damage lands |
| `projectile_speed` | — | 45 | m/s |
| `turn_rate` | 360 | 270 | deg/s |
| `xp` / loot roll | `loot.grunt` | `loot.gunner` | — |

**Leash is measured from the NPC's post, not from the player.** Chasing "until
the player is far away" means a kited enemy follows forever, because the player
controls the distance. Anchoring to the post bounds the fight to its camp.

**Every attack has a windup.** Damage that lands the instant an enemy decides
to attack is unreadable and unfair — the player needs a frame to react in. The
windup is also when the animation would play, so the number is not decoration.

### AI state machine (binding — implement this table, not the concept)

States: `IDLE`, `PATROL`, `AGGRO`, `ATTACK`, `LEASH`, `DEAD`.

| From | To | When |
|---|---|---|
| `IDLE` | `PATROL` | it has a patrol route and `idle_dwell` (3 s) has elapsed |
| `IDLE`/`PATROL` | `AGGRO` | a living player is within `aggro_radius` and in line of sight |
| `AGGRO` | `ATTACK` | target within `attack_range` and in line of sight |
| `ATTACK` | `AGGRO` | target outside `attack_range · 1.15` (hysteresis) or LOS lost |
| `AGGRO`/`ATTACK` | `LEASH` | distance from its POST exceeds `leash_radius` |
| `AGGRO`/`ATTACK` | `LEASH` | the target is LOST — no living candidate within `aggro_radius` with line of sight — for `lose_target` (2 s) |
| `LEASH` | `IDLE` | back within `post_arrive` (1.5 m) of its post |
| any | `DEAD` | health reaches 0 |
| `DEAD` | `IDLE` | `npc_respawn` (20 s) elapsed, at full health, at its post |

**Losing the target must send an NPC home.** Leash keyed only to
distance-from-post leaves an enemy that chased you a short way and then lost
you standing in the open forever: it is inside its leash radius, so it never
disengages, and it has no target, so it never moves. Measured before this rule
existed — an NPC sat 17.9 m from its post for 60 s. The `lose_target` grace
period stops it snapping home the instant a target ducks behind cover.

**The `ATTACK`→`AGGRO` threshold is `attack_range · 1.15`, not `attack_range`.**
Equal thresholds make an enemy at exactly that distance flip state every tick,
which shows up as a stuttering, twitching model and a machine-gun attack
cadence. Hysteresis is not polish here.

**Target selection:** the closest living player inside `aggro_radius` with line
of sight. Re-evaluated every `retarget_interval` (0.5 s), NOT every tick —
per-tick re-evaluation makes an NPC oscillate between two equidistant players
and never reach either.

**Line of sight** is a ray from the NPC's eye to the target's eye against the
static collider list only (GDD "Static colliders"). Terrain is not tested: at
these ranges the horizon does not occlude, and sampling the field along a ray
is far more expensive than the two box tests it replaces.

| Param | Value | Unit |
|---|---|---|
| `idle_dwell` | 3.0 | s |
| `retarget_interval` | 0.5 | s |
| `lose_target` | 2.0 | s |
| `post_arrive` | 1.5 | m |
| `npc_respawn` | 20 | s |
| `attack_range_hysteresis` | 1.15 | × |

### Wildlife — herds and wandering (Phase 14)

The mob library (`art/mobs/CATALOG.md`, 69 archetypes in `server/data/mobs.json`)
is placed in the open country between the POIs as **herds**: small groups of
one archetype that stand on unflattened ground, wander a few metres around
where they spawned, and fight with the same brain, steering, loot and
respawn rules as a camp grunt. Nothing about an individual mob is new; the
herd is the unit of authoring and the wander leg is the one new behaviour.

**Data — `server/data/wildlife.json`** (loaded into `defs.Registry.Herds`,
file order kept; the defs audit refuses the file rather than the server
limping on a bad herd):

```json
{
 "version": 1,
 "herds": [
  {"id": "herd.blobs.east", "def": "mob.blob.green_blob", "count": 4,
   "origin_dir": [0.2462021, 0.7891895, 0.5626407], "spread": 5.0, "wander": 10.0}
 ]
}
```

| Field | Meaning | Audit |
|---|---|---|
| `id` | herd id, `herd.*` | unique |
| `def` | an NPC archetype id | exists in `reg.NPCs`; `max_health > 0` and `move_speed > 0` (a combatant) |
| `count` | members | 1–12 |
| `origin_dir` | unit direction of the herd centre (the `poi` solver's output, pasted and reviewed, exactly like a zone) | finite, non-zero; normalised at load |
| `spread` | metres: members are scattered inside this disc around the centre | `>= 0`; for `count > 1`, `>= 1.5 × def radius × sqrt(count)` so members never spawn stacked |
| `wander` | metres: how far a member strays from its own post while idle; `0` = stands still like a camp NPC | `0 <= wander <= def.leash_radius − 2` |

The audit also rejects a herd centre within `30 m` surface distance of
spawn or of any zone `origin_dir`, and a file whose members total more than
`64` — snapshots carry every world entity to every client (no interest
management yet), so the herd budget is a bandwidth budget.

**Placement (`defs.ComposeHerd`, deterministic, no RNG).** Member `i` of
`count` sits at polar offset `r = spread · sqrt((i + 0.5) / count)`,
`θ = i · 2.399963 rad` (the golden angle) in the herd's tangent frame — the
same east/north frame `ComposeZone` builds from `origin_dir` — glued to the
surface with the terrain's radius function, facing outward along its offset
(a lone member faces east). Herds are placed **after the rover**, in file
order, continuing the world-entity id range, so no existing entity id moves.
Each member is an ordinary NPC entity: `CombatStateFor` post at its own
spot, `npc_respawn` returns it there, the loot table is the archetype's.

**Wandering** is the `PATROL` state given a leg to walk. The brain's table
above is unchanged (`IDLE → PATROL` after `idle_dwell`; `PATROL → AGGRO` on a
target); what `PATROL` does is decided by the member's `wander` radius:

| `wander` | `PATROL` means |
|---|---|
| `0` (every zone NPC) | inert, exactly as today |
| `> 0` | pick a point, walk to it, pause, repeat |

The leg, binding:

| Param | Value | Unit | Rule |
|---|---|---|---|
| `wander_pick` | uniform in the disc | — | a point inside `wander` of the member's **post** (not its current position, so legs never drift away), in the post's tangent plane, glued to the surface; re-rolled (up to 8 tries) while the ground there is not `Walkable`; after 8 misses the leg is skipped and the pause restarts |
| `wander_speed` | 0.5 | × `move_speed` | the walk; `AGGRO`/`LEASH` steer at full `move_speed` as before |
| `wander_arrive` | 1.0 | m | the leg ends when the member is within this of its point |
| `wander_leg` | 8.0 | s | or when the leg has run this long (a point behind a rock is abandoned, not pushed at forever) |
| `wander_pause` | uniform 1.0–4.0 | s | standing still between legs; the first leg starts after a pause too |

The server's RNG rolls the point and the pause (it is server-owned and
already under `s.mu`; NPC motion is not in the conformance diff). Leaving
`PATROL` for any reason drops the current leg; dying drops it; a respawned
member starts from `IDLE` at its post with no leg. A wandering member is
still subject to the leash rule — it never needs it, because
`wander <= leash_radius − 2` is audited.

**What a herd is not.** No pack aggro (a member that notices you does not
wake its neighbours — their own `aggro_radius` does that soon enough), no
herd-level respawn, no migration, no flyers in the air (the `mob.flying.*`
archetypes walk; a flight regime for NPCs waits for a reason). Each of those
is a one-line trigger in the ROADMAP's Deferred table when it is wanted.

### Steering (binding)

NPCs move on the sphere with the same radial-up frame the player uses. They do
NOT run the player's on-foot integrator: no jump, no slide, no air control.

```
step(npc, dt):
  up      ← normalize(pos)
  toward  ← tangent(target − pos, up)          # projected into the tangent plane
  desired ← normalize(toward) · move_speed
  facing  ← rotateToward(facing, normalize(toward), turn_rate · dt)
  vel     ← desired  if slopeOK(terrain, pos)  else  slide(vel, up, terrain)
  pos     ← pos + vel · dt
  pos     ← normalize(pos) · radius(terrain, normalize(pos))   # glued to the surface
```

**NPCs are glued to the surface** rather than simulating gravity: a walking
enemy has no reason to leave the ground, and the moment it does, every
airborne edge case in the player integrator becomes an NPC bug too.

They resolve against static colliders with the same `ResolveColliders` the
player uses — one wall rule, not two.

**No pathfinding, no navmesh.** Direct steering with slope rejection is enough
for an open camp on smooth terrain. The trigger to revisit is the first
encampment whose geometry can trap an NPC in a concave corner.

### Player death and respawn

| Param | Value | Unit |
|---|---|---|
| `player_max_health` | 100 | hp |
| `respawn_delay` | 5.0 | s |
| `respawn_invuln` | 3.0 | s — after respawn |
| `health_regen_delay` | 8.0 | s — out of combat |
| `health_regen_rate` | 8 | hp/s |

- Death: velocity zeroed, `dead` flag set, input ignored except look. The body
  stays where it fell for the delay — an instant vanish reads as a disconnect.
- Respawn at the spawn point, full health, with `respawn_invuln` of immunity.
  Without it a camp that killed you once kills you again before you can move.
- **Inventory and credits are kept on death** — except raw materials, which
  Phase 12 spills where you fell ("Death spills the raw" under Skills).
- Regeneration starts `health_regen_delay` after the last damage TAKEN, and
  stops the moment damage lands again.

### Loot

- A killed NPC drops one entity per its loot table, at its own position.
- Drops are picked up by walking within `loot_pickup_radius` (1.5 m) — no
  prompt, no keypress. A pickup prompt for ammunition after every kill is
  friction with no decision behind it.
- **Pickup is server-authoritative and single-grant.** Two players walking over
  one drop at the same tick: exactly one gets it, the other sees it vanish.
- A drop nobody takes despawns after `loot_lifetime` (120 s).
- Loot that will not fit in the inventory is left on the ground rather than
  destroyed.

| Param | Value | Unit |
|---|---|---|
| `loot_pickup_radius` | 1.5 | m |
| `loot_lifetime` | 120 | s |

Loot tables live in `server/data/loot.json`: each entry is a list of
`{item, qty, chance}` rolled independently against the server's RNG.

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

Phase 2:

- **Weapon numbers are first guesses.** 25 damage / 400 rpm / 30 rounds kills a
  100 hp dummy in four hits and empties a magazine in 4.5 s. Whether that
  *feels* like a rifle is a look-at-it question; the rule table is where it
  moves, and nothing else has to change.
- **Is server-resolved spread the right call?** It is the honest one, but at
  100 ms the client's tracer is drawn along its own `dir` while the server's
  ray went somewhere marginally else. If the mismatch reads as bad hit
  registration, the fix is drawing the tracer from the `shot fired` event
  instead of locally — one client change, no rule change.
- **Chest-sphere collision vs a swept capsule.** Decided lazy (above). The
  trigger to revisit is the first zone that wants a vaultable railing.
- **Does the target range need scoring?** A hit counter and a timer are ~30
  lines of client HUD and make the range a thing you *use* rather than a thing
  you shoot at once. Deferred until the shooting feels right; scoring a bad
  gun is polish on the wrong layer.

M2 → now Phase 4/5 (decided at the old M2 wave 0, 2026-08-22 — see "Vehicles
and crew (M2 spec)"; the decisions stand, the phase number moved):

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
