package store

import (
	"context"
	"encoding/json"
	"fmt"
	"slices"
)

// Structure is one row of the `structure` table (Phase 23, migration 008):
// a bench, forge or chest a character placed in the world. Owner is the
// character's token; OwnerName is the label the world shows. Pos is the
// world-space position, Yaw the facing in radians about the local up.
// Stacks is a chest's contents (empty for a station).
type Structure struct {
	ID        string
	Owner     string
	OwnerName string
	Item      string
	Pos       [3]float64
	Yaw       float64
	PlacedMs  int64
	Stacks    []Stack
}

// Clone is st with its stacks copied, for a write that leaves the lock.
func (st Structure) Clone() Structure {
	st.Stacks = slices.Clone(st.Stacks)
	return st
}

const structureColumns = `id, owner, owner_name, item, x, y, z, yaw, placed_ms, stacks`

// ListStructures returns every placed structure, oldest first (the server
// loads them all at boot).
func (s *Store) ListStructures(ctx context.Context) ([]Structure, error) {
	rows, err := s.DB.QueryContext(ctx, `SELECT `+structureColumns+` FROM structure ORDER BY placed_ms, id`)
	if err != nil {
		return nil, fmt.Errorf("store: listing structures: %w", err)
	}
	defer rows.Close()
	var out []Structure
	for rows.Next() {
		var st Structure
		var stacks string
		if err := rows.Scan(&st.ID, &st.Owner, &st.OwnerName, &st.Item,
			&st.Pos[0], &st.Pos[1], &st.Pos[2], &st.Yaw, &st.PlacedMs, &stacks); err != nil {
			return nil, fmt.Errorf("store: scanning structure: %w", err)
		}
		// Unreadable contents are corrupt, not empty: refusing the boot beats
		// loading an empty chest the next change would save over the real one.
		if err := json.Unmarshal([]byte(stacks), &st.Stacks); err != nil {
			return nil, fmt.Errorf("store: structure %q has unreadable stacks: %w", st.ID, err)
		}
		if st.Stacks == nil {
			st.Stacks = []Stack{}
		}
		out = append(out, st)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("store: listing structures: %w", err)
	}
	return out, nil
}

// PutStructure inserts or updates the row for st.ID (one ON CONFLICT
// statement, both engines, as PutPlayer).
func (s *Store) PutStructure(ctx context.Context, st *Structure) error {
	stacks := st.Stacks
	if stacks == nil {
		stacks = []Stack{}
	}
	raw, err := json.Marshal(stacks)
	if err != nil {
		return fmt.Errorf("store: encoding stacks: %w", err)
	}
	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO structure (`+structureColumns+`)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
		 ON CONFLICT (id) DO UPDATE SET
		   owner = $2, owner_name = $3, item = $4, x = $5, y = $6, z = $7,
		   yaw = $8, placed_ms = $9, stacks = $10`,
		st.ID, st.Owner, st.OwnerName, st.Item, st.Pos[0], st.Pos[1], st.Pos[2],
		st.Yaw, st.PlacedMs, string(raw))
	if err != nil {
		return fmt.Errorf("store: upserting structure: %w", err)
	}
	return nil
}

// DeleteStructure removes one structure. Absent is not an error.
func (s *Store) DeleteStructure(ctx context.Context, id string) error {
	if _, err := s.DB.ExecContext(ctx, `DELETE FROM structure WHERE id = $1`, id); err != nil {
		return fmt.Errorf("store: deleting structure: %w", err)
	}
	return nil
}

// DeleteStructuresByOwner removes every structure a character placed.
func (s *Store) DeleteStructuresByOwner(ctx context.Context, token string) error {
	if _, err := s.DB.ExecContext(ctx, `DELETE FROM structure WHERE owner = $1`, token); err != nil {
		return fmt.Errorf("store: deleting structures: %w", err)
	}
	return nil
}
