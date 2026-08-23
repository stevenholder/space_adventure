// Package store is the game's persistence layer: one query set, one set of
// statements, dialect-aware only in Open. See docs/ARCHITECTURE.md,
// "Persistence".
package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	_ "github.com/jackc/pgx/v5/stdlib"
	_ "modernc.org/sqlite"
)

// ErrUnsupportedScheme is wrapped into the error Open returns when dsn's
// scheme is not "sqlite", "postgres" or "postgresql".
var ErrUnsupportedScheme = errors.New("store: unsupported DATABASE_URL scheme")

// Store is a handle on the game database. There is one query set and one set
// of statements; the only dialect-aware code is Open.
type Store struct {
	DB      *sql.DB
	Dialect string // "sqlite" or "postgres"
}

// Open picks the SQL driver from dsn's scheme and returns a configured
// *Store. See docs/ARCHITECTURE.md, "Persistence", for the DSN rules.
func Open(dsn string) (*Store, error) {
	if dsn == "" {
		dsn = "sqlite://./data/world.db"
	}

	switch {
	case strings.HasPrefix(dsn, "sqlite://"):
		return openSQLite(dsn)
	case strings.HasPrefix(dsn, "postgres://"), strings.HasPrefix(dsn, "postgresql://"):
		return openPostgres(dsn)
	default:
		return nil, fmt.Errorf("%w: %q", ErrUnsupportedScheme, dsn)
	}
}

func openSQLite(dsn string) (*Store, error) {
	path := strings.TrimPrefix(dsn, "sqlite://")

	if dir := filepath.Dir(path); dir != "." {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return nil, fmt.Errorf("store: creating sqlite data dir: %w", err)
		}
	}

	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, fmt.Errorf("store: opening sqlite: %w", err)
	}
	db.SetMaxOpenConns(1)

	for _, pragma := range []string{
		"PRAGMA journal_mode=WAL",
		"PRAGMA busy_timeout=5000",
		"PRAGMA foreign_keys=ON",
	} {
		if _, err := db.Exec(pragma); err != nil {
			db.Close()
			return nil, fmt.Errorf("store: applying %q: %w", pragma, err)
		}
	}

	if err := ping(db); err != nil {
		db.Close()
		return nil, fmt.Errorf("store: pinging sqlite: %w", err)
	}

	return &Store{DB: db, Dialect: "sqlite"}, nil
}

func openPostgres(dsn string) (*Store, error) {
	db, err := sql.Open("pgx", dsn)
	if err != nil {
		return nil, fmt.Errorf("store: opening postgres: %w", err)
	}
	db.SetMaxOpenConns(10)
	db.SetMaxIdleConns(5)
	db.SetConnMaxLifetime(30 * time.Minute)

	if err := ping(db); err != nil {
		db.Close()
		return nil, fmt.Errorf("store: pinging postgres: %w", err)
	}

	return &Store{DB: db, Dialect: "postgres"}, nil
}

func ping(db *sql.DB) error {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	return db.PingContext(ctx)
}

// Close closes the underlying database handle.
func (s *Store) Close() error {
	return s.DB.Close()
}
