# Phase 2 — wave 3: verification

`qa` owns `test/` and verification reports, and **never fixes product code** —
it reports defects for the main thread to dispatch (`.omp/AGENTS.md`).

Two things make this wave bigger than one brief: Phase 1's harness decodes a
44-byte entity row and must be widened before it can run at all, and the
Phase 2 criteria (C11–C18, `docs/ROADMAP.md`) need a new harness.

---

### W3-1 · `qa` · Widen the harness decoder to the 54-byte row

**Dispatches WITH W1-B1 and W1-B2, not after them.** `test/lib/wire.mjs`
hard-asserts `p.length !== 8 + 44 * count` and throws, so the moment the server
emits 54-byte rows the whole Phase 1 suite fails at the decoder. All three
files move together or the game is unverifiable in between.

```
TASK:     Update the shared harness snapshot decoder to the 54-byte entity row.
FILES:    test/lib/wire.mjs (edit)
CONTRACT: The entity row is now 54 bytes (docs/PROTOCOL.md):
            u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
            | u32 parent_id | u16 seat | u16 health | u8 flags | i8 pitch_q
          At test/lib/wire.mjs lines 81/88/91 the value 44 appears in the
          doc comment, the size assertion and the row offset. Replace all
          three, and decode the five new fields into the returned object as
          parentId, seat, health, flags, pitchQ.
          pitch_q is SIGNED: read it with readInt8, NOT readUInt8.
          Introduce a named ENTITY_BYTES constant rather than repeating the
          literal — the value has now changed once and will change again in
          Phase 4.
          `hello` needs NO change: the server parses a payload that ends after
          the name as an absent token (W1-B3), so the harness stays
          token-less and keeps testing that path.
STEPS:    1. Add ENTITY_BYTES = 54 and use it in the assertion and offset.
          2. Decode the five new fields.
VERIFY:   node test/t2-two-client.mjs  → PASS
REPORT:   changed lines and that command's output. Then stop.
BUDGET:   1 file, ~50 lines, 10 tool calls.
```

### W3-1b · `qa` · Give the conformance route a strafe leg

```
TASK:     Add a lateral leg to the C5 route so the conformance diff can see
          the strafe axis at all.
FILES:    test/t5/prerun.ts (edit)
CONTRACT: Every tick of the current C5 script carries move_x == 0 (all 1993 of
          them), and the C10 lap hardcodes moveX: 0. That is how
          right = up x facing -- the player's LEFT in a right-handed frame --
          shipped through all of M1 with A and D swapped: both sims agreed, so
          the diff passed, and no scripted route ever pressed A or D.

          Add a leg on flat ground that strafes right for ~2 s, then left for
          ~2 s, then a diagonal (move_x and move_y both non-zero, which also
          exercises the joint unit-length clamp in GDD "Input").

          The existing tolerance and diff logic are unchanged -- this only
          widens what the route covers.

          Both sims already assert the axis in isolation
          (server/internal/sim TestStrafeDirection, client/tools/smoke.ts).
          What neither covers is the two ends AGREEING while strafing, which is
          exactly what replay convergence depends on and exactly what a
          per-sim unit test cannot see.
STEPS:    1. Add the leg to the route builder.
          2. Regenerate both scripts and both dumps; re-run test/t5/diff.mjs.
VERIFY:   node test/t5/diff.mjs  -> VERDICT: PASS
REPORT:   the new leg's tick range and per-leg dPos/dVel. Then stop.
BUDGET:   1 file, ~60 lines, 10 tool calls.
```

### W3-2 · `qa` · Re-run the Phase 1 regression suite

```
TASK:     Run C1-C10 against the Phase 2 build and report, unchanged, what
          passes and what does not.
FILES:    docs/QA-STATUS.md (edit — append a Phase 2 regression section)
CONTRACT: The Phase 1 criteria in docs/ROADMAP.md, "Phase 1", are the gate:
          nothing in Phase 2 may break them. Run the existing t2-t10 harness
          on the deployed kind stack.
          Report verdicts only. Do NOT edit product code — a failure is a
          defect report with the file, the observed value and the expected
          value, for the main thread to dispatch.
STEPS:    1. make up, run t2 through t10, collect verdicts.
          2. Append the regression table to docs/QA-STATUS.md.
VERIFY:   the appended table, one row per criterion, each PASS or FAIL with
          its measured number.
REPORT:   the table and any defects found. Then stop.
BUDGET:   1 file, ~80 lines, 15 tool calls (a verification pass, not an edit).
```

### W3-3 · `qa` · C11 / C11b — persistence and engine portability

