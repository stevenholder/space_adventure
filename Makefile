# Space Adventure — local kind deployment
#
#   make up    cluster (if absent) -> build + load images -> apply manifests
#              -> restart deployments onto the new images -> wait for the
#              mapped host ports to answer -> print access URLs. Safe to
#              re-run: existing cluster is reused and pods are always replaced,
#              so re-running deploys current code. Host ports come from
#              deploy/kind.yaml's extraPortMappings, not a proxy process.
#   make down  delete the cluster (which releases the mapped host ports) and
#              remove logs. Leaves nothing running.
#   make test-pg  start a throwaway Postgres container, wait for readiness,
#              run the Go store tests against it, then always remove the
#              container (even on test failure).
#
# SERVER_PORT changes only which host port the readiness check probes. The
# real mapping is fixed in deploy/kind.yaml at cluster-creation time -- edit
# that file and recreate the cluster to actually move it.
#
# There is no client port any more. The cluster serves the game SERVER; the
# client is a packaged desktop build (`make godot-build`) that connects to it,
# not a page the cluster hands out. See ROADMAP U18.

CLUSTER     ?= space-adventure
NAMESPACE   ?= space-adventure
SERVER_IMG  := space-adventure/server:latest
SERVER_PORT ?= 18080

# What the server we are about to build will call itself. Stamped into the
# binary (Dockerfile.server -> -X main.buildID) and served at /version, so the
# readiness check below can assert it reached THIS build.
#
# "is the server up?" was never the question. curl /healthz returns ok from
# whatever holds the port -- a pod from yesterday, a cluster nobody remembered
# starting, a second copy -- and a check that cannot tell those apart from the
# thing you just built is a check that cannot fail. A packaged client was
# verified three times against a server image predating the feature under test
# for exactly that reason.
#
# Keyed on the SERVER SUBTREE, not on HEAD. The first version of this hashed
# the whole repo, which meant editing a shader or a README invalidated a
# perfectly good deployed server and the gate fired on every unrelated commit.
# A check that cries wolf gets bypassed, which is worse than not having it.
# `HEAD:server` moves when and only when server code moves, and the dirty
# marker is scoped the same way.
BUILD_ID := $(shell git rev-parse --short HEAD:server 2>/dev/null || echo unknown)$(shell git diff --quiet -- server 2>/dev/null || echo -dirty)

# docker build context root (the repo root). Overridable to validate the
# pipeline against a mirror tree while server/ is not yet compilable.
ROOT   ?= .
LOGDIR := deploy/.logs
# always pin the context: kubectl's current-context may point elsewhere
KUBECTL := kubectl --context kind-$(CLUSTER)

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
	docker build -f Dockerfile.server --provenance=false \
		--build-arg BUILD_ID=$(BUILD_ID) -t $(SERVER_IMG) $(ROOT)
	kind load docker-image $(SERVER_IMG) --name $(CLUSTER)

apply:
	# --prune: `kubectl apply` only ever adds and updates, so deleting a
	# manifest leaves whatever it created running forever. The browser
	# client's Deployment and Service outlived their manifest by 23 hours
	# that way -- still serving, still answering, long after the files were
	# gone from the repo.
	#
	# Scoped by the label every manifest here carries, so pruning cannot
	# reach anything this project did not create.
	#
	# The allowlist is deliberately short, and Namespace and
	# PersistentVolumeClaim are deliberately NOT on it. Those two hold the
	# environment and the Postgres data, and a prune that removes them turns
	# an editing mistake into lost state. Workloads churn; storage should be
	# removed on purpose, by hand.
	$(KUBECTL) apply -f deploy/manifests/ \
		--prune -l app.kubernetes.io/part-of=space-adventure \
		--prune-allowlist=apps/v1/Deployment \
		--prune-allowlist=core/v1/Service \
		--prune-allowlist=core/v1/Secret
	# Both images are tagged :latest, so `apply` reports "unchanged" and
	# Kubernetes keeps the running pods — on freshly built code. Without this
	# restart, `make up` silently serves whatever was built last time, which
	# looks exactly like a code change that did nothing. `kind load` has
	# already replaced the image under the tag on the node, so the new pods
	# come up on the new build.
	$(KUBECTL) -n $(NAMESPACE) rollout restart deploy/server
	$(KUBECTL) -n $(NAMESPACE) rollout status deploy/server --timeout=120s

