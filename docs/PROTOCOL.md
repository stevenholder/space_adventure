# Wire protocol (client ↔ server)

Status: **v2 — frozen for Phase 2. Both ends implement exactly this set.**
A change requires both ends updated in the same phase; the main thread
coordinates the edit.

> **Phase 4/5 sections are marked as such.** `board`, `disembark`,
> `seat_result`, input mode `1` (pilot) and the entity row's
> `parent_id`/`seat` fields are specified here in full but are **not built in
> Phase 2** — they land with the ground vehicle in Phase 4 and are reused by
> the ship in Phase 5. Phase 2 encodes `parent_id = 0` and `seat = 0` and
> ignores mode `1`. The row is sized once now so the codec churns once, not
> twice (`docs/ROADMAP.md`).

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
| `0x0001` | `hello` | C→S | `u16 client_ver` \| `u32 name_len` \| `bytes name` \| `u32 token_len` \| `bytes token` |
| `0x0002` | `hello_ack` | S→C | `u16 server_ver` \| `u16 tick_hz` \| `u32 world_seed` \| `u32 entity_id` |
| `0x0003` | `input` | C→S | `f32 v[5]` \| `u16 action_mask` \| `u16 seq` \| `u8 mode` (25 B; 24 B accepted as mode `0`) |
| `0x0004` | `snapshot` | S→C | `u32 tick` \| `u16 ack_seq` \| `u16 count` \| `entity × count` |
| `0x0005` | `spawn` | S→C | `u32 entity_id` \| `u16 entity_type` \| `u32 data_len` \| `bytes data` (M1: UTF-8 display name) |
| `0x0006` | `despawn` | S→C | `u32 entity_id` |
| `0x0007` | `event` | S→C | `u32 entity_id` \| `u16 event_id` \| `u32 data_len` \| `bytes data` |
| `0x0008` | `ping` | C→S | `u32 ts_ms` |
| `0x0009` | `pong` | S→C | `u32 ts_ms` (echo of ping) |
| `0x000A` | `terrain` | S→C | `u16 face_grid` \| `f32 radius_min` \| `f32 radius_max` \| `u16 radii[6 × face_grid × face_grid]` |
| `0x000B` | `board` | C→S | `u32 vehicle_id` \| `u16 seat` |
| `0x000C` | `disembark` | C→S | (no payload) |
| `0x000D` | `seat_result` | S→C | `u32 entity_id` \| `u16 seat` \| `u8 result` |
| `0x000E` | `cmd` | C→S | `u16 seq` \| `u16 opcode` \| `u32 data_len` \| `bytes data` (UTF-8 JSON) |
| `0x000F` | `cmd_result` | S→C | `u16 seq` \| `u16 opcode` \| `u8 status` \| `u32 data_len` \| `bytes data` (UTF-8 JSON) |
| `0x0010` | `defs` | S→C | `u32 data_len` \| `bytes data` (UTF-8 JSON) |
| `0x0011` | `fire` | C→S | `u16 seq` \| `f32 dir[3]` |
| `0x0012` | `colliders` | S→C | `u16 count` \| `collider × count` |
| `0x0013` | `props` | S→C | `u16 count` \| `prop × count` |

`entity` (inside `snapshot`) — **54 bytes, fixed**:

```
u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
| u32 parent_id | u16 seat | u16 health | u8 flags | i8 pitch_q
```

`collider` (inside `colliders`) — **42 bytes, fixed**:

```
u8 kind | u8 _pad | f32 center[3] | f32 half[3] | f32 quat[4]
```

`prop` (inside `props`) — **variable length**:

```
f32 pos[3] | f32 quat[4] | f32 scale | u16 asset_len | bytes asset
```

`asset` is an id from `art/manifest.json` (`prop.barrel`, `prop.dish`), UTF-8.

