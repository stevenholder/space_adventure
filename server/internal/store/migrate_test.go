package store

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

func TestMigrate_SQLite(t *testing.T) {
	dir := t.TempDir()
	dsn := "sqlite://" + filepath.Join(dir, "world.db")

	s, err := Open(dsn)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer s.Close()

	ctx := context.Background()

	if err := s.Migrate(ctx); err != nil {
		t.Fatalf("Migrate (1st call): %v", err)
	}
	if err := s.Migrate(ctx); err != nil {
		t.Fatalf("Migrate (2nd call): %v", err)
	}

	if _, err := s.DB.ExecContext(ctx, `INSERT INTO player
		(token, name, credits, inventory, equipped, pos_x, pos_y, pos_z, created_ms, updated_ms)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)`,
		"tok-1", "alice", 100, "[]", "{}", 1.0, 2.0, 3.0, 1000, 1000,
	); err != nil {
		t.Fatalf("insert into player: %v", err)
	}

	var count int
	row := s.DB.QueryRowContext(ctx, `SELECT COUNT(*) FROM schema_version`)
	if err := row.Scan(&count); err != nil {
		t.Fatalf("counting schema_version: %v", err)
	}
	if count != 6 {
		t.Fatalf("schema_version row count = %d, want 6 (001..006)", count)
	}
}

func TestMigrate_Postgres(t *testing.T) {
	dsn := os.Getenv("TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("TEST_DATABASE_URL not set; skipping postgres migration test")
	}

	s, err := Open(dsn)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer s.Close()

	ctx := context.Background()

	t.Cleanup(func() {
		s.DB.ExecContext(ctx, `DROP TABLE IF EXISTS player`)
		s.DB.ExecContext(ctx, `DROP TABLE IF EXISTS schema_version`)
	})

	if err := s.Migrate(ctx); err != nil {
		t.Fatalf("Migrate (1st call): %v", err)
	}
	if err := s.Migrate(ctx); err != nil {
		t.Fatalf("Migrate (2nd call): %v", err)
	}

	if _, err := s.DB.ExecContext(ctx, `INSERT INTO player
		(token, name, credits, inventory, equipped, pos_x, pos_y, pos_z, created_ms, updated_ms)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)`,
		"tok-1", "alice", 100, "[]", "{}", 1.0, 2.0, 3.0, 1000, 1000,
	); err != nil {
		t.Fatalf("insert into player: %v", err)
	}

	var count int
	row := s.DB.QueryRowContext(ctx, `SELECT COUNT(*) FROM schema_version`)
	if err := row.Scan(&count); err != nil {
		t.Fatalf("counting schema_version: %v", err)
	}
	if count != 6 {
		t.Fatalf("schema_version row count = %d, want 6 (001..006)", count)
	}
}

// 005 lands on a database that already ran 001–004 with players in it:
// every row gets the default body, guests keep clashing names, and
// account-owned names that clash case-insensitively are made unique (oldest
// keeps its name) before the index is built, instead of failing the boot.
// 006 follows in the same Migrate: every existing row is hair.none.
func TestMigrate005OnExistingData(t *testing.T) {
	s, err := Open("sqlite://" + filepath.Join(t.TempDir(), "world.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	ctx := context.Background()

	if _, err := s.DB.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS schema_version (
  version INTEGER PRIMARY KEY, applied_ms BIGINT NOT NULL)`); err != nil {
		t.Fatal(err)
	}
	all, err := s.loadMigrations()
	if err != nil {
		t.Fatal(err)
	}
	for _, m := range all {
		if m.version <= 4 {
			if err := s.applyMigration(ctx, m); err != nil {
				t.Fatalf("%s: %v", m.name, err)
			}
		}
	}
	insert := func(token, name, account string, created int64) {
		t.Helper()
		var acct any
		if account != "" {
			acct = account
		}
		if _, err := s.DB.ExecContext(ctx, `INSERT INTO player
			(token, name, credits, inventory, equipped, pos_x, pos_y, pos_z, created_ms, updated_ms, account_id)
			VALUES ($1, $2, 0, '[]', '{}', 0, 0, 0, $3, $3, $4)`, token, name, created, acct); err != nil {
			t.Fatal(err)
		}
	}
	insert("aaaa-old", "Kade", "a1", 100)
	insert("bbbb-new", "kade", "a2", 200)
	insert("bbbb-newer", "KADE", "a3", 300) // same token prefix as bbbb-new: the suffix must still differ
	insert("g1", "Kade", "", 50)
	insert("g2", "Kade", "", 60)
	if _, err := s.DB.ExecContext(ctx, `INSERT INTO link_code VALUES ('C', 'a1', 1, 2)`); err != nil {
		t.Fatal(err)
	}

	if err := s.Migrate(ctx); err != nil {
		t.Fatalf("Migrate onto 004: %v", err)
	}

	for tok, want := range map[string]string{"aaaa-old": "Kade", "bbbb-new": "kade bbbb-new", "bbbb-newer": "KADE bbbb-newer", "g1": "Kade", "g2": "Kade"} {
		p, err := s.GetPlayer(ctx, tok)
		if err != nil || p == nil {
			t.Fatalf("GetPlayer(%s) = %v, %v", tok, p, err)
		}
		if p.Name != want || p.Body != DefaultBody || p.Hair != DefaultHair {
			t.Errorf("%s = %q/%q/%q, want %q/%q/%q", tok, p.Name, p.Body, p.Hair, want, DefaultBody, DefaultHair)
		}
	}
	if _, err := s.DB.ExecContext(ctx, `SELECT 1 FROM link_code`); err == nil {
		t.Error("link_code survived 005")
	}
	// The index exists and bites.
	if err := s.SetPlayerAccount(ctx, "g1", "a3"); !errors.Is(err, ErrNameTaken) {
		t.Errorf("index missing after upgrade: err = %v", err)
	}
}
