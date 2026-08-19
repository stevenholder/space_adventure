# Wire protocol (client ↔ server)

Status: **v0 draft — M1 implements exactly this set.** Both ends implement
from this file. A change requires both ends updated in the same milestone;
the main thread coordinates the edit.

## Transport

- WebSocket, binary messages only. No text frames.
- All integers little-endian. No padding, no alignment.

## Frame

```
frame = u16 type | payload
```

Exactly one message per WebSocket message — the transport already preserves
message boundaries, so the frame carries no length prefix and the payload runs
to the end of the message.

Max message size: 64 KiB. A message that exceeds it closes the connection
(code 1009).

## Message types

| id | name | dir | payload |
|----|------|-----|---------|
| `0x0001` | `hello` | C→S | `u16 client_ver` \| `u32 name_len` \| `bytes name` |
| `0x0002` | `hello_ack` | S→C | `u16 server_ver` \| `u16 tick_hz` \| `u32 world_seed` \| `u32 entity_id` |
| `0x0003` | `input` | C→S | `f32 move_x` \| `f32 move_y` \| `f32 look_dir[3]` \| `u16 action_mask` \| `u16 seq` |
| `0x0004` | `snapshot` | S→C | `u32 tick` \| `u16 ack_seq` \| `u16 count` \| `entity × count` |
| `0x0005` | `spawn` | S→C | `u32 entity_id` \| `u16 entity_type` \| `u32 data_len` \| `bytes data` (M1: UTF-8 display name) |
| `0x0006` | `despawn` | S→C | `u32 entity_id` |
| `0x0007` | `event` | S→C | `u32 entity_id` \| `u16 event_id` \| `u32 data_len` \| `bytes data` |
| `0x0008` | `ping` | C→S | `u32 ts_ms` |
| `0x0009` | `pong` | S→C | `u32 ts_ms` (echo of ping) |
| `0x000A` | `terrain` | S→C | `u16 face_grid` \| `f32 radius_min` \| `f32 radius_max` \| `u16 radii[6 × face_grid × face_grid]` |

`entity` (inside `snapshot`):

```
u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
```

Constants:

- `entity_type`: `0x0001` player body. `0x0002` ship — reserved, M2.
- `action_mask` bits: `0x0001` sprint, `0x0002` jump. `0x0004` boost —
  reserved, M2.
- `event_id`: `0x0001` explosion (reserved for later)

## Semantics

- Server ticks at 20 Hz and sends one `snapshot` (full state) per tick.
- A client sends `hello` on connect; the server replies `hello_ack` with the
  client's `entity_id`, then `terrain`, then includes the entity in snapshots.
  A client must not simulate before `terrain` arrives — it has no ground to
  stand on until then.
- `input` carries **on-foot** command state in M1: `move_x`/`move_y` are the
  wish direction in the body's tangent frame, each in [−1, 1] and jointly
  clamped to unit length; `look_dir` is the absolute world-space unit vector
  the eyes point along — not angles, not rates.
- `look_dir` is **client-authoritative**: the server takes it as given (after
  normalizing and clamping it away from local up/down per the GDD) rather than
  simulating it. Aim must be instant, so look is never predicted, never
  replayed, and never corrected.
  - It is a vector rather than a `yaw`/`pitch` pair because the world is round:
    there is no global frame for yaw to be measured against that does not have
    a singularity somewhere on the surface. A world-space direction needs no
    frame and stays idempotent under the "latest input wins" rule.
  - **Facing-hold:** facing is the tangent projection of `look_dir`. When that
    projection's magnitude is < `facing_hold` (0.1, ≈ 5.7° from local vertical),
    facing holds its previous value instead of being recomputed — a 1°-clamped
    near-vertical look has too-short a tangent to define a stable azimuth, so
    recomputing would spin the body on mouse jitter. Both ends run this in the
    integrator (GDD "Integrator" step 2).
- `quat` in a body's `entity` carries the body's **full orientation** — facing
  plus which way is up for it, which on a round world differs per player and
  is needed to draw a remote character standing correctly on the far side of
  the planet. Head pitch is not transmitted in M1, so remote characters look
  level along their own horizon.
- Positions are world-space Cartesian with the planet centre at the origin.
  There is no world boundary.
- `input` is **current command state**, not an event stream: the server
  applies the latest received input every tick (constant between inputs).
- `seq` is a per-connection counter the client increments on every `input`
  it sends. The server stores the `seq` of the input it last applied and
  echoes it as `ack_seq` in that client's next `snapshot`. It orders nothing
  and drops nothing — an input whose `seq` is older than the stored one is
  still applied (latest arrival wins); `seq` exists only so the client can
  replay. Comparisons must be wraparound-safe (`int16(a - b) > 0`), since
  `seq` wraps every ~18 min at 60 Hz.
