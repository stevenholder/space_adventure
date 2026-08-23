package store

import (
	"context"
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
	if count != 1 {
		t.Fatalf("schema_version row count = %d, want 1", count)
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
	if count != 1 {
		t.Fatalf("schema_version row count = %d, want 1", count)
	}
}
