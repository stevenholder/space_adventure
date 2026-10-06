// Command server runs the Space Adventure authoritative game server:
// a WebSocket gateway on /ws and a fixed-tick (20 Hz) simulation.
//
// Subcommands:
//
//	server            run the server (default)
//	server dump       run a JSONL input script through the sim and emit a
//	                  JSONL trajectory dump (see ARCHITECTURE "Client")
//	server codec      emit/parse wire frames as hex, for the cross-language
//	                  codec parity test (test/t12-codec-parity.mjs)
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"space-adventure/server/internal/store"
	"syscall"
	"time"

	"space-adventure/server/internal/server"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
	"space-adventure/server/internal/web"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

// buildID identifies the binary. Set at link time:
//
//	go build -ldflags "-X main.buildID=$(git rev-parse --short HEAD)"
//
// It exists because "is the server up?" is not the question anyone actually
// needs answered. A `curl /healthz` returns ok from WHATEVER is bound to that
// port -- a stale pod, a cluster left running from yesterday, a second copy
// someone forgot -- and a readiness check that cannot tell those from the
// build you just made is a check that cannot fail. That is not hypothetical:
// a packaged client was verified three times against a server image built
// before the feature under test, because a `go run` had lost the port bind
// and healthz answered anyway.
var buildID = "dev"

const (
	defaultListen = ":8080"
	// defaultMetrics is a SEPARATE listener on purpose: the prod ingress
	// routes the whole origin (deploy/prod/25-ingress.yaml) to -listen, so
	// anything mounted there is on the public internet. Prometheus
	// scrapes this port from inside the cluster; nothing routes it out.
	defaultMetrics = ":9100"
	// defaultSeed is fixed on purpose (ARCHITECTURE "Server"): a changing
	// seed gives a different asteroid every restart, which makes movement
	// iteration and bug reports unreproducible.
	defaultSeed = 1337
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "server:", err)
		os.Exit(1)
	}
}

func run(args []string) error {
	if len(args) > 0 {
		sub := map[string]func([]string) error{
			"dump":    runDump,
			"drive":   runDrive,
			"flight":  runFlight,
			"codec":   runCodec,
			"poi":     runPOI,
			"collide": runCollide,
			"rocks":   runRocks,
		}
		if run, ok := sub[args[0]]; ok {
			return run(args[1:])
		}
	}
	return runServer(args)
}

func runServer(args []string) error {
	fs := flag.NewFlagSet("server", flag.ContinueOnError)
	listen := fs.String("listen", defaultListen, "HTTP listen address")
	metrics := fs.String("metrics", defaultMetrics, "Prometheus /metrics listen address (empty disables)")
	seed := fs.Uint("seed", defaultSeed, "world seed (terrain generation + world_seed on the wire)")
	if err := fs.Parse(args); err != nil {
		return err
	}

	// JSON lines on stdout: Alloy tails them to Loki. log.Printf elsewhere
	// flows through this handler as INFO, so no call site has to change.
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))
	shutdownTracing, err := initTracing(context.Background())
	if err != nil {
		return fmt.Errorf("tracing: %w", err)
	}
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = shutdownTracing(ctx) // flush the last spans on SIGTERM
	}()

	field := terrain.Generate(uint64(*seed))
	world, err := server.New(field, uint64(*seed))
	if err != nil {
		return err
	}

	// Persistence is opt-in on DATABASE_URL. A failure here is fatal at
	// STARTUP on purpose: silently falling back to ephemeral sessions would
	// look identical to working, right up until players noticed their
	// progress was never saved. An unset DATABASE_URL is a deliberate
	// choice (every session ephemeral); a set-but-broken one is a mistake.
	var st *store.Store
	if dsn := os.Getenv("DATABASE_URL"); dsn != "" {
		var err error
		st, err = store.Open(dsn)
		if err != nil {
			return fmt.Errorf("opening store: %w", err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		err = st.Migrate(ctx)
		cancel()
		if err != nil {
			st.Close()
			return fmt.Errorf("migrating store: %w", err)
		}
		world.SetStore(st)
		defer st.Close()
		log.Printf("persistence: enabled (%s)", st.Dialect)
	} else {
		log.Printf("persistence: disabled (DATABASE_URL unset) — sessions are ephemeral")
	}

	// Phase 16: a server with a store seats only account characters.
	// SA_GUESTS=1 is the kind fleet's way back in (RUNBOOK "Who may join").
	world.SetGuests(os.Getenv("SA_GUESTS") == "1")
	switch {
	case st == nil:
		log.Printf("join: guests allowed (no store)")
	case world.Guests():
		log.Printf("join: guests allowed (SA_GUESTS)")
	default:
		log.Printf("join: accounts only")
	}

	mux := http.NewServeMux()

	// Phase 7: the account site lives in this binary. It needs the store —
	// without persistence there is nothing an account could manage, so a
	// storeless run simply has no site (the game endpoints stand alone).
	if st != nil {
		reg := world.Registry()
		spawn := [3]float64(sim.SpawnState(field).Pos)
		site := &web.Handler{
			Store: st,
			NewPlayer: func(token, name string) store.Player {
				return server.NewDefaultPlayer(reg, token, name, spawn)
			},
			Online: world.OnlineCount,
			Kick:   world.Kick,
			Retag:  world.Retag,
		}
		site.Mount(mux)
		log.Printf("account site: enabled")
	}
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		fmt.Fprint(w, "ok")
	})
	// Deliberately NOT folded into /healthz: that path is the k8s probe
	// contract and wants to stay the cheapest possible 200.
	mux.HandleFunc("/version", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		w.WriteHeader(http.StatusOK)
		fmt.Fprint(w, buildID)
	})
	mux.HandleFunc("/ws", world.HandleWS)

	srv := &http.Server{Addr: *listen, Handler: mux}

	// Which build, and how many players — the two questions a dashboard
	// asks first. Registered here rather than in package server because
	// the default registry is process-global and server.New runs many
	// times in tests; runServer runs once.
	prometheus.MustRegister(prometheus.NewGaugeFunc(prometheus.GaugeOpts{
		Name:        "space_adventure_build_info",
		Help:        "Always 1; the build label is the git rev of server/ (same as /version).",
		ConstLabels: prometheus.Labels{"build": buildID},
	}, func() float64 { return 1 }))
	prometheus.MustRegister(prometheus.NewGaugeFunc(prometheus.GaugeOpts{
		Name: "space_adventure_players_online",
		Help: "Live WebSocket sessions.",
	}, func() float64 { return float64(world.OnlineCount()) }))

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go world.Run(ctx)

	errCh := make(chan error, 2)
	go func() {
		log.Printf("listening on %s (seed %d, build %s)", *listen, *seed, buildID)
		errCh <- srv.ListenAndServe()
	}()
	if *metrics != "" {
		mm := http.NewServeMux()
		mm.Handle("/metrics", promhttp.Handler())
		msrv := &http.Server{Addr: *metrics, Handler: mm}
		defer msrv.Close()
		go func() {
			log.Printf("metrics: %s/metrics", *metrics)
			errCh <- msrv.ListenAndServe()
		}()
	}

	select {
	case err := <-errCh:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
		// Close the WebSocket connections first: they are not "idle"
		// connections, so Shutdown alone would wait out its timeout.
		world.CloseAll()
		shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return srv.Shutdown(shutdown)
	}
}
