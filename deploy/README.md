# deploy — local run (M1)

- Owner: `infra`
- M1 is two local processes, no containers and no cluster — the rationale is
  in `docs/ARCHITECTURE.md` ("Deployment").

Root `Makefile` targets:

| Target | Does |
|--------|------|
| `make up` | Go server (WS + `/healthz` on :8080) and Vite dev server (:5173, `/ws` proxied to :8080, so client and server are same-origin) |
| `make down` | stops both, leaves no stray processes |
| `make build` | `go build ./...` + `tsc` strict + `npm run build` |

Containers, kind config and K8s manifests arrive at the **scale-out milestone**, when
there is more than one server process to schedule.

Nothing here yet — M1 wave 1 adds the Makefile.
