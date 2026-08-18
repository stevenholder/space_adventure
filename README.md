# Space Adventure

Low-poly 3D MMO space/fantasy adventure, played in the browser, entirely in
first person.

Board a ship with your friends, fly it to a planet, climb out and explore on
foot, drive a rover across the surface, load back up and leave. Vehicles are
shared spaces: one player flies, the rest are aboard. See `docs/GDD.md`.

**M1 builds the middle of that loop first:** a body on foot, with other
players, on a small round world you can walk all the way around in a couple of
minutes. Ships arrive at M2, space at M3 — `docs/ROADMAP.md`.

- **Client:** Three.js + TypeScript, 3D-first UI
- **Server:** Go, authoritative fixed-tick simulation over WebSocket
- **Infra:** two local processes behind a Makefile (containers + kind at the scale-out milestone)
- **Development:** agent-first — specialized AI agents work in parallel on
  non-overlapping module boundaries

## Repo layout

| Path | What | Owner (agent) |
|------|------|---------------|
| `server/` | Go game server: simulation, networking, protocol | `netcode` (rules: `game`) |
| `client/` | Three.js/TS browser client: render, controls, net client, HUD | `frontend` |
| `art/` | Low-poly assets (glTF), shaders, asset manifest | `art` |
| `deploy/` | local run (Makefile); K8s manifests + Dockerfiles from scale-out | `infra` |
| `test/` | Cross-module integration/e2e tests | `qa` |
| `docs/` | Architecture, GDD, protocol, roadmap | main thread (agents propose) |
| `.omp/` | Agent definitions, project context, sticky rules | main thread |

## Reading order

1. `docs/ROADMAP.md` — where we are, what M1 is
2. `docs/ARCHITECTURE.md` — how the system works
3. `docs/PROTOCOL.md` — the wire contract between client and server
4. `docs/GDD.md` — the game itself
5. `.omp/AGENTS.md` — how the agents divide the work

## Local development (from M1 onward)

```sh
make up     # Go server (:8080) + Vite dev server (:5173, /ws proxied)
make down   # stop both
make build  # go build/vet/test + tsc strict + npm run build
```

The Makefile lands with M1 — `infra` owns it.

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
`netcode` builds the tick loop, `frontend` builds flight controls, `infra`
brings up kind. Ownership rules: `.omp/AGENTS.md`. Hard rules: `.omp/RULES.md`.
