// NPC loot drops (docs/GDD.md, "Loot"): a killed NPC drops one entity per
// its loot table at its own position; a drop is picked up by walking within
// loot_pickup_radius (1.5 m) — no prompt, no keypress — and despawns after
// loot_lifetime (120 s) if nobody takes it.
//
// Pickup is server-authoritative and SINGLE-GRANT: the Claimed flag on
// LootState is checked-and-set before AddItem runs, so two players walking
// over one drop on the same tick cannot both succeed — an item granted
// twice is an item created. Loot that will not fit in the inventory is left
// on the ground, not destroyed: Claimed is cleared back to false so a later
// (or another) pickup can still claim it.
package sim

import (
	"encoding/json"
	"math/rand"

	"space-adventure/server/data"
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
)

const (
	// LootPickupRadius is loot_pickup_radius (docs/GDD.md, "Loot"), in
	// metres.
	LootPickupRadius = 1.5

	// LootLifetimeTicks is loot_lifetime (120 s) in whole ticks at the sim's
	// fixed 20 Hz rate, not accumulated seconds — same reasoning as
	// TargetRespawnTicks in target.go: exact on both ends by construction.
	LootLifetimeTicks = int(120 * TickHz)
)

// LootState is the loot-kind's per-entity state, stored in Ent.Data.
type LootState struct {
	Item      string
	Qty       int
	LifeTicks int
	Claimed   bool
}

// lootEntry mirrors one row of a table in server/data/loot.json.
type lootEntry struct {
	Item   string  `json:"item"`
	Qty    int     `json:"qty"`
	Chance float64 `json:"chance"`
}

// lootFile mirrors server/data/loot.json as a whole.
type lootFile struct {
	Tables map[string][]lootEntry `json:"tables"`
}

// lootTables reads and parses server/data/loot.json from the embedded data
// package on every call. defs.Registry does not expose loot tables yet, so
// this reads the same embedded server/data content defs.Load itself reads,
// rather than reimplementing or forking that data.
func lootTables() (map[string][]lootEntry, error) {
	raw, err := data.FS.ReadFile("loot.json")
	if err != nil {
		return nil, err
	}
	var f lootFile
	if err := json.Unmarshal(raw, &f); err != nil {
		return nil, err
	}
	return f.Tables, nil
}

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeLoot), StepLoot)
}

// DropLoot rolls table (server/data/loot.json) and adds one loot entity per
// entry that rolls successfully to w, at pos. Each entry rolls
// independently against rng; a chance of 1.0 always drops. reg validates
// each entry names a known item — an entry for an item the registry doesn't
// have is skipped rather than handed to a player unequippable. nextID
// allocates the new entity's id; one EventLootDropped is emitted per drop
// via ctx.Events (nil-safe, like every other step/spawn path in this
// package).
func DropLoot(w *World, reg *defs.Registry, table string, pos [3]float64,
	nextID func() uint32, rng *rand.Rand, ctx StepCtx) {
	if w == nil || nextID == nil || rng == nil {
		return
	}
	tables, err := lootTables()
	if err != nil {
		return
	}
	for _, entry := range tables[table] {
		if reg != nil {
			if _, ok := reg.Items[entry.Item]; !ok {
				continue
			}
		}
		if rng.Float64() >= entry.Chance {
			continue
		}
		id := nextID()
		w.Add(&Ent{
			ID:   id,
			Kind: EntityKind(protocol.EntityTypeLoot),
			Pos:  pos,
			Def:  entry.Item,
			Data: &LootState{
				Item:      entry.Item,
				Qty:       entry.Qty,
				LifeTicks: LootLifetimeTicks,
			},
		})
		if ctx.Events != nil {
			*ctx.Events = append(*ctx.Events, protocol.Event{
				EntityID: id,
				EventID:  protocol.EventLootDropped,
			})
		}
	}
}

// StepLoot ages one drop out. LifeTicks counts down once per tick
// (integer, not accumulated dt — see LootLifetimeTicks); when it reaches 0
// the drop is removed from the world.
func StepLoot(e *Ent, dt float64, ctx StepCtx) {
	state, ok := e.Data.(*LootState)
	if !ok {
		return
	}
	state.LifeTicks--
	if state.LifeTicks <= 0 && ctx.World != nil {
		ctx.World.Remove(e.ID)
	}
}

// TryPickup grants e's drop to p if playerPos is within LootPickupRadius of
// e.Pos and nobody has claimed it yet. Returns true only for the caller
// that actually gets it.
//
// Claimed is checked-and-set BEFORE AddItem runs so a second caller on the
// same tick — before the first caller's grant has removed the entity from
// the world — cannot also succeed: this is what makes pickup single-grant.
// If AddItem then refuses no_space, Claimed is cleared back to false and
// the drop is left on the ground, still claimable by someone else. On
// success the drop entity is removed from the world (it has been granted;
// it vanishes for every player).
func TryPickup(w *World, e *Ent, playerPos [3]float64,
	p *store.Player, reg *defs.Registry) bool {
	if e == nil || p == nil {
		return false
	}
	state, ok := e.Data.(*LootState)
	if !ok || state.Claimed {
		return false
	}

	dx := e.Pos[0] - playerPos[0]
	dy := e.Pos[1] - playerPos[1]
	dz := e.Pos[2] - playerPos[2]
	if dx*dx+dy*dy+dz*dz > LootPickupRadius*LootPickupRadius {
		return false
	}

	state.Claimed = true
	if err := AddItem(p, state.Item, state.Qty, reg); err != nil {
		state.Claimed = false
		return false
	}

	if w != nil {
		w.Remove(e.ID)
	}
	return true
}
