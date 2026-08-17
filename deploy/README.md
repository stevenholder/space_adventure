# deploy — local Kubernetes (kind)

kind cluster config, K8s manifests, image build pipeline.

- Owner: `infra`
- Namespace: `space-adventure`
- Workloads (M1): `server` (Deployment + Service, :8080 WS), `client`
  (nginx static, :80, same-origin with server)
- Lifecycle: root `Makefile` — `make up` / `make down`
- Local access: `kubectl port-forward` (no ingress in M1)

Nothing here yet — M1 brings up the cluster.
