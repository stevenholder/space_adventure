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
# client is a packaged desktop build (`make unity-build`) that connects to it,
# not a page the cluster hands out. See ROADMAP U18.

CLUSTER     ?= space-adventure
NAMESPACE   ?= space-adventure
SERVER_IMG  := space-adventure/server:latest
SERVER_PORT ?= 18080

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
	docker build -f Dockerfile.server --provenance=false -t $(SERVER_IMG) $(ROOT)
	kind load docker-image $(SERVER_IMG) --name $(CLUSTER)

apply:
	$(KUBECTL) apply -f deploy/manifests/
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
	@echo ""
	@echo "space-adventure is up:"
	@echo "  server : http://localhost:$(SERVER_PORT)/healthz   (WS: ws://localhost:$(SERVER_PORT)/ws)"
	@echo "  client : make unity-build && make unity-run"

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

# ---- Unity client (Phase 3.5) ----------------------------------------------
# C44: Sim and Net build and their checks run with NO Unity Editor. That is not
# a convenience -- the conformance diff against the Go sim (C40) has to run in
# CI, and CI has no Editor.
UNITY_SLN = client-unity/headless/SpaceAdventure.Client.slnx

.PHONY: unity-test unity-gate
unity-test:
	dotnet build $(UNITY_SLN) -v q --nologo
	dotnet run --project client-unity/headless/SimDump --nologo -- --selftest

# C40: the C# sim must match the Go sim on the C5 route within 1e-10 m. This
# is the Phase 3.5 gate -- if it cannot close, everything downstream is wasted
# work against a client that silently disagrees with the server.
.PHONY: unity-conformance
unity-conformance:
	node test/t20-csharp-conformance.mjs

# C41: the C# codec must agree with the Go one BYTE FOR BYTE, both
# directions. A codec's own round-trip test agrees with its own bug, so this
# is the only check that can see a framing or offset slip.
.PHONY: unity-codec
unity-codec:
	node test/t22-csharp-codec.mjs

# U10: the transport's only real test is a real server. Needs `make up`.
.PHONY: unity-join
unity-join:
	dotnet run --project client-unity/headless/SimDump --nologo -- --join ws://127.0.0.1:$(SERVER_PORT)/ws

# The Unity CLI wrapper. It resolves the editor from the project's own
# ProjectVersion.txt and the project path with wslpath, so neither the editor
# version nor the repo location is written down twice -- and it fails on a
# compiler error, which `Unity -quit` does not: batchmode can exit 0 having
# printed "Aborting batchmode due to failure: Scripts have compiler errors".
UNITY_CLI = ./client-unity/unity

# Typecheck every assembly, including the Unity-only ones the headless
# solution cannot see.
.PHONY: unity-compile unity-typecheck unity-build unity-scene unity-run art-sync
unity-compile:
	$(UNITY_CLI) compile

# Typechecks Assets/Game against the editor's own reference assemblies, with
# no Editor process involved -- so it works while the project is open, which
# is exactly when you are iterating on gameplay code.
unity-typecheck:
	$(UNITY_CLI) typecheck

# art/ is the single source of truth for models. Unity can only ship files it
# finds under Assets/, so the .glb files and the manifest are COPIED into
# StreamingAssets -- which Unity packages verbatim, with no importer, no .meta
# semantics that matter, and nothing for C47 to catch.
#
# The copy is gitignored on purpose. Committing it would put two of every
# binary in the repo, free to drift, and the one under Assets/ would be the
# one nobody edits and everybody ships.
# art/ is the single source of truth for models. Unity can only ship files it
# finds under Assets/, so the .glb files and the manifest are COPIED into
# StreamingAssets -- which Unity packages verbatim, with no importer, no .meta
# semantics that matter, and nothing for C47 to catch.
#
# The copy is gitignored on purpose. Committing it would put two of every
# binary in the repo, free to drift, and the one under Assets/ would be the
# one nobody edits and everybody ships.
#
# Driven by manifest.json, NOT by `find art -name '*.glb'`. The manifest is
# already the definition of what the game ships -- the client resolves models
# by looking ids up in it -- so a file the manifest does not name is by
# definition not a game asset. A find picks up art/vendor/, which holds the
# raw downloaded CC0 packs: 153 unprocessed models that would go into the
# build for nothing.
STREAMING := client-unity/Assets/StreamingAssets/art

.PHONY: art-sync
art-sync:
	@rm -rf $(STREAMING)
	@mkdir -p $(STREAMING)
	@cd art && python3 -c "import json,os,shutil,sys; \
	    d=json.load(open('manifest.json')); out=os.path.join('..','$(STREAMING)'); \
	    files=[a['file'] for a in d['assets']]; \
	    missing=[f for f in files if not os.path.exists(f)]; \
	    [os.makedirs(os.path.join(out,os.path.dirname(f)),exist_ok=True) for f in files if os.path.exists(f)]; \
	    [shutil.copy2(f,os.path.join(out,f)) for f in files if os.path.exists(f)]; \
	    shutil.copy2('manifest.json',os.path.join(out,'manifest.json')); \
	    print('art-sync: %d models -> $(STREAMING)'%(len(files)-len(missing))); \
	    sys.stderr.write('art-sync: MISSING %s\n'%missing) if missing else None"

# C45: a packaged desktop build that joins the deployed server from a cold
# start, with the URL from config rather than compiled in. The player is the
# only thing that catches build-only failures -- shader stripping killed the
# first one on its first frame, and the Editor could not have seen it.
unity-build: art-sync
	$(UNITY_CLI) build

# Runs that player headless against the live stack. Needs `make up`.
unity-run:
	$(UNITY_CLI) run 20

# Regenerates the one permitted scene. It is empty by design -- Boot.cs builds
# the hierarchy at runtime -- so this exists to register it in build settings.
unity-scene:
	$(UNITY_CLI) scene

# C47: no agent-authored scenes or prefabs. Unity's native storage is
# GUID-keyed YAML -- unreviewable diffs, unmergeable conflicts, and "verify"
# means opening the Editor. Exactly one boot scene is allowed; everything else
# is built from C# at runtime. Scoped to Assets/ on purpose: Unity generates
# ProjectSettings/*.asset itself and those MUST be committed.
unity-gate:
	@scenes=$$(find client-unity/Assets -name '*.unity' -not -path '*/Scenes/Boot.unity' 2>/dev/null); \
	prefabs=$$(find client-unity/Assets -name '*.prefab' 2>/dev/null); \
	assets=$$(find client-unity/Assets -name '*.asset' 2>/dev/null); \
	if [ -n "$$scenes$$prefabs$$assets" ]; then \
		echo "C47: scene/prefab debt (build these from code instead):" >&2; \
		echo "$$scenes$$prefabs$$assets" >&2; \
		exit 1; \
	fi; \
	echo "C47 clean: no agent-authored scenes or prefabs"