# No proxy process: deploy/kind.yaml maps the host ports straight onto the
# NodePort services, so "forwarding" is now just waiting for the stack to
# answer. See deploy/kind.yaml for why port-forward was removed.
forward:
	@mkdir -p $(LOGDIR)
	@pkill -f 'space-adventure port-forward svc/serve[r]' 2>/dev/null || true
	@for i in $$(seq 1 60); do \
		if curl -fsS -o /dev/null http://127.0.0.1:$(SERVER_PORT)/healthz; then \
			break; \
		fi; \
		sleep 1; \
	done
	@curl -fsS -o /dev/null http://127.0.0.1:$(SERVER_PORT)/healthz \
		|| { echo "ERROR: server not reachable on :$(SERVER_PORT)" >&2; exit 1; }
	@$(MAKE) --no-print-directory check-server
	@echo ""
	@echo "space-adventure is up:"
	@echo "  server : http://localhost:$(SERVER_PORT)/healthz   (WS: ws://localhost:$(SERVER_PORT)/ws)"
	@echo "  client : make godot-build && make godot-run"

down:
	# Legacy cleanup: earlier revisions ran kubectl port-forward (and, briefly,
	# a supervisor loop around it). Harmless if nothing matches.
	@pkill -f 'space-adventure port-forward svc/clien[t]' 2>/dev/null || true
	@pkill -f 'space-adventure port-forward svc/serve[r]' 2>/dev/null || true
	@if kind get clusters 2>/dev/null | grep -qx '$(CLUSTER)'; then \
		kind delete cluster --name $(CLUSTER); \
	else \
		echo "cluster $(CLUSTER): not running"; \
	fi
	# Ports are checked AFTER the cluster is deleted, not before: kind's
	# extraPortMappings are held by docker for as long as the node container
	# exists, so checking first would fail every single time.
	@sleep 1
	@ok=1; \
	for p in $(SERVER_PORT); do \
		if ss -ltnH | awk '{print $$4}' | grep -Eq "[:.]$$p$$"; then \
			echo "ERROR: port $$p still in use after teardown — something else holds it" >&2; \
			ok=0; \
		fi; \
	done; \
	[ $$ok -eq 1 ] || exit 1
	@rm -rf $(LOGDIR)
	@echo "down: cluster removed, host ports released, nothing left running"

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

# ---- Godot client ----------------------------------------------------------
# C44: the whole client compiles and its checks run with NO engine installed.
# Godot.NET.Sdk pulls GodotSharp from NuGet, so `dotnet build` typechecks the
# engine-bound assembly too -- the conformance diff against the Go sim (C40)
# and the codec parity (C41) have to run in CI, and CI has no editor.
CLIENT_SLN = client/SpaceAdventure.Client.slnx
GODOT_CLI  = ./client/godot-cli

.PHONY: godot-test godot-gate
godot-test:
	dotnet build $(CLIENT_SLN) -v q --nologo
	dotnet run --project client/simdump --nologo -- --selftest

# C40: the C# sim must match the Go sim on the C5 route within 1e-10 m.
.PHONY: godot-conformance
godot-conformance:
	node test/t20-csharp-conformance.mjs
	node test/t23-drive-conformance.mjs
	node test/t25-flight-conformance.mjs

# C41: the C# codec must agree with the Go one BYTE FOR BYTE, both
# directions. A codec's own round-trip test agrees with its own bug, so this
# is the only check that can see a framing or offset slip.
.PHONY: godot-codec
godot-codec:
	node test/t22-csharp-codec.mjs

