# Runbook — the pandas cluster

Production operations for the real deployment (ROADMAP Phase 6). The kind
stack has its own story (`make up` / `make down`); nothing here touches it.

Context: `kubectl --context default`, namespace `space-adventure`.

## Deploy

Normal path: push to `main`. CI runs; on green, the `deploy` workflow
builds/pushes the image (tag = server-subtree hash, skipped when server/
is unchanged), applies `deploy/prod`, waits for rollout, and smoke-checks
`/version` against the pushed hash. A failed smoke rolls back
automatically and fails the run.

By hand (bootstrap or when Actions is down):

```sh
docker build -f Dockerfile.server --provenance=false \
  --build-arg BUILD_ID=$(git rev-parse --short HEAD:server) \
  -t ghcr.io/stevenholder/space_adventure/server:$(git rev-parse --short HEAD:server) .
docker push ghcr.io/stevenholder/space_adventure/server:$(git rev-parse --short HEAD:server)
kubectl --context default apply -k deploy/prod   # pin the tag in kustomization.yaml first
```

## Verify what is running

```sh
curl -s http://192.168.1.163/version              # LAN, any node IP
curl -s https://game.stevenholder.info/version    # public, via Cloudflare
kubectl --context default -n space-adventure get pods,cluster
```

Both must equal `git rev-parse --short HEAD:server`. The public door is
Cloudflare-proxied DNS to the router: TLS ends at Cloudflare, clients dial
`wss://game.stevenholder.info/ws` (the packaged client:
`-serverUrl wss://game.stevenholder.info/ws`). Per-IP limits identify
public callers by CF-Connecting-IP — if the Cloudflare proxying is ever
turned off, that header disappears and the limits fall back to the
Traefik-appended X-Forwarded-For hop, which is then the router; check
gatekeeper.go before changing the edge.

## Releases

Every successful deploy publishes a GitHub pre-release for the commit it
rolled out: tag `vYYYY.MM.DD-<sha7>`, the client exports from the CI run
that was deployed packed by Velopack as version `1.0.<deploy run number>`
(`SpaceAdventure.AppImage`, `SpaceAdventure-win-Setup.exe`, full + delta
nupkgs, and the `releases.{linux,win}.json` feeds), and notes made of
a fixed header (what is live, how to run the downloads) plus GitHub's
generated list of merged PRs since the previous `v*` tag. It is the
`release` job at the end of `.github/workflows/deploy.yml`; a failed rollout
means no release, so a release is always a build that was live.

Installed clients update themselves: at launch `Boot.UpdateThenConnect` asks
the newest pre-release's feed, downloads the delta, quits, and Velopack swaps
the files and relaunches. Source runs, godot-cli flows and loose exports
aren't installed and skip the check. Any failure (GitHub down, rate limit)
plays the current build. While playing, an installed client re-checks every
5 minutes (`SA_UPDATE_EVERY` overrides), downloads a newer release in the
background, shows "UPDATE … READY — restart the game", and applies it when
the game quits; the build is top-right on the HUD (`v1.0.N · sha7`, `dev`
from source). To test an update locally, pack two versions with
`vpk pack` into a directory and launch the older one with
`SA_UPDATE_SOURCE=<dir>`.

```sh
gh release list --limit 5
gh release download v2026.09.29-abc1234 -p '*Setup.exe'   # a specific build
```

Flip `--prerelease` off in the workflow when a build should become "Latest".

The landing page's download buttons hit `/download/windows` and
`/download/linux`, which redirect to the newest release carrying
`SpaceAdventure-win-Setup.exe` / `SpaceAdventure.AppImage`
(`server/internal/web/download.go`), so the links never go stale.

```sh
curl -sI https://game.stevenholder.info/download/windows | grep -i location
```

## Roll back

```sh
kubectl --context default -n space-adventure rollout undo deploy/server
```

Or redeploy any previous build by its tag — every server build ever
deployed is in GHCR named by its subtree hash.

## Postgres

CNPG runs `space-db` with 2 instances; failover is automatic. Useful:

```sh
kubectl --context default -n space-adventure get cluster space-db   # PRIMARY column
kubectl --context default -n space-adventure exec space-db-1 -- psql -U postgres -d app -c 'select count(*) from player;'
```

App credentials live in secret `space-db-app` (CNPG-generated, not in git).

## Backups and the restore drill

Nightly `db-backup` CronJob pg_dumps to the `db-backups` PVC (pinned to
panda2), keeping 14. The drill — run it for real (C50), not on paper:

