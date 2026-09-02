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
