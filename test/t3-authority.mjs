#!/usr/bin/env node
/**
 * C3 — the server is authoritative.
 *
 * A state forced client-side is dragged back to the server's within one tick.
 * The harness joins, walks for 1.5 s so the body is somewhere non-trivial,
 * then shoves the PREDICTED position 4 m sideways along the surface and
 * watches what the next snapshot does to it. Prediction is client-side, so
 * forcing it grants nothing — and that is precisely the assertion.
 *
 *   c3a  the forced 4 m offset reached the live predicted state
 *   c3b  one reconcile snaps back to within 1e-3 m of the authoritative pos
 *   c3c  that correction lands within one tick (50 ms) plus network
 *
 * The work is in C#, in SimDump. That is not indirection for its own sake: the
 * unit under test is THE PREDICTOR THAT SHIPS, and since ROADMAP U18 retired
 * the browser client that is Game/Core/Prediction.cs. A JavaScript
 * reimplementation of prediction here would be a fourth sim to keep in step
 * with Go, C# and the GDD, and it would pass while the shipping client failed.
 *
 * This file stays because `t3` is how QA-STATUS and the C43 re-run refer to
 * the criterion, and because the harness should be runnable the same way as
 * every other one in this directory.
 *
 *   node test/t3-authority.mjs [ws-url]        (needs a live server)
 */
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const url = process.argv[2] ?? process.env.SA_SERVER_URL ?? 'ws://127.0.0.1:18080/ws'
const evidence = path.join(root, 'test', 'out', 't3-authority.json')

try {
  const out = execFileSync(
    'dotnet',
    ['run', '--project', 'client/simdump', '--nologo', '--',
      '--authority', url, '--evidence', evidence],
    { cwd: root, encoding: 'utf8' },
  )
  process.stdout.write(out)
} catch (e) {
  // The harness exits non-zero on a failed criterion, and its own output says
  // which one — so print it and inherit the status rather than paraphrasing.
  process.stdout.write(e.stdout ?? '')
  process.stderr.write(e.stderr ?? `${e.message}\n`)
  process.exit(e.status ?? 1)
}
