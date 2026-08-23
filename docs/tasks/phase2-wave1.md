# Phase 2 — wave 1: wire shape, storage, assets

Wave 1 ships the **shape** of Phase 2 with none of the substance, so wave 2
runs in parallel with nothing blocked — the same move that made Phase 1's
waves work. After wave 1: the codec carries the 54-byte row and the new
message ids, the store opens and migrates, the assets exist, and Postgres runs
in the cluster. Nothing shoots anything yet.

**Batch A** (11 briefs. W1-0 runs first — the other `art` briefs verify with
the script it edits; the rest are parallel, no two touching the same file.)
**Batch B** (7 briefs, after A — each names its dependency)

> A brief whose VERIFY cannot pass until a *sibling* brief lands is a broken
> brief, not a tolerable one: the agent then cannot tell its own failure from
> the wave's. Every VERIFY here is satisfiable on its own.

---

## Batch A

### W1-0 · `art` · Teach `verify.mjs` the Phase 2 id classes and an id filter

**Runs FIRST in this batch.** Every other `art` brief verifies with this
script, and until it knows the new id classes those briefs cannot pass. The id
filter is what makes them independent: an art brief must be verifiable while
its sibling assets do not exist yet.

```
TASK:     Extend the asset verifier with the Phase 2 id-class budgets, the new
          node-name contracts, and an optional id argument that limits the run
          to one asset.
FILES:    art/tools/verify.mjs (edit)
CONTRACT: 1. Add to the existing BUDGETS list (triangle caps by id class):
               [/^weapon\./, 400]
               [/^npc\./,    1500]
               [/^struct\./,  200]
          2. Add to the existing node-name contract map:
               weapon.pulse   -> grip, muzzle
               prop.target    -> plate
               npc.shopkeeper -> eye, head, torso, arm.l, arm.r, leg.l, leg.r
                                 (eye and head must be SIBLINGS, the same rule
                                  char.player already has)
          3. Accept an optional argv[2]: when present, verify ONLY the manifest
             entry whose id equals it, and exit 0 if that one asset passes.
             With no argument, behaviour is exactly as today (verify all).
          Do not change the existing entries or the all-assets exit-code
          behaviour.
STEPS:    1. Extend BUDGETS and the node contract map.
          2. Add the argv filter around the existing per-asset loop.
VERIFY:   cd art && node tools/verify.mjs char.player  -> exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~35 lines, 10 tool calls.
```

### W1-1 · `netcode` · Phase 2 protocol constants

```
TASK:     Add the Phase 2 message ids, entity types, flag bits, cmd opcodes,
          status codes, event ids and size constants to the protocol package.
          Additive only — do not modify any existing constant, struct or function.
FILES:    server/internal/protocol/protocol.go (edit)
CONTRACT: Add to the existing const blocks (keep the existing entries as they are):

          // Message type ids (PROTOCOL.md "Message types").
          MsgBoard      uint16 = 0x000B // Phase 4
          MsgDisembark  uint16 = 0x000C // Phase 4
          MsgSeatResult uint16 = 0x000D // Phase 4
          MsgCmd        uint16 = 0x000E
          MsgCmdResult  uint16 = 0x000F
          MsgDefs       uint16 = 0x0010
          MsgFire       uint16 = 0x0011
          MsgColliders  uint16 = 0x0012

          // entity_type values.
          EntityTypeNPC        uint16 = 0x0003
          EntityTypeTarget     uint16 = 0x0004
          EntityTypeVehicle    uint16 = 0x0005 // Phase 4
          EntityTypeLoot       uint16 = 0x0006 // Phase 3
          EntityTypeProjectile uint16 = 0x0007 // Phase 3

          // Entity.Flags bits (PROTOCOL.md constants). 0x10-0x80 reserved, sent as 0.
          FlagGrounded  uint8 = 0x01
          FlagSprinting uint8 = 0x02
          FlagDead      uint8 = 0x04
          FlagFiring    uint8 = 0x08

          // cmd opcodes.
          OpShopList  uint16 = 0x0001
          OpShopBuy   uint16 = 0x0002
          OpEquip     uint16 = 0x0003
          OpInventory uint16 = 0x0004
          OpReload    uint16 = 0x0005

          // cmd_result status codes.
          StatusOK            uint8 = 0
          StatusUnknownOpcode uint8 = 1
          StatusMalformed     uint8 = 2
          StatusRefused       uint8 = 3
          StatusRateLimited   uint8 = 4
          StatusNotFound      uint8 = 5

          // event_id values (extend the existing block).
          EventShotFired   uint16 = 0x0002
          EventHit         uint16 = 0x0003
          EventDeath       uint16 = 0x0004
          EventLootDropped uint16 = 0x0005

          // collider kinds (PROTOCOL.md `colliders`).
          ColliderBox    uint8 = 0
          ColliderSphere uint8 = 1

          // VersionPhase2 is the Phase 2 protocol version (client_ver / server_ver).
          const VersionPhase2 uint16 = 2

          // MaxCmdBody is the cmd/cmd_result JSON body cap in bytes (4 KiB).
          const MaxCmdBody = 4 << 10

          // ColliderSize is the wire size of one collider row in bytes.
          const ColliderSize = 1 + 1 + 3*4 + 3*4 + 4*4 // 42

          // ColliderMax is how many colliders fit one 64 KiB message.
          const ColliderMax = 1560
STEPS:    1. Extend the existing const blocks with the constants above,
             keeping each in the block that matches its comment heading.
          2. Leave VersionM1 in place — Phase 2 adds a version, it does not
             rename the old one.
VERIFY:   cd server && go build ./... && go vet ./...  → exit 0
REPORT:   the constants added and that command's output. Then stop.
BUDGET:   1 file, ~80 lines, 10 tool calls.
```

