# server — Go game server

Authoritative fixed-tick simulation (20 Hz) + WebSocket gateway.

- Owner: `netcode` (gameplay rules: `game`, per `docs/GDD.md`)
- Module: `space-adventure/server`
- Wire contract: `docs/PROTOCOL.md` — implemented exactly as specified
- Structural reference: `docs/ARCHITECTURE.md`

## Run

```
go run ./cmd/server            # listen :8080, world seed 1337
go run ./cmd/server -listen 127.0.0.1:8090 -seed 42
```

- `GET /healthz` → `ok` (the k8s probe contract; kept as the cheapest
  possible 200)
- `GET /version` → the build id stamped in at link time, so a caller can ask
  WHICH server it reached rather than only whether something answered
- `GET /ws` → WebSocket gateway (browser clients connect same-origin; nginx
  proxies `/ws` to this port)

The seed is fixed by default on purpose: terrain must be reproducible across
restarts or movement is impossible to iterate on. `world_seed` in `hello_ack`
is the u32 generation seed.

## Trajectory dump (server/client parity check)

```
go run ./cmd/server dump --inputs script.jsonl
```

Runs the exact same integrator the tick loop uses against the same generated
terrain, so the output is byte-identical to what the server simulates.
Script format (one JSON object per line):

- `{"seed": N}` — override `--seed`; must precede `state`/`input`.
- `{"state": {"pos":[x,y,z], "vel":[x,y,z], "grounded":true, "facing":[x,y,z]}}`
  — initial state; default is the spawn state.
- `{"input": {"move_x":0, "move_y":1, "look":[1,0,0], "action_mask":0}}` —
  one line per tick.

Output: one line per input,
`{"tick":i,"pos":[x,y,z],"vel":[x,y,z],"grounded":bool}` (0-based ticks).
The client's Node entry point consumes the identical format for the M1
parity diff.

## Layout

```
cmd/server/        main.go (flags, mux, shutdown) + dump.go (parity dump)
internal/protocol/ frame + message encode/decode (PROTOCOL.md, 1:1)
internal/terrain/  cube-sphere radius field: generate (seeded), bilinear
                   sample, normal, walkable, wire encode/decode
internal/sim/      on-foot movement integrator (GDD rule table, 1:1)
internal/server/   world: 20 Hz tick loop, entity store, WS gateway
                   (handshake, per-connection reader/writer, queued
                   input (one per tick), per-client ack_seq patch, heartbeat)
```

## Design notes

- **One snapshot body per tick, patched per client.** The snapshot (full
  entity list) is encoded once per tick; `ack_seq` is the only per-client
  field, so fan-out copies the body and patches 2 bytes.
- **Fan-out drops, never blocks.** A per-connection queue (cap 32); a full
  queue drops the tick. Snapshots are full state, so the next tick replaces
  them.
- **Input is lock-free.** Each connection stores its latest `input` in an
  atomic; the tick loop is the only place state mutates (one world mutex for
  step + encode + fan-out, all non-blocking).
- **Heartbeat.** A connection silent for 10 s is dropped; clients send
  `ping` every 2 s when idle (PROTOCOL "Heartbeat").
- **Terrain is sent once on join** (pre-encoded at startup, ~50 KiB) and is
  never re-sent; clients re-derive nothing, the field is the contract.
