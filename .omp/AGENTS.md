# Space Adventure — project context for agents

Low-poly 3D MMO space/fantasy adventure, played **entirely in first person**:
crew a ship with other players, land on a planet, explore on foot, drive
surface vehicles, load back up and leave (`docs/GDD.md`, "Core loop").
Browser client (Three.js + TypeScript) ↔ authoritative Go server (WebSocket,
binary protocol), run locally as two processes via `make up`.

Current phase: **Phase 2 — buy a weapon from an NPC and shoot a target range.**
Phase 1 (multiplayer first person on a small round world) is **done and
verified** — two browser clients walk a low-poly asteroid (150 m radius, a lap
in a couple of minutes) and see each other move, server-authoritative at 20 Hz.
Phase 2 adds identity + persistence, a reliable `cmd` channel, a general entity
store, static colliders, interaction, inventory/currency, weapons and hitscan
combat. **There are no vehicles until Phase 4 and no ships or space until Phase
5** — do not build them, even if `docs/PROTOCOL.md`'s v2 sections describe
them. The world being round stays load-bearing: up is `normalize(pos)`
everywhere, never `+Y`. Task list, contracts and acceptance criteria:
`docs/ROADMAP.md`.

## Module ownership (parallel safety)

Each agent owns paths. **Never edit files outside your ownership.** If you
need a change in another module, state it in your final report (or message the
owning agent via `hub`); the main thread coordinates cross-module work.

| Agent | Owns | Never touches |
|-------|------|---------------|
| `game` | `docs/GDD.md`, `server/data/**.json` (item/NPC/zone/loot data), gameplay rule files (e.g. `server/gameplay/` once it exists), balance data | client rendering, transport, infra, assets |
| `netcode` | `server/` (except `server/data/**.json` and gameplay rule files; `server/data/embed.go` IS netcode's), `docs/PROTOCOL.md` only when explicitly instructed | `client/`, `deploy/`, `art/` |
| `frontend` | `client/` | `server/`, `deploy/`, `art/` (consumes assets, doesn't edit) |
| `art` | `art/` | all code directories |
| `infra` | `deploy/`, root `Makefile` (root `Dockerfile*` from scale-out) | game logic, client code |
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
- Storage: plain `database/sql`, driver chosen from `DATABASE_URL`'s scheme
  (`modernc.org/sqlite` pure-Go locally, `pgx/v5/stdlib` deployed). **No ORM,
  no repository interface, no dialect branches in query code.** Portable SQL
  only: `$1` placeholders, ids generated in Go, booleans as `INTEGER`, JSON as
  `TEXT`, times and money as Unix-millis `BIGINT` (never `INTEGER` — int4 in
  Postgres, overflows any real timestamp). Never query from the tick path.
  Rules: `docs/ARCHITECTURE.md`, "Persistence".
- Protocol: little-endian binary, one message per WebSocket message —
  `docs/PROTOCOL.md`.
- Assets: glTF 2.0 binary (`.glb`) only, flat-shaded low-poly, reproducible
  via committed generation scripts, registered in `art/manifest.json`. The
  manifest is a frozen contract of asset ids; entries may point at files that
  do not exist yet, and `frontend` falls back to a placeholder.
- Commits: conventional (`feat:`, `fix:`, `docs:`, `chore:`, `test:`).
  Never commit or push unless explicitly asked.

## Task sizing (agents run on a small local model)

Agent turns execute on a local ~8B model (Qwen3 via ninfer, `http://localhost:8080`).
It is fast per token but weak at planning, and it degrades on long context long
before the 196k window fills. **The main thread does the thinking; agents do one
bounded edit.** A task that needs discovery, design, or more than a couple of
files is a main-thread task or a chain of small agent tasks — never one dispatch.

### Budget per dispatch (hard)

| Limit | Value |
|-------|-------|
| Files edited | 1 (2 only if the second is a matching test) |
| Lines changed | ~150 |
| Tool calls before reporting | ~10 |
| Prompt size handed to the agent | keep under ~16k tokens; paste the relevant snippets instead of pointing at the repo |
| Verification | exactly one named command |

Over budget = stop and report `BLOCKED: TOO BIG` with a proposed split. A
cheap refusal beats an hour of thrash.

### Task brief format (main thread writes this)

```
TASK:     <one sentence, one verb — "add X to Y", not "implement feature Z">
FILES:    <exact paths, marked (edit)/(read-only)>. Touch nothing else.
CONTRACT: <exact signature, struct layout, constants, or wire bytes>
STEPS:    <at most 3, ordered>
VERIFY:   <one command> → expect <exact string / exit 0>
REPORT:   changed lines + that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

No `CONTRACT` = the task is underspecified for this model. Write the contract
first (main thread, or a `game`/`netcode` design turn) and dispatch after.

### Chaining instead of one big task

Split by verifiable step, not by feature. Each block must compile/pass on its
own and be dispatchable with a fresh agent context:

1. types/constants only → `go build ./...`
2. one function body → its unit test
3. wire it into the caller → smoke command
4. `qa` verifies the whole thing

Sequential blocks that touch the same file go in one batch of dispatches only
if they do **not** overlap; otherwise land them one at a time.

### No discovery inside an agent turn

Grepping the repo burns the small model's context and its planning ability.
The main thread (or a read-only locator pass) finds `file:line` first and pastes
it into the brief. Agents that cannot find what the brief names report
`BLOCKED: NOT FOUND` rather than searching around for it.

## Definition of done

An agent is done when the build/tests it touched pass **and** it ran the
relevant smoke check. Report = what changed, commands run, observed results,
open questions. `qa` is the final gate for any cross-module claim.
