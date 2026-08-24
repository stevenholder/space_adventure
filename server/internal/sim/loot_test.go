package sim

import (
	"math/rand"
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
)

func lootTestRegistry() *defs.Registry {
	return &defs.Registry{
		InvSlots: 2,
		Items: map[string]defs.Item{
			"ammo.cell": {ID: "ammo.cell", StackMax: 300},
		},
		// Loot tables come from the registry now, not a parser inside this
		// package — one source over server/data.
		Loot: map[string][]defs.LootEntry{
			"loot.grunt": {{Item: "ammo.cell", Qty: 15, Chance: 1.0}},
		},
	}
}

func idCounter() func() uint32 {
	next := uint32(0)
	return func() uint32 {
		next++
		return next
	}
}

// A chance-1.0 table always drops (server/data/loot.json "loot.grunt").
func TestDropLoot_ChanceOneAlwaysDrops(t *testing.T) {
	w := NewWorld()
	reg := lootTestRegistry()
	rng := rand.New(rand.NewSource(1))

	DropLoot(w, reg, "loot.grunt", [3]float64{1, 2, 3}, idCounter(), rng, StepCtx{})

	if len(w.Ents) != 1 {
		t.Fatalf("len(w.Ents) = %d, want 1", len(w.Ents))
	}
	var e *Ent
	for _, ent := range w.Ents {
		e = ent
	}
	if e.Kind != EntityKind(protocol.EntityTypeLoot) {
		t.Fatalf("Kind = %v, want EntityTypeLoot", e.Kind)
	}
	state, ok := e.Data.(*LootState)
	if !ok {
		t.Fatalf("Data = %T, want *LootState", e.Data)
	}
	if state.Item != "ammo.cell" || state.Qty != 15 {
		t.Fatalf("state = %+v, want {ammo.cell 15 ...}", state)
	}
	if state.LifeTicks != LootLifetimeTicks {
		t.Fatalf("LifeTicks = %d, want %d", state.LifeTicks, LootLifetimeTicks)
	}
}

// Two callers over one drop on the same tick: exactly one succeeds.
func TestTryPickup_SingleGrant(t *testing.T) {
	w := NewWorld()
	reg := lootTestRegistry()
	e := &Ent{
		ID:   1,
		Kind: EntityKind(protocol.EntityTypeLoot),
		Pos:  [3]float64{0, 0, 0},
		Data: &LootState{Item: "ammo.cell", Qty: 15, LifeTicks: LootLifetimeTicks},
	}
	w.Add(e)

	p1 := &store.Player{}
	p2 := &store.Player{}
	pos := [3]float64{0.1, 0, 0}

	got1 := TryPickup(w, e, pos, p1, reg)
	got2 := TryPickup(w, e, pos, p2, reg)

	if !got1 {
		t.Fatalf("first TryPickup = false, want true")
	}
	if got2 {
		t.Fatalf("second TryPickup = true, want false")
	}
	if len(p1.Inventory) != 1 || p1.Inventory[0] != (store.Stack{Item: "ammo.cell", Qty: 15}) {
		t.Fatalf("p1.Inventory = %+v, want [{ammo.cell 15}]", p1.Inventory)
	}
	if len(p2.Inventory) != 0 {
		t.Fatalf("p2.Inventory = %+v, want empty", p2.Inventory)
	}
}

// A pickup beyond LootPickupRadius fails and leaves the drop claimable.
func TestTryPickup_TooFar(t *testing.T) {
	w := NewWorld()
	reg := lootTestRegistry()
	e := &Ent{
		ID:   1,
		Kind: EntityKind(protocol.EntityTypeLoot),
		Pos:  [3]float64{0, 0, 0},
		Data: &LootState{Item: "ammo.cell", Qty: 15, LifeTicks: LootLifetimeTicks},
	}
	w.Add(e)
	p := &store.Player{}

	if got := TryPickup(w, e, [3]float64{2, 0, 0}, p, reg); got {
		t.Fatalf("TryPickup at 2m = true, want false")
	}
	if len(p.Inventory) != 0 {
		t.Fatalf("p.Inventory = %+v, want empty", p.Inventory)
	}
	if e.Data.(*LootState).Claimed {
		t.Fatalf("Claimed = true, want false after a failed pickup")
	}
	if _, ok := w.Ents[1]; !ok {
		t.Fatalf("drop removed from world after a failed pickup")
	}
}

// A drop that doesn't fit is left on the ground, not destroyed, and stays
// claimable by someone else.
func TestTryPickup_NoSpaceLeavesDropClaimable(t *testing.T) {
	w := NewWorld()
	reg := lootTestRegistry()
	reg.InvSlots = 1
	e := &Ent{
		ID:   1,
		Kind: EntityKind(protocol.EntityTypeLoot),
		Pos:  [3]float64{0, 0, 0},
		Data: &LootState{Item: "ammo.cell", Qty: 15, LifeTicks: LootLifetimeTicks},
	}
	w.Add(e)

	// pFull already occupies the sole inventory slot with a different item,
	// so AddItem(ammo.cell) has nowhere to go: no_space.
	pFull := &store.Player{Inventory: []store.Stack{{Item: "other.junk", Qty: 1}}}
	pos := [3]float64{0, 0, 0}

	if got := TryPickup(w, e, pos, pFull, reg); got {
		t.Fatalf("TryPickup with full inventory = true, want false")
	}
	if e.Data.(*LootState).Claimed {
		t.Fatalf("Claimed = true, want false after a no_space refusal")
	}
	if _, ok := w.Ents[1]; !ok {
		t.Fatalf("drop removed from world after a no_space refusal, want left on the ground")
	}

	// A second player with room still gets it.
	pRoom := &store.Player{}
	if got := TryPickup(w, e, pos, pRoom, reg); !got {
		t.Fatalf("TryPickup for a player with room = false, want true")
	}
	if len(pRoom.Inventory) != 1 || pRoom.Inventory[0].Qty != 15 {
		t.Fatalf("pRoom.Inventory = %+v, want [{ammo.cell 15}]", pRoom.Inventory)
	}
}

// A drop expires after LootLifetimeTicks ticks.
func TestStepLoot_Expires(t *testing.T) {
	w := NewWorld()
	e := &Ent{
		ID:   1,
		Kind: EntityKind(protocol.EntityTypeLoot),
		Pos:  [3]float64{0, 0, 0},
		Data: &LootState{Item: "ammo.cell", Qty: 15, LifeTicks: LootLifetimeTicks},
	}
	w.Add(e)
	ctx := StepCtx{World: w}

	for i := 0; i < LootLifetimeTicks-1; i++ {
		StepLoot(e, DT, ctx)
	}
	if _, ok := w.Ents[1]; !ok {
		t.Fatalf("drop removed before LootLifetimeTicks ticks elapsed")
	}

	StepLoot(e, DT, ctx)
	if _, ok := w.Ents[1]; ok {
		t.Fatalf("drop still present after LootLifetimeTicks ticks")
	}
}
