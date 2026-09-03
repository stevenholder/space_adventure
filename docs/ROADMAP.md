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
| Crafting, quests, chat, guilds | after Phase 5; none of them is on the critical path |
| A persistent world players mutate (bases, territory) | Phase 6 candidate — the persistence layer from Phase 2 is the seed |
| Rig + animation clips | procedural motion stops carrying the fidelity |
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

The dispatch-ready briefs live in `docs/tasks/` — one file per wave, each brief
in the `.omp/AGENTS.md` TASK/FILES/CONTRACT/STEPS/VERIFY/REPORT/BUDGET format
with the contract pasted in, since agents do not explore the repo.