### W1-2 · `frontend` · Mirror the Phase 2 constants in the TS codec

```
TASK:     Add the Phase 2 message ids, entity types, flag bits, opcodes,
          status codes, event ids and size constants to the client codec.
          Additive only — do not modify existing encoders or decoders.
FILES:    client/src/net/protocol.ts (edit)
CONTRACT: Extend the existing exported MSG object with:
            board: 0x000b, disembark: 0x000c, seat_result: 0x000d,
            cmd: 0x000e, cmd_result: 0x000f, defs: 0x0010,
            fire: 0x0011, colliders: 0x0012,

          Add alongside the existing ENTITY_TYPE_PLAYER:
            export const ENTITY_TYPE_SHIP = 0x0002
            export const ENTITY_TYPE_NPC = 0x0003
            export const ENTITY_TYPE_TARGET = 0x0004
            export const ENTITY_TYPE_VEHICLE = 0x0005
            export const ENTITY_TYPE_LOOT = 0x0006
            export const ENTITY_TYPE_PROJECTILE = 0x0007

            export const FLAG = {
              grounded: 0x01, sprinting: 0x02, dead: 0x04, firing: 0x08,
            } as const

            export const OP = {
              shop_list: 0x0001, shop_buy: 0x0002, equip: 0x0003,
              inventory: 0x0004, reload: 0x0005,
            } as const

            export const STATUS = {
              ok: 0, unknown_opcode: 1, malformed: 2, refused: 3,
              rate_limited: 4, not_found: 5,
            } as const

            export const EVENT = {
              explosion: 0x0001, shot_fired: 0x0002, hit: 0x0003,
              death: 0x0004, loot_dropped: 0x0005,
            } as const

            export const COLLIDER_BOX = 0
            export const COLLIDER_SPHERE = 1
            export const MAX_CMD_BODY = 4 * 1024
            export const COLLIDER_SIZE = 42
            export const COLLIDER_MAX = 1560

          DO NOT touch PROTOCOL_VERSION. It stays 1.
          The constants above are additive — declaring an opcode nothing sends
          yet is harmless. The version number is not: client/src/main.ts closes
          the connection when hello_ack's server_ver disagrees with it, so
          bumping one end while the other still speaks 1 is a total outage
          (no terrain, no snapshots, a black sky), not a degraded mode. It
          flips on both ends at once in W1-B8, after the wire is actually v2.
STEPS:    1. Extend MSG, add the new exported constants next to
             ENTITY_TYPE_PLAYER. Leave PROTOCOL_VERSION alone.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json  → exit 0
REPORT:   the constants added and that command's output. Then stop.
BUDGET:   1 file, ~50 lines, 10 tool calls.
```

