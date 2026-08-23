# Phase 2 — wave 2: the substance

Wave 1 left a codec that carries the new messages and a store that opens and
migrates. Wave 2 fills in the insides. Nothing here changes a byte on the wire.

Batches are dispatch groups: everything in a batch is parallel-safe; a later
batch may depend on an earlier one.

Contracts these briefs implement — **paste from these, and when a brief and a
contract disagree, the contract wins**: `docs/PROTOCOL.md` (`cmd`, `defs`,
`fire`, `colliders`, "Identity token"), `docs/GDD.md` ("Phase 2 — items,
weapons, combat, interaction"), `server/data/*.json`.

---

## Batch C — codecs and data loading

### W2-1 · `netcode` · Phase 2 message codecs

```
TASK:     Encode/parse the five Phase 2 messages in the protocol package.
FILES:    server/internal/protocol/phase2.go (new)
          server/internal/protocol/phase2_test.go (new)
CONTRACT: package protocol. Wire layouts are docs/PROTOCOL.md "Message types":

          type Cmd struct { Seq uint16; Opcode uint16; Data []byte }
            // u16 seq | u16 opcode | u32 data_len | bytes data
          type CmdResult struct { Seq, Opcode uint16; Status uint8; Data []byte }
            // u16 seq | u16 opcode | u8 status | u32 data_len | bytes data
          type Defs struct { Data []byte }
            // u32 data_len | bytes data
          type Fire struct { Seq uint16; Dir [3]float32 }
            // u16 seq | f32 dir[3]
          type Collider struct {
              Kind   uint8      // ColliderBox | ColliderSphere
              Center [3]float32
              Half   [3]float32
              Quat   [4]float32
          }
          type Colliders struct { List []Collider }
            // u16 count | collider x count; collider is
            // u8 kind | u8 _pad | f32 center[3] | f32 half[3] | f32 quat[4]

          Functions, matching the package's existing naming:
            EncodeCmd, ParseCmd, EncodeCmdResult, ParseCmdResult,
            EncodeDefs, ParseDefs, EncodeFire, ParseFire,
            EncodeColliders, ParseColliders

          Validation at the boundary — these decode untrusted client bytes:
            - ParseCmd rejects Data longer than MaxCmdBody with ErrBadPayload.
            - ParseFire rejects a non-finite Dir component, and a Dir whose
              length is not within 1e-3 of 1, with ErrBadPayload.
            - Every parser rejects a truncated payload with ErrBadPayload —
              use the package's existing `need` helper.
            - ParseColliders rejects count > ColliderMax.
            - _pad is written as 0 and ignored on read.
STEPS:    1. Write phase2.go using the package's existing putU16/putU32/putF32
             and need helpers.
          2. Write phase2_test.go: round-trip each message, plus the rejection
             cases above.
VERIFY:   cd server && go test ./internal/protocol  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-2 · `frontend` · Phase 2 message codecs, TS side

```
TASK:     Encode/parse the five Phase 2 messages in the client codec.
FILES:    client/src/net/phase2.ts (new)
CONTRACT: Same wire layouts as W2-1 (docs/PROTOCOL.md). Exports:
            encodeCmd(seq, opcode, body: unknown): Uint8Array   // JSON.stringify + UTF-8
            decodeCmdResult(payload: Uint8Array): { seq, opcode, status, body: unknown }
            decodeDefs(payload: Uint8Array): unknown            // JSON.parse
            encodeFire(seq: number, dir: [number, number, number]): Uint8Array
            decodeColliders(payload: Uint8Array): Collider[]
            export interface Collider {
              kind: number; center: [number,number,number];
              half: [number,number,number]; quat: [number,number,number,number];
            }
          Decoders throw ProtocolError (already exported from
          client/src/net/protocol.ts) on a truncated payload, a body over
          MAX_CMD_BODY, or a count over COLLIDER_MAX. A malformed JSON body
          throws ProtocolError too — never a raw SyntaxError, and never a
          silent {}.
          No DOM: this file must compile under tsconfig.sim.json's DOM-free lib.
STEPS:    1. Write phase2.ts importing the frame/unframe and error helpers
             from ./protocol.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W2-3 · `netcode` · Embed and serve `server/data`

```
TASK:     Embed the server/data JSON, parse it once at startup, and build the
          `defs` payload the client receives.
FILES:    server/internal/defs/defs.go (new)
          server/internal/defs/defs_test.go (new)
CONTRACT: package defs

          //go:embed all:data
          // (the data directory is symlinked or moved under the package;
          //  if server/data cannot be embedded from here, report
          //  BLOCKED: NOT FOUND with the path you tried)

          type Weapon struct {
              Damage        int     `json:"damage"`
              FireInterval  float64 `json:"fire_interval"`
              Magazine      int     `json:"magazine"`
              ReloadTime    float64 `json:"reload_time"`
              AmmoItem      string  `json:"ammo_item"`
              MaxRange      float64 `json:"max_range"`
              FalloffStart  float64 `json:"falloff_start"`
              FalloffEnd    float64 `json:"falloff_end"`
              FalloffMin    float64 `json:"falloff_min"`
              SpreadBase    float64 `json:"spread_base"`
              SpreadMax     float64 `json:"spread_max"`
              SpreadPerShot float64 `json:"spread_per_shot"`
              SpreadDecay   float64 `json:"spread_decay"`
          }
          type Item struct {
              ID string; Name string; Kind string; Slot string
              Asset string; StackMax int; Weapon *Weapon
          }
          type EntityDef struct {
              Type string; Asset string; MaxHealth int
              Hitbox struct{ Radius, Height float64 }
              Damageable bool; Respawn float64
          }
          type NPC struct {
              ID, Name, Asset, Kind, Verb string
              Stock []struct{ Item string; Price int64 }
          }

          type Registry struct {
              StartCredits int64
              StartItems   []struct{ Item string; Qty int }
              InvSlots     int
              Items        map[string]Item
              Entities     map[string]EntityDef
              NPCs         map[string]NPC
              Zones        map[string]Zone   // Zone: raw, as authored
              Payload      []byte            // the `defs` message body
          }

          func Load() (*Registry, error)

          Payload is the JSON the client needs to render and predict: the item
          table, entity defs, NPC verbs and display names, and the GDD
          interaction constants. It must NOT contain shop prices' server-side
          bookkeeping beyond what a shop panel shows, and must not contain
          anything the client is not allowed to know. Keep it under 64 KiB —
          one message (docs/PROTOCOL.md, `defs`).
STEPS:    1. Move or symlink server/data so the package can embed it, or embed
             by relative path if the toolchain allows.
          2. Write defs.go: parse, index by id, build Payload.
          3. Write defs_test.go: Load() succeeds, weapon.pulse resolves,
             Payload is valid JSON under 64 KiB.
VERIFY:   cd server && go test ./internal/defs  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-4 · `netcode` · Zone loader: tangent frame → world

```
TASK:     Compose zone files from their local tangent frame into world-space
          colliders and entity placements.
FILES:    server/internal/defs/zone.go (new)
          server/internal/defs/zone_test.go (new)
CONTRACT: Implements docs/GDD.md "Static colliders" -> "Authoring frame"
          VERBATIM. The composition, for a zone with origin_dir and a terrain
          radius sampler `radius(dir) float64`:

            up     = normalize(origin_dir)
            ref    = (0,0,1); if |dot(ref, up)| > 0.999 then ref = (1,0,0)
            north  = normalize(ref - up*dot(ref, up))
            east   = cross(up, north)
            origin = up * radius(up)

            per object with local p = (x, y, z):
              dir       = normalize(origin + east*p.x + north*p.z)
              world_pos = dir * (radius(dir) + p.y)          // y is height ABOVE ground
              north_p   = normalize(north - dir*dot(north, dir))
              east_p    = cross(dir, north_p)
              world_quat = quatFromBasis(east_p, dir, north_p) * local_quat

          local_quat is a rotation of `yaw` degrees about local +y.

          Two rules the GDD calls out and this code must honour:
            - y is height above the GROUND, not above the zone origin plane.
            - each object re-derives its frame at its OWN position (north_p /
              east_p above), never reusing the zone origin's frame.

          func ComposeZone(z Zone, radius func([3]float64) float64)
                  (colliders []protocol.Collider, placements []Placement, err error)

          type Placement struct {
              Type string      // "npc" | "target"
              Def  string      // npcs.json id, or entity_defs type
              Pos  [3]float64
              Quat [4]float64
          }
STEPS:    1. Write zone.go per the contract.
          2. Write zone_test.go with a UNIT-SPHERE radius func (radius = 150
             everywhere): assert a collider at local (0,0,0) lands at
             origin_dir*150; assert an object at local (40,0,0) has an up
             vector within 1e-9 of its own normalized position, NOT the zone
             origin's — this is the test that catches the leaning-wall bug.
VERIFY:   cd server && go test ./internal/defs  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

---

## Batch D — sim: colliders, entities, history

### W2-5 · `netcode` · Flatten the terrain under a zone

```
TASK:     Level the generated radius field under each zone before it is
          encoded for the wire.
FILES:    server/internal/terrain/flatten.go (new)
CONTRACT: docs/GDD.md, "Static colliders" -> "The site is flattened, not
          assumed flat".

          func Flatten(f *Field, originDir [3]float64, radius, falloff float64)

          For every grid sample whose direction d makes an arc distance
          a = acos(clamp(dot(d, originDir),-1,1)) * planetRadius from the zone
          origin:
            a <= radius            -> set the sample to the zone origin's radius
            radius < a <= radius+falloff
                                   -> smoothstep blend from the origin radius
                                      to the sample's own value
            a > radius + falloff   -> unchanged
          Use smoothstep t*t*(3-2*t) so the seam has no visible crease.

          MUST run BEFORE the field is encoded for the `terrain` message, so
          both ends see the same flattened field with no special case. Shared
          cube-face edges must still carry identical values afterwards — the
          function operates on directions, so duplicated edge samples get the
          same result by construction; the test asserts it.
STEPS:    1. Write flatten.go operating on the existing terrain Field type.
          2. Add one test asserting: the centre sample equals the origin
             radius; a sample beyond radius+falloff is unchanged; the two
             copies of a shared cube-face edge sample remain equal.
VERIFY:   cd server && go test ./internal/terrain  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~120 lines, 10 tool calls.
```

### W2-6 · `netcode` · Static collider resolution in the on-foot step

```
TASK:     Add collider push-out and terrain re-seat to the sim step.
FILES:    server/internal/sim/collide.go (new)
CONTRACT: docs/GDD.md, "Static colliders" -> "Integrator addition", steps 8
          and 9, VERBATIM and in that order.

            func ResolveColliders(pos, vel [3]float64, up [3]float64,
                                  grounded bool, cs []protocol.Collider,
                                  radiusAt func([3]float64) float64)
                    (outPos, outVel [3]float64, outGrounded bool)

          Step 8, for each collider in the order received:
            c = pos + up*body_sphere_h
            (hit, n, depth) = nearest(c, body_radius, collider)
            if hit:
                pos = pos + n*depth
                c   = pos + up*body_sphere_h        // recompute after moving
                if dot(vel, n) < 0: vel = vel - n*dot(vel, n)
                if dot(n, up) >= cos(max_slope): grounded = true
          Step 9:
            r = radiusAt(normalize(pos))
            if |pos| < r:
                pos = normalize(pos)*r
                vel = vel - up*min(dot(vel, up), 0)

          Constants (GDD rule table): body_radius 0.35, body_sphere_h 0.9,
          max_slope 50 deg.

          `nearest` is sphere-vs-OBB (rotate the sphere centre into the
          collider's local frame with the conjugate quat, clamp to +/-half,
          rotate back) and sphere-vs-sphere. Both total, neither allocating.
          Degenerate case: a sphere centre exactly at a box centre has no
          defined exit normal — return `up` and the full depth rather than
          NaN.
STEPS:    1. Write collide.go per the contract. Do not modify the existing
             step function — wiring it in is W2-7.
          2. Add one test: a sphere driven into a box face stops at the face,
             a sphere clear of the box is untouched, and the degenerate case
             returns a finite result.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-7 · `netcode` · Call the collider resolve from the step *(after W2-6)*

```
TASK:     Wire ResolveColliders into the authoritative on-foot step, after
          terrain resolution.
FILES:    server/internal/sim/sim.go (edit)
CONTRACT: The step gains the collider list as state it already has access to.
          Call ResolveColliders IMMEDIATELY after the existing terrain
          resolution line (GDD integrator step 7), with the result feeding
          pos/vel/grounded. Nothing before step 7 changes.
          An empty collider list must produce a bit-identical result to the
          Phase 1 step — the Phase 1 conformance test (ROADMAP criterion 5)
          still has to pass unchanged.
STEPS:    1. Add the collider slice to the step's inputs.
          2. Insert the single call after terrain resolution.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~40 lines, 10 tool calls.
```

### W2-8 · `frontend` · Mirror the collider resolve *(after W2-6)*

```
TASK:     Port ResolveColliders to the client sim, identically.
FILES:    client/src/sim/collide.ts (new)
CONTRACT: The SAME algorithm and the SAME constants as W2-6 — this is the
          Go/TS pair the conformance test diffs, so a difference here is a
          player standing inside a wall on one screen and outside it on the
          other.
            export function resolveColliders(
              pos: Vec3, vel: Vec3, up: Vec3, grounded: boolean,
              cs: Collider[], radiusAt: (d: Vec3) => number
            ): { pos: Vec3; vel: Vec3; grounded: boolean }
          Same iteration order, same degenerate-case fallback, f64 throughout.
          No DOM: this file compiles under tsconfig.sim.json.
STEPS:    1. Port collide.go line for line.
          2. Wire the call into client/src/sim/step.ts at the same point the
             Go step calls it — if that needs a second file, do it, and say so.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.sim.json  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (collide.ts + step.ts), ~150 lines, 10 tool calls.
```

### W2-9 · `netcode` · Generic entity store

```
TASK:     Generalise the world from "a map of player bodies" to type-tagged
          entities with a per-type step.
FILES:    server/internal/sim/entities.go (new)
CONTRACT: type EntityKind uint16   // protocol.EntityType* values

          type Ent struct {
              ID     uint32
              Kind   EntityKind
              Pos, Vel [3]float64
              Quat   [4]float64
              Health int
              Flags  uint8
              PitchQ int8
              Def    string        // defs id: npc/item/entity-def key
              Data   any           // kind-specific state (player body, target timer)
          }

          type World struct {
              Ents  map[uint32]*Ent
              order []uint32       // deterministic iteration order
          }
          func (w *World) Add(e *Ent)
          func (w *World) Remove(id uint32)
          func (w *World) Step(dt float64, ctx StepCtx)   // dispatch by Kind

          Iteration MUST be deterministic — step `order`, never the map. A Go
          map's iteration order is randomised per run, and a sim that steps
          entities in a different order every boot is not reproducible, which
          breaks every conformance and regression test downstream.
          Player bodies keep their exact Phase 1 behaviour: this brief moves
          them into the store, it does not change how they step.
STEPS:    1. Write entities.go.
          2. Add one test: adding three entities and stepping twice visits
             them in insertion order both times.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-10 · `netcode` · Position history ring

```
TASK:     Keep 0.5 s of per-entity position history for lag compensation.
FILES:    server/internal/sim/history.go (new)
CONTRACT: docs/GDD.md, "Health and damage" -> "Lag compensation".

            type History struct{ ... }   // ring buffer, no allocation per tick
            func NewHistory(ticks int) *History
            func (h *History) Record(tick uint32, id uint32, pos [3]float64, up [3]float64)
            // At returns the recorded pos/up for id at the tick nearest to
            // `tick`, and false if that tick is outside the retained window.
            func (h *History) At(tick uint32, id uint32) (pos, up [3]float64, ok bool)

          Window: rewind_max 0.5 s at 20 Hz = 10 ticks. Size the ring from
          that, not from a magic number.
          `up` is recorded alongside pos because a hitbox capsule stands along
          the entity's OWN radial up (GDD "Health and damage"), which differs
          per entity on a round world and cannot be re-derived from a rewound
          position alone without re-sampling.
STEPS:    1. Write history.go.
          2. Add one test: record 20 ticks, assert tick-3-ago resolves and
             tick-15-ago reports ok == false.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~120 lines, 10 tool calls.
```

---

## Batch E — gameplay rules

### W2-11 · `netcode` · Inventory, credits and shop rules

```
TASK:     Implement the inventory/currency operations the cmd opcodes call.
FILES:    server/internal/sim/inventory.go (new)
CONTRACT: docs/GDD.md, "Items and currency" and "Shop NPCs".

            func Buy(p *store.Player, npc defs.NPC, item string, qty int,
                     reg *defs.Registry) error
            func Equip(p *store.Player, slot, item string, reg *defs.Registry) error
            func AddItem(p *store.Player, item string, qty int, reg *defs.Registry) error

          Buy validates IN THIS ORDER and returns a typed refusal reason on
          the first failure (the strings become cmd_result's
          {"reason": ...}): unknown_item, no_stock, insufficient_credits,
          no_space. Then it debits credits and grants the items
          ATOMICALLY — on any error the player struct must be UNCHANGED. Build
          the new state and swap it in; do not mutate then roll back.
          inv_slots 20 stacks; stack_max per item def; weapons stack_max 1.
          Equip refuses (unknown_item / not_owned / wrong_slot).
          qty must be >= 1 and <= 1000; anything else is unknown_item's
          sibling refusal `bad_qty`. A negative qty must never credit the
          player — that is the classic shop exploit and the test asserts it.
STEPS:    1. Write inventory.go.
          2. Add one test covering: a successful buy; each refusal reason;
             qty = -1 leaves credits unchanged; a failed buy leaves the
             player byte-identical.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-12 · `netcode` · Hitscan resolution

```
TASK:     Resolve a shot: rewind, spread, ray-vs-capsule, falloff, damage.
FILES:    server/internal/sim/combat.go (new)
CONTRACT: docs/GDD.md, "Weapons" and "Health and damage".

            type Shot struct {
                Shooter uint32; Dir [3]float64; Tick uint32; RewindTicks int
            }
            type Hit struct {
                Victim uint32; Point [3]float64; Damage int; HealthAfter int
            }
            func ResolveShot(w *World, h *History, s Shot, wp defs.Weapon,
                             rng *rand.Rand) (Hit, bool)

          Order:
            1. Origin = the SHOOTER's rewound eye position from History —
               never a client-supplied value.
            2. Apply spread: rotate Dir by an angle drawn from the server's
               rng within the current cone half-angle. The cone is
               spread_base, grown spread_per_shot per shot and decayed
               spread_decay deg/s, clamped to spread_max.
            3. For each damageable entity except the shooter, test the ray
               against a capsule of the entity's def hitbox radius/height,
               standing along that entity's OWN rewound up.
            4. Nearest hit within max_range wins; beyond max_range there is
               no hit at all.
            5. damage = round(wp.Damage * falloff), falloff linear from 1.0
               at falloff_start to falloff_min at falloff_end, clamped both ends.
            6. Apply damage, clamp health at 0.

          RewindTicks is passed in already clamped to [0, 10] by the caller —
          this function must NOT read anything client-supplied to decide how
          far to rewind.
STEPS:    1. Write combat.go with a ray-vs-capsule helper.
          2. Add one test: a shot down a known axis at a 30 m target hits and
             deals 25; the same shot offset 1 m laterally misses; a target at
             130 m is out of range.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-13 · `netcode` · Target dummies

```
TASK:     Give target entities their hit response and respawn.
FILES:    server/internal/sim/target.go (new)
CONTRACT: docs/GDD.md, "Health and damage".
            - Entity kind protocol.EntityTypeTarget, max_health 100.
            - Health reaching 0: set FlagDead, start a target_respawn (3.0 s)
              timer, and emit a death event through the step context.
            - On timer expiry: restore full health, clear FlagDead, same
              position. The entity is NEVER removed and NEVER re-added — its
              entity_id is stable across respawns, so clients do not churn
              spawn/despawn for a target that is shot every four seconds.
            - A dead target is not damageable and not hit-testable.
            func StepTarget(e *Ent, dt float64, ctx StepCtx)
STEPS:    1. Write target.go and register it in the World.Step dispatch.
          2. Add one test: damage to 0 sets dead, 3.0 s later health is back
             to 100 with the same ID, and a shot during the dead window
             resolves no hit.
VERIFY:   cd server && go test ./internal/sim  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~120 lines, 10 tool calls.
```

### W2-14 · `netcode` · `cmd` dispatch and rate limit

```
TASK:     Route cmd opcodes to the rules, enforce the rate limit, reply once.
FILES:    server/internal/server/cmd.go (new)
CONTRACT: docs/PROTOCOL.md, "`cmd` / `cmd_result` — the reliable channel".

            func (c *Client) handleCmd(raw protocol.Cmd)

          Rules, all enforced server-side regardless of what the client sent:
            - Rate limit 10/s per connection, burst 20. Over: reply
              StatusRateLimited and DO NOT execute. Use a token bucket
              refilled from the tick, not a timestamp list.
            - Body over MaxCmdBody, invalid UTF-8, or invalid JSON:
              StatusMalformed.
            - Unknown opcode: StatusUnknownOpcode.
            - shop_list / shop_buy RE-VALIDATE interact_dist (3.0 m) and
              interact_cone (20 deg half-angle) against the server's own
              positions, per GDD "Interaction". A client-side range check is a
              UI affordance and is not trusted here.
            - Exactly one cmd_result per cmd, unicast, echoing seq and opcode.
            - A refusal carries {"reason":"<code>"} — machine-readable codes
              only, never prose for display.
          Opcodes: OpShopList, OpShopBuy, OpEquip, OpInventory, OpReload,
          calling into the W2-11 inventory functions.
STEPS:    1. Write cmd.go.
          2. Add one test: over-rate cmds are refused and not executed; an
             out-of-range shop_buy is refused with reason out_of_range and
             leaves credits unchanged.
VERIFY:   cd server && go test ./internal/server  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-15 · `netcode` · Identity: token → player row

```
TASK:     Load a player's row on join and save it on leave and on a timer.
FILES:    server/internal/server/identity.go (new)
CONTRACT: docs/PROTOCOL.md, "Identity token"; docs/ARCHITECTURE.md,
          "Persistence".
            - On hello: if Token is empty, this is an EPHEMERAL session —
              start from the default loadout and never write a row.
            - Otherwise GetPlayer(token). Absent: create the row with
              start_credits and start_items from the registry, and the
              spawn position.
            - PutPlayer on disconnect, and every 30 s while connected.
            - Every store call runs on a BACKGROUND goroutine with a context
              deadline, never from the tick loop — the tick budget is 50 ms
              and a Postgres round trip is not allowed inside it
              (ARCHITECTURE, "Persistence"). The tick reads and writes the
              in-memory player struct only.
            - A store error is logged and the session continues unsaved. A
              database outage must not disconnect players mid-fight.
STEPS:    1. Write identity.go.
          2. Add one test with a temp sqlite store: join with a token, mutate
             credits, save, rejoin with the same token, assert credits
             restored; join with an empty token and assert no row is written.
VERIFY:   cd server && go test ./internal/server  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~150 lines, 10 tool calls.
```

### W2-16 · `netcode` · Wire it into the connection lifecycle *(after C, D, E)*

```
TASK:     Send defs and colliders on join, route fire, populate the new entity
          row fields in the snapshot.
FILES:    server/internal/server/server.go (edit)
CONTRACT: - Join order becomes: hello_ack, terrain, defs, colliders, then
            snapshots. The client may not simulate before terrain AND may not
            fire or draw an inventory before defs (docs/PROTOCOL.md).
          - defs and colliders are built ONCE at startup and cached, like the
            terrain field already is — never rebuilt per connection.
          - MsgFire: parse, clamp rewind to [0, 10] ticks from the server's
            own smoothed RTT/2 for that connection, enforce fire_interval with
            one tick of tolerance, decrement the magazine, call ResolveShot,
            and broadcast the shot_fired / hit / death events.
          - Snapshot encoding fills Health, Flags and PitchQ from the entity;
            ParentID and Seat stay 0.
STEPS:    1. Add the cached defs/colliders payloads and the join-order sends.
          2. Add the MsgFire case.
          3. Fill the new entity fields at snapshot encode.
VERIFY:   cd server && go build ./... && go test ./...  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

---

## Batch F — client

### W2-17 · `frontend` · Identity token

```
TASK:     Generate a token once, keep it, and send it in hello.
FILES:    client/src/net/identity.ts (new)
CONTRACT: export function getToken(): string
            - Reads localStorage key "sa.token".
            - Absent or malformed: generate 32 hex chars from
              crypto.getRandomValues (NOT Math.random — this is the key to a
              player's persistent row), store it, return it.
            - localStorage unavailable (private window, blocked storage):
              return "" and let the session be ephemeral. Never throw — a
              storage exception must not stop the game from loading.
          Wrap every read and write in try/catch.
STEPS:    1. Write identity.ts.
          2. Pass getToken() into the existing hello encoder call in
             client/src/net/netClient.ts.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files, ~60 lines, 10 tool calls.
```

### W2-18 · `frontend` · Consume `defs`

```
TASK:     Parse the defs message and expose typed lookups to the rest of the
          client.
FILES:    client/src/net/defs.ts (new)
CONTRACT: export interface Registry { items, entities, npcs, interact }
          export function parseDefs(payload: Uint8Array): Registry
          export function itemDef(reg, id) / entityDef(reg, type)

          The client MUST NOT fire, predict damage, or draw an inventory
          before defs arrives (docs/PROTOCOL.md, `defs`) — expose a
          `ready: boolean` the caller gates on, the same way it already gates
          on terrain.
          Treat the payload as untrusted: a missing field is a typed default,
          never an undefined that reaches the renderer.
STEPS:    1. Write defs.ts on top of decodeDefs from ./phase2.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~120 lines, 10 tool calls.
```

### W2-19 · `frontend` · Interaction raycast and prompt

```
TASK:     Find what the player is looking at and show its verb.
FILES:    client/src/input/interact.ts (new)
CONTRACT: docs/GDD.md, "Interaction".
            - Candidate: within interact_dist 3.0 m of the eye AND
              dot(look_dir, normalize(target_eye - eye)) >= cos(20 deg).
            - Among candidates the LARGEST dot product wins — the thing you
              are most directly looking at, not the nearest. Nearest picks the
              wrong target when two NPCs stand together.
            - Expose { entityId, verb } | null for the HUD to render.
            - E sends the opcode the verb maps to. The client check is a UI
              affordance; the server re-validates (W2-14).
STEPS:    1. Write interact.ts as a pure function over the entity list plus
             the eye transform, called from the render loop.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~120 lines, 10 tool calls.
```

### W2-20 · `frontend` · Shop panel

```
TASK:     A DOM panel listing an NPC's stock, buying through cmd.
FILES:    client/src/hud/shop.ts (new)
CONTRACT: - Opens on interact with a shop NPC; sends OpShopList and renders
            the returned stock (item name, price, affordability).
          - Buy sends OpShopBuy and applies the RESULT — never optimistically.
            A cmd is not idempotent and not predicted (docs/PROTOCOL.md).
          - A refusal renders the mapped text for its {"reason"} code; unknown
            codes render a generic message.
          - Item names come from defs and are rendered with textContent, NEVER
            innerHTML. Same rule the nametag code already follows: display
            strings crossing into the DOM are treated as text, not markup.
          - Escape and movement close the panel; pointer lock is released
            while it is open and restored when it closes.
STEPS:    1. Write shop.ts using the existing HUD's DOM-overlay pattern.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W2-21 · `frontend` · Weapon in hands

```
TASK:     Attach the equipped weapon to the body and animate firing.
FILES:    client/src/scene/weapon.ts (new)
CONTRACT: - Load the item def's asset through art/manifest.json, falling back
            to a flat-shaded box when the .glb is absent — the existing
            placeholder rule (ARCHITECTURE, "Client"), so this never blocks
            on art.
          - Parent the model to the character's `arm.r` so it follows the
            existing procedural animation; align the model's `grip` node to
            the hand.
          - The LOCAL player sees it from inside the body (GDD "First-person
            body"): the weapon renders, the head does not. No separate
            viewmodel arms, no second camera, no FOV hack.
          - Muzzle flash at the model's `muzzle` node; a short recoil kick on
            the model only — never on the camera. Look is never modified by
            anything but the player's mouse (ARCHITECTURE: a corrected view is
            motion sickness).
STEPS:    1. Write weapon.ts with attach/detach and a fire() animation hook.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W2-22 · `frontend` · Fire, tracers and hit markers

```
TASK:     Send fire on click and render the resulting events.
FILES:    client/src/net/fire.ts (new)
CONTRACT: - Left click (while pointer-locked, defs ready, weapon equipped,
            not reloading) sends encodeFire(inputSeq, lookDir).
          - Client-side cadence gate mirrors fire_interval so a held button
            does not spam the socket; the server enforces the real rule.
          - Draw the tracer from the shot_fired EVENT (origin, dir, dist), not
            from local state — the server resolves spread, so the local
            direction is not where the shot went (GDD "Weapons", and the open
            question about tracer source).
          - hit events: a brief hit marker and a damage number at `point`.
          - death events: let the target's own renderer handle the state; this
            file only forwards the event.
STEPS:    1. Write fire.ts.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~140 lines, 10 tool calls.
```

### W2-23 · `frontend` · HUD: ammo, credits, prompt

```
TASK:     Extend the HUD with the Phase 2 readouts.
FILES:    client/src/hud/hud.ts (edit)
CONTRACT: Add to the existing overlay, in its existing style: magazine /
          reserve ammo, credits, the reload indicator, and the interaction
          prompt from W2-19. Keep the Phase 1 readouts (speed, nearest player,
          connection state) unchanged.
          All values come from cmd_result and defs — the HUD never computes an
          authoritative number itself.
          Render every string with textContent, never innerHTML.
STEPS:    1. Extend the HUD's element set and its update function.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~100 lines, 10 tool calls.
```

### W2-24 · `frontend` · Remote aim, targets, and world structures

```
TASK:     Apply pitch_q to remote bodies and render targets, NPCs and
          collider structures.
FILES:    client/src/scene/world.ts (edit)
CONTRACT: - pitch_q -> radians: pitchQ * (Math.PI / 2) / 127. Apply to the
            remote character's `head` node and its held weapon ONLY. The body
            stays upright (GDD "First-person body"); applying pitch to the
            torso is the bug this field exists to avoid.
          - Spawn a renderer per entity_type: NPC (npc.shopkeeper),
            target (prop.target, `plate` recoloured while dead).
          - Render the colliders list: struct.wall scaled to each box's
            half-extents, struct.post scaled to each sphere's radius, both
            placed by the collider's world pos/quat. VISUAL ONLY — the
            authoritative shape is the message, so never derive collision from
            the mesh.
          - Placeholder fallback for every missing .glb, as today.
STEPS:    1. Add the pitch application and the three renderers.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json && npm run build  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

---

## Wave 2 exit check (main thread)

```
cd server && go build ./... && go vet ./... && go test ./...
cd client && npx tsc --noEmit -p tsconfig.json && npx tsc --noEmit -p tsconfig.sim.json && npm run build
make up
```

Then the trajectory diff with an empty collider list must still match Phase 1's
result — if it does not, W2-7 changed the step for the no-collider case and
that is a product bug, not a tolerance to widen.
