package store

import (
	"context"
	"embed"
	"fmt"
	"io/fs"
	"sort"
	"strconv"
	"strings"
	"time"
)

//go:embed migrations/*.sql
var migrationsFS embed.FS

// migration is one parsed migration file, ready to apply.
type migration struct {
	version int
	name    string // base filename actually applied, e.g. "001_player.sql"
	sql     string
}

// Migrate applies every migration newer than the recorded schema version,
// each in its own transaction. Applying twice is a no-op. See
// docs/ARCHITECTURE.md, "Persistence" ("Migrations without a dependency").
func (s *Store) Migrate(ctx context.Context) error {
	if _, err := s.DB.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS schema_version (
  version INTEGER PRIMARY KEY, applied_ms BIGINT NOT NULL)`); err != nil {
		return fmt.Errorf("store: ensuring schema_version table: %w", err)
	}

	var current int
	row := s.DB.QueryRowContext(ctx, `SELECT COALESCE(MAX(version), 0) FROM schema_version`)
	if err := row.Scan(&current); err != nil {
		return fmt.Errorf("store: reading schema version: %w", err)
	}

	migrations, err := s.loadMigrations()
	if err != nil {
		return fmt.Errorf("store: loading migrations: %w", err)
	}

	for _, m := range migrations {
		if m.version <= current {
			continue
		}
		if err := s.applyMigration(ctx, m); err != nil {
			return fmt.Errorf("store: applying migration %s: %w", m.name, err)
		}
	}

	return nil
}

// loadMigrations reads migrations/*.sql, resolves .postgres.sql overrides
// for s.Dialect, and returns them sorted by parsed numeric version.
func (s *Store) loadMigrations() ([]migration, error) {
	entries, err := fs.ReadDir(migrationsFS, "migrations")
	if err != nil {
		return nil, err
	}

	base := map[int]string{}       // version -> base filename
	pgOverride := map[int]string{} // version -> postgres override filename

	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".sql") {
			continue
		}
		if strings.HasSuffix(name, ".postgres.sql") {
			prefix := strings.TrimSuffix(name, ".postgres.sql")
			version, ok := parseVersion(prefix)
			if !ok {
				continue
			}
			pgOverride[version] = name
			continue
		}
		prefix := strings.TrimSuffix(name, ".sql")
		version, ok := parseVersion(prefix)
		if !ok {
			continue
		}
		base[version] = name
	}

	migrations := make([]migration, 0, len(base))
	for version, baseName := range base {
		name := baseName
		if s.Dialect == "postgres" {
			if override, ok := pgOverride[version]; ok {
				name = override
			}
		}
		content, err := migrationsFS.ReadFile("migrations/" + name)
		if err != nil {
			return nil, err
		}
		migrations = append(migrations, migration{version: version, name: name, sql: string(content)})
	}

	sort.Slice(migrations, func(i, j int) bool { return migrations[i].version < migrations[j].version })
	return migrations, nil
}

// parseVersion extracts the leading zero-padded numeric prefix from a
// migration base filename like "001_player" (no extension).
func parseVersion(prefix string) (int, bool) {
	idx := strings.Index(prefix, "_")
	if idx < 0 {
		return 0, false
	}
	n, err := strconv.Atoi(prefix[:idx])
	if err != nil {
		return 0, false
	}
	return n, true
}

func (s *Store) applyMigration(ctx context.Context, m migration) error {
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("beginning transaction: %w", err)
	}
	defer tx.Rollback() //nolint:errcheck // no-op once committed

	if _, err := tx.ExecContext(ctx, m.sql); err != nil {
		return fmt.Errorf("executing: %w", err)
	}

	appliedMs := time.Now().UnixMilli()
	if _, err := tx.ExecContext(ctx,
		`INSERT INTO schema_version (version, applied_ms) VALUES ($1, $2)`,
		m.version, appliedMs,
	); err != nil {
		return fmt.Errorf("recording schema_version: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return fmt.Errorf("committing: %w", err)
	}

	return nil
}
