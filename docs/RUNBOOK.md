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
curl -s http://192.168.1.163/version   # any node IP; must equal `git rev-parse --short HEAD:server`
kubectl --context default -n space-adventure get pods,cluster
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

# 2. Restore into a scratch database and count rows
kubectl --context default -n space-adventure run drill-restore --rm -i --restart=Never \
  --image=ghcr.io/cloudnative-pg/postgresql:17 --overrides='{"spec":{"nodeSelector":{"kubernetes.io/hostname":"panda2"},"containers":[{"name":"drill-restore","image":"ghcr.io/cloudnative-pg/postgresql:17","stdin":true,"command":["/bin/sh"],"volumeMounts":[{"name":"b","mountPath":"/backups"}],"env":[{"name":"URI","valueFrom":{"secretKeyRef":{"name":"space-db-app","key":"uri"}}}]}],"volumes":[{"name":"b","persistentVolumeClaim":{"claimName":"db-backups"}}]}' <<'EOF'
set -eu
latest=$(ls -t /backups/space-db-*.sql.gz | head -1)
echo "restoring $latest"
base=${URI%/*}
psql "$base/postgres" -c 'drop database if exists drill; create database drill;'
gunzip -c "$latest" | psql "$base/drill" >/dev/null
psql "$base/drill" -c 'select count(*) as players from player;'
psql "$base/postgres" -c 'drop database drill;'
EOF
```

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
