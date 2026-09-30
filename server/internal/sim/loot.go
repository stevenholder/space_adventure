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
	"math/rand"

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

// extra is the chance of one additional pass over the table (Phase 11
// Scavenging efficacy, plus the Recon synergy inside a discovered POI);
// 0 consumes no extra randomness, so an untrained kill rolls as before.
func DropLoot(w *World, reg *defs.Registry, table string, pos [3]float64,
	nextID func() uint32, rng *rand.Rand, ctx StepCtx, extra float64) {
	if w == nil || nextID == nil || rng == nil {
		return
	}
	if reg == nil {
		return
	}
	// The registry is the one parser over server/data. This file briefly had
	// its own copy of the loot.json schema, which is a second thing to update
	// when the schema moves and a silent divergence when someone forgets.
	roll := func() {
		for _, entry := range reg.Loot[table] {
			{
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
	roll()
	if extra > 0 && rng.Float64() < extra {
		roll()
	}
}

func init() {
	// Drops have carried LifeTicks since Phase 3 and never expired: the
	// step was written but never registered. Phase 12 makes the lifetime
	// real (GDD "Death spills the raw").
	RegisterStep(EntityKind(protocol.EntityTypeLoot), StepLoot)
}

// DropStack places one loot entity holding qty of item at pos and emits
// loot_dropped — the death spill's unit, and what DropLoot's roll builds
// on. Returns the new entity id.
func DropStack(w *World, item string, qty int, pos [3]float64, nextID func() uint32, ctx StepCtx) uint32 {
	id := nextID()
	w.Add(&Ent{
		ID:   id,
		Kind: EntityKind(protocol.EntityTypeLoot),
		Pos:  pos,
		Def:  item,
		Data: &LootState{Item: item, Qty: qty, LifeTicks: LootLifetimeTicks},
	})
	if ctx.Events != nil {
		*ctx.Events = append(*ctx.Events, protocol.Event{EntityID: id, EventID: protocol.EventLootDropped})
	}
	return id
}

// RollLoot rolls a table once and returns what dropped, for grants that go
// straight into a bag (a node yield) rather than onto the ground.
func RollLoot(reg *defs.Registry, table string, rng *rand.Rand) []defs.ItemQty {
	var out []defs.ItemQty
	for _, entry := range reg.Loot[table] {
		if _, ok := reg.Items[entry.Item]; !ok {
			continue
		}
		if rng.Float64() >= entry.Chance {
			continue
		}
		out = append(out, defs.ItemQty{Item: entry.Item, Qty: entry.Qty})
	}
	return out
}

// CanFit reports whether every entry of a loot table would fit in p's bag
// at once, chance ignored — the pre-channel no_space check (GDD "The
// channel"), so a bar never fills for nothing.
func CanFit(p *store.Player, reg *defs.Registry, table string) bool {
	scratch := store.Player{Inventory: p.Inventory}
	for _, entry := range reg.Loot[table] {
		if AddItem(&scratch, entry.Item, entry.Qty, reg) != nil {
			return false
		}
	}
	return true
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
