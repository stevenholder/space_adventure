# Space Adventure — project context for agents

Low-poly 3D MMO space/fantasy adventure. Browser client (Three.js +
TypeScript) ↔ authoritative Go server (WebSocket, binary protocol) on local
Kubernetes (kind). Current milestone: **M1 — multiplayer space flight** — two
browser clients fly low-poly ships in shared space; the server is
authoritative at 20 Hz. See `docs/ROADMAP.md`.

## Module ownership (parallel safety)

Each agent owns paths. **Never edit files outside your ownership.** If you
need a change in another module, state it in your final report (or message the
owning agent via `hub`); the main thread coordinates cross-module work.

| Agent | Owns | Never touches |
|-------|------|---------------|
| `game` | `docs/GDD.md`, gameplay rule files (e.g. `server/gameplay/` once it exists), balance data | client rendering, transport, infra, assets |
| `netcode` | `server/` (except gameplay rule files), `docs/PROTOCOL.md` only when explicitly instructed | `client/`, `deploy/`, `art/` |
| `frontend` | `client/` | `server/`, `deploy/`, `art/` (consumes assets, doesn't edit) |
| `art` | `art/` | all code directories |
| `infra` | `deploy/`, root `Makefile`, root `Dockerfile*` | game logic, client code |
| `qa` | `test/`, verification reports | all product code (reports bugs, never fixes) |
| main | `docs/`, `.omp/`, `README.md`, root config | orchestrates; edits contract files directly |

## Contracts (single source of truth)

- **`docs/PROTOCOL.md`** — wire contract. `netcode` and `frontend` both
  implement from it. A change requires both ends updated in the same
  milestone; only the main thread (or an explicitly instructed agent) edits it.
- **`docs/GDD.md`** — design contract. `game` owns it; everyone else implements
  from it. When design and implementation disagree, the GDD wins; file the
  discrepancy in your report.
- **`docs/ARCHITECTURE.md`** — structural reference. Don't drift from it;
  propose changes in your report instead of editing.

## Conventions

- TypeScript: strict mode, no `any`, Vite for `client/`.
- Go: module `space-adventure/server`, gofmt-clean, stdlib-first
  (`net/http`, `encoding/binary`), gorilla/websocket for WS, wrap errors
  (`%w`), no global mutable state.
- Protocol: little-endian binary, length-prefixed — `docs/PROTOCOL.md`.
- Assets: glTF 2.0 binary (`.glb`) only, flat-shaded low-poly, reproducible
  via committed generation scripts, registered in `art/manifest.json`.
- Commits: conventional (`feat:`, `fix:`, `docs:`, `chore:`, `test:`).
  Never commit or push unless explicitly asked.

## Definition of done

An agent is done when the build/tests it touched pass **and** it ran the
relevant smoke check. Report = what changed, commands run, observed results,
open questions. `qa` is the final gate for any cross-module claim.