### W1-3 · `netcode` · `store.Open(dsn)` — driver from the DSN scheme

```
TASK:     Create the store package's Open function: pick the SQL driver from
          the DATABASE_URL scheme and return a configured *sql.DB.
FILES:    server/internal/store/open.go (new)
          server/internal/store/open_test.go (new)
CONTRACT: package store

          // Store is a handle on the game database. There is one query set and
          // one set of statements; the only dialect-aware code is Open.
          // docs/ARCHITECTURE.md, "Persistence".
          type Store struct {
              DB      *sql.DB
              Dialect string // "sqlite" or "postgres"
          }

          func Open(dsn string) (*Store, error)

          DSN rules (docs/ARCHITECTURE.md):
            ""                    -> treated as "sqlite://./data/world.db"
            "sqlite://<path>"     -> driver "sqlite"   (modernc.org/sqlite)
            "postgres://..."      -> driver "pgx"      (github.com/jackc/pgx/v5/stdlib)
            "postgresql://..."    -> driver "pgx"
            anything else         -> error, wrapped with %w

          For sqlite: strip the "sqlite://" prefix to get the file path,
          MkdirAll the parent directory, SetMaxOpenConns(1), and exec
          "PRAGMA journal_mode=WAL", "PRAGMA busy_timeout=5000",
          "PRAGMA foreign_keys=ON".
          For postgres: pass the DSN through unchanged, SetMaxOpenConns(10),
          SetMaxIdleConns(5), SetConnMaxLifetime(30 * time.Minute).
          Both: PingContext with a 5 s timeout before returning; on failure
          close the DB and return the error wrapped with %w.

          Add a Close() error method that closes s.DB.
STEPS:    1. go get modernc.org/sqlite github.com/jackc/pgx/v5/stdlib
          2. Write open.go per the contract (blank-import both drivers).
          3. Write open_test.go: Open("sqlite://"+t.TempDir()+"/t.db"),
             assert Dialect=="sqlite" and DB.Ping() succeeds; assert
             Open("mysql://x") returns an error.
VERIFY:   cd server && go test ./internal/store  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~120 lines, 10 tool calls.
```

### W1-4 · `netcode` · The `player` table migration

```
TASK:     Add the first migration file: the player table, in portable SQL.
FILES:    server/internal/store/migrations/001_player.sql (new)
CONTRACT: Exactly this, and nothing else in the file:

          CREATE TABLE IF NOT EXISTS player (
            token       TEXT    PRIMARY KEY,
            name        TEXT    NOT NULL,
            credits     BIGINT  NOT NULL,
            inventory   TEXT    NOT NULL,
            equipped    TEXT    NOT NULL,
            pos_x       REAL    NOT NULL,
            pos_y       REAL    NOT NULL,
            pos_z       REAL    NOT NULL,
            created_ms  BIGINT  NOT NULL,
            updated_ms  BIGINT  NOT NULL
          );

          Portable-SQL rules this obeys and must keep obeying
          (docs/ARCHITECTURE.md, "Persistence"): no SERIAL, no AUTOINCREMENT,
          no BOOLEAN, no JSONB, no TIMESTAMP. inventory/equipped are JSON in
          TEXT; times are Unix-millis BIGINT — NOT INTEGER, which is int4 in
          Postgres and overflows on any real timestamp.
STEPS:    1. Create the directory and the file with exactly the DDL above.
VERIFY:   cd server && go build ./...  → exit 0
          (the file is data; the build proves nothing was broken)
REPORT:   the file contents. Then stop.
BUDGET:   1 file, ~15 lines, 3 tool calls.
```

### W1-5 · `art` · `weapon.pulse` GLB

