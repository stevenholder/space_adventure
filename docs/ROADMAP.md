# Roadmap

Status: **Phases 1–3 complete**, and both open Phase 2 criteria closed
2026-08-27. C16's weapon clause landed as the `equipped` event. C14, re-pointed
at a moving NPC, first went red — lag compensation rewound by RTT/2, which
lands on the target's present position rather than the frame the client fired
at, so a player shooting what their screen showed missed a moving body — and is
now fixed by adopting the reference design the PROTOCOL already described:
`rewind = staleness + RTT/2 + interp_delay`, with `fire.seq` finally used.
Three defects surfaced on the way, all fixed: camp NPCs were invulnerable to
gunfire, a `hit` event shipped with no body, and the TS client rendered remotes
against local receive time. **One half of the C14 fix is not landed and cannot
be here: `interp_delay` is now a client-binding contract, and the Unity client
owes it at U13** (see that row). Verdicts and evidence: `docs/QA-STATUS.md`.

**Phase 3.5 — rebuild the client in Unity as a native desktop build** was
inserted 2026-08-26 before Phase 4, because Phase 4/5 is where client work
explodes and the Phase 1–3 client is the cheapest version of that port that
will ever exist. Browser delivery is dropped.

### Where Phase 3.5 stands (2026-09-02)

The Unity client renders real art, and `client/` is gone.

**Landed.** A runtime glTF asset pipeline: models live in `art/`, are named by
the `asset` id the SERVER already sends for every entity and item, and are
loaded by `Assets/Game/AssetRegistry.cs` from StreamingAssets — no prefabs, no
Editor import, so C47 holds. `art/tools/import_pack.mjs` conforms downloaded
CC0 models to this project (bakes colour to vertices, fits to game units, adds
mount nodes, retargets animation clips). 18 assets, all Kenney CC0, recorded in
`art/ATTRIBUTION.md`. Characters walk. The camp and range are visible and
dressed from zone data over a new `props` message (0x0013). U18 retired the
TypeScript client, with its harnesses ported to C# first.

**Verified.** All eight acceptance criteria, C40–C47, are PASS as of the C43
gate run on 2026-09-02 — `docs/QA-STATUS.md` has the per-criterion numbers and
what the run surfaced (harness rot on the retired nginx path, a vsync-pinned
frame rate misread as a frame budget problem, and U13's missing C#-path
verification, all fixed). CI (`.github/workflows/ci.yml`) runs the
editor-free gates on every commit.

**Open for Phase 4, not blocking 3.5.**

1. Ship and vehicle entities render as a loot crate: `EntityType.Ship` and
   `Vehicle` have no entity def, which is correct until Phase 4/5 creates one.
2. `vehicle.rover.v1` does not exist. Kenney's `rover` is already vendored.
3. `t18` failed 1 of 7 once between harness runs sharing a live server, then
   passed 5 straight; unreproduced. Watch it on the next sweep.

**Two things to know before touching this.** The Unity Editor takes the
project lock, so `unity compile` and `unity build` fail while it is open —
`unity typecheck` works either way. And the kind cluster owns `:18080`: a
`go run ./cmd/server` beside it fails to bind and exits unread, which is what
`make check-server` now exists to catch.

Phases 2–5 replaced the old M2/M3 ordering (ship first, combat later). Ships
land last, after the game has NPCs, combat and a vehicle. Nothing spec'd for
the old M2 is wasted — the seat and attachment contract moves to Phase 4
unchanged.

## The game we are building

A low-poly first-person online RPG on a persistent world: walk a planet, find
and buy gear, fight NPCs, drive a rover, buy a ship, fly it. "MMO" is the
shape, not the near-term target — **one server process, one world, tens of
concurrent players.** Sharding, delta snapshots and 100+ load are a
scaling problem to solve when player count makes them hurt, not before
(`Deferred` at the bottom).

## Where we are

**Phase 1 — on-foot multiplayer on a round world — DONE.** All 10 acceptance
criteria verified on the deployed kind stack (`docs/QA-STATUS.md`).
They stay as the regression gate for every phase
below: nothing lands that breaks C1–C10.

What exists and is load-bearing:

| Thing | State |
|---|---|
| Go authoritative sim, fixed 20 Hz, deterministic | done |
| Cube-sphere terrain, radial gravity, on-foot rule table | done |
| Shared Go/TS sim + JSONL trajectory diff (conformance test) | done |
| Prediction + replay reconciliation from `ack_seq` | done |
| Binary wire protocol v1 (10 messages, 44 B entity row) | done |
| First-person body, procedural animation, nametags, HUD | done |
| kind deploy, `make up` / `make down`, nginx `/ws` | done |
| e2e harness t2–t10 | done |
| Agent ownership + dispatch sizing for a local ~8B model | done |

## Verdict: on track, with five things to fix before Phase 2

The foundation is right, and it is the part most projects of this shape get
wrong. Server authority, a fixed tick, one rule table implemented twice with a
conformance diff, and replay reconciliation are exactly what an FPS-with-other-
players needs, and they are already verified. The round world — the part that
looks like a gimmick and is actually the hard geometry — works and has been
walked in a full lap.

What does **not** yet exist is everything that makes it an RPG rather than a
walking simulator, and five of those gaps are structural: they get more
expensive the later they land, so they go in Phase 2's contract freeze rather
than being discovered mid-phase.

1. **No identity, no persistence.** A reconnect yields a new `entity_id` and an
   empty world. "Buy a weapon" is meaningless until a player is a row that
   survives a disconnect. Land the minimum in Phase 2: one `player` table
   (token, name, credits, inventory JSON, last position), written on disconnect
   and every 30 s. Not accounts, not sessions.

   **Portable from the first line, without an abstraction layer.** The store
   speaks `database/sql` and picks its driver from the scheme of
   `DATABASE_URL` — pure-Go SQLite for a bare `go run` and for tests, Postgres
   everywhere it is deployed. No repository interface, no two implementations:
   `database/sql` already is the seam, and the portability is kept honest by a
   test suite that runs on both engines plus a kind stack that runs Postgres
   for real. The rule that actually makes the swap free is that **the tick loop
   never touches the database** — load on join, save on leave and on a timer,
   from a background goroutine. Details, portable-SQL rules and the Redis
   trigger: `docs/ARCHITECTURE.md`, "Persistence".
2. **The entity row grows twice if we are not careful.** Phase 2 needs health
   and aim pitch on the wire; Phase 4 needs `parent_id`/`seat`. That is two
   codec changes across two languages plus two test-harness updates. **Land the
   final 54-byte row once, in Phase 2**, with `parent_id`/`seat` sent as zero
   until Phase 4 fills them in. One churn, not two.
3. **There is no reliable request/response channel.** Shops, dialog, equipping,
   inventory moves and purchases are all "ask the server, get an answer" — and
   adding a bespoke binary message per feature is how a protocol becomes
   unmaintainable. Add **one** generic pair, `cmd` / `cmd_result`, carrying a
   u16 opcode plus a JSON body. Keep binary for the hot path only (`input`,
   `snapshot`, `fire`); everything cold goes through `cmd`.
4. **Props have no collision.** Rocks are scattered client-side from
   `world_seed` and the server does not know they exist (ARCHITECTURE, "Client")
   — deliberate in Phase 1, fatal for a target range and an enemy encampment,
   which are structures you must not walk through. Phase 2 adds a **static
   collider list** (boxes and spheres, server-owned, sent once) that both sims
   resolve against. This is the one Phase-1 simplification that has to be paid
   back rather than extended.
5. **The sim knows only player bodies.** NPCs, targets, dropped loot and
   projectiles need a general entity store with per-type step functions.
   ARCHITECTURE already describes one; the code has players. Generalise it in
   Phase 2 while there is one NPC to generalise for, not in Phase 3 with thirty.

Three smaller course corrections:

- **Remote aim is not transmitted.** Head pitch is deliberately absent in Phase
  1 (PROTOCOL, "quat"). In an FPS you must see where another player is
  pointing. Add a quantised `i8` pitch to the entity row — visual only; hit
  detection uses the server's record of the shooter's fire direction.
- **Snapshots go to everyone, always.** Fine for 10 bodies, not for an
  encampment full of NPCs. 54 B × 150 entities × 20 Hz = 162 KB/s per client.
  Add distance-based interest culling in Phase 3, when entity count first
  justifies it, and not before.
- **`art/ships/v1.glb` is built and idle** until Phase 5. That is fine — it
  cost nothing and it froze the seat contract early. Leave it.

## System ledger — what gets built, and where

| System | Phase | Notes |
|---|---|---|
| On-foot movement, terrain, prediction | 1 | done |
| Identity + persistence (player row) | 2 | `database/sql` + `DATABASE_URL`; SQLite local, Postgres deployed |
| `cmd`/`cmd_result` reliable channel | 2 | one channel, all cold gameplay traffic |
| Generic entity store + per-type step | 2 | NPCs, targets, loot, projectiles depend on it |
| Static colliders (box/sphere) | 2 | both sims; unblocks buildings |
| Interaction (look-at + E + prompt) | 2 | reused by NPCs (2), loot (3), vehicles (4, 5) |
| Static NPC + dialog/shop UI | 2 | shopkeeper only; no AI |
| Currency + inventory + item defs | 2 | server-authoritative; defs shipped over the wire |
| Equipment + weapon in hands | 2 | real body holding a real model, per pillar 2 |
| Health + damage | 2 | targets only in 2; players in 3 |
| Hitscan combat + server rewind | 2 | ~500 ms position history, rewind by client RTT |
| Remote aim pitch on the wire | 2 | i8 quantised |
| Target range zone | 2 | the Phase 2 playable proof |
| NPC AI (idle/patrol/aggro/attack/flee) | 3 | steering, no navmesh |
| Melee attack + NPC projectiles | 3 | |
| Player death, respawn, damage feedback | 3 | |
| Loot drops + pickup | 3 | reuses interaction |
| Spawners + encampment zone | 3 | |
| Interest management (distance cull) | 3 | when entity count earns it |
| Seats, attachment, control handoff (PROTOCOL v2) | 4 | already spec'd for the ship; built for the rover |
| Ground vehicle drive model | 4 | terrain-following arcade model |
| Vehicle ownership + spawn/despawn | 4 | persists via the Phase 2 player row |
| Ship purchase + persistent ownership | 5 | |
| Flight model | 5 | already spec'd in GDD "Flight model — M2 context" |
| Surface ↔ space transition | 5 | the hard part of Phase 5; nothing else new lands with it |
| Space skybox + planet seen from outside | 5 | |

## Wire id allocation (reserved now, so phases never collide)

| id | name | dir | phase |
|----|------|-----|-------|
| `0x0001`–`0x000A` | v1 set (hello … terrain) | — | 1 (done) |
| `0x000B` | `board` | C→S | 4 |
| `0x000C` | `disembark` | C→S | 4 |
| `0x000D` | `seat_result` | S→C | 4 |
| `0x000E` | `cmd` | C→S | 2 |
| `0x000F` | `cmd_result` | S→C | 2 |
| `0x0010` | `defs` | S→C | 2 |
| `0x0011` | `fire` | C→S | 2 |
| `0x0012` | `colliders` | S→C | 2 |

Final entity row (**land once, in Phase 2**, 54 B):

```
u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
| u32 parent_id | u16 seat | u16 health | u8 flags | i8 pitch_q
```

`parent_id`/`seat` are zero until Phase 4. `flags`: `0x01` grounded, `0x02`
sprinting, `0x04` dead, `0x08` firing; rest reserved. `pitch_q` is pitch in
units of π/254 rad (visual only).

`event_id` allocation: `0x0001` explosion (reserved), `0x0002` shot fired,
`0x0003` hit, `0x0004` death, `0x0005` loot dropped, `0x0006` `equipped`
(Phase 3.5 — how a client learns what another player is holding; see C16).

`input` gains a trailing `u8 mode` in Phase 3.5. Appended, not prepended, so
every existing field keeps its offset; a 24-byte payload still reads as mode
`0`, so the change needs no version flip and no lockstep client update.

---

# Phase 2 — buy a weapon from an NPC and shoot the range

**Playable proof.** Spawn on foot. A shopkeeper NPC stands near spawn; look at
them, press E, a shop panel opens. Buy a pulse rifle with starting credits. It
appears in your inventory, equips to your hands, and the HUD shows ammo and
credits. Walk 60 m to a target range with walls you cannot walk through. Shoot
five targets; each registers a hit, shows damage, and resets after 3 s. Another
player watching sees your weapon, sees where you are aiming, and sees the
shots. Disconnect, reconnect: you still have the rifle and the credits.

### Wave 0 — contracts, main thread, before any dispatch

Nothing dispatches until these are frozen; a small model cannot infer a
contract, and Phase 1 proved that freezing them first is what makes the waves
actually parallel.

- `docs/PROTOCOL.md` → **v2**: the 54-byte entity row; `cmd`/`cmd_result`
  (opcode table + JSON body shape); `defs`; `fire`; `colliders`; the new
  `event_id`s. Both ends implement it in this phase.
- `docs/GDD.md` (`game` agent) → new sections: **Items and currency**,
  **Weapons** (rule table: damage, rpm, spread, falloff, max range, magazine,
  reload), **Health and damage**, **Interaction** (look cone, `interact_dist`,
  prompt rules), **Shop NPCs**, **Static colliders** (resolve order against the
  terrain step).
- `server/data/` schemas (`game` owns): `items.json`, `npcs.json`,
  `zones/range.json`. Server-owned and shipped to the client in `defs` — one
  source of truth, no duplicated data files in the client.
