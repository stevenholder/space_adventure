# Task briefs

One file per wave. Each brief is **one agent dispatch**: 1 file (2 only when
the second is that file's test), ~150 changed lines, ~10 tool calls, exactly
one verify command — the budget in `.omp/AGENTS.md`, "Task sizing".

Briefs are written by the main thread and handed to the agent verbatim. They
paste the contract rather than pointing at it, because **agents do not explore
the repo** (`.omp/RULES.md`): an agent that cannot find what its brief names
reports `BLOCKED: NOT FOUND` instead of searching.

**Order matters inside a wave.** Batches are labelled: everything in the same
batch can dispatch in parallel (no two briefs touch the same file); a later
batch may depend on an earlier one. Never run two briefs against one file
concurrently.

| Wave | File | What |
|---|---|---|
| Phase 2 wave 1 | `phase2-wave1.md` | wire shape, storage plumbing, assets, deploy — the substance is stubbed |
| Phase 2 wave 2 | `phase2-wave2.md` | the substance: defs, colliders, cmd, combat, UI |
| Phase 2 wave 3 | `phase2-wave3.md` | `qa` against C11–C18 |

Contracts these briefs are derived from, and which win in any disagreement:
`docs/PROTOCOL.md` (wire), `docs/GDD.md` (rules), `docs/ARCHITECTURE.md`
(structure), `server/data/*.json` (content).
