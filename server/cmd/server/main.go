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
	"net/http"
	"os"
	"os/signal"
	"space-adventure/server/internal/store"
	"syscall"
	"time"

	"space-adventure/server/internal/server"
	"space-adventure/server/internal/terrain"
)

const (
	defaultListen = ":8080"
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
	if len(args) > 0 && args[0] == "dump" {
		return runDump(args[1:])
	}
	if len(args) > 0 && args[0] == "codec" {
		return runCodec(args[1:])
	}
	if len(args) > 0 && args[0] == "collide" {
		return runCollide(args[1:])
	}
	if len(args) > 0 && args[0] == "route" {
		return runRoute(args[1:])
	}
	if len(args) > 0 && args[0] == "reach" {
		return runReach(args[1:])
	}
	if len(args) > 0 && args[0] == "lapscan" {
		return runLapScan(args[1:])
	}
	if len(args) > 0 && args[0] == "rimscan" {
		return runRimScan(args[1:])
	}
	if len(args) > 0 && args[0] == "lap" {
		return runLap(args[1:])
	}
	return runServer(args)
}

func runServer(args []string) error {
	fs := flag.NewFlagSet("server", flag.ContinueOnError)
	listen := fs.String("listen", defaultListen, "HTTP listen address")
	seed := fs.Uint("seed", defaultSeed, "world seed (terrain generation + world_seed on the wire)")
	if err := fs.Parse(args); err != nil {
		return err
	}

	field := terrain.Generate(uint64(*seed))
	world := server.New(field, uint64(*seed))

	// Persistence is opt-in on DATABASE_URL. A failure here is fatal at
	// STARTUP on purpose: silently falling back to ephemeral sessions would
	// look identical to working, right up until players noticed their
	// progress was never saved. An unset DATABASE_URL is a deliberate
	// choice (every session ephemeral); a set-but-broken one is a mistake.
	if dsn := os.Getenv("DATABASE_URL"); dsn != "" {
		st, err := store.Open(dsn)
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

	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		fmt.Fprint(w, "ok")
	})
	mux.HandleFunc("/ws", world.HandleWS)

	srv := &http.Server{Addr: *listen, Handler: mux}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go world.Run(ctx)

	errCh := make(chan error, 1)
	go func() {
		fmt.Printf("listening on %s (seed %d)\n", *listen, *seed)
		errCh <- srv.ListenAndServe()
	}()

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
