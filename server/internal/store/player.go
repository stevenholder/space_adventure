package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"maps"
	"slices"
	"time"
)

// Stack is one inventory slot: an item id and how many of it.
type Stack struct {
	Item string `json:"item"`
	Qty  int    `json:"qty"`
}

// Player is one row of the `player` table (schema pinned in
// docs/ARCHITECTURE.md, "The `player` table").
//
// Token is a BEARER STRING, not authentication: whoever holds it is this
// player (docs/PROTOCOL.md, "Identity token"). It exists so a reconnect
// restores your own progress, and so the seam real accounts slot into exists
// before there is anything worth stealing.
type Player struct {
	Token     string
	Name      string
	Credits   int64
	Inventory []Stack
	Equipped  map[string]string
	Pos       [3]float64
	// Missions is per-mission progress keyed by mission id (Phase 10).
	Missions map[string]*MissionState
	// Skills is Phase 11's sheet: XP per skill id, and the POIs this
	// player has permanently discovered (Recon pays each exactly once).
	Skills SkillsState
	CreatedMs int64
	UpdatedMs int64
}

// Clone is p with every map, slice and mission copied, so the copy can be
// read with no lock held while the original keeps changing.
func (p Player) Clone() Player {
	p.Inventory = slices.Clone(p.Inventory)
	p.Equipped = maps.Clone(p.Equipped)
	if p.Missions != nil {
		ms := make(map[string]*MissionState, len(p.Missions))
		for k, m := range p.Missions {
			if m != nil {
				c := *m
				m = &c
			}
			ms[k] = m
		}
		p.Missions = ms
	}
	p.Skills.XP = maps.Clone(p.Skills.XP)
	p.Skills.Discovered = slices.Clone(p.Skills.Discovered)
	return p
}

// SkillsState is the whole skill sheet.
type SkillsState struct {
	XP         map[string]int64 `json:"xp,omitempty"`
	Discovered []string         `json:"discovered,omitempty"`
}

// MissionState is one mission's progress for one player.
type MissionState struct {
	Active bool `json:"active"`
	Count  int  `json:"count"`
	Done   int  `json:"done"`
}

const playerColumns = `token, name, credits, inventory, equipped, missions, skills, pos_x, pos_y, pos_z, created_ms, updated_ms`

// GetPlayer returns the row for token, or (nil, nil) when there is none.
// Absence is not an error: a first-time token is the normal case, and making
// the caller distinguish sql.ErrNoRows from a real failure is how a database
// outage gets silently treated as "new player" and wipes someone's progress.
func (s *Store) GetPlayer(ctx context.Context, token string) (*Player, error) {
	row := s.DB.QueryRowContext(ctx,
		`SELECT `+playerColumns+` FROM player WHERE token = $1`, token)

	var p Player
	var inventory, equipped, missions, skillsCol string
	err := row.Scan(&p.Token, &p.Name, &p.Credits, &inventory, &equipped, &missions, &skillsCol,
		&p.Pos[0], &p.Pos[1], &p.Pos[2], &p.CreatedMs, &p.UpdatedMs)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("store: selecting player: %w", err)
	}

	// A row whose JSON will not parse is corrupt, not empty. Returning an
	// error keeps the session ephemeral instead of handing the player a blank
	// inventory and then overwriting the real one on the next save.
	if err := json.Unmarshal([]byte(inventory), &p.Inventory); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable inventory: %w", token, err)
	}
	if err := json.Unmarshal([]byte(equipped), &p.Equipped); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable equipped: %w", token, err)
	}
	if err := json.Unmarshal([]byte(missions), &p.Missions); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable missions: %w", token, err)
	}
	if err := json.Unmarshal([]byte(skillsCol), &p.Skills); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable skills: %w", token, err)
	}
	if p.Inventory == nil {
		p.Inventory = []Stack{}
	}
	if p.Equipped == nil {
		p.Equipped = map[string]string{}
	}
	if p.Missions == nil {
		p.Missions = map[string]*MissionState{}
	}
	if p.Skills.XP == nil {
		p.Skills.XP = map[string]int64{}
	}
	return &p, nil
}

// PutPlayer inserts or updates the row for p.Token, stamping UpdatedMs.
//
// One statement, ON CONFLICT DO UPDATE — supported by both engines
// (SQLite >= 3.24 and Postgres), so there is no dialect branch here. Never
// called from the tick loop: see docs/ARCHITECTURE.md, "Persistence".
func (s *Store) PutPlayer(ctx context.Context, p *Player) error {
	inventory, err := json.Marshal(p.Inventory)
	if err != nil {
		return fmt.Errorf("store: encoding inventory: %w", err)
	}
	equipped, err := json.Marshal(p.Equipped)
	if err != nil {
		return fmt.Errorf("store: encoding equipped: %w", err)
	}
	missions, err := json.Marshal(p.Missions)
	if err != nil {
		return fmt.Errorf("store: encoding missions: %w", err)
	}
	if p.Missions == nil {
		missions = []byte("{}")
	}
	skillsCol, err := json.Marshal(p.Skills)
	if err != nil {
		return fmt.Errorf("store: encoding skills: %w", err)
	}

	now := time.Now().UnixMilli()
	if p.CreatedMs == 0 {
		p.CreatedMs = now
	}
	p.UpdatedMs = now

	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO player (`+playerColumns+`)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
		 ON CONFLICT (token) DO UPDATE SET
		   name = $2, credits = $3, inventory = $4, equipped = $5,
		   missions = $6, skills = $7, pos_x = $8, pos_y = $9, pos_z = $10, updated_ms = $12`,
		p.Token, p.Name, p.Credits, string(inventory), string(equipped), string(missions), string(skillsCol),
		p.Pos[0], p.Pos[1], p.Pos[2], p.CreatedMs, p.UpdatedMs)
	if err != nil {
		return fmt.Errorf("store: upserting player: %w", err)
	}
	return nil
}
