# Space Adventure

Low-poly 3D MMO space/fantasy adventure, played in the browser, entirely in
first person.

Board a ship with your friends, fly it to a planet, climb out and explore on
foot, drive a rover across the surface, load back up and leave. Vehicles are
shared spaces: one player flies, the rest are aboard. See `docs/GDD.md`.

**Phase 1 built the middle of that loop first, and is done:** a body on foot,
with other players, on a small round world you can walk all the way around in a
couple of minutes. From there the game grows outward in the order things depend
on each other — NPCs and gun combat (Phase 2), fighting an enemy encampment
(Phase 3), a rover you climb into and drive (Phase 4), then a ship you buy,
board and fly into space (Phase 5). See `docs/ROADMAP.md`.

- **Client:** Three.js + TypeScript, 3D-first UI
- **Server:** Go, authoritative fixed-tick simulation over WebSocket
- **Infra:** local kind cluster (server + client in a namespace, nginx `/ws` proxy) behind a Makefile
- **Development:** agent-first — specialized AI agents work in parallel on
  non-overlapping module boundaries

## Repo layout

| Path | What | Owner (agent) |
|------|------|---------------|
| `server/` | Go game server: simulation, networking, protocol | `netcode` (rules: `game`) |
| `client/` | Three.js/TS browser client: render, controls, net client, HUD | `frontend` |
| `art/` | Low-poly assets (glTF), shaders, asset manifest | `art` |
| `deploy/` | local kind run (Makefile) + K8s manifests + Dockerfiles | `infra` |
| `test/` | Cross-module integration/e2e tests | `qa` |
| `docs/` | Architecture, GDD, protocol, roadmap | main thread (agents propose) |
| `.omp/` | Agent definitions, project context, sticky rules | main thread |

## Reading order

1. `docs/ROADMAP.md` — where we are, what each phase is, and the task list
2. `docs/ARCHITECTURE.md` — how the system works
3. `docs/PROTOCOL.md` — the wire contract between client and server
4. `docs/GDD.md` — the game itself
5. `.omp/AGENTS.md` — how the agents divide the work
6. `docs/tasks/` — the dispatch-ready task briefs for the current phase

## Local development

```sh
make up     # kind cluster + server/client images + manifests + port-forwards
make down   # tear down the cluster, forwards, and logs
```

## Agent-first development

Agents live in `.omp/agents/*.md` and are auto-discovered by the harness. The
main session orchestrates; agents run in parallel on paths they own:

| Agent | Focus |
|-------|-------|
| `game` | Gameplay design → precise, implementable rules (GDD) |
| `netcode` | Go server, simulation tick loop, network protocol |
| `frontend` | Three.js client, 3D UI, prediction/interpolation |
| `art` | Low-poly asset pipeline (glTF, shaders, manifest) |
| `infra` | kind, K8s manifests, Docker, Makefile |
| `qa` | Verification against acceptance criteria, e2e tests |

Parallel dispatch example (one batch, no file overlap by construction):
`netcode` builds the tick loop, `frontend` builds on-foot movement + the 3D scene, `infra`
brings up kind. Ownership rules: `.omp/AGENTS.md`. Hard rules: `.omp/RULES.md`.

Agents run on a small local model, so each dispatch is one bounded edit
(one file, ~150 lines, one verify command) written as a task brief — format
and budgets in `.omp/AGENTS.md`, "Task sizing".