Props are **visual only**: zone dressing with no collider, which the sim never
sees. A client that ignored this message entirely would still agree with the
server about everything that can be walked into or shot — which is why the row
may be variable-length where `collider` may not. Colliders are bulk data
(1560 rows in a message) and every byte counts; props are hand-authored and the
two zones that have any carry fourteen between them, so the row simply carries
the name instead of an index into a table the client would have to hold.

Sent once, immediately after `colliders`, and never resent.

Constants:

- `entity_type`: `0x0001` player body; `0x0002` ship (Phase 5, manifest id
  `ship.v1`); `0x0003` NPC; `0x0004` target dummy; `0x0005` ground vehicle
  (Phase 4); `0x0006` loot drop (Phase 3); `0x0007` projectile (Phase 3);
  `0x0008` node (Phase 12 — `spawn.data` is the node def id, e.g.
  `node.ore.iron`; the snapshot `health` is yields remaining, `0` =
  depleted).
- `action_mask` bits: `0x0001` sprint, `0x0002` jump (mode 0); `0x0004`
  boost (mode 1, Phase 5). Other bits ignored.
- **`input`'s mode byte is trailing and optional** (landed in Phase 3.5;
  earlier drafts described a *leading* byte that was never built — following
  that draft misaligns every field by one). The 25-byte layout is the table
  row above; a 24-byte payload is read as mode `0`, so a client that predates
  the byte keeps working. Modes: `0` on foot
  (`v = [move_x, move_y, look_dir.x, look_dir.y, look_dir.z]`), `1` pilot
  (Phase 5, `v = [thrust, roll, yaw_rate, pitch_rate, 0]`), `2` ground
  vehicle (Phase 4, `v = [throttle, steer, 0, 0, 0]` — `throttle` ∈ [−1, 1],
  negative reverses; `steer` ∈ [−1, 1], positive turns toward the vehicle's
  local +X (right); the last three floats are sent as zero and ignored).
- `seat` (Phase 4): `0` not aboard; `1` driver/pilot; `2`–`3` passenger. The
  field is u16 — more seats later is a rule-table change, not a wire change.
- `seat_result` `result` (Phase 4): `0` granted; `1` seat occupied; `2` out of
  range; `3` not seated / invalid.
- `health`: current hit points, `0` = dead. Maximum comes from the entity's
  def in `defs`, not the wire. An entity with no health concept (loot, a
  projectile) sends `0` and sets no `dead` flag.