```
TASK:     Generate art/weapons/pulse.glb: a low-poly pulse rifle with the
          named nodes the client mounts it by.
FILES:    art/tools/gen_weapon.py (new)
CONTRACT: Follow the structure of the existing art/tools/gen_rocks.py:
            sys.path.insert(0, dirname(abspath(__file__)))
            from glb import Node, box, face_normal, out_path, write_glb
          Output: out_path("weapons", "pulse.glb").

          Model contract (art/manifest.json entry "weapon.pulse"):
            - -Z is forward. Roughly 0.9 m long, 0.12 m tall, 0.06 m wide.
            - Flat-shaded low poly, exactly 180 triangles to match the
              manifest's `tris` field (art/tools/verify.mjs asserts equality).
            - Two named empty nodes, both children of the root:
                grip   - where the character's right hand holds it
                muzzle - barrel tip, at the forward (-Z) end
            - Reads in silhouette at 30 m and from the first-person
              down-view at 0.5 m.
          Deterministic: no clock, no unseeded RNG — a rebuild is byte-identical.
STEPS:    1. Write gen_weapon.py building the receiver, barrel, grip and stock
             from glb.box calls plus the two empty nodes.
          2. Run it and adjust the box count until the triangle total is
             exactly 180.
VERIFY:   cd art && python3 tools/gen_weapon.py && node tools/verify.mjs weapon.pulse  → exit 0
REPORT:   triangle count and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W1-6 · `art` · `npc.shopkeeper` GLB

```
TASK:     Generate art/chars/shopkeeper.glb: the quartermaster NPC, sharing
          char.player's node layout so client code works on it unchanged.
FILES:    art/tools/gen_npc.py (new)
CONTRACT: Same glb.py helpers and out_path pattern as art/tools/gen_player.py.
          Output: out_path("chars", "shopkeeper.glb").

          Model contract (art/manifest.json entry "npc.shopkeeper"):
            - Named nodes, exactly as char.player has them:
                eye, head, torso, arm.l, arm.r, leg.l, leg.r
              eye and head must be SIBLINGS (art/tools/verify.mjs asserts it).
            - eye at 1.7 m, -Z forward, limb pivots at shoulder and hip.
            - Exactly 220 triangles, to match the manifest's `tris`.
            - Different palette from char.player so it does not read as a
              player: muted green/grey work coat, no orange.
          Deterministic, as above.
STEPS:    1. Write gen_npc.py, reusing gen_player.py's node layout with a
             different palette and proportions.
          2. Adjust until the triangle total is exactly 220.
VERIFY:   cd art && python3 tools/gen_npc.py && node tools/verify.mjs npc.shopkeeper  → exit 0
REPORT:   triangle count and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W1-7 · `art` · `prop.target` GLB

```
TASK:     Generate art/props/target.glb: the range target dummy.
FILES:    art/tools/gen_target.py (new)
CONTRACT: Same glb.py helpers and out_path pattern as art/tools/gen_rocks.py.
          Output: out_path("props", "target.glb").

          Model contract (art/manifest.json entry "prop.target"):
            - Base at the origin, up +Y, faces -Z. About 1.8 m tall.
            - A concentric plate on a post.
            - One node named `plate`, which the client recolours to show
              hit/reset state — it must be a separate node from the post.
            - Exactly 64 triangles, to match the manifest's `tris`.
          Deterministic, as above.
STEPS:    1. Write gen_target.py: post + plate, plate as its own named node.
          2. Adjust until the triangle total is exactly 64.
VERIFY:   cd art && python3 tools/gen_target.py && node tools/verify.mjs prop.target  → exit 0
REPORT:   triangle count and that command's output. Then stop.
BUDGET:   1 file, ~120 lines, 10 tool calls.
```

### W1-8 · `art` · `struct.wall` + `struct.post` GLBs

```
TASK:     Generate art/structs/wall.glb and art/structs/post.glb: the range's
          visual geometry, scaled by the client to each collider's extents.
FILES:    art/tools/gen_struct.py (new)
CONTRACT: Same glb.py helpers and out_path pattern as art/tools/gen_rocks.py.
          Outputs: out_path("structs", "wall.glb"), out_path("structs", "post.glb").

          Model contract (art/manifest.json entries):
            struct.wall - unit box, base at the origin, up +Y, exactly 24
                          triangles. The client scales it to a box collider's
                          half-extents.
            struct.post - unit sphere (radius 1, centre at the origin),
                          exactly 48 triangles. The client scales it to a
                          sphere collider's radius.
          Both are VISUAL ONLY — the authoritative shape is the `colliders`
          message (docs/GDD.md, "Static colliders"). Do not add collision data.
          Deterministic, as above.
STEPS:    1. Write gen_struct.py emitting both files.
          2. Adjust until the triangle totals are exactly 24 and 48.
VERIFY:   cd art && python3 tools/gen_struct.py && node tools/verify.mjs struct.wall && node tools/verify.mjs struct.post  → exit 0
REPORT:   both triangle counts and that command's output. Then stop.
BUDGET:   1 file, ~120 lines, 10 tool calls.
```