- `art/manifest.json`: `weapon.pulse` (with `grip` and `muzzle` nodes),
  `npc.shopkeeper`, `prop.target`, `struct.range.*`.
- **Storage contract** (`docs/ARCHITECTURE.md`, "Persistence"): `DATABASE_URL`
  and its scheme→driver mapping, the `player` table schema, the portable-SQL
  rules (`$1` placeholders, no `SERIAL`/`BOOLEAN`/`JSONB`/`TIMESTAMP`), and the
  migration file convention. Frozen before task 8 dispatches — a dialect
  decision made mid-implementation by a small model is a rewrite.

`.omp/AGENTS.md` ownership gains one row: `game` owns `server/data/**`.

### Task list

Each row is one dispatch: 1 file, ~150 changed lines, one verify command.
**The written briefs — contracts pasted, ready to hand to an agent — are in
`docs/tasks/phase2-wave1.md`, `phase2-wave2.md` and `phase2-wave3.md`;** the
table below is the index.

| # | Agent | Task | File | Verify |
|---|---|---|---|---|
| 1 | `netcode` | New message-id and event-id constants | `server/internal/protocol/protocol.go` | `go test ./internal/protocol` |
| 2 | `netcode` | 54-byte entity row encode/decode | `server/internal/protocol/entity.go` | `go test ./internal/protocol` |
| 3 | `frontend` | Mirror ids + 54-byte row in the TS codec | `client/src/net/protocol.ts` | `npm run codec-smoke` |
| 4 | `art` | `weapon.pulse` GLB (`grip`, `muzzle` nodes) | `art/tools/gen_weapon.py` | `python art/tools/render_check.py` |
| 5 | `art` | `npc.shopkeeper` + `prop.target` GLBs | `art/tools/gen_npc.py` | `python art/tools/render_check.py` |
| 6 | `game` | Item defs (pulse rifle, ammo, start credits) | `server/data/items.json` | `jq . server/data/items.json` |
| 7 | `game` | Range zone: collider boxes, target and NPC placements | `server/data/zones/range.json` | `jq . server/data/zones/range.json` |
| 8 | `netcode` | `open(dsn)`: scheme→driver, per-dialect pool/WAL settings | `server/internal/store/open.go` | `go test ./internal/store` |
| 8b | `netcode` | Embedded numbered migrations + `schema_version` runner | `server/internal/store/migrate.go` | `go test ./internal/store` |
| 8c | `netcode` | `player` table schema (portable SQL, `$1`, no engine types) | `server/internal/store/migrations/001_player.sql` | `go test ./internal/store` |
| 9 | `netcode` | `GetPlayer` / `PutPlayer` on `database/sql`, no dialect branches | `server/internal/store/player.go` | `go test ./internal/store` |
| 9b | `netcode` | Identity: `hello` token → player row, load on join, save on leave + 30 s timer | `server/internal/server/identity.go` | `go test ./internal/server` |
| 9c | `infra` | Postgres pod + PVC + Secret; server Deployment reads `DATABASE_URL` | `deploy/manifests/30-postgres.yaml` | `make up` → pod 1/1 |
| 9d | `infra` | `make test-pg`: Postgres container + `TEST_DATABASE_URL` | `Makefile` | `make test-pg` |
| 10 | `netcode` | `go:embed` the data dir, encode the `defs` message | `server/internal/defs/defs.go` | `go test ./internal/defs` |
| 11 | `frontend` | Parse `defs`, expose the item/zone tables | `client/src/net/defs.ts` | `npm run codec-smoke` |
| 12 | `netcode` | Generic entity store: type tag + per-type step dispatch | `server/internal/sim/entities.go` | `go test ./internal/sim` |
| 13 | `netcode` | Static collider resolve (box/sphere) in the on-foot step | `server/internal/sim/collide.go` | `go test ./internal/sim` |
| 14 | `frontend` | The same collider resolve, mirrored | `client/src/sim/collide.ts` | `npm run sim:dump` diff |
| 15 | `netcode` | `cmd` dispatch: `shop_list`, `shop_buy`, `equip` | `server/internal/server/cmd.go` | `go test ./internal/server` |
| 16 | `netcode` | Inventory + credits apply/validate on the player row | `server/internal/sim/inventory.go` | `go test ./internal/sim` |
| 17 | `frontend` | Look-at interaction raycast + prompt | `client/src/input/interact.ts` | `npm run build` |
| 18 | `frontend` | Shop panel (DOM), buy via `cmd` | `client/src/hud/shop.ts` | `npm run build` |
| 19 | `netcode` | 500 ms entity position history ring | `server/internal/sim/history.go` | `go test ./internal/sim` |
| 20 | `netcode` | Hitscan resolve with RTT rewind + damage apply | `server/internal/sim/combat.go` | `go test ./internal/sim` |
| 21 | `netcode` | Targets: health, hit response, 3 s respawn | `server/internal/sim/target.go` | `go test ./internal/sim` |
| 22 | `frontend` | Weapon in hands: attach to `grip`, muzzle flash, recoil | `client/src/scene/weapon.ts` | `npm run build` |
| 23 | `frontend` | Send `fire`, render tracers/hit markers from `event` | `client/src/net/fire.ts` | `npm run build` |
| 24 | `frontend` | HUD: ammo, credits, hit feedback | `client/src/hud/hud.ts` | `npm run build` |
| 25 | `frontend` | Remote aim: apply `pitch_q` to the remote head/weapon | `client/src/scene/character.ts` | `npm run build` |
| 26 | `qa` | e2e harness against C11–C18 | `test/t11-range.mjs` | `node test/t11-range.mjs` |

### Acceptance criteria (C1–C10 keep holding as regression)

- **C11 Persistence.** A client buys the rifle, disconnects, reconnects with the
  same token: inventory, credits and equipped weapon are restored; a fresh
  token starts with the default loadout and starting credits.
- **C11b Engine portability.** The store's test suite passes identically
  against SQLite and against Postgres (`make test-pg`), and `make up` brings the
  kind stack up on Postgres with `DATABASE_URL` supplied only by a Secret — no
  DSN in an image or a committed manifest. A migration applied twice is a no-op.
- **C12 Server authority over currency.** A client sends `shop_buy` for an item
  it cannot afford, and a forged `cmd` claiming a free purchase: both are
  refused by `cmd_result`, and the snapshot/inventory is unchanged.
- **C13 Colliders.** A scripted walk into every range wall stops at the surface
  — no penetration > 1e-2 m, no tunnelling at sprint speed — and the Go and TS
  sims agree on the whole run to the Phase-1 conformance tolerance.
- **C14 Hit registration.** With 100 ms injected latency, 20 scripted shots at a
  static target from 30 m all register; 20 shots deliberately aimed 1 m wide
  register zero. Rewind is what makes the first number 20 and not 12.
- **C15 Damage and death.** A target's health decreases by the GDD table value
  per hit, reaches 0, emits a death `event`, and respawns at full health within
  3 s ±100 ms.
- **C16 Remote fidelity.** A second client sees the shooter's equipped weapon,
  aim pitch within 1°, and one `shot fired` event per shot, ordered.
- **C17 Interaction.** The prompt appears only within `interact_dist` and inside
  the look cone; E opens the shop; the shop refuses to open from out of range.
- **C18 Budget.** `go build/vet/test ./...` and `tsc` strict + `npm run build`
  clean; snapshot stays ≤ 54 B/entity; 60 fps with 10 players, 1 NPC and 8
  targets. **No database call on the tick path** — with the store pointed at a
  Postgres given 200 ms of injected latency, tick duration is unchanged.

---

# Phase 3 — fight NPCs at an encampment

**Playable proof.** A hostile encampment sits on the far side of the test
planet, walled, with a mix of melee enemies that charge you and ranged enemies
that shoot back. They notice you, close or take cover, and hurt you. You can
die and respawn. Killed enemies drop loot you walk over and pick up. Two
players can clear it together and both see the same fight.

### Wave 0 — contracts

- `docs/GDD.md`: **NPC archetypes** (health, damage, speed, aggro radius,
  leash range, attack cadence, projectile speed), **AI state machine** (the
  exact states and transitions — a small model implements a table, not a
  concept), **Player death and respawn**, **Loot tables**.
- `server/data/npcs.json` archetypes, `server/data/zones/camp.json` layout.
- `docs/PROTOCOL.md`: no new message types — reuse `event` and the entity row.
  If Phase 3 needs a new message, that is a signal the Phase 2 `cmd` channel
  was drawn too narrow; fix it there rather than adding a message here.

### Task list

| # | Agent | Task | File | Verify |
|---|---|---|---|---|
| 1 | `game` | NPC archetype table | `server/data/npcs.json` | `jq .` |
| 2 | `game` | Encampment layout: colliders, spawn points, patrol routes | `server/data/zones/camp.json` | `jq .` |
| 3 | `game` | Loot tables | `server/data/loot.json` | `jq .` |
| 4 | `art` | `npc.grunt` (melee) + `npc.gunner` (ranged) GLBs | `art/tools/gen_npc.py` | `render_check.py` |
| 5 | `art` | Encampment structure kit | `art/tools/gen_struct.py` | `render_check.py` |
| 6 | `netcode` | Steering on the sphere: seek/flee a target, slope-aware | `server/internal/ai/steer.go` | `go test ./internal/ai` |
| 7 | `netcode` | AI state machine: idle → patrol → aggro → attack → leash | `server/internal/ai/brain.go` | `go test ./internal/ai` |
| 8 | `netcode` | Melee attack: range, cadence, damage apply | `server/internal/ai/melee.go` | `go test ./internal/ai` |
| 9 | `netcode` | Projectile entity + step + hit resolve | `server/internal/sim/projectile.go` | `go test ./internal/sim` |
| 10 | `netcode` | NPC ranged attack: lead the target, fire cadence | `server/internal/ai/ranged.go` | `go test ./internal/ai` |
| 11 | `netcode` | Player damage, death, respawn timer | `server/internal/sim/player_death.go` | `go test ./internal/sim` |
| 12 | `netcode` | Spawners: zone-driven, respawn on a timer, cap per zone | `server/internal/sim/spawner.go` | `go test ./internal/sim` |
| 13 | `netcode` | Loot drop entity + pickup via `cmd` | `server/internal/sim/loot.go` | `go test ./internal/sim` |
| 14 | `netcode` | Interest culling: per-client entity set by distance | `server/internal/server/interest.go` | `go test ./internal/server` |
| 15 | `frontend` | NPC rendering + procedural animation reuse | `client/src/scene/npc.ts` | `npm run build` |
| 16 | `frontend` | Projectile rendering + impact effects | `client/src/scene/projectile.ts` | `npm run build` |
| 17 | `frontend` | Damage feedback: hit direction, health bar, death/respawn screen | `client/src/hud/vitals.ts` | `npm run build` |
| 18 | `frontend` | Loot prompt + pickup, reusing the Phase 2 interaction | `client/src/input/interact.ts` | `npm run build` |
| 19 | `qa` | e2e harness against C19–C25 | `test/t12-camp.mjs` | `node test/t12-camp.mjs` |

### Acceptance criteria

- **C19 Aggro and leash.** An NPC notices a player inside its aggro radius
  within 1 s, pursues, and returns to its post when the player leaves leash
  range — it never follows across the planet, and never stops in mid-air or
  under the terrain.
- **C20 Melee.** A grunt closes to melee range and lands hits at the GDD
  cadence ±10%; the player's health decreases by the table value; a player who
  outruns it takes no further damage.
- **C21 Ranged.** A gunner's projectile is server-simulated, deterministic
  against the same input, and hits a stationary player from 30 m; both clients
  see the same projectile path.
- **C22 Death and respawn.** A player at 0 health dies, respawns at the spawn
  point with full health within the GDD timer, and keeps their inventory. Other
  clients see the death and the respawn.
- **C23 Loot.** A killed NPC drops per its loot table; a player picks the drop
  up once — a second pickup, and a simultaneous pickup by two clients, yields
  exactly one item total.
- **C24 Two-player fight.** Two clients clear the camp together; both see the
  same NPC positions within the interpolation tolerance, and the same deaths in
  the same order.
- **C25 Budget.** 30 NPCs live in the camp: server tick stays inside its 50 ms
  budget with ≥ 50% headroom, clients hold 60 fps, and interest culling keeps
  per-client snapshot bandwidth under 100 KB/s.

---

# Phase 3.5 — rebuild the client in Unity (native desktop)

**Decision, 2026-08-26: browser delivery is dropped.** The target is a packaged
native desktop build. This phase changes the renderer, input and asset
pipeline. It changes no gameplay.

**Playable proof.** Exactly Phase 3's proof, in Unity, from a packaged build:
launch the desktop client, join the deployed server, walk the planet, buy the
rifle, shoot the range, fight the camp, die, respawn. C1–C25 re-verified
against the new client. If it plays differently from the TS client, that is a
bug, not a feature.

**Why here and not later.** Phase 4 and 5 are where client work explodes —
rover cockpit, seats, ship interior, space transition. The Phase 1–3 client is
the cheapest version of this port that will ever exist. Building rover and ship
UI in Three.js and then porting it means paying twice.

**What does not change.** 8.5k lines of Go, 160 Go tests, the wire protocol,
the sim rule tables, all 12 `.mjs` harnesses, and the kind deploy. Roughly 60%
of the repo is untouched. Of the 6k-line TS client, ~2.5k (`net/`, `sim/`) is
ported and ~3.5k (`scene/`, `hud/`, `input/`, most of `util/`) is deleted
because engine features replace it.

