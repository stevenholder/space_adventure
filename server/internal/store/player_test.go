package store

import (
	"context"
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func openMigrated(t *testing.T) *Store {
	t.Helper()
	s, err := Open("sqlite://" + filepath.Join(t.TempDir(), "p.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	if err := s.Migrate(context.Background()); err != nil {
		t.Fatalf("Migrate: %v", err)
	}
	return s
}

func sample() *Player {
	return &Player{
		Token:     "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",
		Name:      "Steve",
		Credits:   750,
		Inventory: []Stack{{Item: "ammo.cell", Qty: 120}, {Item: "weapon.pulse", Qty: 1}},
		Equipped:  map[string]string{"primary": "weapon.pulse"},
		Pos:       [3]float64{0, 150.0002, 0},
	}
}

func TestPlayerRoundTrip(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()

	if got, err := s.GetPlayer(ctx, "nobody"); err != nil || got != nil {
		t.Fatalf("GetPlayer(absent) = %v, %v; want nil, nil", got, err)
	}

	want := sample()
	if err := s.PutPlayer(ctx, want); err != nil {
		t.Fatalf("PutPlayer: %v", err)
	}
	got, err := s.GetPlayer(ctx, want.Token)
	if err != nil || got == nil {
		t.Fatalf("GetPlayer: %v, %v", got, err)
	}
	if !reflect.DeepEqual(got.Inventory, want.Inventory) {
		t.Errorf("Inventory = %v, want %v", got.Inventory, want.Inventory)
	}
	if !reflect.DeepEqual(got.Equipped, want.Equipped) {
		t.Errorf("Equipped = %v, want %v", got.Equipped, want.Equipped)
	}
	if got.Credits != want.Credits || got.Name != want.Name || got.Pos != want.Pos {
		t.Errorf("scalar mismatch: %+v", got)
	}
	// Unix millis is ~1.7e12 — it overflows Postgres int4, which is why the
	// column is BIGINT. Assert it survived rather than silently truncating.
	if got.CreatedMs < 1_000_000_000_000 {
		t.Errorf("CreatedMs = %d, want a full millisecond timestamp", got.CreatedMs)
	}
}

func TestPlayerUpsertOverwrites(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()
	p := sample()
	if err := s.PutPlayer(ctx, p); err != nil {
		t.Fatalf("first PutPlayer: %v", err)
	}
	created := p.CreatedMs

	p.Credits = 12
	p.Inventory = []Stack{{Item: "ammo.cell", Qty: 3}}
	if err := s.PutPlayer(ctx, p); err != nil {
		t.Fatalf("second PutPlayer: %v", err)
	}

	got, err := s.GetPlayer(ctx, p.Token)
	if err != nil {
		t.Fatalf("GetPlayer: %v", err)
	}
	if got.Credits != 12 || len(got.Inventory) != 1 {
		t.Errorf("upsert did not overwrite: %+v", got)
	}
	if got.CreatedMs != created {
		t.Errorf("CreatedMs changed on update: %d -> %d", created, got.CreatedMs)
	}

	var n int
	if err := s.DB.QueryRow(`SELECT COUNT(*) FROM player`).Scan(&n); err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Errorf("row count = %d after upsert, want 1", n)
	}
}

func TestPlayerCorruptJSONIsAnError(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()
	p := sample()
	if err := s.PutPlayer(ctx, p); err != nil {
		t.Fatal(err)
	}
	if _, err := s.DB.Exec(`UPDATE player SET inventory = $1 WHERE token = $2`, "{not json", p.Token); err != nil {
		t.Fatal(err)
	}
	// Must be an error, not an empty inventory: treating corruption as "new
	// player" hands them a blank loadout and then overwrites the real row.
	if _, err := s.GetPlayer(ctx, p.Token); err == nil {
		t.Error("corrupt inventory JSON returned no error")
	}
}

// The Postgres half runs under `make test-pg`; portability that is not
// executed on both engines is portability that is already broken.
func TestPlayerRoundTripPostgres(t *testing.T) {
	dsn := os.Getenv("TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("TEST_DATABASE_URL not set; run `make test-pg`")
	}
	s, err := Open(dsn)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer s.Close()
	t.Cleanup(func() { s.DB.Exec(`DROP TABLE IF EXISTS player`); s.DB.Exec(`DROP TABLE IF EXISTS schema_version`) })
	if err := s.Migrate(context.Background()); err != nil {
		t.Fatalf("Migrate: %v", err)
	}
	ctx := context.Background()
	p := sample()
	if err := s.PutPlayer(ctx, p); err != nil {
		t.Fatalf("PutPlayer: %v", err)
	}
	got, err := s.GetPlayer(ctx, p.Token)
	if err != nil || got == nil {
		t.Fatalf("GetPlayer: %v, %v", got, err)
	}
	if got.Credits != p.Credits || !reflect.DeepEqual(got.Inventory, p.Inventory) {
		t.Errorf("postgres round-trip mismatch: %+v", got)
	}
}