- `ack_seq` is the only per-client field in `snapshot`. The server encodes the
  snapshot body once per tick and patches those 2 bytes (offset 4) per
  connection.
- The client uses `ack_seq` to reconcile its prediction — see ARCHITECTURE
  "Network model".
- **Player identity.** `hello.name` is the display name the client asks for;
  the server sanitizes it and echoes the accepted value as the `spawn.data`
  payload for that entity, so every client learns each character's name exactly
  once, when it appears. Server-side rules: valid UTF-8, trimmed, ≤ 24 bytes,
  control characters stripped, empty replaced with `Player <entity_id>`. Names
  are **not** unique and carry no identity meaning — M1 has no accounts.
  - Sanitizing server-side matters: the name is rendered on every other
    player's screen, so it is untrusted input crossing a trust boundary. The
    client must also render it as text only, never as markup.
- `spawn`/`despawn` are sent to clients as entities enter/leave the world
  (M1: player join/leave, heartbeat timeout).
- Heartbeat: client sends `ping` every 2 s if no other traffic; server
  answers `pong` and drops connections silent for 10 s.
- `ts_ms` is a **client-local monotonic** millisecond value
  (`performance.now()`), not wall-clock epoch — u32 epoch ms would overflow.
  The server never interprets it, only echoes it, so the client gets RTT by
  subtracting from its own clock. No clock sync in M1.
- `terrain` is sent once, after `hello_ack` and before the first `snapshot`.
  The world is a **cube-sphere**: six square grids of surface radii, indexed
  `radii[face · face_grid² + row · face_grid + col]`, each a `u16` decoded as
  `radius_min + r / 65535 · (radius_max − radius_min)` metres from the planet
  centre. Face order is `+X, −X, +Y, −Y, +Z, −Z`. Both ends sample it exactly
  as GDD "Terrain sampling" specifies.
  - **Shared edges are duplicated between adjacent faces and must carry
    identical values.** The server guarantees it; a mismatch is a visible crack
    in the ground and a spot where players fall through.
  - At the GDD's `face_grid` 65 the message is ~51 KB, inside the 64 KiB limit.
    **`face_grid` above 73 overflows a single message** (6 · 73² · 2 = 63,948 B)
    and forces chunking — treat that as the point where this design has to
    change, not as something to discover at runtime.
  - The server sends the field rather than both ends generating it from
    `world_seed` on purpose: identical terrain then costs one message instead
    of a noise function that has to produce bit-comparable results in Go and
    TypeScript.
- `world_seed` seeds client-side decoration only — prop scatter, sky, and
  colour variation. It does not generate collidable geometry; terrain comes
  over the wire.
- Reconnect (M1): connection loss = entity despawns (after heartbeat
  timeout); reconnecting yields a new `entity_id`.

## Terrain sampling (pinned — both ends implement this exactly)

The radius field is consumed identically by the server generator, the server
sim, and the client (mesh + sim). A mismatch sinks a player under the ground
at specific cube-face edges, so the exact mapping is pinned here rather than
left to the GDD's prose.

- **Face.** For a normalized direction `d = (dx, dy, dz)`, the face is the
  axis of largest magnitude; exact ties resolve to the earlier face in
  `+X, −X, +Y, −Y, +Z, −Z`.
- **(u, v).** Divide the two non-dominant components by the dominant magnitude,
  keeping world order: `+X/−X → (u, v) = (dy, dz)`; `+Y/−Y → (dx, dz)`;
  `+Z/−Z → (dx, dy)`. Each lies in `[−1, 1]`.
- **Grid index.** `col = (u + 1) / 2 · (face_grid − 1)`;
  `row = (v + 1) / 2 · (face_grid − 1)`; bilinearly interpolate over the four
  surrounding cells of `radii[face · face_grid² + row · face_grid + col]`.
  Equivalently, `radii[face][row][col]` is the radius of the surface point on
  face `face` whose direction sets the non-dominant axes to
  `2·col/(grid−1) − 1` (first) and `2·row/(grid−1) − 1` (second), with the
  dominant axis as the face normal, then normalized.
- **Normal / slope.** Use the GDD "Terrain sampling" finite-difference formula
  verbatim (same `normal_eps` on both ends).

## Versioning

`client_ver` / `server_ver` are u16 protocol versions; M1 = `1`. The server
rejects `hello` with a different major version by closing (code 1002).
