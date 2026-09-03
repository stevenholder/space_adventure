// Identity: token -> player row (docs/tasks/phase2-wave2.md "W2-15";
// docs/PROTOCOL.md "Identity token"; docs/ARCHITECTURE.md "Persistence").
//
// The tick loop never touches the database. It reads and writes the
// in-memory store.Player struct that an identity owns; the store is only
// touched on join, on disconnect, and once every saveInterval, and always
// from a goroutine the tick loop never waits on.
package server

import (
	"context"
	"log"
	"sync"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// saveInterval is how often a joined session autosaves while connected.
const saveInterval = 30 * time.Second

// storeTimeout bounds a single background store call.
const storeTimeout = 5 * time.Second

// identity is one connection's persistence session. player is mutated by
// the tick loop (credits, inventory, position, ...) and read by the
// background saver, so every access goes through mu.
//
// Concurrency: mutex + snapshot-under-lock. save() takes mu only long
// enough to copy the struct; the copy, not the live struct, is handed to
// the goroutine that calls the store, so the DB round trip never holds the
// lock the tick loop needs.
type identity struct {
	st    *store.Store // nil for an ephemeral session (Token == "")
	token string

	mu     sync.Mutex
	player store.Player

	stop     chan struct{}
	stopOnce sync.Once
	wg       sync.WaitGroup
}

// joinIdentity loads token's row, or builds the default loadout, and — for
// a non-empty token — starts the autosave loop. An empty token is an
// EPHEMERAL session: the server never mints a token, so an empty one means
// no row is ever read or written for it (docs/PROTOCOL.md).
func joinIdentity(ctx context.Context, st *store.Store, reg *defs.Registry, token, name string, spawn [3]float64) *identity {
	id := &identity{token: token, stop: make(chan struct{})}
	id.player = defaultPlayer(reg, token, name, spawn)

	if token == "" {
		return id
	}
	id.st = st

	loaded, err := callStore(ctx, func(ctx context.Context) (*store.Player, error) {
		return st.GetPlayer(ctx, token)
	})
	if err != nil {
		log.Printf("identity: load %q: %v", token, err)
	} else if loaded != nil {
		id.player = *loaded
	}

	id.wg.Add(1)
	go id.autosave()
	return id
}

// defaultPlayer builds the starting row for a brand-new or ephemeral
// player: start credits, start items, and the given spawn position.
// NewDefaultPlayer is defaultPlayer for callers outside the gateway — the
// web package mints account players with exactly a joining guest's start
// (Phase 7): one definition of "a new player", not two.
func NewDefaultPlayer(reg *defs.Registry, token, name string, spawn [3]float64) store.Player {
	return defaultPlayer(reg, token, name, spawn)
}

func defaultPlayer(reg *defs.Registry, token, name string, spawn [3]float64) store.Player {
	p := store.Player{
		Token:    token,
		Name:     name,
		Credits:  reg.StartCredits,
		Equipped: map[string]string{},
		Pos:      spawn,
	}
	for _, it := range reg.StartItems {
		_ = sim.AddItem(&p, it.Item, it.Qty, reg) // trusted content; refusal would be a data bug
	}
	return p
}

// Snapshot returns a copy of the current player row. Safe to call from the
// tick loop or any other goroutine.
func (id *identity) Snapshot() store.Player {
	id.mu.Lock()
	defer id.mu.Unlock()
	return id.player
}

// Mutate applies fn to the live player row under the lock. The tick loop
// and cmd handlers use this for every read-modify-write.
func (id *identity) Mutate(fn func(p *store.Player)) {
	id.mu.Lock()
	defer id.mu.Unlock()
	fn(&id.player)
}

// autosave saves every saveInterval until Close stops it.
func (id *identity) autosave() {
	defer id.wg.Done()
	t := time.NewTicker(saveInterval)
	defer t.Stop()
	for {
		select {
		case <-t.C:
			id.save(context.Background())
		case <-id.stop:
			return
		}
	}
}

// save snapshots the player row under the lock, then hands the copy to a
// background goroutine bounded by storeTimeout. A failure is logged and
// otherwise ignored: a database outage must not disconnect players
// mid-fight (docs/ARCHITECTURE.md "Persistence").
func (id *identity) save(parent context.Context) {
	if id.st == nil {
		return // ephemeral session: never write a row
	}
	p := id.Snapshot()
	if _, err := callStore(parent, func(ctx context.Context) (struct{}, error) {
		return struct{}{}, id.st.PutPlayer(ctx, &p)
	}); err != nil {
		log.Printf("identity: save %q: %v", id.token, err)
	}
}

// Close stops the autosave loop and performs one final, synchronous save
// (still off the tick loop — Close is called from connection teardown,
// never from tick()) so a clean disconnect is not lost.
func (id *identity) Close(ctx context.Context) {
	id.stopOnce.Do(func() { close(id.stop) })
	id.wg.Wait()
	id.save(ctx)
}

// callStore runs fn on a background goroutine under a storeTimeout
// deadline and waits for it. It never runs from the tick loop; callers are
// join/save/Close, all off that loop. Returning on ctx.Done() even if fn
// has not finished keeps a stuck driver from hanging the caller forever.
func callStore[T any](parent context.Context, fn func(context.Context) (T, error)) (T, error) {
	ctx, cancel := context.WithTimeout(parent, storeTimeout)
	defer cancel()
	type result struct {
		v   T
		err error
	}
	done := make(chan result, 1)
	go func() {
		v, err := fn(ctx)
		done <- result{v, err}
	}()
	select {
	case r := <-done:
		return r.v, r.err
	case <-ctx.Done():
		var zero T
		return zero, ctx.Err()
	}
}