### W1-9 · `infra` · Postgres in the kind cluster

```
TASK:     Add a Postgres pod, PVC, Service and Secret to the kind manifests.
FILES:    deploy/manifests/30-postgres.yaml (new)
CONTRACT: Namespace: space-adventure (as in deploy/manifests/00-namespace.yaml).
          Four objects in one file, --- separated:
            1. Secret `postgres-auth`, stringData:
                 POSTGRES_USER: space
                 POSTGRES_PASSWORD: <a fixed dev-only value>
                 POSTGRES_DB: space
                 DATABASE_URL: postgres://space:<same>@postgres:5432/space?sslmode=disable
            2. PersistentVolumeClaim `postgres-data`, 1Gi, ReadWriteOnce.
            3. Deployment `postgres`, 1 replica, image postgres:17-alpine,
               envFrom the Secret, PGDATA=/var/lib/postgresql/data/pgdata,
               volumeMount the PVC at /var/lib/postgresql/data,
               readiness + liveness probes running
                 pg_isready -U space -d space
               (initialDelaySeconds 5, periodSeconds 5).
            4. Service `postgres`, ClusterIP, port 5432.
          The password is a LOCAL DEVELOPMENT credential for a kind cluster
          that listens only on localhost. Add a comment at the top of the file
          saying exactly that, and that a real deployment supplies this Secret
          out-of-band and never from the repo.
STEPS:    1. Write the manifest per the contract.
          2. Do not edit 10-server.yaml — wiring DATABASE_URL into the server
             Deployment is W1-B6.
VERIFY:   kubectl apply --dry-run=client -f deploy/manifests/30-postgres.yaml  → exit 0
REPORT:   the object names created and that command's output. Then stop.
BUDGET:   1 file, ~80 lines, 10 tool calls.
```

### W1-10 · `infra` · `make test-pg`

```
TASK:     Add a Makefile target that runs the Go tests against a throwaway
          Postgres container.
FILES:    Makefile (edit)
CONTRACT: New phony target `test-pg`, matching the existing targets' style:
            1. docker run -d --rm --name sa-test-pg -e POSTGRES_PASSWORD=test
               -e POSTGRES_USER=test -e POSTGRES_DB=test -p 55432:5432
               postgres:17-alpine
            2. poll `docker exec sa-test-pg pg_isready -U test` until ready
               or 30 s elapse, then fail
            3. cd server && TEST_DATABASE_URL='postgres://test:test@localhost:55432/test?sslmode=disable'
               go test -count=1 ./internal/store/...
               -count=1 is REQUIRED: Go's test cache keys on inputs, and an env
               var the test does not read is not one — without it a Postgres run
               can be answered from a cached SQLite-only result, which is a
               false green on C11b.
            4. docker rm -f sa-test-pg  — ALWAYS, including when the tests
               fail (trap or `-` prefixed cleanup), so a failed run leaves no
               container behind
          Add `test-pg` to the Makefile's help text if it has one.
STEPS:    1. Add the target with guaranteed cleanup.
          2. Do not change any existing target.
VERIFY:   make -n test-pg  → exit 0
REPORT:   the target added and that command's output. Then stop.
BUDGET:   1 file, ~30 lines, 10 tool calls.
```

---

## Batch B — after batch A

### W1-B1 · `netcode` · Widen the entity row to 54 bytes *(after W1-1)*