```sh
# 1. Trigger a fresh backup now
kubectl --context default -n space-adventure create job --from=cronjob/db-backup drill-backup
kubectl --context default -n space-adventure wait --for=condition=complete job/drill-backup --timeout=120s

# 2. Restore into a scratch database and count rows. Notes hard-won by the
#    first drill: the image major must match the cluster's Postgres major
#    (pg_dump refuses newer servers); creating a database needs the
#    SUPERUSER secret (the app user cannot); and drop/create must be two
#    separate -c calls (one -c is one transaction, and DROP DATABASE
#    refuses to run in one).
kubectl --context default -n space-adventure run drill-restore -i --restart=Never \
  --image=ghcr.io/cloudnative-pg/postgresql:18 --overrides='{"apiVersion":"v1","spec":{"nodeSelector":{"kubernetes.io/hostname":"panda2"},"containers":[{"name":"drill-restore","image":"ghcr.io/cloudnative-pg/postgresql:18","stdin":true,"command":["/bin/sh"],"volumeMounts":[{"name":"b","mountPath":"/backups"}],"env":[{"name":"SU","valueFrom":{"secretKeyRef":{"name":"space-db-superuser","key":"uri"}}}]}],"volumes":[{"name":"b","persistentVolumeClaim":{"claimName":"db-backups"}}]}}' <<'EOF'
set -eu
latest=$(ls -t /backups/space-db-*.sql.gz | head -1)
echo "restoring $latest"
base=${SU%/*}
psql -q "$base/postgres" -c 'drop database if exists drill;'
psql -q "$base/postgres" -c 'create database drill;'
gunzip -c "$latest" | psql -q "$base/drill" >/dev/null
echo "players in restored backup: $(psql "$base/drill" -tAc 'select count(*) from player;')"
psql -q "$base/postgres" -c 'drop database drill;'
EOF
kubectl --context default -n space-adventure logs drill-restore | tail -3
kubectl --context default -n space-adventure delete pod drill-restore
```

Drilled 2026-09-02: failover in ~60 s with zero row loss; restore counted
the live row count out of the backup.

To rotate the superuser password: patch the `password` key of
`space-db-superuser` — CNPG applies it to the database and regenerates the
`uri` keys within seconds. (Done 2026-09-02 after debug output echoed the
old one into container logs.)

## Why there is no NetworkPolicy

There was one (Postgres reachable only from the server, its replicas and
the backup job). This cluster's k3s network-policy enforcement is broken
for CROSS-NODE pod traffic: with the policy in place, even an
allow-all-pods rule blocked any cross-node connection (same-node passed;
probed 2026-09-02 with labeled busybox pods on pinned nodes). The trap it
set: the game server kept working on connections pooled from before
enforcement and would have lost Postgres on its next restart. A fence
that blocks legitimate traffic depending on pod placement is worse than
no fence, so the policy is gone; in-cluster Postgres exposure is guarded
by scram auth with generated credentials and by not being on the
ingress. If the fence is ever wanted, fix the CNI first (flannel/netpol
cross-node — a cluster-level repair, not a manifest) and re-probe.

A node-death drill: `kubectl delete pod space-db-1` (or cordon+drain its
node) and watch the PRIMARY column move with no server restart needed.

## Logs

```sh
kubectl --context default -n space-adventure logs deploy/server --tail=100
kubectl --context default -n space-adventure logs jobs/db-backup --tail=20   # last backup
```

## Metrics (and logs, traces)

The server serves Prometheus `/metrics` on its own port, 9100 (never the
ingress: that routes the whole public origin). Grafana Alloy, in the same
namespace, reads the `ServiceMonitor`, scrapes it, stamps `env="prod"`
and `cluster="pandas"` on every series, and pushes OTLP to the LGTM box
at `192.168.1.112:4317`. The kind stack does the same with `env="kind"`,
`cluster="kind-space-adventure"` (part of `make up`), so the two are one
label apart in any query: `{env="prod"}` vs `{env="kind"}`.

CD deploys Alloy and the ServiceMonitor with everything else
(`deploy/prod` includes `deploy/observability/prod`). What CD cannot do
is the cluster-scoped half — the CRD, Alloy's ClusterRole, and widening
its own Role — so that is a one-time bootstrap by hand, BEFORE the first
deploy that carries it (the same category as the namespace, the CNPG
operator and the ghcr-pull secret):

```sh
kubectl --context default apply -f deploy/prod/05-rbac.yaml        # CI's widened Role: admin-applied, or CD may not grant it to itself
kubectl --context default apply -k deploy/observability/bootstrap  # CRD + Alloy's ClusterRole
kubectl --context default wait --for condition=established crd/servicemonitors.monitoring.coreos.com
```

