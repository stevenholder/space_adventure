# Roadmap

## M0 — Scaffold (done)

Agent team (`.omp/agents/`), docs, module ownership, wire-protocol draft,
movement spec, and the three contracts wave-2 agents build against:
`docs/PROTOCOL.md`, the GDD rule table, and the `art/manifest.json` schema.

## M1 — Vertical slice: multiplayer first-person on a planet

Two browser clients walk around the same small round world — an asteroid you
can circle on foot in a couple of minutes — in first person, and see each other
move; the Go server is authoritative at 20 Hz; both run as local processes via
`make up`.

**No vehicles in M1.** The player is a body, not a ship — walking is the
foundation every later milestone hands control back to, so it is built first
and never rebuilt (GDD "M1 scope").

### Deliverables

- `server/` — tick loop, WS gateway, entities, protocol per
  `docs/PROTOCOL.md`, procedural cube-sphere terrain generation (valleys,
  ranges, plains, craters) + send, radial-gravity on-foot movement and terrain
  collision per `docs/GDD.md`
- `client/` — Three.js scene, cube-sphere terrain mesh from the server's radius
  field, seed-scattered rock props, first-person camera at eye height on the
  body with a radial up vector (the only camera the game gets — GDD pillar 2),
  mouse-look + WASD controls, net client, prediction + replay reconciliation,
  remote interpolation, HUD
- `art/` — player character model, terrain material, surface props (rocks and
  friends) (`.glb`, manifest, generation scripts)
- `deploy/` — root Makefile (`make up` / `make down` / `make build`) running
  the Go server and Vite dev server locally. **No containers, no kind in M1**
  — see ARCHITECTURE "Deployment"; that work moves to M6 (scale-out).
- `test/` — e2e two-client harness + movement conformance test

### Acceptance criteria (`qa` verifies all)

1. `make up` from a clean clone: server and client both running, `/healthz`
   green, client page and WS endpoint reachable.
2. Two browser clients connect to the same server; each sees the other's
   character within 1 s of spawn, standing on the terrain rather than floating
   or sunk in it.
3. Server is authoritative: state forced client-side (dev override) is
   corrected by a snapshot within one tick (50 ms + network).
4. Connection loss despawns the entity on other clients within 10 s
   (heartbeat timeout).
5. Movement conforms to the GDD on-foot rule table within 5% tolerance,
   measured by running one JSONL input script through both sims and diffing
   their JSONL trajectory dumps (ARCHITECTURE "Client"). The sequence must
   cover level ground, a walkable slope, a slope steeper than `max_slope`
   (slide), a `max_step` ledge, a jump, **and at least one cube-face seam
   crossing**. Both dump entry points ship in wave 2, so this is runnable
   before `qa` picks it up.
6. **Prediction quality:** with 100 ms latency injected in the test harness, a
   scripted run (sprint, direction reversal, jump onto a slope) keeps p95
   |predicted − authoritative| position error under 0.25 m, with no visible
   snap-back. This is the criterion that fails if reconciliation regresses to
   blending.
7. Sustained 20 Hz snapshots with p95 server→client snapshot latency < 50 ms
   (one tick) on loopback; client holds 60 fps with 10 simulated players.
8. `go build/vet/test ./...` and `tsc` strict + `npm run build` clean;
   `make down` leaves no processes running.
9. **Terrain is walkable, varied and navigable:** scanning the generated field,
   ≥ 70% of samples are below `max_slope`, the spawn disc is flat to ±0.5 m
   and walkable outward in every direction, every radius lies inside
   `[radius_min, radius_max]`, the expected crater count is present, and all
   six landmarks exist at their specified radii, at least `landmark_min_sep`
   apart, with ≥ 60% of sampled surface points able to see one over the
   horizon. These are checked against the generated field, not against the
   generator's code, so retuning the noise never breaks the test.
10. **Circumnavigation:** a scripted client walks a full great-circle lap
   holding one direction, crossing every cube face, and arrives back within
   1 m of its start having stayed on the ground the whole way — no fall
   through, no seam hitch, no accumulated drift off the surface. Two clients
   standing on opposite sides of the world each render the other upright on
   their own horizon. This is the criterion that catches a hardcoded `+Y` up,
   which works fine everywhere near spawn.

### Parallel task split (suggested dispatch)

