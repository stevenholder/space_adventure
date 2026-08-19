# deploy — local kind deployment

- Owner: `infra`

The full stack runs in a kind cluster (namespace `space-adventure`):

| Component | Image (built from) | Port | Notes |
|---|---|---|---|
| server | `space-adventure/server:latest` (`Dockerfile.server`) | 8080 | WS at `/ws`, health at `/healthz`; readiness + liveness probes on `/healthz` |
| client | `space-adventure/client:latest` (`Dockerfile.client`) | 80 | nginx serves the static bundle and proxies `/ws` → `server:8080/ws`, so the browser is same-origin (no CORS) |

Root `Makefile` targets:

| Target | Does |
|---|---|
| `make up` | Create the kind cluster if absent, build + load both images, `kubectl apply -f deploy/manifests/`, wait for rollouts, start port-forwards, print access URLs. Safe to re-run: existing cluster is reused, stale port-forwards are replaced. Fails fast on missing toolchain, docker daemon down, or a busy host port. |
| `make down` | Stop port-forwards (verifies the host ports are actually free), delete the cluster, remove logs. Leaves nothing running. |

Access (default host ports):

- client: `http://localhost:3000` — WS `ws://localhost:3000/ws` (same-origin)
- server (direct, for debugging): `http://localhost:18080/healthz` — WS `ws://localhost:18080/ws`

Override with `make up CLIENT_PORT=... SERVER_PORT=...`.

Files:

- `kind.yaml` — kind cluster config (single control-plane node)
- `manifests/` — namespace, server Deployment+Service, client Deployment+Service;
  all labeled `app.kubernetes.io/part-of=space-adventure`, idempotent `kubectl apply`
- `nginx/client.conf` — nginx config baked into the client image
- `.logs/` — port-forward logs (created by `make up`, removed by `make down`;
  not committed)

Both Dockerfiles use the repo root as build context, so `make up` builds them
with `-f Dockerfile.<x> .`. `ROOT` is overridable (`make up ROOT=<dir>`) to
point the context at a mirror tree — used to validate the pipeline before
`server/` and `client/` are compilable.
