package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"maps"
	"slices"
	"strings"
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
	// AccountID owns the row, "" for a guest (NULL). Read-only to
	// PutPlayer on an existing row: SetPlayerAccount owns it.
	AccountID string
	// Body is the character's model + gender id; "" saves as char.player.
	Body string
	// Hair is the character's hair piece id (Phase 17); "" saves as hair.none.
	Hair      string
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

// DefaultBody is every player's body until they pick another (Phase 16).
const DefaultBody = "char.player"

// DefaultHair is no hair (Phase 17, migration 006's column default).
const DefaultHair = "hair.none"

// ErrNameTaken is the player_character_name index refusing a character name
// another account-owned row already has (case-insensitive).
var ErrNameTaken = errors.New("store: character name taken")

const playerColumns = `token, name, credits, inventory, equipped, missions, skills, pos_x, pos_y, pos_z, created_ms, updated_ms, body, hair`

// playerSelect reads account_id last; NULL (a guest) scans as "".
const playerSelect = `SELECT ` + playerColumns + `, COALESCE(account_id, '') FROM player`

// GetPlayer returns the row for token, or (nil, nil) when there is none.
// Absence is not an error: a first-time token is the normal case, and making
// the caller distinguish sql.ErrNoRows from a real failure is how a database
// outage gets silently treated as "new player" and wipes someone's progress.
func (s *Store) GetPlayer(ctx context.Context, token string) (*Player, error) {
	p, err := scanPlayer(s.DB.QueryRowContext(ctx, playerSelect+` WHERE token = $1`, token))
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	return p, err
}

// scanner is *sql.Row or *sql.Rows.
type scanner interface{ Scan(dest ...any) error }

// scanPlayer reads one playerSelect row. sql.ErrNoRows comes back unwrapped
// so GetPlayer can tell absence from failure.
func scanPlayer(row scanner) (*Player, error) {
	var p Player
	var inventory, equipped, missions, skillsCol string
	err := row.Scan(&p.Token, &p.Name, &p.Credits, &inventory, &equipped, &missions, &skillsCol,
		&p.Pos[0], &p.Pos[1], &p.Pos[2], &p.CreatedMs, &p.UpdatedMs, &p.Body, &p.Hair, &p.AccountID)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, err
	}
	if err != nil {
		return nil, fmt.Errorf("store: selecting player: %w", err)
	}

	// A row whose JSON will not parse is corrupt, not empty. Returning an
	// error keeps the session ephemeral instead of handing the player a blank
	// inventory and then overwriting the real one on the next save.
	if err := json.Unmarshal([]byte(inventory), &p.Inventory); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable inventory: %w", p.Token, err)
	}
	if err := json.Unmarshal([]byte(equipped), &p.Equipped); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable equipped: %w", p.Token, err)
	}
	if err := json.Unmarshal([]byte(missions), &p.Missions); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable missions: %w", p.Token, err)
	}
	if err := json.Unmarshal([]byte(skillsCol), &p.Skills); err != nil {
		return nil, fmt.Errorf("store: player %q has unreadable skills: %w", p.Token, err)
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
	if p.Body == "" {
		p.Body = DefaultBody
	}
	if p.Hair == "" {
		p.Hair = DefaultHair
	}

	// account_id is written on INSERT only, so a new character is created
	// owned in one statement (the name index checks it there); the update
	// arm leaves it alone, so a game save never orphans or re-homes a row.
	_, err = s.DB.ExecContext(ctx,
		`INSERT INTO player (`+playerColumns+`, account_id)
		 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, NULLIF($15, ''))
		 ON CONFLICT (token) DO UPDATE SET
		   name = $2, credits = $3, inventory = $4, equipped = $5,
		   missions = $6, skills = $7, pos_x = $8, pos_y = $9, pos_z = $10, updated_ms = $12,
		   body = $13, hair = $14`,
		p.Token, p.Name, p.Credits, string(inventory), string(equipped), string(missions), string(skillsCol),
		p.Pos[0], p.Pos[1], p.Pos[2], p.CreatedMs, p.UpdatedMs, p.Body, p.Hair, p.AccountID)
	if err != nil {
		if isUnique(err) {
			return ErrNameTaken
		}
		return fmt.Errorf("store: upserting player: %w", err)
	}
	return nil
}

// isUnique reports a unique-constraint violation. Both engines say "unique"
// somewhere in the message; matching the text beats importing two driver
// error types.
func isUnique(err error) bool {
	return strings.Contains(strings.ToLower(err.Error()), "unique")
}