- `flags` bits: `0x01` grounded, `0x02` sprinting, `0x04` dead, `0x08` firing,
  `0x10` space (Phase 5: the entity — a ship — is in the space regime, GDD
  "Space regime"; carried state mirrored onto the wire the same way a
  vehicle's grounded is, so the client renders the regime it will predict)
  (set on the tick a shot is resolved). `0x10`–`0x80` reserved, sent as 0.
- `pitch_q`: the entity's view pitch quantised as
  `round(pitch / (π/2) · 127)`, clamped to `[−127, 127]` — about 0.7° of
  resolution. **Visual only.** Hit resolution never reads it; the server uses
  its own record of the shooter's aim (see `fire`).
- `collider` `kind`: `0` box (`half` = half-extents along the collider's local
  axes), `1` sphere (`half[0]` = radius, `half[1]`/`half[2]` sent as 0 and
  ignored). `_pad` is sent as 0 so the row stays 4-byte aligned for readers
  that care.
- `cmd` `opcode`: `0x0001` `shop_list`; `0x0002` `shop_buy`; `0x0003` `equip`;
  `0x0004` `inventory`; `0x0005` `reload`. Phase 10 (bodies are UTF-8 JSON,
  like every cmd):
  `0x0006` `party_invite` `{"target": <entity_id>}`;
  `0x0007` `party_respond` `{"accept": true|false}` (answers the pending
  invite — a player holds at most one at a time, newest wins);
  `0x0008` `party_leave` `{}`;
  `0x0009` `mission_list` `{"npc": <entity_id>}` (the board; result carries
  the offered templates plus this player's active/completed state);
  `0x000A` `mission_accept` `{"id": "<mission id>"}` (for a bounty this is
  the CLAIM — first party wins, everyone else gets status 3 with reason
  `"claimed"`; a priority offer is accepted from anywhere, board missions
  within interact range of the board);
  `0x000B` `mission_abandon` `{"id": "<mission id>"}`;
  `0x000C` `mission_turnin` `{"npc": <entity_id>, "id": "<mission id>"}`
  (fetch only — consumes the collected items at the board; kill/scout/bounty
  complete on the spot);
  `0x000D` `mission_share` `{"id": "<mission id>"}` — pushes a held, active,
  non-bounty mission to every party member who lacks it (works anywhere; the
  party IS the authorisation). Result carries `{"shared": <n>}`.
  Phase 11: `0x000E` `skills` `{}` — the whole sheet:
  `{"xp":{"<skill>":<n>...},"levels":{"<skill>":<L>...},"discovered":["<zone>"...]}`.
  Levels derive from XP through the curve both ends compute (GDD "Skills").
  Phase 12 (GDD "Phase 12 — the artisan loop"):
  `0x000F` `shop_sell` `{"npc": <entity_id>, "item": "<id>", "qty": <n>}` —
  the mirror of `shop_buy`; pays `floor(qty × value × sell_rate × (1 +
  sell_bonus))`;
  `0x0010` `gather` `{"node": <entity_id>}` — starts the server-timed
  channel on a node; result `{"node", "duration"}` (seconds, after
  efficacy); the outcome arrives as a `gather_end` event;
  `0x0011` `gather_cancel` `{}` — ends the channel with nothing granted;
  `0x0012` `craft` `{"npc": <entity_id>, "recipe": "<id>", "qty": <n>}` —
  at the workbench (`npc.workbench`), instant and atomic.
  Phase 13 (GDD "Phase 13 — use, modify, and the hotbar"):
  `0x0013` `use` `{"item": "<id>"}` — a carried consumable (one unit
  leaves the bag) or a worn item with an `ability`; result
  `{"item", "effect": {…}, "cooldown": <s>}`, the effect shape is the
  item's (a medkit: `{"health"}`; the scanner: `{"pings": [{"id", "def",
  "pos", "health"}]}`).
  `0x0014` `shop_buyback` `{"npc": <entity_id>, "item": "<id>"}` — the
  newest sale of that item this session comes back whole at what the
  shop paid; result is `shop_buy`'s shape.
  `0x0015`+ still reserved.
- `cmd_result` `status`: `0` ok; `1` unknown opcode; `2` malformed body;
  `3` refused by a game rule (cannot afford, out of range, unknown item,
  magazine full); `4` rate limited; `5` target not found.
- `event_id`: `0x0001` explosion (reserved); `0x0002` shot fired; `0x0003`
  hit; `0x0004` death; `0x0005` loot dropped (Phase 3); Phase 10 events carry
  UTF-8 JSON in `data` and are UNICAST to the players they concern unless
  noted: `0x0007` `mission_progress` `{"id","count","goal"}`;
  `0x0008` `mission_complete` `{"id","credits"}`;
  `0x0009` `party_update` `{"members":[{"id","name"}...]}` (sent to every
  member on any roster change; an empty list means the party dissolved);
  `0x000A` `priority_offer` `{"id","poi","expires_s"}` — BROADCAST when a
  bounty posts, so every HUD can raise the toast;
  `0x000B` `party_invited` `{"from","name"}` (to the invitee);
  `0x000C` `mission_shared` `{"from", "mission": {<the full template>}}` (to
  each recipient — the template rides along because a recipient may never
  have visited a board, and a journal that shows a bare id is not a journal);
  Phase 11: `0x000D` `skill_xp` `{"skill","xp","level","next_at","leveled"}`
  (to the trained player; awards batch server-side, at most one per skill per
  second; `leveled: true` raises the banner);
  Phase 12: `0x000E` `gather_end` `{"node","reason","item","qty"}` (to the
  channelling player; `reason` is `done` — then `item`/`qty` are the yield —
  or `moved` / `hit` / `died` / `cancel` / `depleted`, with `item`/`qty`
  absent; `entity_id` is the player);
  and `0x0006` `equipped`
  (Phase 3.5) — `entity_id` is the player whose primary slot changed and
  `data` is the item id as UTF-8, empty for "nothing equipped". Broadcast when
  the slot changes, and sent once per already-armed player when a client
  joins, so a late joiner starts with correct state. This is how another
  client learns what someone is holding: the entity row has no weapon field,
  because a value that changes a few times a session has no business costing
  bytes on every entity on every tick.
  And `0x000F` `worn` (own art, 2026-09-30) — `entity_id` is the player whose
  armor changed and `data` is `slot=item` as UTF-8 (`slot` one of `head`,
  `chest`, `legs`, `hands`, `feet`, `back`; `item` empty when the slot was
  cleared). Broadcast per changed slot, and replayed per worn slot when a
  client joins, exactly like `equipped`. The client resolves the model from
  the item's `asset` in `defs` and hangs it on the wearer's skeleton.

## Semantics

- Server ticks at 20 Hz and sends one `snapshot` (full state) per tick.
- A client sends `hello` on connect; the server replies `hello_ack` with the
  client's `entity_id`, then `terrain`, then includes the entity in snapshots.
  A client must not simulate before `terrain` arrives — it has no ground to
  stand on until then.
- `input` carries **command state with a mode**. The five floats are one
  vector `v[5]` whose meaning the trailing `mode` byte selects; every mode
  uses the same 20 bytes, so the message never changes shape.
- **The mode byte is the LAST field, and it is optional.** Appending rather
  than prepending leaves every other field at the offset it has had since
  Phase 1, and a 24-byte payload is read as mode `0` — so a client that
  predates the byte keeps working. This is the same backward-compatible shape
  as `hello`, whose payload may end after the name.
- Mode `0` is the on-foot layout: `v = [move_x, move_y, look_dir[0..2]]`.
  `move_x`/`move_y` are the wish direction in the body's
  tangent frame, each in [−1, 1] and jointly clamped to unit length;
  `look_dir` is the absolute world-space unit vector the eyes point along —
  not angles, not rates. Mode `1` is the pilot layout: `thrust` ∈ [−1, 1],
  `roll` ∈ [−1, 1], `yaw_rate`/`pitch_rate` ∈ [−`angvel_max`,
  `angvel_max`] rad/s — sanitised (clamped, non-finite → 0) per the GDD
  "Flight model" (Phase 5). Mode `2` is the ground-vehicle layout:
  `throttle` ∈ [−1, 1] (negative reverses), `steer` ∈ [−1, 1] (positive turns
  toward the vehicle's local +X), sanitised the same way per the GDD "Ground
  drive model" (Phase 4); `v[2..4]` are sent as zero and ignored.
- **The mode byte is a declaration, not authority.** The server interprets
  the payload by its own occupancy: a seated driver's input is read as
  ground-vehicle fields (a seated pilot's as pilot fields, Phase 5); an
  on-foot or seated-passenger input is read as on-foot fields, and a seated
  passenger's movement is ignored (the body is composed from the vehicle).
  The client keeps the mode consistent with the occupancy it reads from the
  snapshot (`parent_id`/`seat`).
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
- **`board` / `disembark` are events, not command state** (Phase 4): processed once,
  in receive order, on the tick they arrive — not idempotent, not replayed.
  `board` is validated server-side (GDD "Seats and occupancy"): the
  requester's body within `board_dist` of the vehicle origin, `seat` in
  1..3, seat empty. `disembark` requires the requester to be seated. Every
  request gets a `seat_result`, **unicast to the requester**, granted or
  refused (codes above); the authoritative occupancy change is visible to
  all clients in the next snapshot.
- **A vehicle in the snapshot** (Phase 4/5). The vehicle appears
  every tick with its full rigid-body state (`pos`/`quat`/`vel`, f32). A
  seated body's `entity` carries `parent_id` = the ship's `entity_id` and
  `seat` = the seat index; its `pos`/`quat`/`vel` are the **server-composed
  world transform** (ship transform × the GDD `seat_pos` offset — the local
  transform is the shared GDD table, not wire data): the client renders it
  like a remote body and never re-composes. Unboarded entities and the ship
  itself carry `parent_id = 0`, `seat = 0`.
- `quat` in a body's `entity` carries the body's **full orientation** — facing
  plus which way is up for it, which on a round world differs per player and
  is needed to draw a remote character standing correctly on the far side of
  the planet. Pitch is **not** in the quat — the body stays upright when you
  look down (GDD "First-person body"). Where a remote entity is *aiming* rides
  in `pitch_q` instead, applied to the head and the held weapon only.
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
  (M1: player join/leave, heartbeat timeout). A joining client receives
  `spawn` for every entity already in the world (M1: existing players; M2:
  the ship, which exists from server start — its `spawn.data` is the display
  name, fixed `Ship` in M2).
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

### Identity token (Phase 2)

`hello` carries a `token` alongside the display name: an opaque client-generated
string (≤ 64 bytes, printable ASCII) that the client stores locally and reuses
on every connect. The server looks up the `player` row for that token, creating
it with the starting loadout if absent, and the player's credits, inventory and
equipped item come back with it (`docs/ARCHITECTURE.md`, "Persistence").

> **This is not authentication.** The token is a bearer string with no
> verification: anyone who has it *is* that player. It exists so a reconnect
> restores your stuff on a machine you control, and so the seam that real
> accounts slot into exists before there is anything worth stealing. It must
> not be shipped to untrusted players as-is — real accounts are the named
> prerequisite in `docs/ROADMAP.md`, "Deferred".

An empty or malformed token is treated as absent, and an absent token means an
**ephemeral session whose progress is not saved**. The server never generates or
returns a token — handing out a credential over an unauthenticated channel is
exactly the thing this design is trying not to normalise. Generating and keeping
one is the client's job (`crypto.getRandomValues` → `localStorage`).

### `cmd` / `cmd_result` — the reliable channel (Phase 2)

Everything that is a question rather than a continuous control goes through one
pair of messages instead of growing the protocol a message at a time: shops,
inventory, equipping, reloading, and later looting.

- `data` is UTF-8 JSON, **≤ 4 KiB**. Malformed UTF-8, malformed JSON, or an
  oversized body is `status` 2 — never a connection close, because a buggy
  client should not be indistinguishable from a hostile one.
- Every `cmd` gets exactly one `cmd_result`, **unicast to the requester**,
  echoing `seq` and `opcode`. `seq` is a per-connection counter independent of
  `input.seq`.
- `cmd` is **processed once, in receive order, on the tick it arrives** — not
  idempotent, not replayed, not predicted. A client shows the outcome when the
  result arrives; it never assumes success.
- **Rate limited server-side: 10 `cmd` per connection per second, burst 20.**
  Excess gets `status` 4 and is not executed. This is a trust boundary — a
  purchase loop is otherwise free to hammer the store — so the limit is
  enforced regardless of what the client believes it sent.
- **Every rule is re-checked server-side**, including ones the client also
  checks: interaction range, affordability, item existence, stock. A client
  range check is a UI affordance, never a gate.
- Authoritative side effects (credits, inventory, equipment) are applied by the
  server and reflected in the result body and, where visible to others, in the
  next snapshot.

Bodies per opcode:

| opcode | request | success body |
|---|---|---|
| `shop_list` | `{"npc": <entity_id>}` | `{"stock":[{"item":"weapon.pulse","price":250}],"buyback":[{"item":"mat.ore.iron","qty":4,"price":12}]}` — `buyback` (Phase 13) is this connection's sales, newest last |
| `shop_buy` | `{"npc": <entity_id>, "item":"weapon.pulse", "qty":1}` | `{"credits":750,"inventory":[{"item":"weapon.pulse","qty":1}]}` |
| `equip` | `{"slot":"primary","item":"weapon.pulse"}` — slot is one of `defs.equip_slots`; an `accessory` item fits `accessory1`/`accessory2`; an empty `item` clears the slot (Phase 11.7) | `{"equipped":{"primary":"weapon.pulse", …}}` — the WHOLE worn map, every slot |
| `inventory` | `{}` | `{"credits":750,"inventory":[…],"equipped":{…}}` |
| `reload` | `{}` | `{"magazine":30,"reserve":90}` |
| `shop_sell` (Phase 12) | `{"npc": <entity_id>, "item":"mat.ore.iron", "qty":4}` | `{"credits":762,"inventory":[…]}` — same shape as `shop_buy` |
| `gather` (Phase 12) | `{"node": <entity_id>}` | `{"node":1048577,"duration":2.85}` |
| `gather_cancel` (Phase 12) | `{}` | `{}` (refused `not_gathering` if no channel is running) |
| `use` (Phase 13) | `{"item":"consumable.medkit"}` | `{"item":"consumable.medkit","effect":{"health":100},"cooldown":8}` |
| `craft` (Phase 12) | `{"npc": <entity_id>, "recipe":"recipe.cells", "qty":1}` | `{"inventory":[…],"crafted":{"item":"ammo.cell","qty":30}}` — `qty` includes any `craft_extra` bonus |

A refusal (`status` 3) carries `{"reason":"<machine-readable code>"}` — e.g.
`insufficient_credits`, `out_of_range`, `unknown_item`, `no_stock`,
`magazine_full`, `no_ammo`, `locked` (a `shop_buy` below the item's
`unlock_requirements` level, GDD "Skills"; Phase 12: also a `gather` below
the node's `level` or a `craft` below the recipe's). Phase 12 adds
`unsellable`, `equipped`, `not_owned`, `bad_qty` (`shop_sell`); `busy`,
`no_tool`, `depleted`, `no_space` (`gather`); `not_gathering`
(`gather_cancel`); `unknown_recipe`, `missing_materials` (`craft`).
Phase 13 adds `unusable`, `dead`, `no_effect` and `cooldown` (`use`; a
`cooldown` refusal carries `"ready_in": <s>` beside the reason) and
`no_buyback` (`shop_buyback`). The
client maps codes to text; the server never sends prose for display.

### `defs` — the data the client needs (Phase 2)

Sent once, **after `terrain` and before the first `snapshot`**. UTF-8 JSON: the
item table, weapon rule tables, entity-type hitboxes and max health, the
interactable metadata for the zone, and (Phase 11) the skill roster with its
per-level efficacy rates and the declared synergies, so the K panel's
tooltips run the server's own arithmetic. It is the server's own `server/data/`
content, filtered to what a client needs to render and predict.

Phase 11.7 adds, additively: `equip_slots` (the ordered slot set the character panel draws and `equip` validates against), `inv_slots`, and per item `desc`, `armor:{value}`, `weapon:{damage,fire_interval,magazine,max_range,…}` for tooltips and the stats block.

Phase 12 adds, additively: `nodes` (the `nodes.json` table — `id`, `name`,
`asset`, `skill`, `level`, `tool`, `channel`, `yields`; the client draws
prompts, models and the locked/no-tool hint from it), `recipes` (the
`recipes.json` table verbatim), `constants.sell_rate`, and per item
`value` (absent = unsellable) and `supersedes` (on a tool). `npcs[]`
gains the workbench with `verb: "Use"`.

Phase 13 adds, additively: per item `consumable: {heal, cooldown}`,
`ability: {id, range, cooldown}`, `mod: {damage, magazine, max_range,
falloff_start, falloff_end}` (each key optional, a delta), and `mod`
in `equip_slots`. Nothing about the hotbar is on the wire.

The client **must not fire, predict damage, or draw an inventory before `defs`
arrives** — the same rule as `terrain`, for the same reason: it has no data to
do it with. One source of truth, shipped, rather than a copy of the same JSON
checked into the client.

Like `terrain`, it is one message and therefore capped at 64 KiB. That is
generous for Phase 2 and is a known, bounded place where chunking becomes
necessary later — not a surprise to discover at runtime.

### `fire` — shooting (Phase 2)

`fire` is an **event**: processed once, in receive order, on the tick it
arrives. Not command state, not idempotent, not replayed.

- `seq` is the `input.seq` in effect when the client pulled the trigger. It
  correlates the shot with the input stream and, with the server's own RTT
  measurement, tells the server how far to rewind.
- `dir` is the world-space unit aim vector. Non-finite or non-unit values are
  rejected (the shot is dropped, no event).
- **The origin is never client-supplied.** The server uses its own recorded eye
  position for that shooter at the rewound tick. A client that could name its
  own muzzle position could shoot from anywhere on the planet.
- **Rewind is bounded by the server's measurement, not the client's claim.**
  The server rewinds candidate targets by `staleness + RTT/2 + interp_delay`,
  clamped to `[0, 500] ms` (GDD "Lag compensation" has the derivation). Every
  term is server-observed: the RTT is its own measurement, and `staleness`
  comes from the tick the server itself chose to run that `seq` on. Naming an
  ancient `seq` buys nothing but the clamp. A client cannot ask for more.
- **The client owes `interp_delay` back.** The rewind above assumes the shooter
  was rendering remote entities at `serverClock − interp_delay`, on a clock
  synchronised to the server's. A client that instead renders a fixed offset
  behind local packet arrival sits a whole one-way trip further into the past
  than the server rewinds to, and will miss every moving target. See GDD "Lag
  compensation".
- Cadence is enforced server-side from the weapon's rule table with one tick of
  tolerance; an early shot is dropped, not queued. Ammunition is likewise
  checked and decremented server-side.
- A resolved shot produces broadcast `event`s — `shot fired` always, `hit` and
  `death` when applicable — so every client draws the same tracer and the same
  outcome. There is no `fire_result`: the shooter learns what happened from the
  same events as everyone else.

`event` payloads (binary, little-endian, following the `event` header):

| event | `entity_id` | `data` |
|---|---|---|
| shot fired `0x0002` | shooter | `f32 origin[3]` \| `f32 dir[3]` \| `f32 dist` |
| hit `0x0003` | victim | `u32 shooter` \| `f32 point[3]` \| `u16 damage` \| `u16 health_after` |
| death `0x0004` | victim | `u32 killer` (0 = none) |
| equipped `0x0006` | the player | item id, UTF-8 (empty = nothing equipped) |

### `colliders` — static world geometry (Phase 2)

Sent once, after `terrain` and before the first `snapshot`. The list is
**world-space** and absolute: the server composes it at load from zone files
authored in a local tangent frame, so neither the client nor the sim ever does
that composition (GDD "Static colliders").

- Both sims resolve against this exact list, in the exact order the GDD
  integrator specifies. A mismatch is a player standing inside a wall on one
  screen and outside it on the other.
- Rock props remain client-scattered and **non-collidable** — they are not in
  this list and the server does not know about them (ARCHITECTURE, "Client").
  Anything the player must not walk through is authored into a zone file.
- One message, 42 B per collider: **1,560 colliders is the cap** at the 64 KiB
  frame limit. Same bounded-change note as `terrain`.

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

`client_ver` / `server_ver` are u16 protocol versions: Phase 1 = `1`,
Phase 2 = `2`. The server rejects a `hello` carrying a different version by
closing the connection (code 1002).