# Asserts that whatever is answering on :$(SERVER_PORT) is the build in this
# working tree, not something left running. Depended on by anything that
# measures the live stack, because measuring the wrong server is worse than
# measuring nothing: it produces a green result about code that is not there.
#
# Retries for ~16 s before judging: `make up` returns while the rollout is
# still swapping pods, and a probe in that window sees the OLD pod (or a
# connection reset) and fails a check that would pass two seconds later —
# which burned three verification runs before this loop existed. A genuinely
# wrong build still fails, with the last answer in the message.
.PHONY: check-server
check-server:
	@got=""; \
	for i in 1 2 3 4 5 6 7 8; do \
		got=$$(curl -fsS --max-time 5 http://127.0.0.1:$(SERVER_PORT)/version 2>/dev/null) && \
			[ "$$got" = "$(BUILD_ID)" ] && break; \
		sleep 2; \
	done; \
	[ -n "$$got" ] || { \
		echo "ERROR: nothing answered /version on :$(SERVER_PORT)." >&2; \
		echo "  Either no server is running, or one too old to have /version" >&2; \
		echo "  is holding the port. Run: make up" >&2; \
		exit 1; \
	}; \
	if [ "$$got" != "$(BUILD_ID)" ]; then \
		echo "ERROR: :$(SERVER_PORT) serves server build $$got; this tree is $(BUILD_ID)." >&2; \
		echo "  The deployed server is not built from the server/ code you have." >&2; \
		echo "  Either something else holds the port, or the cluster predates a" >&2; \
		echo "  server change. Anything measured now is about other code." >&2; \
		echo "  Run: make up" >&2; \
		exit 1; \
	fi; \
	echo "server on :$(SERVER_PORT) is build $$got (matches this tree)"

# The transport's only real test is a real server. Needs `make up`, and gated
# on check-server for the same reason godot-run is -- "a real server" has to
# mean THIS one.
.PHONY: godot-join
godot-join: check-server
	dotnet run --project client/simdump --nologo -- --join ws://127.0.0.1:$(SERVER_PORT)/ws

# The Godot CLI wrapper: version from client/.godot-version, editor resolved
# from it, export via export_presets.cfg. No project lock, no licence, and it
# fails on the errors Godot only prints (a C# exception does not fail the
# process on its own).
.PHONY: godot-import godot-build godot-run godot-dev godot-play

# Generates .godot/ (import cache, font import). Idempotent.
godot-import:
	$(GODOT_CLI) import

# C45: a packaged desktop build that joins the deployed server from a cold
# start, with the URL from config rather than compiled in. art/ is the single
# source of truth for models; the export step stages the manifest-listed .glb
# files beside the executable, and a dev run reads art/ in place. Nothing is
# copied into the project, so nothing is imported as a scene (C91).
godot-build:
	$(GODOT_CLI) build $(PRESET)

# Runs that player headless against the live stack. Needs `make up`.
godot-run: check-server
	$(GODOT_CLI) run 20

# From source, headless, no export: the fast loop while iterating.
godot-dev:
	$(GODOT_CLI) dev $(SECS)

# From source, windowed (WSLg or a native desktop).
godot-play:
	$(GODOT_CLI) play

# C91: no agent-authored scenes or resources. Exactly one scene is allowed,
# client/godot/Boot.tscn, and no .tres/.res/.gd/.material/.mesh; models never
# go under the project (they would be imported as scenes). The shared
# assemblies reference no engine: a `using Godot` there is the rule broken.
godot-gate:
	@scenes=$$(find client/godot -name '*.tscn' -not -path 'client/godot/Boot.tscn' -not -path '*/.godot/*' 2>/dev/null); \
	res=$$(find client/godot \( -name '*.tres' -o -name '*.res' -o -name '*.scn' -o -name '*.gd' \
	       -o -name '*.material' -o -name '*.mesh' -o -name '*.glb' -o -name '*.gltf' \) -not -path '*/.godot/*' 2>/dev/null); \
	if [ -n "$$scenes$$res" ]; then \
		echo "C91: scene/resource debt (build these from code instead):" >&2; \
		echo "$$scenes$$res" >&2; \
		exit 1; \
	fi; \
	if grep -rl --include='*.cs' "using Godot" client/shared client/simdump 2>/dev/null; then \
		echo "C91: engine reference in an engine-free assembly" >&2; \
		exit 1; \
	fi; \
	echo "C91 clean: one scene, no resources, shared/ engine-free"