```
TASK:     Verify that a player's progress survives a reconnect, and that the
          store behaves identically on SQLite and Postgres.
FILES:    test/t11-persist.mjs (new)
CONTRACT: docs/ROADMAP.md criteria C11 and C11b.
            C11  - connect with a token, buy the rifle, disconnect, reconnect
                   with the SAME token: inventory, credits and equipped item
                   restored. A FRESH token starts with start_credits 1000 and
                   start_items (ammo.cell x120) and no weapon.
            C11b - `make test-pg` green (the store suite on Postgres), the
                   kind stack up on Postgres with DATABASE_URL supplied only
                   by the Secret, and a migration applied twice is a no-op.
                   Also assert no DSN literal appears in
                   deploy/manifests/10-server.yaml or either Dockerfile.
VERIFY:   node test/t11-persist.mjs  → PASS
REPORT:   measured values per assertion and that command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W3-4 · `qa` · C12 / C17 — authority over currency and interaction

```
TASK:     Verify the server refuses what it should, including forged commands.
FILES:    test/t11-shop.mjs (new)
CONTRACT: docs/ROADMAP.md criteria C12 and C17.
            C12 - a shop_buy the player cannot afford is refused
                  (cmd_result status 3, reason insufficient_credits) and
                  credits are unchanged; a forged cmd claiming a free purchase
                  is likewise refused; a negative qty never credits the player;
                  exceeding the rate limit returns status 4 and executes
                  nothing.
            C17 - the prompt appears only within interact_dist and inside the
                  look cone; a shop_list from 10 m away is refused with
                  out_of_range even when the client sends it anyway.
          Drive these at the WIRE level, not through the UI — the point is
          that a hostile client cannot get a different answer than a polite one.
VERIFY:   node test/t11-shop.mjs  → PASS
REPORT:   one line per assertion with its observed status/reason. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W3-5 · `qa` · C13 — static colliders and sim agreement

```
TASK:     Verify wall collision holds and that the two sims agree over a run
          that includes colliders.
FILES:    test/t11-collide.mjs (new)
CONTRACT: docs/ROADMAP.md criterion C13.
            - A scripted walk into every range wall stops at the surface:
              penetration <= 1e-2 m, and no tunnelling at sprint_speed.
            - The same input script through the Go dump subcommand and the TS
              sim dump agrees to the Phase 1 conformance tolerance.
            - An empty collider list still reproduces the Phase 1 trajectory
              exactly — this is the check that W2-7 did not change the
              no-collider path.
VERIFY:   node test/t11-collide.mjs  → PASS
REPORT:   max penetration, max trajectory deviation, and that command's
          output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W3-6 · `qa` · C14 / C15 / C16 — combat

```
TASK:     Verify hit registration under latency, damage and death, and what a
          second client sees.
FILES:    test/t11-combat.mjs (new)
CONTRACT: docs/ROADMAP.md criteria C14, C15, C16.
            C14 - with 100 ms injected latency, 20 scripted shots at a static
                  target from 30 m ALL register; 20 shots aimed 1 m wide
                  register ZERO. The first number is what rewind buys; if it
                  is 12, rewind is not working and a widened tolerance would
                  be hiding the defect.
            C15 - health drops by the GDD table value per hit, reaches 0,
                  emits a death event, and full health returns within
                  3.0 s +/- 100 ms with the SAME entity_id (no despawn churn).
            C16 - a second client sees the shooter's equipped weapon, aim
                  pitch within 1 deg of truth, and exactly one shot_fired
                  event per shot, in order.
VERIFY:   node test/t11-combat.mjs  → PASS
REPORT:   hit counts, damage values, respawn timing, pitch error, and that
          command's output. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W3-7 · `qa` · C18 — budgets

```
TASK:     Verify the build, wire and frame budgets.
FILES:    test/t11-budget.mjs (new)
CONTRACT: docs/ROADMAP.md criterion C18.
            - go build/vet/test ./... clean; tsc strict (all three configs)
              and npm run build clean.
            - Snapshot entity row is exactly 54 B.
            - 60 fps with 10 players, 1 NPC and 8 targets.
            - NO DATABASE CALL ON THE TICK PATH: with the store pointed at a
              Postgres given 200 ms of injected latency, tick duration is
              unchanged. Measure tick duration directly; do not infer it from
              snapshot arrival, which the port-forward's jitter already
              contaminates (ARCHITECTURE, "Deployment").
VERIFY:   node test/t11-budget.mjs  → PASS
REPORT:   each measured budget against its limit. Then stop.
BUDGET:   1 file, ~150 lines, 10 tool calls.
```

### W3-8 · `qa` · Phase 2 status report

```
TASK:     Write the Phase 2 verdict: every criterion, its measured value, and
          its evidence.
FILES:    docs/QA-STATUS.md (edit)
CONTRACT: One row per criterion C1-C18 with PASS/FAIL, the measured number,
          and the harness file that produced it — the same shape as the
          Phase 1 table already in that file.
          A criterion with no runnable evidence is FAIL, not "assumed".
STEPS:    1. Collect the outputs of W3-2 through W3-7.
          2. Write the table and the defect list.
VERIFY:   the completed table.
REPORT:   the table. Then stop.
BUDGET:   1 file, ~100 lines, 10 tool calls.
```
