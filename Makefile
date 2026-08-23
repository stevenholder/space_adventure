# Space Adventure — local kind deployment
#
#   make up    cluster (if absent) -> build + load images -> apply manifests
#              -> restart deployments onto the new images -> port-forwards ->
#              print access URLs. Safe to re-run: existing cluster is reused,
#              pods are always replaced so re-running deploys current code,
#              stale port-forwards are replaced, missing toolchain or a busy
#              port fails fast.
#   make down  stop port-forwards (verifies ports are actually free), delete
#              the cluster, remove logs. Leaves nothing running.
#   make test-pg  start a throwaway Postgres container, wait for readiness,
#              run the Go store tests against it, then always remove the
#              container (even on test failure).
#
# Overrides: make up CLIENT_PORT=8081 SERVER_PORT=18081

CLUSTER     ?= space-adventure
NAMESPACE   ?= space-adventure
SERVER_IMG  := space-adventure/server:latest
CLIENT_IMG  := space-adventure/client:latest
CLIENT_PORT ?= 3000
SERVER_PORT ?= 18080

# docker build context root (the repo root). Overridable to validate the
# pipeline against a mirror tree while server/ and client/ are not yet
# compilable.
ROOT   ?= .
LOGDIR := deploy/.logs
# always pin the context: kubectl's current-context may point elsewhere
KUBECTL := kubectl --context kind-$(CLUSTER)
PF      := $(KUBECTL) -n $(NAMESPACE) port-forward

.PHONY: up down check cluster images apply forward test-pg

up: check cluster images apply forward
	@echo "make up complete"

check:
	@command -v docker  >/dev/null 2>&1 || { echo "ERROR: docker not found in PATH" >&2; exit 1; }
	@command -v kind    >/dev/null 2>&1 || { echo "ERROR: kind not found in PATH" >&2; exit 1; }
	@command -v kubectl >/dev/null 2>&1 || { echo "ERROR: kubectl not found in PATH" >&2; exit 1; }
	@docker info >/dev/null 2>&1 || { echo "ERROR: docker daemon not running or not reachable" >&2; exit 1; }
	@echo "toolchain OK (docker daemon reachable, kind, kubectl)"

cluster:
	@if kind get clusters 2>/dev/null | grep -qx '$(CLUSTER)'; then \
		echo "cluster $(CLUSTER) already running"; \
	else \
		echo "creating cluster $(CLUSTER)"; \
		kind create cluster --name $(CLUSTER) --config deploy/kind.yaml; \
	fi

images:
	# --provenance=false: local dev images don't need buildx attestations;
	# without it the loaded image is an OCI index, a plain manifest is simpler
	# for the kind node's containerd to handle.
	docker build -f Dockerfile.server --provenance=false -t $(SERVER_IMG) $(ROOT)
	kind load docker-image $(SERVER_IMG) --name $(CLUSTER)
	docker build -f Dockerfile.client --provenance=false -t $(CLIENT_IMG) $(ROOT)
	kind load docker-image $(CLIENT_IMG) --name $(CLUSTER)

apply:
	$(KUBECTL) apply -f deploy/manifests/
	# Both images are tagged :latest, so `apply` reports "unchanged" and
	# Kubernetes keeps the running pods — on freshly built code. Without this
	# restart, `make up` silently serves whatever was built last time, which
	# looks exactly like a code change that did nothing. `kind load` has
	# already replaced the image under the tag on the node, so the new pods
	# come up on the new build.
	$(KUBECTL) -n $(NAMESPACE) rollout restart deploy/server deploy/client
	$(KUBECTL) -n $(NAMESPACE) rollout status deploy/server --timeout=120s
	$(KUBECTL) -n $(NAMESPACE) rollout status deploy/client --timeout=120s

