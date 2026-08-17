# Space Adventure

Low-poly 3D MMO space/fantasy adventure, played in the browser.

- **Client:** Three.js + TypeScript, 3D-first UI
- **Server:** Go, authoritative fixed-tick simulation over WebSocket
- **Infra:** local Kubernetes (kind) + Docker
- **Development:** agent-first — specialized AI agents work in parallel on
  non-overlapping module boundaries

## Repo layout

| Path | What | Owner (agent) |
|------|------|---------------|
| `server/` | Go game server: simulation, networking, protocol | `netcode` (rules: `game`) |
| `client/` | Three.js/TS browser client: render, controls, net client, HUD | `frontend` |
| `art/` | Low-poly assets (glTF), shaders, asset manifest | `art` |
| `deploy/` | kind config, K8s manifests, Dockerfiles | `infra` |
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
make up     # kind cluster + build images + deploy + port-forwards
make down   # tear it all down
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
