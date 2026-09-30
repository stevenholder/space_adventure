package sim

import (
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
)

// TestDeathSpillsMaterials pins C124's rule half: only materials leave the
// bag, one entry per stack, everything else and the worn map stay.
func TestDeathSpillsMaterials(t *testing.T) {
	reg := artisanReg()
	reg.Items["mat.ore.iron"] = defs.Item{ID: "mat.ore.iron", Kind: "material", StackMax: 50, Value: 6}
	reg.Items["mat.scrap"] = defs.Item{ID: "mat.scrap", Kind: "material", StackMax: 50, Value: 8}
	p := &store.Player{Credits: 500,
		Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 7}, {Item: "ammo.cell", Qty: 90}, {Item: "tool.drill", Qty: 1}, {Item: "mat.scrap", Qty: 3}},
		Equipped:  map[string]string{"tool": "tool.drill"}}
	got := SpillMaterials(p, reg)
	if len(got) != 2 || got[0].Item != "mat.ore.iron" || got[0].Qty != 7 || got[1].Item != "mat.scrap" || got[1].Qty != 3 {
		t.Fatalf("spilled %v", got)
	}
	if CountItem(p, "ammo.cell") != 90 || CountItem(p, "tool.drill") != 1 || p.Credits != 500 || p.Equipped["tool"] != "tool.drill" || len(p.Inventory) != 2 {
		t.Fatalf("bag after spill: %v credits=%d worn=%v", p.Inventory, p.Credits, p.Equipped)
	}
	if got := SpillMaterials(p, reg); len(got) != 0 {
		t.Fatalf("second spill found %v", got)
	}
}

// TestLootExpires pins the other half of C124: a drop lasts loot_lifetime
// ticks and then leaves the world — which never happened before Phase 12
// registered StepLoot.
func TestLootExpires(t *testing.T) {
	w := NewWorld()
	next := uint32(100)
	var events []protocol.Event
	id := DropStack(w, "mat.ore.iron", 4, [3]float64{0, 150, 0}, func() uint32 { next++; return next }, StepCtx{Events: &events})
	if len(events) != 1 || events[0].EventID != protocol.EventLootDropped || events[0].EntityID != id {
		t.Fatalf("events %v", events)
	}
	dt := 1.0 / TickHz
	for i := 0; i < LootLifetimeTicks-1; i++ {
		w.Step(dt, StepCtx{World: w})
	}
	if w.Ents[id] == nil {
		t.Fatal("drop expired early")
	}
	w.Step(dt, StepCtx{World: w})
	if w.Ents[id] != nil {
		t.Fatal("drop did not expire at loot_lifetime")
	}
}