forward:
	@mkdir -p $(LOGDIR)
	@pkill -f 'space-adventure port-forward svc/clien[t]' 2>/dev/null || true
	@pkill -f 'space-adventure port-forward svc/serve[r]' 2>/dev/null || true
	@sleep 1
	@for p in $(CLIENT_PORT) $(SERVER_PORT); do \
		if ss -ltnH | awk '{print $$4}' | grep -Eq "[:.]$$p$$"; then \
			echo "ERROR: port $$p is already in use — free it or override CLIENT_PORT/SERVER_PORT" >&2; \
			exit 1; \
		fi; \
	done
	@nohup $(PF) svc/client $(CLIENT_PORT):80 >$(LOGDIR)/client.log 2>&1 & \
	nohup $(PF) svc/server $(SERVER_PORT):8080 >$(LOGDIR)/server.log 2>&1 &
	@for i in $$(seq 1 30); do \
		if curl -fsS -o /dev/null http://127.0.0.1:$(CLIENT_PORT)/ \
		   && curl -fsS -o /dev/null http://127.0.0.1:$(SERVER_PORT)/healthz; then \
			break; \
		fi; \
		sleep 1; \
	done
	@curl -fsS -o /dev/null http://127.0.0.1:$(CLIENT_PORT)/ \
		|| { echo "ERROR: client not reachable on :$(CLIENT_PORT) — see $(LOGDIR)/client.log" >&2; exit 1; }
	@curl -fsS -o /dev/null http://127.0.0.1:$(SERVER_PORT)/healthz \
		|| { echo "ERROR: server not reachable on :$(SERVER_PORT) — see $(LOGDIR)/server.log" >&2; exit 1; }
	@echo ""
	@echo "space-adventure is up:"
	@echo "  client : http://localhost:$(CLIENT_PORT)   (WS: ws://localhost:$(CLIENT_PORT)/ws, same-origin)"
	@echo "  server : http://localhost:$(SERVER_PORT)/healthz   (WS: ws://localhost:$(SERVER_PORT)/ws, direct)"

down:
	@pkill -f 'space-adventure port-forward svc/clien[t]' 2>/dev/null || true
	@pkill -f 'space-adventure port-forward svc/serve[r]' 2>/dev/null || true
	@ok=1; \
	for p in $(CLIENT_PORT) $(SERVER_PORT); do \
		if ss -ltnH | awk '{print $$4}' | grep -Eq "[:.]$$p$$"; then \
			echo "ERROR: port $$p still in use — a process other than ours holds it" >&2; \
			ok=0; \
		fi; \
	done; \
	[ $$ok -eq 1 ] || exit 1
	@if kind get clusters 2>/dev/null | grep -qx '$(CLUSTER)'; then \
		kind delete cluster --name $(CLUSTER); \
	else \
		echo "cluster $(CLUSTER): not running"; \
	fi
	@rm -rf $(LOGDIR)
	@echo "down: port-forwards stopped, cluster removed, nothing left running"

PG_TEST_CONTAINER := sa-test-pg
PG_TEST_URL        := postgres://test:test@localhost:55432/test?sslmode=disable

test-pg:
	@docker rm -f $(PG_TEST_CONTAINER) >/dev/null 2>&1 || true
	@docker run -d --rm --name $(PG_TEST_CONTAINER) \
		-e POSTGRES_PASSWORD=test -e POSTGRES_USER=test -e POSTGRES_DB=test \
		-p 55432:5432 postgres:17-alpine >/dev/null
	@ok=0; \
	for i in $$(seq 1 30); do \
		if docker exec $(PG_TEST_CONTAINER) pg_isready -U test >/dev/null 2>&1; then \
			ok=1; break; \
		fi; \
		sleep 1; \
	done; \
	if [ $$ok -ne 1 ]; then \
		echo "ERROR: postgres not ready after 30s" >&2; \
		docker rm -f $(PG_TEST_CONTAINER) >/dev/null; \
		exit 1; \
	fi
	@cd server && TEST_DATABASE_URL='$(PG_TEST_URL)' go test -count=1 ./internal/store/...; \
	status=$$?; \
	docker rm -f $(PG_TEST_CONTAINER) >/dev/null; \
	exit $$status
