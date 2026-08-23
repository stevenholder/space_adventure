package store

import (
	"os"
	"path/filepath"
	"testing"
)

func TestOpenSQLite(t *testing.T) {
	dsn := "sqlite://" + filepath.Join(t.TempDir(), "t.db")

	s, err := Open(dsn)
	if err != nil {
		t.Fatalf("Open(%q) returned error: %v", dsn, err)
	}
	defer s.Close()

	if s.Dialect != "sqlite" {
		t.Errorf("Dialect = %q, want %q", s.Dialect, "sqlite")
	}
	if err := s.DB.Ping(); err != nil {
		t.Errorf("DB.Ping() returned error: %v", err)
	}
	assertPortableSQL(t, s)
}

func TestOpenUnsupportedScheme(t *testing.T) {
	if _, err := Open("mysql://x"); err == nil {
		t.Fatal("Open(\"mysql://x\") returned nil error, want an error")
	}
}

// TestOpenPostgres is the other half of the portability claim. Without it
// `make test-pg` is a no-op green: it starts a Postgres, runs a SQLite-only
// suite against it, and reports success. Portability that is not executed on
// both engines is portability that is already broken
// (docs/ARCHITECTURE.md, "Persistence").
func TestOpenPostgres(t *testing.T) {
	dsn := os.Getenv("TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("TEST_DATABASE_URL not set; run `make test-pg`")
	}

	s, err := Open(dsn)
	if err != nil {
		t.Fatalf("Open(%q) returned error: %v", dsn, err)
	}
	defer s.Close()

	if s.Dialect != "postgres" {
		t.Errorf("Dialect = %q, want %q", s.Dialect, "postgres")
	}
	if err := s.DB.Ping(); err != nil {
		t.Errorf("DB.Ping() returned error: %v", err)
	}

	assertPortableSQL(t, s)
}

// assertPortableSQL runs the portable-SQL subset both engines must accept
// identically (docs/ARCHITECTURE.md, "Persistence"): $1 placeholders, an
// INTEGER round-trip, TEXT for JSON, and ON CONFLICT ... DO UPDATE. Both
// engine tests call it, because a rule only checked on one engine is not a
// portability rule.
func assertPortableSQL(t *testing.T, s *Store) {
	t.Helper()

	// CAST, not a bare SELECT $1: Postgres has no column to infer the
	// parameter's type from, and `::int` is not SQLite syntax. CAST(... AS
	// INTEGER) is the spelling both engines accept.
	var got int
	if err := s.DB.QueryRow("SELECT CAST($1 AS INTEGER)", 42).Scan(&got); err != nil {
		t.Fatalf("placeholder round-trip returned error: %v", err)
	}
	if got != 42 {
		t.Errorf("placeholder round-trip = %d, want 42", got)
	}

	if _, err := s.DB.Exec(`CREATE TABLE IF NOT EXISTS portable_probe (
		k TEXT PRIMARY KEY, v TEXT NOT NULL, n INTEGER NOT NULL)`); err != nil {
		t.Fatalf("CREATE TABLE returned error: %v", err)
	}
	t.Cleanup(func() { _, _ = s.DB.Exec("DROP TABLE portable_probe") })

	const upsert = `INSERT INTO portable_probe (k, v, n) VALUES ($1, $2, $3)
		ON CONFLICT (k) DO UPDATE SET v = $2, n = $3`
	if _, err := s.DB.Exec(upsert, "a", `{"item":"ammo.cell"}`, 1); err != nil {
		t.Fatalf("insert returned error: %v", err)
	}
	if _, err := s.DB.Exec(upsert, "a", `{"item":"weapon.pulse"}`, 2); err != nil {
		t.Fatalf("upsert returned error: %v", err)
	}

	var v string
	var n int
	if err := s.DB.QueryRow("SELECT v, n FROM portable_probe WHERE k = $1", "a").Scan(&v, &n); err != nil {
		t.Fatalf("select returned error: %v", err)
	}
	if v != `{"item":"weapon.pulse"}` || n != 2 {
		t.Errorf("after upsert: v=%q n=%d, want the updated row", v, n)
	}
}
