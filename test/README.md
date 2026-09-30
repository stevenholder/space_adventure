# test — cross-module verification

- Owner: `qa` (reports bugs, never fixes product code)

## Why parity tests, not per-side unit tests

Anything implemented twice — once in Go on the server, once in C# in the Godot
client — needs a test that runs BOTH and diffs. Per-side unit tests exercise
one implementation against itself and prove nothing about agreement, and this
project has been bitten by that twice: the strafe axis was wrong in both sims
for all of M1 (they agreed, so C5 passed and every criterion stayed green), and
the Phase 2 codecs were each internally consistent while disagreeing on framing
(one side returned bare payloads where the other expected `u16 type | payload`,
so the seq would have gone on the wire as the message type).

Hence the standing pair:

- **`t20-csharp-conformance.mjs`** (`make godot-conformance`, C40) — one input
  script through the Go sim and the C# `Sim` assembly, trajectories diffed
  per tick. `t13-collide-parity.mjs` covers the collider resolve the C5 route
  never touches.
- **`t22-csharp-codec.mjs`** (`make godot-codec`, C41) — the C# `Net` codec
  against Go-emitted vectors, byte-for-byte, both directions.

The harnesses in this directory carry their **own independent third
implementation** of the protocol and terrain, written from `docs/PROTOCOL.md`
and the GDD only — `lib/wire.mjs`, `lib/ws.mjs` (raw-TCP RFC 6455 client),
`lib/field.mjs` — so a shared bug between client and server cannot hide from
them.

## What's here

- `tNN-*.mjs` harnesses, `t2`–`t37` (t30–t33 were never written). Run as `node test/tNN-*.mjs` against the
  deployed stack (`make up`, then `make check-server`). No runner, no
  framework; each exits non-zero on failure. The per-criterion map lives in
  `docs/QA-STATUS.md` under "Harnesses, for the C43 re-run".
- `t9-terrain.py` — terrain field audit (walkability, landmarks, craters).
- Go entry points the harnesses shell out to: `server codec emit|parse`,
  `server dump`, `server collide`.
- C# entry point: `client/simdump/` (`SimDump` — `--selftest`, `--join`,
  `--predict`, `--authority`, `--collide`, `--dump`), which builds `Sim`/`Net`
  with no engine.
- `test/out/` — committed evidence (result JSONs, reports) plus regenerable
  artifacts held back by `.gitignore`.
- The latency-injecting WS proxy used for prediction-error measurement lives in
  the harnesses (`lib/proxy.mjs`, driven by `t6`), not in the product.
