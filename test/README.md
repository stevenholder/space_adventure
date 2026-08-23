# test — cross-module verification

- Owner: `qa` (reports bugs, never fixes product code)

**Cross-language parity (`t12-codec-parity.mjs`, `t13-collide-parity.mjs`,
Phase 2).** Anything implemented twice — once in Go, once in TypeScript — needs
a test that runs BOTH and diffs. Per-side unit tests exercise one
implementation against itself and prove nothing about agreement, and this
project has been bitten by that twice: the strafe axis was wrong in both sims
for all of M1 (they agreed, so C5 passed and every criterion stayed green), and
the Phase 2 codecs were each internally consistent while disagreeing on
framing. `t13` exists because the C5 route has no colliders on any tick, so the
trajectory diff cannot see that mirror pair at all.

 The Go and
TypeScript codecs are two independent implementations of `docs/PROTOCOL.md`,
and each one's unit tests only prove it round-trips *itself*. That is not the
property that matters. Phase 2's first cut compiled on both ends and passed
both suites while still disagreeing: the TS encoders returned bare payloads
where the Go parsers expected `u16 type | payload`, so the seq would have gone
on the wire as the message type. Run it with tsx (it imports the client codec
directly); the Go half is the `server codec emit|parse` subcommand, the same
file-in/file-out shape as `server dump` for the C5 diff.
- Verifies `docs/ROADMAP.md` M1 acceptance criteria 1–10, PASS/FAIL per
  criterion

`qa` owns two pieces of test infrastructure the criteria assume:

- **A latency-injecting WS proxy** sitting between client and server, so
  criterion 6 can measure prediction error at a controlled 100 ms. Nothing in
  the product provides this.
- **The conformance diff.** Both sims ship their own JSONL trajectory-dump
  entry points in wave 2 (ARCHITECTURE "Client"), so this is running a shared
  input script through each and comparing the dumps — not building a harness
  that reaches inside either one. If the client sim cannot be imported under
  Node, report it as a blocker rather than working around it; the DOM-free
  `tsconfig.sim.json` is supposed to make that impossible.

Planned for M1:

- **Two-client e2e harness** — scripted WS clients against a running server:
  spawn visibility (standing on terrain, not floating), authority correction,
  heartbeat despawn, snapshot rate and latency (criteria 1–4, 7).
- **Movement conformance** — one input script through the Go sim and the
  TypeScript client sim, trajectories diffed against the GDD on-foot rule table
  at 5% tolerance. Must cover level ground, a walkable slope, a slide slope, a
  `max_step` ledge, a jump, and a cube-face seam crossing (criterion 5).
- **Prediction-error harness** — injects 100 ms latency and measures p95
  |predicted − authoritative| through sprint + reversal + jump (criterion 6).
- **Terrain shape audit** — scans the generated radius field: walkable
  fraction ≥ 70%, spawn disc flat to ±0.5 m, all radii within
  `[radius_min, radius_max]`, expected crater count present, all six landmarks
  present at their radii and ≥ `landmark_min_sep` apart, and ≥ 60% of sampled
  surface points with a landmark above the horizon (criterion 9). Asserts on
  the *field*, never on the generator's internals, so retuning the noise never
  breaks the test.
- **Circumnavigation** — a scripted client walks one full great-circle lap
  across every cube face and returns within 1 m of its start, never leaving the
  surface; plus two clients on opposite sides rendering each other upright
  (criterion 10). This is the round-world regression test — a hardcoded `+Y`
  up passes everything else.

Nothing here yet — M1 wave 3 fills it in.
