# Roadmap

## M0 — Scaffold (done)

Agent team (`.omp/agents/`), docs, module ownership, wire-protocol draft,
flight-model spec. This commit.

## M1 — Vertical slice: multiplayer space flight

Two browser clients fly low-poly ships in shared space; the Go server is
authoritative at 20 Hz; everything runs on local kind.

### Deliverables

- `server/` — tick loop, WS gateway, entities, protocol per
  `docs/PROTOCOL.md`, flight model per `docs/GDD.md`
- `client/` — Three.js scene, flight controls, net client, prediction +
  interpolation, HUD
- `art/` — ship model + starfield/asteroid props (`.glb`, manifest,
  generation scripts)
- `deploy/` — kind + manifests + Dockerfiles + root Makefile
  (`make up` / `make down`)
- `test/` — e2e two-client harness + flight-model conformance test

### Acceptance criteria (`qa` verifies all)

1. `make up` from a clean state: kind cluster + server + client running,
   health checks green, client page and WS endpoint reachable.
2. Two browser clients connect to the same server; each sees the other's
   ship within 1 s of spawn.
3. Server is authoritative: state forced client-side (dev override) is
   corrected by a snapshot within one tick (50 ms + network).
4. Connection loss despawns the entity on other clients within 10 s
   (heartbeat timeout).
5. Movement conforms to the GDD flight-model table within 5% tolerance
   (measured by a scripted test client).
6. Sustained 20 Hz snapshots with p95 server→client snapshot latency <
   100 ms on the local network; client holds 60 fps with 10 simulated
   players.
7. `go build/vet/test ./...` and `tsc` strict + `npm run build` clean;
   `make down` removes everything.

### Parallel task split (suggested dispatch)

| Wave | Agents (run in parallel) | Task |
|------|--------------------------|------|
| 1 | `game`, `infra` | finalize GDD flight model + rule table; kind + manifests + Dockerfiles + Makefile with health checks (server can be a stub binary that serves `/healthz` + WS) |
| 2 | `netcode`, `frontend`, `art` | server tick + WS + protocol; client scene + controls + net client + HUD; ship + props + manifest |
| 3 | `qa` | e2e harness against criteria 1–7; PASS/FAIL report |

Wave 2 depends on wave 1 (cluster to deploy into, stub server to connect
against). The protocol (`docs/PROTOCOL.md`) and flight table (`docs/GDD.md`)
are fixed at M0, so wave-2 agents never block on each other's contracts.

## M2+ — draft (not committed)

- **M2** — fantasy planet landing: gravity, walking, dock cycle (ship →
  planet transition is the interesting problem).
- **M3** — combat + world events (`event` message type earns its keep).
- **M4** — persistence + accounts: database, sessions, rejoin identity.
- **M5** — scale-out: per-system server shards, delta snapshots, load test
  at 100+ concurrent.
