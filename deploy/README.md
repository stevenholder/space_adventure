# deploy — local kind deployment

- Owner: `infra`

The full stack runs in a kind cluster (namespace `space-adventure`):

| Component | Image (built from) | Port | Notes |
|---|---|---|---|
| server | `space-adventure/server:latest` (`Dockerfile.server`) | 8080 | WS at `/ws`, health at `/healthz`, build id at `/version`; readiness + liveness probes on `/healthz` |

There is no client container. The client is a packaged Godot desktop build
(`make godot-build`) that connects to this cluster; it is not something the
cluster serves. The browser client and its nginx pod were retired in ROADMAP
U18.

Root `Makefile` targets:

| Target | Does |
|---|---|
| `make up` | Create the kind cluster if absent, build + load the server image, `kubectl apply -f deploy/manifests/`, wait for the rollout, print access URLs. Safe to re-run: an existing cluster is reused. Fails fast on missing toolchain, docker daemon down, or a busy host port. |
| `make check-server` | Assert that whatever answers on the host port is the build in this working tree, by comparing `/version` against the git rev. `godot-run` depends on it. |
| `make down` | Stop port-forwards (verifies the host ports are actually free), delete the cluster, remove logs. Leaves nothing running. |

Access (default host ports):

- server: `http://localhost:18080/healthz` — WS `ws://localhost:18080/ws`

Override with `make up SERVER_PORT=...`.

Files:

- `kind.yaml` — kind cluster config (single control-plane node)
- `manifests/` — namespace, server Deployment+Service, Postgres; all labeled
  `app.kubernetes.io/part-of=space-adventure`, idempotent `kubectl apply`
- `.logs/` — port-forward logs (created by `make up`, removed by `make down`;
  not committed)

Both Dockerfiles use the repo root as build context, so `make up` builds them
with `-f Dockerfile.<x> .`. `ROOT` is overridable (`make up ROOT=<dir>`) to
point the context at a mirror tree — used to validate the pipeline before
`server/` is compilable.
