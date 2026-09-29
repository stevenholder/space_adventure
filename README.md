# Space Adventure

Low-poly 3D MMO space/fantasy adventure, played entirely in first person.

Board a ship with your friends, fly it to a planet, climb out and explore on
foot, drive a rover across the surface, load back up and leave. Vehicles are
shared spaces: one player flies, the rest are aboard. See `docs/GDD.md`.

**Phase 1 built the middle of that loop first, and is done:** a body on foot,
with other players, on a small round world you can walk all the way around in a
couple of minutes. From there the game grows outward in the order things depend
on each other — NPCs and gun combat (Phase 2), fighting an enemy encampment
(Phase 3), a rover you climb into and drive (Phase 4), then a ship you buy,
board and fly into space (Phase 5). See `docs/ROADMAP.md`.

- **Client:** Godot 4 (C#), packaged desktop build, 3D-first UI
- **Server:** Go, authoritative fixed-tick simulation over WebSocket
- **Infra:** local kind cluster (server + client in a namespace, nginx `/ws` proxy) behind a Makefile
- **Development:** agent-first — specialized AI agents work in parallel on
  non-overlapping module boundaries

## Repo layout

| Path | What | Owner (agent) |
|------|------|---------------|
| `server/` | Go game server: simulation, networking, protocol | `netcode` (rules: `game`) |
| `client/` | Godot 4 client: engine-free `shared/` assemblies, the `godot/` game, the `simdump/` headless runner | `frontend` |
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
make up     # kind cluster + server image + manifests + readiness check
make down   # tear down the cluster, forwards, and logs
```

### The Godot client

`client/godot-cli` is the command line for the client. The Godot version is
written down once, in `client/.godot-version`, and the wrapper resolves the
editor binary from it; nothing needs Windows, a project lock, or a licence.

```sh
./client/godot-cli where        # which editor, which project, are the templates installed
./client/godot-cli dev 8        # run from source, headless, against the live server
./client/godot-cli play         # run from source, windowed
./client/godot-cli build        # export a Linux player to client/build/linux (Windows: build Windows)
./client/godot-cli run 20       # run that player headless against the live server
./client/godot-cli editor       # open the editor
```

The same commands are `make godot-dev`, `godot-play`, `godot-build`,
`godot-run`. `make godot-test`, `godot-codec` and `godot-conformance` need no
editor at all: `dotnet build client/SpaceAdventure.Client.slnx` compiles every
assembly including the engine-bound one (`Godot.NET.Sdk` pulls the bindings
from NuGet), which is what keeps C40 (sim conformance), C41 (codec parity) and
now the whole client compile runnable in CI.

`Boot.cs` builds the whole hierarchy at runtime from the one four-line scene,
so no scene setup is needed. It connects to `ws://127.0.0.1:18080/ws` unless
`SA_SERVER_URL` or `-serverUrl` (after `--`) says otherwise, so bring the
server up with `make up` first.

## Agent-first development

Agents live in `.omp/agents/*.md` and are auto-discovered by the harness. The
main session orchestrates; agents run in parallel on paths they own:

| Agent | Focus |
|-------|-------|
| `game` | Gameplay design → precise, implementable rules (GDD) |
| `netcode` | Go server, simulation tick loop, network protocol |
| `frontend` | Godot client, 3D UI, prediction/interpolation |
| `art` | Low-poly asset pipeline (glTF, shaders, manifest) |
| `infra` | kind, K8s manifests, Docker, Makefile |
| `qa` | Verification against acceptance criteria, e2e tests |

Parallel dispatch example (one batch, no file overlap by construction):
`netcode` builds the tick loop, `frontend` builds on-foot movement + the 3D scene, `infra`
brings up kind. Ownership rules: `.omp/AGENTS.md`. Hard rules: `.omp/RULES.md`.

Agents run on a small local model, so each dispatch is one bounded edit
(one file, ~150 lines, one verify command) written as a task brief — format
and budgets in `.omp/AGENTS.md`, "Task sizing".