| Wave | Agents (run in parallel) | Task |
|------|--------------------------|------|
| 1 | `game`, `infra`, `netcode` | finalize GDD on-foot rule table + integrator + what the asteroid's surface actually looks like; root Makefile (`up`/`down`/`build`) + Vite `/ws` proxy; **protocol skeleton server** (below) |
| 2 | `netcode`, `frontend`, `art` | real terrain generation + tick + movement + collision + trajectory-dump subcommand; `sim/` module (headless, DOM-free) then renderer, first-person controls + own body + net client + prediction/replay + HUD + nametags + trajectory-dump entry point; segmented character + rock variants + manifest |
| 3 | `qa` | e2e harness against criteria 1–10; PASS/FAIL report |

**The wave-1 protocol skeleton is what makes wave 2 actually parallel.** A
server that only serves `/healthz` and accepts a socket leaves `frontend` with
nothing to build against — no terrain to mesh, no snapshots to interpolate — so
it would sit blocked on `netcode` for most of the milestone. Wave 1 therefore
ships a `netcode` skeleton that speaks the whole wire shape with none of the
substance:

- `hello` → `hello_ack`, `ping` → `pong`, heartbeat timeout
- one `terrain` message carrying a **featureless sphere** (every radius
  `planet_radius`) — correct shape, correct size, no generation
- `snapshot` at 20 Hz that applies input with a trivial integrator and echoes
  `ack_seq`
- `spawn`/`despawn` with the sanitized name payload

That is enough for `frontend` to build the mesh, the camera, prediction and
reconciliation end to end. Wave 2 replaces the insides — real generation, real
movement — without changing a byte on the wire.

Wave 2 depends on wave 1 (a skeleton server to develop against, a `make up`
that runs both). Wave-2 agents never block on each other because all of their
shared contracts are frozen before wave 1 starts:

- `docs/PROTOCOL.md` — the wire, for `netcode` + `frontend`
- the GDD rule table + integrator — the sim, for `netcode` + `frontend`
- `art/manifest.json` — the asset ids, for `frontend` + `art`

`frontend` never waits on a `.glb`: it resolves character and prop geometry
through the manifest and falls back to a flat-shaded capsule/box placeholder
when the file is absent, so `art` can land real models at any point in the wave
without a handoff. Terrain geometry comes from the server's radius field, not
from `art`, so it is never blocked either.

The **cube-sphere sampling rule (GDD "Terrain sampling") is a fourth frozen
contract** for this milestone: `netcode` and `frontend` each implement it, and
they must agree on face order, axis assignment and seam values or players fall
through the ground at specific edges.

## M2+ — draft (not committed)

The target loop is board → fly → land → explore on foot → drive → load up →
leave, all first person, all with a crew (`docs/GDD.md`, "Core loop"). M1
builds the *explore on foot* verb. The rest arrives in dependency order rather
than narrative order — each milestone adds one hard thing to a working game:

- **M2 — walk into a ship and fly it.** The ship sits on the M1 planet as a
  world entity you can walk up to, walk into, and take the pilot seat of.
  Boarding, seats, one control seat, passengers riding along, control handoff,
  and the flight model already spec'd in the GDD ("Flight model") — flown
  around the planet and set back down. **Deliberately no space and no orbital
  transition**: everything happens in the M1 world, so the milestone's only new
  problem is crewed vehicles, not crewed vehicles *plus* a world transition.
  This is where the protocol debt in GDD "Vehicles and crew" gets paid —
  attachment transforms, seat messages, input modes.
- **M3 — leave the atmosphere.** Space as a place, the surface↔space
  transition with the crew aboard, and more than one destination. The
  transition is the genuinely hard problem and it lands last of the three,
  when both a body and a crewed ship already work.
- **M3.5 / whenever — surface vehicles.** Rovers and friends: the same seat and
  control-authority machinery from M2 with a ground movement model. Not really
  a milestone — if M2 is built right this is a weekend, and if it is not, this
  is where that shows up. Slot it wherever it is wanted.
- **M4** — combat + world events (`event` message type earns its keep).
- **M5** — persistence + accounts: database, sessions, rejoin identity, and
  vehicles that stay where you parked them.
- **M6** — scale-out: containerize both processes, kind cluster + manifests,
  per-system server shards, delta snapshots, load test at 100+ concurrent.
  This is where Kubernetes earns its keep and `deploy/` grows past a Makefile.