```
TASK:     Grow the snapshot entity row from 44 to 54 bytes: add ParentID,
          Seat, Health, Flags and PitchQ.
FILES:    server/internal/protocol/protocol.go (edit)
CONTRACT: Replace the existing Entity struct and EntitySize with:

          // Entity is one snapshot row (PROTOCOL.md, 54 bytes):
          // u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
          // | u32 parent_id | u16 seat | u16 health | u8 flags | i8 pitch_q
          //
          // ParentID and Seat are always 0 in Phase 2; Phase 4 fills them in.
          type Entity struct {
              ID       uint32
              Pos      [3]float32
              Quat     [4]float32
              Vel      [3]float32
              ParentID uint32
              Seat     uint16
              Health   uint16
              Flags    uint8
              PitchQ   int8
          }

          const EntitySize = 4 + 3*4 + 4*4 + 3*4 + 4 + 2 + 2 + 1 + 1 // 54

          AppendEntity appends the five new fields after Vel, in that exact
          order, little-endian. PitchQ is a signed byte: append it as
          byte(e.PitchQ) and decode it as int8(b[off]).
          The snapshot parser already sizes itself from EntitySize; extend its
          per-entity decode to read the five new fields.
STEPS:    1. Update the Entity struct and EntitySize.
          2. Extend AppendEntity with the five fields.
          3. Extend the snapshot entity decode with the same five fields.
VERIFY:   cd server && go test ./internal/protocol  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~60 lines, 10 tool calls.

NOTE:     server/internal/protocol/protocol_test.go and callers in
          server/internal/server may fail to compile until they set the new
          fields. If a caller outside protocol.go needs changing, STOP and
          report `BLOCKED: CROSS-FILE` naming the file and line — the main
          thread dispatches that separately.
```

### W1-B2 · `frontend` · Mirror the 54-byte row *(after W1-2)*

```
TASK:     Grow the TS snapshot entity decode from 44 to 54 bytes.
FILES:    client/src/net/protocol.ts (edit)
CONTRACT: The decoded entity object gains five fields, read in this order
          immediately after vel, little-endian:
            parentId : u32
            seat     : u16
            health   : u16
            flags    : u8
            pitchQ   : i8   (DataView.getInt8 — signed, NOT getUint8)
          Update the exported entity type/interface and the per-entity stride
          constant from 44 to 54.
          parentId and seat are always 0 in Phase 2; decode them anyway.
STEPS:    1. Extend the entity type with the five fields.
          2. Extend the decode loop and the stride constant.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~50 lines, 10 tool calls.
```

### W1-B3 · `netcode` · `hello` carries an identity token *(after W1-B1)*

```
TASK:     Add the token field to the hello message on the server codec.
FILES:    server/internal/protocol/protocol.go (edit)
CONTRACT: hello becomes (PROTOCOL.md "Message types"):
            u16 client_ver | u32 name_len | bytes name | u32 token_len | bytes token

          type Hello struct {
              ClientVer uint16
              Name      string
              Token     string
          }

          EncodeHello appends u32 len(Token) then the token bytes after the name.
          ParseHello reads the token after the name; a payload that ENDS after
          the name (no token fields) parses as Token == "" rather than an
          error — an old client is a token-less session, not a protocol
          violation.
          Reject a token longer than 64 bytes, or containing any byte outside
          printable ASCII (0x21-0x7E), by returning ErrBadPayload. The token is
          untrusted input used as a database key: validate it here, at the
          boundary, not at the call site.
STEPS:    1. Add Token to the Hello struct.
          2. Extend EncodeHello and ParseHello per the contract.
VERIFY:   cd server && go test ./internal/protocol  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~50 lines, 10 tool calls.
```

### W1-B4 · `frontend` · Send a token in `hello` *(after W1-B2)*

```
TASK:     Add the token field to the client's hello encoder.
FILES:    client/src/net/protocol.ts (edit)
CONTRACT: encodeHello gains a `token: string` parameter and appends, after the
          existing name bytes:
            u32 token_len | bytes token   (UTF-8)
          Nothing else changes. Generating and storing the token is a separate
          brief — this one only widens the encoder.
STEPS:    1. Add the parameter and the two appended fields.
VERIFY:   cd client && npx tsc --noEmit -p tsconfig.json  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~25 lines, 10 tool calls.

NOTE:     The caller in client/src/net/netClient.ts will not compile until it
          passes a token. If so, STOP and report `BLOCKED: CROSS-FILE` with the
          file and line — the main thread dispatches that separately.
```

### W1-B5 · `netcode` · Embedded migration runner *(after W1-3, W1-4)*

