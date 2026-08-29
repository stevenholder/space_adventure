#!/usr/bin/env node
/**
 * C6 — prediction quality at 100 ms injected latency.
 *
 * Topology:  harness --(test/lib/proxy.mjs, 50 ms/direction)--> the server
 *
 * THE GATE is same-instant pairing. The obvious metric, |P_M − snapshot(ack
 * M)|, pairs two DIFFERENT instants: P_M is the client state when input M was
 * sent, and the snapshot is the server ~RTT later. Under sustained sprint that
 * gap is exactly one tick of motion (7.5 m/s × 0.05 s = 0.375 m), which is why
 * the old metric read a flat 0.375 m no matter what the client did. And the
 * same-wall-time metric it reached for is unachievable by ANY client: the
 * server's state is a 20 Hz step function lagging continuous motion by
 * U(0, 50 ms), so p95 = 0.356 m > 0.25 for a perfect one. A criterion nothing
 * can pass is not a gate.
 *
 * So: after reconciling ack M the client has snapped to the server and
 * replayed what is still unacked, and its belief is about tick M + pending —
 * which is the snapshot to compare against. That difference is real prediction
 * error, and it separates exact replay (clears to wire precision) from
 * blending (leaves a residual toward the stale anchor).
 *
 *   PASS ⟺ p95(err) < 0.25 m AND snap-backs = 0 AND the scenario really
 *   happened (sprint, reversal, jump onto a walkable slope) AND the proxy
 *   actually injected the latency.
 *
 * Adjudication and the lower-bound proof: test/out/t6-c6-measurement-resolution.md
 *
 * The measurement lives in C# (SimDump --predict) because the unit under test
 * is THE PREDICTOR THAT SHIPS, which since ROADMAP U18 is
 * Game/Core/Prediction.cs. This file owns the proxy, because injecting latency
 * is a transport concern and test/lib/proxy.mjs already does it.
 *
 *   node test/t6-prediction.mjs        (needs a live server on :18080)
 */
import { execFileSync, spawn } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const PROXY_PORT = 28092
const TARGET_HOST = process.env.SA_TARGET_HOST ?? '127.0.0.1'
const TARGET_PORT = Number(process.env.SA_TARGET_PORT ?? 18080)

const proxy = spawn(
  'node',
  ['test/lib/proxy.mjs', String(PROXY_PORT), TARGET_HOST, String(TARGET_PORT)],
  { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] },
)

const ready = new Promise((resolve, reject) => {
  let out = ''
  const onData = (b) => {
    out += b.toString()
    if (out.includes('proxy ready')) resolve()
  }
  proxy.stdout.on('data', onData)
  proxy.stderr.on('data', onData)
  proxy.on('exit', (c) => reject(new Error(`proxy exited ${c}: ${out}`)))
  setTimeout(() => reject(new Error(`proxy not ready: ${out}`)), 10_000)
})

let status = 0
try {
  await ready
  const out = execFileSync(
    'dotnet',
    ['run', '--project', 'client-unity/headless/SimDump', '--nologo', '--',
      '--predict', `ws://127.0.0.1:${PROXY_PORT}/ws`,
      '--evidence', path.join(root, 'test', 'out', 't6-prediction.json')],
    { cwd: root, encoding: 'utf8' },
  )
  process.stdout.write(out)
} catch (e) {
  process.stdout.write(e.stdout ?? '')
  process.stderr.write(e.stderr ?? `${e.message}\n`)
  status = e.status ?? 1
} finally {
  proxy.kill()
}
process.exit(status)
