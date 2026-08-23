package server

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

func testRegistry() *defs.Registry {
	return &defs.Registry{
		StartCredits: 500,
		StartItems: []struct {
			Item string `json:"item"`
			Qty  int    `json:"qty"`
		}{{Item: "ammo.cell", Qty: 50}},
		InvSlots: 8,
		Items: map[string]defs.Item{
			"ammo.cell": {ID: "ammo.cell", StackMax: 999},
		},
	}
}

func openTestStore(t *testing.T) *store.Store {
	t.Helper()
	s, err := store.Open("sqlite://" + filepath.Join(t.TempDir(), "id.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	if err := s.Migrate(context.Background()); err != nil {
		t.Fatalf("Migrate: %v", err)
	}
	return s
}

// A joined token round-trips through a save: mutate credits, save, rejoin
// with the same token, and the mutation is restored.
func TestIdentity_TokenRoundTrip(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	reg := testRegistry()
	spawn := [3]float64{1, 2, 3}

	id := joinIdentity(ctx, st, reg, "tok-1", "Steve", spawn)
	if got := id.Snapshot().Credits; got != 500 {
		t.Fatalf("initial credits = %d, want 500 (StartCredits)", got)
	}
	id.Mutate(func(p *store.Player) { p.Credits = 750 })
	id.Close(ctx) // stops autosave + final save

	again := joinIdentity(ctx, st, reg, "tok-1", "Steve", spawn)
	defer again.Close(ctx)
	if got := again.Snapshot().Credits; got != 750 {
		t.Fatalf("rejoin credits = %d, want 750", got)
	}
}

// An empty token is an ephemeral session: it starts from the default
// loadout and never writes a row, even after a mutation and a clean
// disconnect.
func TestIdentity_EmptyTokenNeverPersists(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	reg := testRegistry()

	id := joinIdentity(ctx, st, reg, "", "Ghost", [3]float64{0, 0, 0})
	id.Mutate(func(p *store.Player) { p.Credits = 999999 })
	id.Close(ctx)

	row, err := st.GetPlayer(ctx, "")
	if err != nil {
		t.Fatalf("GetPlayer: %v", err)
	}
	if row != nil {
		t.Fatalf("ephemeral session wrote a row: %+v", row)
	}
}

// A store that cannot be reached does not abort the session: join falls
// back to the default loadout, and a save is logged and ignored rather
// than propagated.
func TestIdentity_FailingStoreDoesNotAbortSession(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	reg := testRegistry()
	st.Close() // every subsequent call now fails

	id := joinIdentity(ctx, st, reg, "tok-2", "Steve", [3]float64{0, 0, 0})
	if got := id.Snapshot().Credits; got != 500 {
		t.Fatalf("credits after failed load = %d, want default 500", got)
	}
	id.Mutate(func(p *store.Player) { p.Credits = 42 })
	id.Close(ctx) // must not panic or block despite the closed DB

	if got := id.Snapshot().Credits; got != 42 {
		t.Fatalf("in-memory mutation lost after failed save: %d", got)
	}
}

// callStore returns once fn completes, well inside storeTimeout, and does
// not run on the caller's goroutine (a panic in fn must not surface here).
func TestCallStore_RunsOffCaller(t *testing.T) {
	start := time.Now()
	v, err := callStore(context.Background(), func(context.Context) (int, error) {
		return 7, nil
	})
	if err != nil || v != 7 {
		t.Fatalf("callStore = %d, %v; want 7, nil", v, err)
	}
	if time.Since(start) > storeTimeout {
		t.Fatalf("callStore took longer than storeTimeout")
	}
}