Re-run only if `bootstrap/` or the CI Role changes (a CRD bump, a new
CI permission).

Is it flowing? Alloy's own counters, before looking at Grafana:

```sh
kubectl --context default -n space-adventure port-forward deploy/alloy 12345 &
curl -s localhost:12345/metrics | grep -E 'otelcol_exporter_(sent|send_failed)_metric_points'
```

`sent` climbing and `send_failed` flat is healthy. The same counters
exist for the other two signals: `otelcol_exporter_sent_log_records_total`
and `otelcol_exporter_sent_spans_total`.

The dashboard: `http://192.168.1.112:3000/d/space-adventure` — players,
build, scrape health, tick rate, tick p50/p99 against the 5 ms / 50 ms
lines, slow ticks, network, memory, CPU, goroutines; the `env` variable
picks kind, prod, or both overlaid (kind blue, prod orange). It is code,
`deploy/observability/dashboards.py`; edit, then `make dashboards` with
`GRAFANA_TOKEN` set (a service-account token with Editor) to upsert it.

The server's own series
are `space_adventure_build_info{build=...}` (equals `/version`),
`space_adventure_players_online`, and `space_adventure_tick_seconds`
(histogram; the 50 ms budget of 20 Hz is the line that matters).

**Logs.** The server writes JSON lines to stdout (`log/slog`; every
`log.Printf` arrives as `level=INFO`). Alloy tails every pod's stdout in
`space-adventure` through the API server (`loki.source.kubernetes`, which
is why its ClusterRole has `pods/log get`), lifts the pod's labels into
OTLP resource attributes, and pushes OTLP to the LGTM box -- Loki :3100 is
not reachable from the clusters, OTLP is the only way in. Index labels:
`service_name` (`space-adventure-` + the pod's `app.kubernetes.io/name`:
`-server`, `-postgres`, `-alloy`), `deployment_environment` (kind|prod),
`k8s_cluster_name`, `k8s_namespace_name`, `k8s_pod_name`,
`k8s_container_name`. In Grafana Explore → Loki:

```logql
{service_name="space-adventure-server", deployment_environment="prod"} | json | msg=~"slow tick.*"
```

**Traces.** With `OTEL_EXPORTER_OTLP_ENDPOINT` set (the manifests set
`http://alloy:4317`; unset = no tracer, zero cost -- tests run without
it) the server exports spans to Alloy's OTLP receiver (Service `alloy`,
:4317), which stamps `k8s.cluster.name` and forwards them. Resource:
`service.name=space-adventure-server`, `service.version` = `/version`,
`deployment.environment` from `DEPLOY_ENV`. One trace is one player
session: root `join` (attrs `player.id`, `player.has_token`), children
`store.get_player`, a `cmd` per command (`cmd.opcode`, `cmd.status`) and
the autosave `store.put_player`s. Ticks are never traced except
`tick.slow`, emitted only past the 5 ms slow-tick line and spanning the
real tick. In Grafana Explore → Tempo (TraceQL):

```
{resource.service.name="space-adventure-server" && resource.deployment.environment="prod" && name="join"}
{name="tick.slow"}
```

The pandas needs by hand, once, before the first deploy that carries
this: re-apply `deploy/observability/bootstrap` (above) -- its ClusterRole
gained `pods/log get`, and CD cannot widen a ClusterRole. Without it Alloy
still runs and ships metrics and traces; only the log tailers fail
(`forbidden` in `kubectl logs deploy/alloy`).

## CD plumbing (when deploys stop arriving)

- Actions joins the tailnet as `tag:ci` (ephemeral; devices self-remove).
- kubectl goes through `tailscale-operator.perch-acrux.ts.net` (the
  operator's API proxy, `APISERVER_PROXY=true` via helm value
  `apiServerProxyConfig.mode`).
- The ACL grant maps `tag:ci` → group `ci-deployers`;
  `deploy/prod/05-rbac.yaml` is that group's entire authority.
- Secrets: `TS_OAUTH_CLIENT_ID` / `TS_OAUTH_SECRET` in the repo
  (Tailscale OAuth client, writable `auth_keys`, tag `tag:ci`).
- Image pulls: the GHCR package is private; the namespace holds a
  hand-created `ghcr-pull` docker-registry secret from a classic PAT
  scoped to read:packages ONLY. Rotate by recreating:

  ```sh
  kubectl --context default -n space-adventure create secret docker-registry ghcr-pull \
    --docker-server=ghcr.io --docker-username=stevenholder --docker-password=<PAT> \
    --dry-run=client -o yaml | kubectl --context default apply -f -
  ```