**Engine.** Unity, C#. Godot 4 was the better pick only while browser delivery
was a requirement (far smaller web export, same C# port work); dropping browser
removes its advantage and leaves Unity's animation tooling and asset ecosystem
deciding — and humanoid locomotion blending for the Phase 3 melee/ranged NPCs
is the single largest thing Three.js was never going to give us.

### Wave 0 — contracts, main thread, before any dispatch

0. **Settle two Phase 2 criteria that the 2026-08-26 verdict run found open**
   (`docs/QA-STATUS.md` "Phase 2"), because both are wire or spec decisions and
   this is the phase that opens the wire:
   - **C16 — decided 2026-08-26: a new `equipped` event, `event_id 0x0006`.**
     Nothing carried a player's equipped weapon, so no client could render
     another player's gun. The fix reuses the existing event channel rather
     than widening the entity row: a value that changes a few times a session
     has no business costing bytes on every entity on every tick. Broadcast on
     change, and replayed once per armed player at join so late joiners are
     correct. **Landed 2026-08-27; `t19` is green on all three clauses.**
   - **C14 — decided 2026-08-26: re-point it at a moving NPC.** It fires at a
     *static* target, so the rewound position equals the live one and lag
     compensation is a no-op; the criterion cannot tell a server with rewind
     from one without. Phase 3's camp NPCs move. The harnesses are being
     touched for the mode byte anyway, so this rides along with that work.

     **Done and fixed 2026-08-27 (`test/t21-lagcomp-moving.mjs`).** The
     re-pointed criterion went red — rewind was RTT/2, which reconstructs the
     present rather than what the client saw — and is now
     `staleness + RTT/2 + interp_delay`, the reference design, using the
     `fire.seq` the PROTOCOL always specified and the code never read. The
     stale-aim volley went 0/8 to 8/8, and the live-aim volley — the position
     no real client can know — went 8/8 to 2/8. The `hit` event's own point
     puts the resolved capsule 0.33 m from the axis the client aimed at,
     inside the 0.35 m hitbox.
     **`interp_delay` (0.1 s) is now a contract that binds the client**, and
     the second half of the fix belongs to U13: remotes must be rendered at
     `serverClock − interp_delay` against a synchronised clock, never at a
     fixed offset behind local packet arrival. Nothing server-side can catch
     a breach of that.
1. **Land the Phase 4 input mode byte first, and do not update the TS client.**
   `input` gains its mode byte in `docs/PROTOCOL.md`, the Go server, and the
   harness. The TS client is being retired, so it is not a third end. The
   harness becomes the reference implementation and the Unity client is the
   first client to implement the new layout — built against a frozen protocol
   that a running headless client already validates.
2. **`docs/ARCHITECTURE.md` — client delivery.** Packaged desktop build; nginx
   no longer serves a client bundle; `/ws` stays. Server URL becomes client
   config, not same-origin, so the CORS-free assumption dies with it.
3. **`client-unity/CONVENTIONS.md` — the rule that protects the workflow:**
   - **Code-first. One near-empty scene.** All hierarchy built in C# at runtime.
   - **No agent touches a `.unity`, `.prefab`, or `.meta`.** Unity's native unit
     is GUID-keyed YAML: unreviewable diffs, unmergeable conflicts, and "verify"
     means opening the Editor. That is a direct collision with one-file,
     ~150-line dispatch. Anything needing scene authoring is a main-thread task.
   - Assembly definitions split `Sim`, `Net`, `Game`. **`Sim` references
     `UnityEngine` nowhere** and must compile and test headless.
   - `.gitignore`: `Library/`, `Temp/`, `Logs/`, `Build/`, `*.csproj`, `*.sln`.
3b. **The client's render clock is server-synced.** Remote entities are drawn
   at `serverClock − interp_delay` (0.1 s, GDD "Lag compensation"), against a
   clock estimated from the server's own, **never** at a fixed offset behind
   whenever a packet arrived locally. The server rewinds shots by that exact
   offset, so the two conventions differ by a whole one-way trip and a client
   on the wrong one misses every moving target. The TS client had it wrong and
   C14 caught it only at the wire level; no server-side test can catch it, so
   U13 carries the obligation and `t21` is its check.

4. **`Sim` carries its own math types**, originally mirroring the TypeScript
   client's `sim/types.ts` (retired in U18; in git history) —
   not `UnityEngine.Vector3`. Normalize and lerp implementations differ between
   libraries and C5's bar is 1e-10 m.
5. **C5 runs three-way during the transition** — Go / TS / C#. TS leaves the
   diff only once C# matches Go.

### Task list

| # | Agent | Task | File | Verify |
|---|---|---|---|---|
| U1 | main | Unity project skeleton, three asmdefs, gitignore | `client-unity/` | `Sim` builds headless, references no UnityEngine |
| U2 | sonnet | Port math types (vec3, quat, basis) | `Sim/Types.cs` | golden values in `unity-test` |
| U3 | sonnet | Port cube-sphere terrain sampling | `Sim/Terrain.cs` | face/dir addressing golden values |
| U4 | sonnet | Port deterministic RNG | `Sim/Rng.cs` | same sequence as the TS generator (golden values in `unity-test`) |
| U5 | sonnet | Port on-foot step rule table | `Sim/Step.cs` | trajectory diff vs Go (C40, `t20`) |
| U6 | sonnet | Port collider resolution | `Sim/Collide.cs` | `t13` diffs Go against C# over the shared scenarios |
| U7 | main | Conformance runner (Go vs C#) | `test/t20-csharp-conformance.mjs` | max dPos < 1e-10 m; TS leg dropped with U18 |
| U8 | sonnet | Little-endian binary reader/writer | `Net/Wire.cs` | round-trip fuzz |
| U9 | sonnet | v2 message codecs, all opcodes | `Net/Messages.cs` | byte-identical vs the Go vectors (`t22`) |
| U10 | sonnet | WebSocket transport, hello/join, reconnect | `Net/Client.cs` | joins deployed server, decodes snapshot |
| U11 | main | Prediction + replay reconciliation from `ack_seq` | `Game/Core/Prediction.cs` | `t6` p95 clears to wire precision (~7e-06 m) with 0 snap-backs — see C42 |
| U12 | sonnet | Terrain mesh from u16 radius grids | `Game/TerrainMesh.cs` | mesh matches sampled radii |
| U13 | sonnet | Entity views, interpolation, nametags | `Game/Entities.cs` | two clients agree (C24, strengthened); `t21` stale volley stays green |
| U14 | sonnet | FPS controller + Input System, emits mode byte | `Game/Fps.cs` | walks, strafes correct handedness |
| U15 | sonnet | HUD: vitals, hotbar, shop, interact prompt | `Game/UI/` | buy flow completes |
| U16 | sonnet | Weapon, projectiles, hit feedback | `Game/Combat.cs` | shot_fired renders |
| U17 | **main** | **Wire it into the frame loop** | `Game/Boot.cs` | end-to-end join → walk → shoot |
| U18 | main | Retire `client/`, drop TS from C5, update Makefile + deploy | — | **done** — harnesses ported to C# first (t3, t6, t13), t12 superseded by t22 |

U17 is a task because Phase 2 and Phase 3 both shipped fully-built subsystems
that nothing referenced. That failure mode is not going to be fixed by hoping.

### Acceptance criteria (C40–C47, numbered clear of Phases 4–5)

- **C40 Sim conformance.** The C# sim matches Go on the C5 trajectory route
  within 1e-10 m, running headless with no UnityEngine reference.
- **C41 Codec parity.** C# encodes and decodes every v2 message byte-identically
  to the Go implementation, against Go's own vectors (`t22`). The Node half
  went with the TypeScript client in U18; `t22` asserts the same bytes t12 did.
- **C42 Prediction.** Replay reconciliation from `ack_seq`, never blending.
  Was defined as "the same corrections as the TS client on an identical input
  trace", which stopped being a definition when U18 retired that client —
  a criterion whose reference implementation does not exist cannot be run.

  Stated as the observable property instead, which is stronger than deferring
  to another implementation: after reconciling ack M the client has snapped to
  the server's state and replayed what is still unacked, so its belief is
  about tick `M + pending` and must agree with the server's own state at that
  tick to wire precision. Blending cannot reach that — it leaves a persistent
  residual toward the stale anchor, which is the whole difference the
  criterion exists to catch. Evidence: `t6` measures exactly this pairing and
  clears to ~7e-06 m, plus the replay/reconcile checks in `make unity-test`.
- **C43 Regression.** C1–C25 re-run against the Unity client on the deployed
  kind stack. All pass. This is the phase gate.
- **C44 Headless CI.** `Sim` and `Net` build and test with no Unity Editor, in
  CI, on every commit.
- **C45 Cold start.** A packaged desktop build joins the deployed server from a
  cold start with the server URL from config, not compiled in.
- **C46 Frame budget.** 60 fps with the camp live (30 NPCs) on target hardware.
- **C47 No scene debt.** The repo contains no agent-authored `.prefab` or
  `.unity` beyond the single boot scene. Enforced by a grep gate in CI, because
  a convention nobody checks is a convention that lasts two weeks.

### The risk that actually matters

C40. The 1.1e-10 m Go↔TS agreement is the spine of the prediction model, and
this phase asks a third language to hit the same bar. It is mechanical work —
916 lines, hand-written float math, no engine physics involved — but it is
mechanical work with a numerical gate, and it should be finished and green
before a single line of rendering code is written. U2–U7 land first for that
reason. If C40 will not close, stop the phase there: everything downstream is
wasted otherwise.

---

# Phase 4 — get in a rover and drive

### Where Phase 4 stands (2026-09-02)

Built and green end to end in one pass: tasks 1–14 and 16 landed, C26–C32
all PASS against the deployed stack (`docs/QA-STATUS.md` "Phase 4" has the
measured values; `node test/t24-rover.mjs` is the harness — the t13 name
this table originally assigned was already taken by collide-parity). C30
conformance runs in CI via `make unity-conformance` (t23). Task 15 (rover
ownership/purchase) is **deferred to Phase 5 deliberately**: no Phase 4
criterion touches ownership, the playable proof uses the parked world
rover, and C33 builds purchase-persistence properly for ships — rover
ownership should ride that machinery, not grow a parallel one. The e2e
run added one rule the spec missed: `hold_speed` (GDD), because a parked
rover with only exponential damping creeps downhill forever.

**Playable proof.** A rover is parked near spawn. Walk up to it, press E, your
body sits in the driver seat and the camera moves to the driver's eye point.
Drive it over the terrain — up slopes, over crests, around the camp. A second
player takes the passenger seat and rides along, free to look around. Either
can get out anywhere; both bodies land on the ground, not inside the rover.

This is where the seat and attachment machinery lands. It was spec'd for the
ship (GDD "Vehicles and crew (M2 spec)", PROTOCOL v2 `board`/`disembark`/
`seat_result`); it is **built here, unchanged**, for a vehicle that does not
also have to solve flight. Phase 5 then reuses it.

### Wave 0 — contracts

- `docs/PROTOCOL.md`: activate `board` / `disembark` / `seat_result` and start
  filling `parent_id`/`seat` in the entity row already shipped in Phase 2. The
  input mode byte — shipped in Phase 3.5 as a trailing, optional byte — gains
  mode `2`, ground vehicle (`v = [throttle, steer, 0, 0, 0]`).
- `docs/GDD.md`: the existing "Seats and occupancy" applies as written; add a
  **ground drive model** rule table (accel, top speed, steer rate, grip, slope
  limit, terrain-following suspension) and the rover's seat table.
- `art/manifest.json`: `vehicle.rover.v1` with `seat.driver` and
  `seat.passenger.0` eye-point nodes.

### Task list

| # | Agent | Task | File | Verify |
|---|---|---|---|---|
| 1 | `netcode` | `board`/`disembark`/`seat_result` codec | `server/internal/protocol/seats.go` | `go test ./internal/protocol` |
| 2 | `frontend` | Same, C# side (opcodes `0x000B`–`0x000D` are already declared in `Messages.cs`) | `client-unity/Assets/Net/Messages.cs` | `make unity-codec` (extend `t22` vectors) |
| 3 | `art` | `vehicle.rover.v1` GLB with seat nodes | `art/tools/gen_rover.py` | `render_check.py` |
| 4 | `game` | Ground drive rule table + rover seat table | `docs/GDD.md` | review |
| 5 | `netcode` | Rover entity: state, deterministic spawn, snapshot row | `server/internal/sim/vehicle.go` | `go test ./internal/sim` |
| 6 | `netcode` | `stepRover`: drive model + terrain following | `server/internal/sim/drive.go` | `go test ./internal/sim` |
| 7 | `frontend` | `stepRover`, mirrored | `client-unity/Assets/Sim/` (new `Drive.cs`, engine-free) | `make unity-conformance` (trajectory diff) |
| 8 | `netcode` | Board/disembark validation, occupancy, control repoint | `server/internal/server/seats.go` | `go test ./internal/server` |
| 9 | `netcode` | Seated body composition (ship transform × seat offset) | `server/internal/sim/compose.go` | `go test ./internal/sim` |
| 10 | `netcode` | Driver disconnect: coast, stop, seat freed | `server/internal/server/handoff.go` | `go test ./internal/server` |
| 11 | `frontend` | Rover rendering + camera mount at the seat node | `client-unity/Assets/Game/Vehicle.cs` | `make unity-typecheck` |
| 12 | `frontend` | Input mode switch driven by snapshot occupancy | `client-unity/Assets/Game/Boot.cs` | `make unity-typecheck` |
| 13 | `frontend` | Rover prediction + replay | `client-unity/Assets/Game/Core/Prediction.cs` | `make unity-test` |
| 14 | `frontend` | Board prompt, seat UI, passenger free-look | `client-unity/Assets/Game/Interact.cs`, `Boot.cs` | `make unity-typecheck` |
| 15 | `netcode` | Rover ownership — **deferred to Phase 5** (see the status block: rides C33's purchase machinery) | — | — |
| 16 | `qa` | e2e harness against C26–C32 | `test/t24-rover.mjs` | `node test/t24-rover.mjs` |

### Acceptance criteria

Structurally the old M2 criteria, retargeted at a ground vehicle:

- **C26 Boarding and occupancy.** A boards the driver seat; B sees the
  occupancy within 1 s; A's body is at the composed seat position, deviation
  < 1e-2 m.
- **C27 Server authority.** A dev override forces the rover 4 m under the
  surface: the next snapshot has it on the surface, no penetration.
- **C28 Seat race.** Two clients request the same seat: exactly one gets result
  0, the other result 1, and the snapshot shows one occupant.
- **C29 Refusals.** Boarding from beyond `board_dist` → result 2; disembarking
  while not seated → result 3.
- **C30 Drive conformance.** One input script through the Go and C# sims:
  per-tick pos/quat/vel deviation ≤ 1e-6 over ≥ 1000 ticks, and conformance to
  the GDD drive table within 5%.
- **C31 Passengers produce no vehicle input.** A passenger's movement input
  leaves rover pos/quat/vel invariant; their body stays at the seat position.
- **C32 No one is trapped.** Disembarking at any speed puts the body on the
  terrain within 1 s and within 10 m of the rover, never inside geometry.

---

# Phase 5 — buy a ship, fly it in space

### Where Phase 5 stands (2026-09-02)

Built and green: tasks 1–12 and 14 landed, C33–C39 all PASS
(`docs/QA-STATUS.md` "Phase 5" has the measured values; `t26`/`t27` are
the live harnesses, `t25` the cross-sim gate in CI). The seat machinery
generalised as designed — the ship reuses Phase 4's board/disembark/
composition through a shared SeatBank, and ownership reuses Phase 2's
shop and inventory persistence outright (ship.v1 is an item; possession
IS ownership). Task 13 (cockpit polish) is deferred as the spec itself
suggested ("polish the cockpit last"): ship.v1 already carries the
walk-in cockpit and seat nodes, and polish has no criterion. Task 15 of
Phase 4 (rover ownership) can now ride this machinery whenever a rover
shop entry is wanted — one items.json row plus one spawn branch.

**Playable proof.** Buy a ship from an NPC. It spawns on a pad. Walk up to it,
walk in, take the pilot seat. Fly off the surface, out of the atmosphere, and
around the planet in space — with the planet below reading as a planet. Come
back down and land. A passenger rides along, seated.

Phase 5 is deliberately the last one because it contains the genuinely hard
problem — the surface↔space transition — and it should meet that problem with
seats, control handoff, purchasing and persistence already working and
verified. **Passengers stay seated in flight**: walking around inside a moving
ship is a moving-reference-frame problem, and it is not part of this phase.

### Wave 0 — contracts

- `docs/GDD.md`: "The flight model — M2 context" already specifies the flight
  step; retarget the prose to Phase 5. Add **space regime** rules (where
  gravity and drag stop, how the transition is triggered and what changes at
  the boundary) and **landing** rules.
- `docs/PROTOCOL.md`: input mode `1` (pilot) already specified. Add a regime
  flag bit in the entity `flags` byte reserved in Phase 2 — no new message.
- `art/manifest.json`: `ship.v1` exists and carries `seat.pilot` and
  `seat.passenger.0/1`. Verify the nodes against the GDD seat table; polish the
  cockpit last.

### Task list

| # | Agent | Task | File | Verify |
|---|---|---|---|---|
| 1 | `game` | Space regime + landing rules | `docs/GDD.md` | review |
| 2 | `netcode` | Ship entity + deterministic spawn on the pad | `server/internal/sim/ship.go` | `go test ./internal/sim` |
| 3 | `netcode` | `stepShip` flight model (Go) | `server/internal/sim/flight.go` | `go test ./internal/sim` |
| 4 | `frontend` | `stepShip`, mirrored | `client-unity/Assets/Sim/` (new `Flight.cs`, engine-free) | `make unity-conformance` (trajectory diff) |
| 5 | `netcode` | Regime switch: gravity/drag off above the boundary, hysteresis | `server/internal/sim/regime.go` | `go test ./internal/sim` |
| 6 | `frontend` | Regime switch, mirrored | `client-unity/Assets/Sim/` | `make unity-conformance` (trajectory diff) |
| 7 | `netcode` | Ship purchase + persistent ownership + spawn on request | `server/internal/sim/ownership.go` | `go test ./internal/sim` |
| 8 | `netcode` | Landing: contact detection, settle, grounded state | `server/internal/sim/landing.go` | `go test ./internal/sim` |
| 9 | `frontend` | Ship rendering + walk-in interior + pilot camera mount | `client-unity/Assets/Game/Ship.cs` | `make unity-typecheck` |
| 10 | `frontend` | Pilot input mapping + ship prediction/replay | `client-unity/Assets/Game/Boot.cs`, `Core/Prediction.cs` | `make unity-test` |
| 11 | `frontend` | Space visuals: starfield, planet from outside, horizon fade | `client-unity/Assets/Game/Sky.cs` | `make unity-typecheck` |
| 12 | `frontend` | Flight HUD: speed, altitude, attitude, regime | `client-unity/Assets/Game/Hud.cs` | `make unity-typecheck` |
| 13 | `art` | Cockpit polish pass on `ship.v1` | `art/tools/gen_ship.py` | `render_check.py` |
| 14 | `qa` | e2e harness against C33–C39 | `test/t14-flight.mjs` | `node test/t14-flight.mjs` |

### Acceptance criteria

- **C33 Purchase to pilot seat.** Buy → spawn on the pad → walk in → take the
  pilot seat, all in one uninterrupted first-person session, with the ship
  persisting across a reconnect.
- **C34 Flight conformance.** One input script (hover, banked turn, boost to
  `vmax`, dive, landing) through both sims: per-tick deviation ≤ 1e-6 over
  ≥ 1000 ticks, GDD table within 5%.
- **C35 Transition.** Ascending through the boundary and descending back
  produces no discontinuity in position or velocity, no camera flip, and no
  oscillation at the boundary (hysteresis holds under a scripted hover exactly
  at the threshold).
- **C36 Orbit.** A scripted flight circles the planet in space and returns to
  the pad: no drift off the world, no numerical blow-up, terrain and the planet
  render correctly from every altitude flown.
- **C37 Landing.** The ship touches down at ≤ the GDD landing speed and settles
  grounded with no penetration; a hard landing is refused or penalised per the
  GDD rather than tunnelling.
- **C38 Passenger.** A seated passenger sees the flight from their seat, free
  to look, produces no ship input, and disembarks safely once landed.
- **C39 Latency.** At 100 ms injected latency a scripted pilot run keeps p95
  |predicted − authoritative| position error under 0.5 m with no snap-back.

---

# Phase 6 — host it for real

### Where Phase 6 stands (2026-09-02, night: CLOSED)

C48 went green on run 33693371037 after six instructive failures
(`docs/QA-STATUS.md` "Phase 6" lists all six — each was a future outage
pre-paid). Push to main now reaches the pandas with no human step, smoke
compares `/version` to main's own server hash, and the rollback path is
proven because a smoke bug once fired it against a correct deploy. All
criteria C48–C53 PASS (C52's NetworkPolicy clause waived with probe
evidence). The phase is closed; the game runs, for real, on real metal,
continuously deployed.

Live. The pandas cluster serves the game through Traefik on every node IP
(`ws://192.168.1.163/ws`), build-identity verified; CNPG runs Postgres 18
with two instances; the packaged client joins and plays; t3/t7/t15/t24/t26
all pass against it (20 Hz exact, one-way p95 6.3 ms). C50 is DRILLED:
primary killed → failover in ~60 s, zero row loss; backup restored and
counted. C49, C51, C53 hold. C48 fires on the first merge to main.

Two things the drills caught, both on the record in the RUNBOOK: the
backup CronJob's pg_dump was silently producing empty files (image major
below the server's Postgres 18, the refusal eaten by a pipe — now bash
with pipefail, a size assertion, and the matching major), and this
cluster's k3s NetworkPolicy enforcement is broken for cross-node traffic
(even allow-all blocked; the policy was a landmine that would have severed
the server from Postgres on its next pod restart). C52's NetworkPolicy
clause is therefore WAIVED with that probe as evidence — the fence wants a
CNI repair, not a manifest — while its other clauses (origin allowlist,
per-IP cap, join rate limit, generated credentials) are implemented and
tested. The operator's API proxy is enabled; the workflows are written and
take their first live run at the merge.

**Playable proof.** Push to `main`. CI goes green, and with no further human
step the pandas cluster (k3s, 4 nodes, on the tailnet) is running that exact
build — `/version` says so. A packaged client on the LAN connects through
Traefik and plays the whole game: walk, fight, buy, drive, fly. A node dies
mid-week and no player data is lost.

This is the Deferred table's "deploying somewhere real" trigger firing, so
the deferrals it gated come due: hosted Postgres with replication and
backups, and the origin-check hardening the code has carried as a "before
public exposure" note since Phase 2. What does NOT come due: real accounts
(players are still us — the LAN is the household) and sharding (one process
is nowhere near saturated). The kind stack stays exactly as it is — the
local development path does not change.

**Decisions taken at wave 0 (2026-09-02).** Client exposure through Traefik
on the LAN (the cluster's existing LoadBalancer); GitHub Actions reaches the
cluster by joining the tailnet as an ephemeral node (Tailscale GitHub
Action + the operator's API-server proxy, currently disabled and to be
enabled); Postgres via the CloudNativePG operator (replicated across nodes —
local-path storage is node-local, so replication is what makes "a node dies"
a non-event); images on GHCR pushed by the same workflow with the built-in
token.

### Wave 0 — contracts

- `docs/ARCHITECTURE.md`: a "Production deployment" section — the pandas
  topology, the request path (client → Traefik host rule → Service → pod),
  the CD path (Actions → tailnet → operator API proxy → kubectl), and the
  explicit statement that kind remains the dev path.
- `deploy/` splits: the kind manifests stay put; `deploy/prod/` holds the
  real-cluster kustomization. One server image serves both.
- The server hostname (Traefik host rule) and the `SA_ALLOWED_ORIGINS`
  contract for the origin check (below) are pinned before any manifest.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | GHCR publish workflow: build server image on `main`, tag `latest` + git SHA, BUILD_ID stamped | `.github/workflows/publish.yml` | image pullable, `/version` = SHA |
| 2 | Enable the Tailscale operator's API-server proxy + tailnet ACL grants for a `tag:ci` principal | operator helm values (user's install) | `kubectl --server https://tailscale-operator.<tailnet>` works from a tailnet node |
| 3 | Namespace, scoped ServiceAccount + Role for CD, imagePullSecret for GHCR | `deploy/prod/00-namespace.yaml` etc. | `kubectl auth can-i` matrix |
| 4 | CNPG operator (pinned version) + `Cluster`: 2 instances on different nodes, scheduled backups + a RESTORE DRILL | `deploy/prod/postgres/` | kill the primary; C50 |
| 5 | Server Deployment/Service/Ingress (Traefik, websocket), resources, probes, NetworkPolicies (server→pg only; pg accepts only server) | `deploy/prod/` | C49, C52 |
| 6 | CD workflow: on CI-green `main` — tailscale join → kubectl apply -k → rollout status → smoke `/version == SHA` | `.github/workflows/deploy.yml` | C48 |
| 7 | Origin check: `SA_ALLOWED_ORIGINS` allowlist — empty/absent Origin allowed (native clients send none), browser origins must match; the "must change together" note retired | `server/internal/server/server.go` | unit + C52 |
| 8 | Join hardening for LAN exposure: per-IP connection cap and a hello rate limit | `server/internal/server/` | unit + C52 |
| 9 | Non-default DB credentials via Secret, generated not committed; kind keeps its own | `deploy/prod/` | C52 |
| 10 | e2e against prod: the existing harnesses honour `SA_SERVER_URL` — run t3/t14/t15/t24/t26 against the LAN URL | harness sweep | C49 |
| 11 | Runbook: deploy, roll back (previous SHA tag), restore from backup, read logs | `docs/RUNBOOK.md` | review + the C50 drill follows it |

### Acceptance criteria

- **C48 Continuous deployment.** A push to `main` that passes CI reaches the
  pandas cluster with no human step; `/version` equals the pushed SHA;
  a failed smoke check leaves the previous build serving (rollout undo).
- **C49 Real play.** A packaged client pointed at the LAN URL plays the full
  loop — join, fight, buy, drive, fly, land — against the real cluster, with
  the sustain harness holding 20 Hz and one-way p95 under 50 ms on the LAN.
- **C50 A node dies.** Kill the Postgres primary's pod, then drain its node:
  the cluster fails over with zero committed-write loss, and a restore from
  the latest backup is DRILLED, not assumed, following the runbook.
- **C51 Server churn.** Deleting the server pod mid-session: clients
  reconnect on the same token and persistence holds (t15 against prod).
- **C52 Hardening holds.** A browser-origin WS from an unlisted origin is
  refused; Postgres is unreachable from anything but the server pods
  (NetworkPolicy probed, not assumed); DB credentials are not the defaults
  and not in git; the connection cap and hello rate limit refuse a
  flood without disturbing seated players.
- **C53 Dev path intact.** `make up` + the full local sweep still pass
  untouched on kind — production hosting changed nothing local.

### Explicitly still deferred

Real accounts and token signing (players are the household; the trigger
stays "players other than us"), wss/TLS on the LAN (an internal CA every
client must trust buys little on a private LAN — revisit if the LAN stops
being trusted), sharding, and everything else in the table below.

# Phase 7 — accounts, and the site that manages them

### Where Phase 7 stands (2026-09-03)

Built and green on kind: all seven tasks landed, C54–C59 PASS (25 checks,
`docs/QA-STATUS.md` "Phase 7"), the packaged client carries the F1 link
panel, and the site ships inside the server binary. Prod re-verification
rides the merge (CD deploys it; run `t28` against the public URL after).
The run's one catch — argon2's 64 MiB first-choice parameters OOM-killing
a 128Mi pod on the first registration — is fixed and recorded. Still
deliberately absent: password reset email (no SMTP exists; a locked-out
account is admin-fixable via the store) and strict accounts-only join
(coexistence rule stands).

**Playable proof.** Visit `https://game.stevenholder.info/`: a landing page
with live stats and how to get the client. Register with email + password.
Your account page mints a link code; type it into the game and you are
playing as your account's player. Change your password, see your credits
and ships on the web, delete the account and everything yours is gone.

**Trigger.** The Deferred table's "real accounts (players other than us)"
armed the moment the public door opened. This lands the minimum: accounts
that ISSUE game identity, not decorate it — the account mints the token.

**Coexistence rule, load-bearing.** Anonymous tokens keep working exactly
as today (join with any fresh token = a guest). Every e2e harness and all
current progress rides them; an account can IMPORT a legacy token to adopt
that progress. Strict accounts-only join is deferred until abuse appears.

### Wave 0 — contracts

- Schema (`002_accounts.sql`): `account` (id, email UNIQUE, argon2id
  `pw_hash`, created), `web_session` (id, account_id, expires),
  `link_code` (code, account_id, expires), `player.account_id` nullable.
  One player per account per world: redeeming a code returns the
  account's existing player token when there is one.
- HTTP API under `/api/` in the game server binary (embedded site via
  go:embed, no build toolchain — the site is plain HTML/CSS/JS, C47's
  spirit applied to the web): register, login, logout, me, link-code,
  redeem (the game client's exchange), import-token, password, delete,
  stats (public). Sessions: HttpOnly SameSite=Strict cookie backed by
  `web_session`; mutating routes require the `X-Requested-With` header
  (CSRF); login/register ride the gatekeeper's per-IP buckets.
- Ingress: the public host rule widens to PathPrefix `/`; the hostless
  LAN rule keeps its three exact paths.
- Unity: an account panel (IMGUI, like everything) that takes a link
  code, redeems it over HTTPS derived from the server URL, stores the
  token in PlayerPrefs and reconnects.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | Migration 002 + store methods (accounts, sessions, codes, import, delete cascade) | `server/internal/store/` | store suite, both engines |
| 2 | Auth: argon2id hashing, session mint/check, login rate limit | `server/internal/web/` | unit |
| 3 | API handlers + embedded site (landing/login/account pages) | `server/internal/web/`, `site/` | `t28` |
| 4 | Token redeem: code → account player token; hello path unchanged | `server/internal/web/`, `server.go` | `t28` |
| 5 | Ingress PathPrefix + landing stats | `deploy/prod/25-ingress.yaml` | live |
| 6 | Unity account panel: enter code, redeem, store, reconnect | `client-unity/Assets/Game/` | typecheck + live |
| 7 | e2e harness against C54–C59 | `test/t28-accounts.mjs` | `node test/t28-accounts.mjs` |

### Acceptance criteria

- **C54 Register/login.** Email+password registers (argon2id at rest, never
  logged), logs in, sessions survive server restarts (DB-backed), a wrong
  password is refused, login is rate-limited per IP.
- **C55 Account-issued identity.** A link code minted on the site, redeemed
  by the client, joins the game as the account's player; redeeming twice
  yields the same player; codes expire and are single-use.
- **C56 The account page tells the truth.** Credits, inventory and
  ownership shown match a live t15-style probe of the same player.
- **C57 Lifecycle.** Password change invalidates other sessions; account
  delete removes account, sessions, codes and owned players — and the
  issued game token is refused afterward.
- **C58 The front door.** The landing page serves publicly with live
  player counts and client instructions; no authenticated data leaks to
  anonymous visitors.
- **C59 Coexistence.** The full existing harness fleet still passes
  unchanged (anonymous tokens live); an imported legacy token's progress
  appears under the account.

# Phase 8 — the interface earns its looter stripes

### Where Phase 8 stands (2026-09-03)

Built and green on kind: all eleven tasks landed, C60/C62–C65 PASS and
C61 PARTIAL (`docs/QA-STATUS.md` "Phase 8"). The entire interface is
code-built UI Toolkit — IMGUI is retired, `grep OnGUI Assets/Game` finds
nothing — with the gallery in `test/out/ui/` as the C60 artifact and
framestats at 120 fps / worst 8.6 ms for C65. Two things only a human
can do remain: eyeball the damage numbers in a live camp fight (C61),
and click a UI Toolkit button (buy / equip / account link) in the
packaged player once — the cmd bytes those clicks send are already
proven byte-identical by t14.

**Playable proof.** The game LOOKS like a looter shooter: shots land with
damage numbers and crits that pop, a hit from behind points behind you, a
compass strip names where the shop and your ship are, the bags are a slot
grid of item cards with rarity color bands, and every panel — HUD, shop,
bags, sheet, map, flight, account — speaks one visual language: Scrapyard
Comic. Nothing about the wire or the sim changes; t2–t28 pass untouched.

**Style: Scrapyard Comic** (GDD "UI style guide" pins the tokens). The
Borderlands school — the HUD as a device the character also sees, angled
panels, thick ink outlines, chunky display type, damage feedback as
spectacle — sized to this game's chunky low-poly world; the extraction
school contributes the grid inventory's utilitarian bones. Research trail
in the phase PR.

### Wave 0 — contracts

- `docs/GDD.md` "UI style guide": the palette (ink/slate/cream/amber +
  the five-tier rarity ramp), typography, the panel construction rules
  (skew, outline, notch), damage-number behavior. The style guide is the
  contract every screen is reviewed against.
- `server/data/items.json`: every item gains `rarity` (common → legendary);
  the defs payload carries it through (additive JSON — old clients ignore
  it, no wire change).
- Tech: Unity UI Toolkit constructed ENTIRELY from C# — no UXML, no USS
  assets, PanelSettings created at runtime; C47's gate stays green. One
  vendored OFL display font (a font file is not a scene asset; license
  text ships beside it). **Spike first**: task 1 proves runtime-only UI
  Toolkit in the packaged player before anything is ported; if Unity's
  asset expectations block it, the recorded fallback is styled IMGUI with
  GUI.matrix skews, and the phase proceeds unchanged above the seam.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | SPIKE: runtime-only UI Toolkit (PanelSettings from code) in the packaged player | `client-unity/Assets/Game/UI/UiRoot.cs` | build + run log line |
| 2 | Style guide in GDD + `Ui.Styles` (tokens as code: colors, spacing, panel factory) | GDD, `UI/Styles.cs` | review + typecheck |
| 3 | Vendored OFL display font + runtime FontAsset | `client-unity/Assets/Game/Resources/Fonts/` | renders in player |
| 4 | HUD port: health/shield bar, ammo mag/reserve split, credits | `UI/Hud*.cs` | screenshots + t-fleet |
| 5 | Damage numbers + crit styling + directional hit indicator | `UI/Combat*.cs` | live camp fight |
| 6 | Compass strip with entity markers (shop, rover, own ship, camp) | `UI/Compass.cs` | bearing unit test + live |
| 7 | Rarity in defs → item cards (color band, icon, hover stats) | data + `UI/Items.cs` | t28/t14 pass + visual |
| 8 | Grid inventory + shop port (buy/equip flows byte-identical on the wire) | `UI/Bags.cs`, `UI/Shop.cs` | t14 unchanged |
| 9 | Map, flight HUD, account panel, interact prompt ports | `UI/` | visual + live |
| 10 | Screenshot gallery per screen (the C60 review artifact) | `test/out/ui/` | files exist |
| 11 | Frame budget re-measured with the new UI | framestats | C65 |

### Acceptance criteria

- **C60 One language.** Every screen conforms to the style guide; the
  gallery in `test/out/ui/` is the review artifact.
- **C61 Combat feedback.** Damage numbers appear on hits with crit
  styling; a directional indicator shows incoming damage; verified in a
  live camp fight.
- **C62 Nothing broke.** The full harness fleet (t2–t28) passes
  unchanged — the refresh is client-side presentation only.
- **C63 The compass tells the truth.** Marker bearings match entity
  positions (unit-tested math, live-checked markers).
- **C64 Rarity end-to-end.** defs carry rarity; shop, bags and loot
  prompts show the band; unknown rarity degrades to common, never breaks.
- **C65 Budget holds.** 120 fps capped, worst frame under 16.7 ms with
  the new UI live (framestats).

# Phase 9 — a world worth walking to

### Where Phase 9 stands (2026-09-03)

Built and green on kind: C66–C71 PASS (docs/QA-STATUS.md "Phase 9").
The kit tiles, the camp and range are rebuilt on layouts, the solver
placed the outpost and the relay, and the audit test holds every site
to the clearance rules. Gallery: poi-range-colony.png,
poi-camp-scrap.png. Owed to a human: eyeballing the two new POIs in a
live walk (no solved routes reach them yet — routes are a natural next
task alongside more POI templates).

**Playable proof.** Stand anywhere and the horizon tells you where to go:
one glowing mast per point of interest, each a different silhouette. Walk
to one and it was BUILT, not stretched — walls tile from a modular kit,
corners belong to posts, nothing clips, and who built it reads at a
glance (Scrapyard lean vs Colony symmetry). Enter it and there is a
reason you came: cover to fight through and a core to loot. The camp is
the first rebuild; two new POIs follow from templates a solver placed.

**Contracts** (wave 0, landed with this section): GDD "World art style
guide" — the two builders, the 4 m module grid, the mast/horizon math,
POI anatomy, the solver's clearance rules.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | Kit generator: wall4/corner/gate4/tower/mast/hab/shack × two faction skins, recipes + manifest ids | `art/tools/gen_kit.py`, `art/recipes/` | `npm --prefix art test` |
| 2 | verify.mjs learns cell bounds: a kit GLB must fit its declared 4 m cells + skirt | `art/tools/verify.mjs` | red on a violating piece |
| 3 | Zone schema v2: `layout` (kit placements on the grid) with colliders DERIVED from it, camp/range migrated | `server/internal/defs`, `server/data/zones/` | go test + t-fleet unchanged |
| 4 | Client renders layouts: tile modules per placement, posts own corners, skirts down — stretched-box path retired | `client-unity/Assets/Game/Structures.cs` | screenshots, no clipping |
| 5 | Camp rebuilt on the Scrapyard kit; spawn + range get Colony dressing | `server/data/zones/`, art | gallery + live look |
| 6 | Masts: emissive tips, per-POI silhouettes; compass discovery gated by the visibility formula | art + `UI/HudView.cs` | bearing + visibility unit test |
| 7 | Placement solver: clearance/slope/spacing rules from the seed, emits zone JSON for review | `server/cmd/server` (new subcommand) | t30 clearance audit |
| 8 | Two new POIs from templates (one Scrapyard loot pocket, one Colony relay), loot cores wired | zones + `server/data/loot.json` | t-fleet + live loot run |
| 9 | QA: gallery per POI, C65 re-run, criteria table | `test/out/ui/`, docs | C66–C71 |

### Acceptance criteria

- **C66 The kit is honest.** Every kit piece passes verify (cell bounds,
  skirt, tri budget); no visual in the world is a unit box scaled past
  its banded axis.
- **C67 Nothing clips.** The rebuilt camp has no interpenetrating
  pieces (pairwise bounds check in the layout validator) and the fleet
  passes unchanged against its colliders.
- **C68 Landmarks work.** Each POI mast's height clears the visibility
  formula for its intended discovery range; compass markers appear at
  discovery range, not before.
- **C69 The solver is a contract.** Same seed, same sites; every
  clearance rule holds, proven by an audit test (t30) not a promise.
- **C70 POIs pay off.** Two new POIs live with guarded loot cores;
  killing the guards and looting the core works end to end (t29-style
  run extended or a sibling).
- **C71 Budget holds.** 120 fps capped, worst frame < 16.7 ms with the
  new world live.

# Phase 10 — missions, parties, and the bounty

### Where Phase 10 stands (2026-09-08)

Built and green on kind: C72–C77 recorded (docs/QA-STATUS.md "Phase
10"). Server: parties, the mission engine (kill/scout/fetch,
party-wide credit, the dispatcher board at the relay), and the bounty
machine (post → race → claim → expire/steal/abandon → re-post), all
wire- or white-box-tested. Client: journal (J), party panel (P),
look+E invites, priority banner, WARLORD compass marker. Owed to
humans: a two-player live session — party up, share a camp fight,
claim one warlord together.

**Playable proof.** Walk to the relay and the dispatcher has work: kill
missions, scout missions, fetch missions — take them alone or invite a
friend (look + E, or the party panel) and every kill counts for both of
you, full pay each. Then the HUD toast fires: a warlord has been sighted
at a POI. First party to accept claims it; kill it together inside 15
minutes or it re-posts for someone else. Everything — offers, claims,
progress, pay — is the server's word; the journal just shows it.

**Contracts** (wave 0, landed with this section): GDD "Missions and
parties", PROTOCOL cmd ops `0x0006`–`0x000C` and events
`0x0007`–`0x000B`, `server/data/missions.json`.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | Party core: membership, invite/respond/leave cmds, roster events, disconnect handling | `server/internal/server/party.go` | unit tests + t31 |
| 2 | Party client: P panel (roster, invite, accept toast), look+E invite path | `client-unity/Assets/Game/UI/` | typecheck + live |
| 3 | Mission registry + per-player persisted state + list/accept/abandon cmds | `server/internal/missions/`, store | unit tests |
| 4 | Progress engine: kill/scout/fetch hooks on existing event paths, party-wide credit, completion pays | server | unit tests + t32 |
| 5 | Dispatcher NPC at the relay + board flow + fetch turn-in | zones, `server/data/missions.json` | t32 |
| 6 | Journal UI (J): offers, active missions, progress bars; HUD objective line + compass marker to the objective POI | `client-unity/Assets/Game/UI/` | screenshots |
| 7 | Bounty lifecycle: warlord archetype, spawn at solver-eligible POI, claim/expiry/release state machine, priority_offer broadcast | server | unit tests |
| 8 | t33: two-client claim RACE (exactly one wins), party-shared completion, expiry re-post | `test/t33-bounty.mjs` | the test |
| 9 | QA: C72–C77 recorded, gallery, fleet unchanged | docs, `test/out/ui/` | criteria |

### Acceptance criteria

- **C72 Parties work.** Invite (both paths), accept, leave, dissolve;
  roster events reach every member; a member's disconnect updates the
  rest within a tick's breath. (t31)
- **C73 Personal missions pay.** Kill, scout and fetch each complete
  end to end and pay their credits exactly once; fetch consumes the
  items at turn-in; abandon works. (t32)
- **C74 Party credit is party-wide.** Two clients in a party: one acts,
  both progress, both get full pay. (t32)
- **C75 The bounty is exactly-one.** Two clients race `mission_accept`:
  one claim, one `"claimed"` refusal — the seat-race proof, replayed
  for missions. (t33)
- **C76 The bounty never wedges.** Expiry releases and re-posts; a
  stolen kill (non-claimant) releases without pay; all-claimants-offline
  releases. (t33 + unit)
- **C77 Nothing else moved.** The full harness fleet t2–t29 passes
  unchanged; frame budget holds with the journal open.

# Phase 11 — skills: what you did is what you are

### Where Phase 11 stands (2026-09-15)

Built and green on kind: C78–C83 recorded (docs/QA-STATUS.md "Phase
11"). Server: the roster and curve, the award engine (metres, damage,
kills, pickups, credits, discovery — batched once a second), movement
efficacy inside both sims, damage/price/loot efficacy and the declared
synergies on the server side, the data-driven unlock gate. Client: the
K panel (ten rows, bars, arrows, reserved greyed), the XP drip and the
LEVEL UP banner. `t34` plays the whole loop over the wire and watches
seven skills move and persist. Owed to humans: the drip and the banner
seen live. Phase 12's artisan loop is next; `shop_sell` is the first
verb it should add, so the Scavenging→Commerce synergy has something to
touch.

**Playable proof.** Open the sheet (K) and ten skills stare back, seven
of them moving: sprint to the camp and Athletics ticks, win the fight
and Marksmanship climbs, drag the loot home and Scavenging pays
Commerce a visible synergy bonus at the shop counter. A level-up
banners mid-fight and your next magazine hits harder. A fresh player's
numbers are exactly 1.0 — nothing changes until trained — and the
conformance suites hold at 1e-6 WITH multipliers live in both sims.

**Contracts** (wave 0, landed with this section): GDD "Skills" — the
ten-skill roster, the frozen RS curve, the efficacy/two-sims
constraint, declared synergies, the K panel.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | skills.json (roster, awards, efficacy, synergies, unlock reqs) + registry parse | `server/data/`, `internal/defs` | go test |
| 2 | Store: skills column (XP map + discovered POIs), migration 004 | `internal/store` | store tests both engines |
| 3 | Curve math, pinned: points(L) landmarks, level-from-XP, both directions | `internal/skills` | unit vs 83 / 101,333 / 13,034,431 |
| 4 | Award engine: hooks on damage/kill, distance (sprint/drive/fly), pickups, commerce, discovery; per-skill ≤1/s batch → skill_xp events | server | unit + wire |
| 5 | Sim multipliers: sprint/drive/fly efficacy in BOTH sims, shipped on the sheet + level-ups, predictor applies; conformance cases at non-unit mults | `internal/sim`, C# mirror | t20/t23/t25 extended |
| 6 | Server efficacy: damage mult, shop prices, loot extra-roll; synergy resolution; unlock gate on purchases | server | unit + t-fleet |
| 7 | Client: sheet state, K panel (rows, bars, synergy arrows, greyed reserved), XP drip + LEVEL UP banner | `client-unity` | screenshots |
| 8 | t34: live loop — sprint/fight/loot/trade/discover, watch seven skills move and persist across reconnect | `test/` | the test |
| 9 | QA: C78–C83, gallery, docs | docs | criteria |

### Acceptance criteria

- **C78 Doing trains.** Every hooked verb awards its skill; events
  batch (≤1/s/skill); XP survives reconnect. (t34)
- **C79 The curve is RuneScape's.** Landmarks pinned by unit test;
  level 92 is half of 99.
- **C80 Efficacy is real and conformant.** Measured deltas at trained
  levels vs 1; C30/C34-class conformance holds at 1e-6 with non-unit
  multipliers in both sims; fresh players are bit-identical to today.
- **C81 Synergies apply and show.** Bonus math unit-tested; the panel
  draws the links.
- **C82 Unlocks gate.** A data-gated purchase refuses below its level
  and passes at it.
- **C83 Nothing else moved.** Fleet t2–t29 unchanged; frame budget
  holds with the panel open.

# Phase 11.5 — the engine swap: Unity out, Godot in

**Why.** Unity's toolchain fought the agent-first, code-first workflow: the
project had to live on `/mnt/c` (Unity fatals on WSL paths), the Editor held a
project lock so `unity compile`/`build` failed while it was open, batchmode
exited 0 on compiler errors, no agent could drive the Editor, and CI never
built the player. On top of that, Unity-the-company and its licensing.

**What made it cheap.** Phase 3.5's split paid off: `Sim`, `Net` and
`Game/Core` referenced no engine, so they moved to `client/shared/` unchanged
(5.1k lines, `git mv`), and the harnesses that drive them through `SimDump`
proved the move before a single engine API was touched. Only the engine-bound
half of `Game/` was rewritten (~6.4k Godot lines for ~7.6k Unity lines), and
there was no scene or prefab to migrate because there never was one (C47).

**Two findings that shaped the port.** The Sim was already in Godot's frame —
right-handed, Y-up, +Z forward — so the Unity build's Z-mirror and every
"rebuild the rotation from two converted axes" workaround simply disappeared;
`Frame.cs` is the identity plus one 180° model flip for −Z-facing glTF. And
`Godot.NET.Sdk` pulls the engine bindings from NuGet, so `dotnet build`
typechecks the whole client with no editor: CI went from compiling 0% of the
client to 100%.

**Engine.** Godot 4.7.2 (.NET), pinned in `client/.godot-version`;
`net8.0` game assembly over `netstandard2.1` shared ones; `gl_compatibility`
renderer so WSLg runs it. Export is `godot --headless --export-release`,
Linux and Windows presets from the same Linux box.

### Task list

| # | Task | Where | Verify |
|---|---|---|---|
| M0 | Move Sim/Net/Core/SimDump into `client/`, Godot project skeleton, `godot-cli`, Makefile and CI renames | `client/` | `make godot-gate godot-test godot-codec godot-conformance` |
| M1 | Connect, terrain, local walk with prediction; `Frame`, `InputState`, `Fps`, `TerrainMesh` | `client/godot/Game/` | `make godot-build godot-run` → `world ready`, `colliders:` |
| M2 | Entities, characters, glTF at runtime, animation, structures, props | `AssetRegistry`, `Entities`, `CharacterAnim`, `Structures` | `-dumpNodes char.player`; screenshot |
| M3 | Seats, rover and ship prediction, sky, rocks | `Boot`, `Sky`, `Rocks` | `node test/t24 t26 t27`; screenshot |
| M4 | The interface: HUD, panels, map, journal, party, skills | `UI/`, `Map`, `Missions`, `Skills` | `-uiPanel` gallery |
| M5 | Combat FX, the first-person rig through a SubViewport | `Combat`, `ViewModel` | `-rigArmed` screenshot |
| M6 | Screenshot rig, Windows export, CI export job, docs, `client-unity/` deleted | `Rig.cs`, `.github/`, `docs/` | full `make godot-*` sweep |

### Acceptance criteria

| # | Criterion | Verify |
|---|---|---|
| C84 | Every engine-free assembly moved byte-identical and every SimDump harness stayed green | `make godot-test godot-codec godot-conformance`; `node test/t13 t35` |
| C85 | The client compiles with no editor installed, in CI, including the engine-bound assembly | `dotnet build client/SpaceAdventure.Client.slnx` in `ci.yml` |
| C86 | A player exports headlessly from the command line and joins the deployed server from a cold start | `make godot-build godot-run` |
| C87 | Sign rules pinned headless: mouse-right turns toward facing × up, mouse-up looks up, winding measure, model flip | `godot --headless --path client/godot -- -selftest` |
| C88 | glTF node contract survives the importer (`hand.r` → `hand_r`, clips `idle walk sprint die`) | `-dumpNodes char.player` |
| C89 | The C60 gallery reproduces: HUD, skills, map, bags, armed rig | `-uiShot` with `-uiPanel` / `-rigArmed` |
| C90 | Windows export from Linux | `./client/godot-cli build Windows` |
| C91 | No agent-authored scene or resource; one `Boot.tscn`; no `using Godot` in `shared/` | `make godot-gate` |

# Phase 11.7 — the interface, drawn (2026-09-29)

The HUD earned its stripes in Phase 8; the panels were still lists. This
phase makes the character panel and the backpack graphical — WoW's paper
doll, Borderlands' item cards — on a framework the other panels are
restyled onto next: `ItemSlot` (icon, rarity frame, count, drag source and
target, right-click, tooltip), `Icons` (build-rendered PNGs), `Doll` (the
character's own model in its own viewport). Contract: GDD "Character panel
and backpack", GDD "Equipment", PROTOCOL `equip`/`defs`.

**Playable proof.** Buy the Scout set and the rifle, press C: the
astronaut turns in the middle of the panel wearing what you bought, each
slot framed in its rarity, ARMOR reads 23, DAMAGE reads 25 and climbs with
Marksmanship. Press B: cells and the ship in a 5×4 grid, the worn things
gone from it. Drag the helmet from the bag onto HEAD; right-click it to
take it off.

| # | Task | Where | Verify |
|---|---|---|---|
| 1 | `equip_slots` in items.json, `Equip` validates the set, accessories, unequip, one-slot-per-item | server | `TestEquipSlots` |
| 2 | Scout set + Rabbit's Foot with `armor`/`desc`, at the quartermaster | `server/data` | defs audit |
| 3 | `gen_icons.py` → `art/icons`, staged beside art | `art/tools`, `godot-cli` | icons exist for every item with a model |
| 4 | `ItemSlot`, `Icons`, tooltips, drag/drop | `client/godot/Game/UI/Inventory.cs` | C107 |
| 5 | `CharacterView` (doll, slots, stats, skills), `BackpackView` (grid) | same | C107, C108 |
| 6 | Boot: one modal at a time, equip result redraws, rig `-uiPanel character|backpack`, `-uiBuy a,b,c` | `Boot.cs`, `Rig.cs` | shots |
| 7 | Restyle shop, journal, party onto the framework (cards: `Styles.Card`/`Tile`/`Progress`), map gets the drag grip; skills already had bars | `Panels.cs`, `MissionPanels.cs`, `Map.cs` | C115–C117 |

# Phase 12 — the artisan loop (2026-09-29)

### Where Phase 12 stands (2026-09-29)

Built and green on a bare server: C120–C128 recorded (docs/QA-STATUS.md
"Phase 12"). Server: nodes with health = yields and in-place respawn,
the server-timed channel with every cancel reason, `shop_sell` with the
synergy that waited since Phase 11, the bench and three recipes,
materials spilling on death (and loot finally expiring), `gather_speed`
/ `craft_extra` efficacy with the Engineering→Mining synergy. Art: two
ore clusters, a wreck, a bench, a drill and a cutter. Client: node
models with a depleted state, drill/cut/use prompts that name the
missing tool, the channel bar, the bench panel, the shop's SELL column,
the tool on the doll, the K panel's three new rows. `t36` plays the pad
loop and the walk to the bench over the wire. Owed to humans: a real
death with ore in the bag, a wreck cut under fire, cells crafted with
scrap in hand. Medkits and weapon mods still wait for a `use` verb.

The three reserved skills get their verbs. Ore in the rocks around the
pad, wrecks inside the guarded scrapyard, a workbench at the relay, a
buyer at the pad — and the loop is the walk between them. Raw materials
spill where you die; nothing else does. Contract: GDD "Phase 12 — the
artisan loop" (nodes, the channel, `shop_sell`, recipes, the spill,
efficacy), PROTOCOL `shop_sell` / `gather` / `gather_cancel` / `craft`,
entity type `node`, event `gather_end`, `defs.nodes` / `defs.recipes`.

**Playable proof.** Buy a drill (120 cr), equip it in TOOL, walk to the
rock pile east of the pad and press E on an ore node: a bar fills for
three seconds and two iron ore land in the bag, the node's glow dims
one step. Do it five times and it goes dark; come back in ninety
seconds. Walk to the scrapyard with a cutter, clear the guards, cut the
wrecks for scrap. Die on the way out and the ore and scrap lie where
you fell for two minutes — run back, or lose them to whoever is
closer; the rifle and the credits never left you. At the relay bench,
two ore and a scrap become thirty cells; at Engineering 5 an iron
chest plate out-armors the Scout Suit. Sell the rest to the
quartermaster and watch Commerce pay Scavenging's synergy for the
first time. On K, Mining, Salvaging and Engineering are no longer grey.

**Contracts** (wave 0, this section): GDD "Phase 12 — the artisan
loop" and the roster/synergy edits; PROTOCOL opcodes `0x000F`–`0x0012`,
event `0x000E`, entity type `0x0008`, the new refusal codes, the `defs`
additions. The task table below IS the brief set — every row is one
dispatch with its contract in the GDD/PROTOCOL paragraph it names, as
Phases 3–11.7 did (no `docs/tasks/` files since Phase 2).

### Task list

Wave 1 lands the wire and the data with no substance (every new verb
answers, every node spawns, nothing yields); wave 2 fills it in
parallel; wave 3 is `qa`.

| # | Wave | Task | Where | Verify |
|---|---|---|---|---|
| 1 | 1 | Data: `nodes.json`, `recipes.json`; tools, materials, `armor.plate.iron`, per-item `value`/`supersedes` in items.json; `npc.workbench` + tool prices in npcs.json; node loot tables; skills.json un-reserves the three, adds their efficacy and Engineering→Mining; zone placements (spawn ×3 iron + ×1 copper, outpost ×2 wreck, relay bench) | `server/data` | defs audit test |
| 2 | 1 | Registry + payload: parse nodes/recipes/value/supersedes, ship `nodes`, `recipes`, `constants.sell_rate`; still under 64 KiB | `internal/defs` | `go test ./internal/defs` |
| 3 | 1 | Constants both ends: opcodes `0x000F`–`0x0012`, event `0x000E`, `EntityTypeNode` 8 | `protocol.go`, `Messages.cs` | `go build` + `dotnet build` |
| 4 | 1 | Node entity: `sim.Ent` kind Node, `NodeState{Yields, RespawnTicks}`, zone spawn branch (`type:"node"`), registered step that respawns, `health` = yields | `internal/sim`, `server.go` | `TestNodeDepletesAndRespawns` |
| 5 | 1 | Skeleton handlers: `gather` walks every pre-channel refusal and ends at `not_implemented`; `gather_cancel` answers `not_gathering` | `server/cmd.go` | `TestHandleCmdPhase12Skeleton` |
| 6 | 1 | `sim.SellAt` (value, `sell_rate`, bonus, worn refusal, atomic) + handler + Commerce XP — landed with wave 1: it was smaller than its skeleton | `internal/sim`, `server/cmd.go` | `TestSellAt` |
| 7 | 2 | The channel: `gatherNode`/`gatherTicks`/`gatherFrom` on the client struct, stepped in `tick()`; cancel on move/hit/death/disconnect/verb; yield roll + grant + node `−1` + XP + `gather_end` outside `s.mu` like pickups | `server/gather.go` | `TestGatherChannel` |
| 8 | 1 | `sim.Craft` (inputs out, output in, atomic per call, bonus units) + handler + Engineering XP — landed with wave 1; the `craft_extra` roll waits for task 10 | `internal/sim`, `server/cmd.go` | `TestCraft` |
| 9 | 2 | Death spill: materials → loot drops at the body (queued under `s.mu`, granted via Mutate, added after); register `StepLoot` so drops finally expire | `server/npcs.go`, `internal/sim/loot.go` | `TestDeathSpillsMaterials`, `TestLootExpires` |
| 10 | 2 | Efficacy: `gather_speed` (floored), `craft_extra`, `sell_bonus` read, tool `supersedes`, `unlock_requirements` on `tool.drill.mk2` | `server/skillsengine.go` | unit |
| 11 | 2 | Art: `gen_nodes.py` → `prop.node.ore.iron`, `prop.node.ore.copper`, `prop.wreck`, `prop.bench`, `tool.drill`, `tool.cutter` (manifest + icons) | `art/` | `verify.mjs` + icons exist |
| 12 | 2 | Client: node entity type → model from `defs.nodes`, depleted state (0.6 scale, dark), prompts `drill`/`cut`/`use` | `Entities.cs`, `Interact.cs` | shot |
| 13 | 2 | Client: E sends `gather` / opens the bench, channel bar under the crosshair for `duration`, `gather_end` clears it and drips the yield | `Boot.cs`, `HudView.cs` | shot |
| 14 | 2 | Client: bench panel — recipe cards (have/need, locked, CRAFT), material counts | `UI/BenchPanel.cs` | shot |
| 15 | 2 | Client: shop SELL column (unit price with synergy, click one, shift-click stack) | `UI/Panels.cs` | shot |
| 16 | 2 | Client: skills rows un-grey + the new arrow; tool slot on the doll | `SkillsPanel.cs`, `Inventory.cs` | shot |
| 17 | 2 | Rig: `-uiPanel bench`, `-uiGatherDemo`, `-uiSellDemo`, `-uiSpillDemo` | `Rig.cs`, `Boot.cs` | shots |
| 18 | 3 | `t36-artisan.mjs`: buy + equip drill, gather (timing, move-cancel, no-tool, locked), sell (price, XP), craft (atomic, locked), die with ore → spill → recover, three skills persist across reconnect | `test/` | the test |
| 19 | 3 | QA: C120–C128, gallery, docs | docs | criteria |

### Acceptance criteria

- **C120 Nodes exist and deplete.** Three iron and one copper node spawn
  at the pad, two wrecks inside the outpost walls, all as type `0x0008`
  with `health` = yields; five yields dark an iron node and ninety
  seconds relight it. (t36)
- **C121 The channel is server-timed.** `gather` returns `duration`; the
  yield lands `duration` later (±1 tick), never before; moving 0.5 m
  ends it `moved`, damage ends it `hit`, an empty tool slot refuses
  `no_tool`, copper below Mining 10 refuses `locked`, a full bag refuses
  `no_space` before the bar ever starts. (unit + t36)
- **C122 Selling pays.** Four iron ore at the quartermaster → 12 cr at
  `sell_rate` 0.5; Commerce XP moves; at Scavenging > 1 the unit price
  is visibly higher — the synergy declared in Phase 11 finally applies.
  Ammo has no value and refuses `unsellable`; a worn plate refuses
  `equipped`. (unit + t36)
- **C123 Crafting is atomic and gated.** `recipe.cells` consumes 2 ore +
  1 scrap and yields 30 cells; short one scrap → `missing_materials`
  with nothing consumed; the plate below Engineering 5 → `locked`;
  `qty: 3` is three crafts or none. (unit + t36)
- **C124 Death spills the raw.** Die holding ore and scrap: one loot
  drop per stack at the body, bag empty of materials, rifle, tools,
  credits and cells intact; walking back recovers them (Scavenging XP);
  an untouched drop is gone at 120 s. (unit + t36)
- **C125 Three skills move and stay.** Mining, Salvaging and
  Engineering each raise `skill_xp`; levels survive reconnect. (t36)
- **C126 Efficacy is real.** Channel time shortens per Mining level and
  floors at 1.0 s; `craft_extra` rolls; Engineering→Mining shows on the
  panel and shortens the bar; the mk2 drill satisfies an iron node.
  (unit + shot)
- **C127 Seen.** Node models with a depleted state, the channel bar, the
  bench panel, the SELL column, three un-greyed rows and a fourth arrow
  — via the rig. (shots)
- **C128 Nothing else moved.** t14, t34, t35 and the whole `godot-*`
  sweep green; a player who never touches a node plays exactly the
  game they had. (sweep)

# Phase 13 — use, modify, and the hotbar (2026-09-30)

### Where Phase 13 stands (2026-09-30)

Built and green on a bare server: C129–C136 recorded (docs/QA-STATUS.md
"Phase 13"). Server: `use` with the GDD's order and per-connection
cooldowns, the medkit and the scanner, mods applied wherever a weapon
table is read, the mag clamp that hands rounds back. Client: the
twenty-slot hotbar (references, defaults on E and R, `user://sa.cfg`),
drag from the bag or the doll, right-click USE, cooldown sweeps, the
shift row, the prompt reading whichever key holds interact, scan pings
on the compass, MOD under WEAPON with live DAMAGE / MAGAZINE / RANGE.
`t37` plays the medkit over the wire. Owed to humans: dragging on a
real mouse, a barrel mod's 140 m hit, a scan in the scrapyard.

Phase 12 put ore in the bag and a bench at the relay; this phase gives
the bench things worth making and the player a bar to put them on. A
`use` verb for consumables and gear abilities, weapon mods that spend
the copper tier, and a twenty-slot hotbar the player fills by dragging.
Contract: GDD "Phase 13 — use, modify, and the hotbar", PROTOCOL `use`
(`0x0013`), `defs` `consumable` / `ability` / `mod` blocks, `mod` in
`equip_slots`. The hotbar is client state and never on the wire.

**Playable proof.** Craft two medkits and a scanner at the bench. Open
the backpack and drag a medkit onto the hotbar's 1; drag the scanner,
worn in GADGET, onto Q. Walk into the scrapyard, take a hit, press 1:
health jumps 50, the cell darkens and sweeps back over eight seconds,
its count reads 1. Press Q: three ore markers and a drop light the
compass for twenty seconds. Hold Shift and the bar shows the other
row. E still drills and R still reloads, because they are on the bar
too, and moving interact to F makes the world prompt say `F · drill`.
At Engineering 12, a barrel mod on the rifle reads RANGE 160 on the
sheet and lands hits at 140 m that used to miss.

**Contracts** (wave 0, this section): GDD "Phase 13 — use, modify, and
the hotbar" and the `mod` slot line under Equipment; PROTOCOL opcode
`0x0013`, the four refusal codes, the `defs` additions. Task table is
the brief set, as before.

### Task list

Wave 1 lands the verb, the data and the mod slot (a medkit heals, a mod
changes a number); wave 2 builds the bar and the panels; wave 3 is `qa`.

| # | Wave | Task | Where | Verify |
|---|---|---|---|---|
| 1 | 1 | Data: `consumable.medkit`, `gadget.scanner`, `mod.barrel` / `mod.mag` / `mod.coil` with their blocks and values; `mod` in `equip_slots`; five recipes in recipes.json | `server/data` | defs audit test |
| 2 | 1 | Registry + payload: `Consumable`, `Ability`, `Mod` on `Item`; audit that a consumable's cooldown and a mod's deltas are sane; `equip_slots` carries `mod` | `internal/defs` | `go test ./internal/defs` |
| 3 | 1 | Constants both ends: `OpUse` `0x0013` | `protocol.go`, `Messages.cs` | build |
| 4 | 1 | `sim.ApplyMod(weapon, mod) defs.Weapon` (deltas add) and every weapon-table read goes through the worn mod: `ResolveShot`, `reload` (clamp, never empty), fire-interval check | `internal/sim`, `server/` | `TestApplyMod`, `TestReloadWithMagMod` |
| 5 | 1 | `use` handler: the GDD's validation order, per-connection cooldown map, gather channel cancelled, `consumable.medkit` heals through `sim.Heal` (capped, `no_effect` at full), `gadget.scanner` pings nodes and drops within range | `server/use.go`, `internal/sim` | `TestUseMedkit`, `TestUseScanner`, `TestUseRefusals` |
| 6 | 2 | Client defs: `ConsumableDef`, `AbilityDef`, `ModDef` on `ItemDef`; `Character` stats apply the worn mod (DAMAGE / FIRE RATE / MAGAZINE / RANGE) and the magazine display clamps | `Defs.cs`, `Character.cs` | `dotnet build` + sheet shot |
| 7 | 2 | `Hotbar` model: twenty `HotbarRef {kind, id}`, defaults (E interact, R reload), `user://sa.cfg [hotbar]` load/save, key table `1 2 3 4 5 Q E R T F` × Shift | `UI/Hotbar.cs` | `TestHotbarDefaults` in the headless self-test |
| 8 | 2 | `HotbarView`: ten cells bottom centre, key labels, icons via `Icons`, counts from the bag, greyed when empty/unworn, cooldown sweep from the `use` result, shift row swap while Shift is held | `UI/Hotbar.cs` | shot |
| 9 | 2 | Firing: keys route through the bar — `item`/`ability` send `use`, `interact`/`reload` call the old paths; the world prompt reads the key that holds `interact`; `use` results and refusals to the HUD | `Boot.cs`, `Interact.cs` | shot + t37 |
| 10 | 2 | Drag: `ItemSlot` drags from the backpack (consumables) and the character panel (gear with `ability`) drop onto a cell; cell → cell moves; right-click clears; right-click USE on a bag consumable | `UI/Inventory.cs`, `UI/Hotbar.cs` | shot |
| 11 | 2 | Character panel: MOD slot under WEAPON; bench cards for the five recipes (no new code expected — verify) | `UI/Inventory.cs` | shot |
| 12 | 2 | Compass: scan pings as markers for `scan_show`, one toast | `Hud.cs`, `UI/HudView.cs` | shot |
| 13 | 2 | Art: `gen_icons` covers the new items (medkit, scanner and mods get small models or initials tiles — decide by what reads at 96 px) | `art/` | icons exist or initials fall back |
| 14 | 2 | Rig: `-uiHotbarDemo` (fills the bar locally: a medkit on 1, scanner on Q, a cooldown sweep on 1), `-uiShift` (hold Shift for the shot) | `Rig.cs` | shots |
| 15 | 3 | `t37-use.mjs`: buy medkits at the quartermaster (stocked, GDD); `use` at full → `no_effect`; walk t34's route into the camp, take a hit, `use` → +50 and `cooldown: 8`, a second inside 8 s → `cooldown` with `ready_in`, the bag count drops by one; the rifle → `unusable`, the unworn scanner → `not_owned`; `use` mid-channel at the pad ends the gather `cancel`. The scanner's pings and the mods' numbers are unit-tested and photographed (they are bench-made behind Engineering levels the harness cannot reach in one life) | `test/` | the test |
| 16 | 3 | QA: C129–C136, gallery, docs | docs | criteria |
| 17 | 2 | Selling as a bag gesture (WoW): right-click sells one, Shift the stack, while a shop is open; the SELL column goes | `UI/Inventory.cs`, `UI/Panels.cs` | shot |
| 19 | 2 | Display and settings: 1080p canvas scaling in project.godot, SETTINGS off the Esc menu (display mode, UI scale, mouse sensitivity), `[settings]` in sa.cfg, applied at boot; Shift row gated off | `project.godot`, `UI/SettingsPanel.cs`, `Boot.cs` | shots at 1080 and 720 |
| 18 | 2 | Buyback: per-connection sale log (12, newest last), `shop_list` carries it, `shop_buyback` returns the newest sale whole at the shop's price; BUYBACK tab on the shop | `server/shop.go`, `cmd.go`, `UI/Panels.cs` | `TestShopBuyback`, t36 |

### Acceptance criteria

- **C129 `use` heals and cools.** A carried medkit heals 50 capped at
  max, one unit leaves the bag, the result carries `cooldown: 8`; a
  second `use` inside 8 s refuses `cooldown` with `ready_in`; at full
  health `no_effect` and nothing consumed; dead → `dead`; a medkit not
  carried → `not_owned`; the rifle → `unusable`. (unit + t37)
- **C130 The scanner pings.** Worn in GADGET, `use` returns every node
  and drop within 120 m with positions and node health; unworn →
  `not_owned`; 30 s cooldown; the compass draws them for 20 s. (unit +
  t37 + shot)
- **C131 Mods change the numbers everywhere.** `mod.barrel` lands a hit
  at 140 m that misses bare; `mod.mag` reloads to 40 and a mag of 40
  clamps to 30 on the next reload after the mod comes off, rounds never
  vanish; `mod.coil` deals 30; the sheet's DAMAGE / MAGAZINE / RANGE
  read the same numbers the server uses. (unit + t37 + shot)
- **C132 The bar fires.** 1–5 Q E T Z X fire their slots; Shift + key
  fires the second row; F interacts, R reloads only with a gun worn,
  neither is on the bar; a shop or bench panel closes when the player
  walks more than `ui_close_dist` from its NPC. (t37 for the wire half,
  shots and the self-test for the rest)
- **C133 The bar is filled by dragging.** Backpack → cell for a
  consumable, character panel → cell for a worn gadget, cell → cell
  moves, right-click clears, drop on occupied replaces; the layout
  survives a restart through `user://sa.cfg`. (rig drag demo + shot)
- **C134 The bar draws its state.** Key labels, icons, bag counts,
  greyed at 0 or unworn, a cooldown sweep for exactly the result's
  seconds, the shift row while Shift is held. (shots)
- **C135 Recipes exist.** Five new bench cards, medkit at Engineering
  3, scanner at 6, barrel and mag at 12, coil at 15; copper is spent
  for the first time. (defs audit + shot)
- **C137 Selling is a bag gesture.** With a shop open, right-click on a
  bag stack sells one and Shift-right-click the stack; the bag's hint
  says so; with no shop open right-click equips or uses as before. (shot
  + t36's `shop_sell`)
- **C138 Buyback.** A sale lands on the list at what the shop paid,
  `shop_list` carries it, `shop_buyback` returns the newest sale whole
  for that price and removes the entry, refusals leave it, twelve kept,
  gone at logout; the shop's BUYBACK tab lists them with BUY BACK.
  (unit + t36 + shot)
- **C139 Display and settings.** The UI is laid out for 1920×1080 and a
  smaller window scales it down as one piece (no overlap), a larger one
  up; SETTINGS off the Esc menu switches windowed / windowed fullscreen
  / fullscreen, UI scale and mouse sensitivity, and they survive a
  restart. (shots at 1920×1080 and 1280×720)
- **C140 The Shift row is off.** Shift + key does nothing on the bar;
  the twenty slots and their saves stay for later. (self-test)
- **C136 Nothing else moved.** t14, t34, t36 and the whole sweep green;
  a fresh profile plays the game it had. (sweep)

# Phase 14 — wildlife (2026-10-05)

### Where Phase 14 stands (2026-10-05)

Built and green on a bare server: C142–C147 recorded (docs/QA-STATUS.md
"Phase 14"). Nine herds, 25 members, wandering; `t41` plays the proof
herd end to end. Owed to humans: the walk east with a real client, and
whether a herd reads as alive or as scenery that twitches.

PR #57 shipped 69 creatures as hostile archetypes and not one of them is
in the world: the zones place five grunts, three gunners and the vendors.
This phase puts the library on the ground as **herds** in the open country
between the POIs, gives them a wander leg so the planet moves when nobody
is shooting at it, and gives a creature kill a creature's loot instead of a
raider's helmet. Contract: GDD "Wildlife — herds and wandering (Phase 14)",
`server/data/wildlife.json`. No wire change: a herd member is an NPC
entity like a grunt, announced by the same `spawn` frame with its `mob.*`
def, drawn by the client's existing mob renderer.

**Playable proof.** Walk a hundred metres east of spawn. Four green blobs
are grazing on the slope, each drifting a few metres and stopping. One
notices you and the others follow a moment later; kill one and it drops
scrap, not ammo; twenty seconds later it is back where it stood. The camp
grunts, meanwhile, have not moved a step.

**Herds** (the `poi` solver's sites at `-flatten 6 -falloff 4 -spacing 45
-slope-max 20`, nearest first; 25 members):

| herd | def | n | site | spread | wander |
|---|---|---|---|---|---|
| `herd.blobs.east` | `mob.blob.green_blob` | 4 | 11 (99 m from spawn — the proof herd) | 5 | 10 |
| `herd.birbs.north` | `mob.blob.birb` | 3 | 3 | 4 | 12 |
| `herd.dinos` | `mob.big.dino` | 2 | 5 | 5 | 8 |
| `herd.mushnubs` | `mob.blob.mushnub` | 4 | 10 | 5 | 6 |
| `herd.drones` | `mob.mech.eye_drone` | 3 | 23 of a 24-site run at `-spacing 40` (site 4 sat 26 m off the camp route and shot t16's walker) | 6 | 10 |
| `herd.scolitex` | `mob.alien.scolitex` | 3 | 8 | 6 | 8 |
| `herd.imps` | `mob.dungeon.imp` | 3 | 12 | 5 | 10 |
| `herd.yeti` | `mob.big.yeti` | 1 | 1 | 0 | 15 |
| `herd.frogs` | `mob.big.frog` | 2 | 9 | 4 | 8 |

Site directions are the solver's output for seed 1337, recorded in
`wildlife.json` and reviewed there, as zones are. One more rule than the
solver knows: a herd centre stays `aggro_radius + wander + 5 m` clear of
the committed harness route (`test/out/route-camp.json`), because t16 and
t17 walk it unarmed; t41 asserts it.

### Task list

Wave 1 lands the data and the herds standing still; wave 2 makes them
walk; wave 3 is `qa`.

| # | Wave | Task | Where | Verify |
|---|---|---|---|---|
| 1 | 1 | Data: `wildlife.json` with the nine herds; `loot.wild.small` / `loot.wild.big` / `loot.wild.mech` in loot.json (scrap, ore, potions, a charm — no armor, no ammo except the mechs); `ROLES` in `art/tools/mobs.mjs` point swarm+flyer, brute+lurker, drone+walker at them (raiders keep `loot.grunt`) and `mobs.json` + `CATALOG.md` regenerated | `server/data`, `art/tools` | defs audit test, `npm --prefix art test` |
| 2 | 1 | Registry: `Herd` type, `Herds` on `Registry`, `wildlife.json` loaded and audited per the GDD table, `ComposeHerd(h, radiusFn) []Placement` (golden-angle disc, outward yaw) | `internal/defs` | `TestWildlifeAudit`, `TestComposeHerd` |
| 3 | 1 | Placement: herds after the rover, in file order, ids continuing; `npcAI.wander` set from the herd | `server/server.go` | `TestWildlifePlaced` (count, ids after the rover, posts within spread) |
| 4 | 2 | The leg: `PATROL` with `wander > 0` picks, walks at half speed, pauses, repeats per the GDD param table; any other state drops the leg | `server/npcs.go` | `TestWanderStaysInRadius`, `TestWanderZeroIsInert`, `TestWanderDropsOnAggro` |
| 5 | 3 | `t41-wildlife.mjs`: C142–C146 over the wire (herd counts from `spawn` frames, a watched far member's track, the walk east to the proof herd, a kill, the drop, the respawn) | `test/` | `node test/t41-wildlife.mjs` |
| 6 | 3 | Record: QA-STATUS "Phase 14", this section's status line | `docs/` | — |

### Acceptance criteria (C142–C147)

- **C142 Herds exist.** A joiner's `spawn` frames carry exactly the
  members `wildlife.json` declares, by def and count, with ids above the
  rover's. (t41)
- **C143 Placed clear and apart.** Every member stands within
  `spread + 1 m` of its herd centre on the surface; no two members of a
  herd are closer than their archetype's diameter; every herd centre is
  `>= 30 m` from spawn and from every zone origin. (audit test + t41)
- **C144 They wander.** A member with no player inside its `aggro_radius`,
  watched for 30 s from spawn, moves at least 2 m in total and is never
  more than `wander + 2 m` from its post. (t41, a far herd)
- **C145 They fight, drop and return.** Walking into the proof herd draws
  aggro (a member closes and `hit` events land); killing a member emits
  its `death`, spawns a drop whose contents come from a `loot.wild.*`
  table, and `npc_respawn` later it stands at its post again at full
  health. (t41)
- **C146 The camp has not moved.** A camp grunt's position over 30 s of
  idling is unchanged to 0.01 m; t16 and t17 stay green. (t41 + sweep)
- **C147 Nothing else moved.** The whole sweep green; conformance
  untouched (NPC motion is not in it). (sweep)

## Deferred — and what would earn each one a place

Named so nobody builds them speculatively, and so the trigger is explicit.

| Thing | Build it when |
|---|---|
| Sharding, delta snapshots, multiple server processes | one process actually saturates — measure first |
| Real accounts (email/OAuth, sessions) | players other than us play it |
| Managed/hosted Postgres, replicas, backups | deploying somewhere real — the DSN is already the only thing that changes |
| Redis (cache, pub/sub, shared sessions) | a second server process needs to see the first one's state; until then the in-memory world is the cache and a function call is the bus |
| Multiple planets / star systems | one planet has enough content to leave |
| Walking around inside a moving ship | Phase 5 ships and the seated version feels limiting |
| PvP, player-vs-player collision | after NPC combat is fun; PvP changes every balance number |
| Chat, guilds | after Phase 5; not on the critical path (crafting landed in Phase 12, quests in Phase 10) |
| A persistent world players mutate (bases, territory) | Phase 6 candidate — the persistence layer from Phase 2 is the seed |
| Rig + animation clips | procedural motion stops carrying the fidelity |
| Pack aggro (one herd member waking the rest) | a playtest finds herds too easy to pick off one at a time; until then overlapping `aggro_radius` does it |
| NPC flight (the `mob.flying.*` archetypes off the ground) | a herd that should be unreachable on foot is wanted; it needs an NPC airborne regime in steer.go |
| Gear abilities in the movement sim (hover boots, a dash) | Phase 13's `use`/ability framework and hotbar exist; the first sim ability needs both sims stepped identically plus conformance cases, like Phase 11's multipliers — build it when a second ability wants the sim, not the first |
| UDP / WebTransport | WebSocket latency is measured as the limiter, not assumed to be |

## Dispatch pattern (unchanged from Phase 1, because it worked)

Each phase runs the same three waves: **wave 0** freezes contracts (main
thread, docs only), **wave 1** lands the wire shape and the data with none of
the substance, **wave 2** fills in the substance in parallel, **wave 3** is
`qa` against the criteria. Wave 1 is what keeps wave 2 unblocked — `frontend`
develops against a server that speaks the whole protocol and does nothing
interesting, exactly as the Phase 1 skeleton server did.

Every table row above is sized for one agent dispatch: one file, ~150 changed
lines, one verify command, per `.omp/AGENTS.md` "Task sizing". A row that turns
out bigger than that is a main-thread task or a chain — split it, do not grow
the dispatch.

Phase 2's dispatch-ready briefs live in `docs/tasks/` in the `.omp/AGENTS.md`
TASK/FILES/CONTRACT/STEPS/VERIFY/REPORT/BUDGET format. Every phase since has
used its task table as the brief set, each row naming the contract paragraph
it implements.