```
TASK:     Add the migration runner: embed migrations/*.sql and apply the
          unapplied ones in order.
FILES:    server/internal/store/migrate.go (new)
          server/internal/store/migrate_test.go (new)
CONTRACT: package store

          //go:embed migrations/*.sql
          var migrationsFS embed.FS

          // Migrate applies every migration newer than the recorded schema
          // version, each in its own transaction.
          func (s *Store) Migrate(ctx context.Context) error

          Rules (docs/ARCHITECTURE.md, "Migrations without a dependency"):
            - Ensure the bookkeeping table first:
                CREATE TABLE IF NOT EXISTS schema_version (
                  version INTEGER PRIMARY KEY, applied_ms BIGINT NOT NULL);
            - Current version = COALESCE(MAX(version), 0) from that table.
            - File names are NNN_name.sql with a zero-padded numeric prefix.
              Sort by that numeric prefix, not lexically.
            - A file named NNN_name.postgres.sql OVERRIDES NNN_name.sql when
              s.Dialect == "postgres"; it is skipped otherwise.
            - Apply each file with version > current inside a transaction that
              also INSERTs its schema_version row; roll back the whole
              transaction on error and return it wrapped with %w.
            - Applying twice is a no-op.
STEPS:    1. Write migrate.go per the contract.
          2. Write migrate_test.go: open a temp-dir sqlite store, call Migrate
             twice, assert no error both times and that the player table
             accepts an INSERT.
VERIFY:   cd server && go test ./internal/store  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files (impl + its test), ~140 lines, 10 tool calls.
```

### W1-B6 · `infra` · Point the server at Postgres *(after W1-9)*

```
TASK:     Give the server Deployment its DATABASE_URL from the Postgres Secret.
FILES:    deploy/manifests/10-server.yaml (edit)
CONTRACT: Add to the server container spec, without changing anything else:
            env:
              - name: DATABASE_URL
                valueFrom:
                  secretKeyRef:
                    name: postgres-auth
                    key: DATABASE_URL
          The DSN must reach the process ONLY this way — never as a literal in
          the manifest, never baked into the image, never a command-line flag
          that shows up in `ps`.
STEPS:    1. Add the env block to the existing container spec.
VERIFY:   kubectl apply --dry-run=client -f deploy/manifests/10-server.yaml  → exit 0
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~12 lines, 10 tool calls.
```

### W1-B8 · `netcode` + `frontend` · Flip both ends to protocol version 2 *(LAST — after W1-B1 … W1-B6)*

```
TASK:     Declare the wire v2 on both ends, in one change.
FILES:    server/internal/server/server.go (edit)
          client/src/net/protocol.ts (edit)
CONTRACT: server.go: the hello_ack's ServerVer becomes protocol.VersionPhase2,
          and the hello handler accepts client_ver == VersionPhase2 (closing
          with code 1002 on any other value, per docs/PROTOCOL.md
          "Versioning").
          protocol.ts: `export const PROTOCOL_VERSION = 1` becomes `= 2`.

          THIS BRIEF DELIBERATELY TOUCHES TWO FILES ACROSS TWO MODULES, which
          the one-file budget otherwise forbids. The exception is the point:
          client/src/main.ts closes the connection when hello_ack's server_ver
          disagrees with PROTOCOL_VERSION, so a version flip that lands on one
          end only is a guaranteed outage — the client connects, hangs up
          before terrain, and renders an empty sky. Splitting this across two
          dispatches would ship exactly that state in between.

PRECONDITION: do not dispatch until the 54-byte entity row (W1-B1, W1-B2) and
          the hello token (W1-B3, W1-B4) are landed and green on both ends.
          The version number claims the wire is v2; it must already be true.
STEPS:    1. server.go: ServerVer + the client_ver check.
          2. protocol.ts: the constant.
VERIFY:   make up && node test/t2-two-client.mjs  -> PASS
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   2 files, ~20 lines, 10 tool calls.
```

---

## Wave 1 exit check (main thread, before wave 2 dispatches)

```
cd server && go build ./... && go vet ./... && go test ./...
cd client && npx tsc --noEmit -p tsconfig.json && npm run build
cd art    && node tools/verify.mjs
make -n test-pg && make test-pg
kubectl apply --dry-run=client -f deploy/manifests/
```

All green = wave 2 dispatches. Phase 1's C1–C10 must still pass: the entity row
grew, so `test/t2-two-client.mjs` and `test/t6-prediction.mjs` are the ones most
likely to need their decoders widened — that is `qa`'s brief, not a product fix.
