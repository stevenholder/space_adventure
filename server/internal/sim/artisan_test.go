package sim

import (
	"errors"
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

func artisanReg() *defs.Registry {
	return &defs.Registry{
		InvSlots:   20,
		EquipSlots: []string{"chest", "tool"},
		Items: map[string]defs.Item{
			"mat.ore.iron":     {ID: "mat.ore.iron", StackMax: 50, Value: 6},
			"mat.scrap":        {ID: "mat.scrap", StackMax: 50, Value: 8},
			"ammo.cell":        {ID: "ammo.cell", StackMax: 300},
			"armor.plate.iron": {ID: "armor.plate.iron", StackMax: 1, Value: 90, Slot: "chest"},
			"tool.drill":       {ID: "tool.drill", StackMax: 1, Value: 40, Slot: "tool"},
			"tool.drill.mk2":   {ID: "tool.drill.mk2", StackMax: 1, Value: 150, Slot: "tool"},
		},
		Recipes: map[string]defs.Recipe{
			"recipe.cells": {ID: "recipe.cells", Level: 1,
				Inputs: []defs.ItemQty{{Item: "mat.ore.iron", Qty: 2}, {Item: "mat.scrap", Qty: 1}},
				Output: defs.ItemQty{Item: "ammo.cell", Qty: 30}},
			"recipe.plate.iron": {ID: "recipe.plate.iron", Level: 5,
				Inputs: []defs.ItemQty{{Item: "mat.ore.iron", Qty: 6}, {Item: "mat.scrap", Qty: 3}},
				Output: defs.ItemQty{Item: "armor.plate.iron", Qty: 1}},
			"recipe.drill.mk2": {ID: "recipe.drill.mk2", Level: 10,
				Inputs: []defs.ItemQty{{Item: "mat.ore.iron", Qty: 8}, {Item: "mat.scrap", Qty: 6}, {Item: "tool.drill", Qty: 1}},
				Output: defs.ItemQty{Item: "tool.drill.mk2", Qty: 1}},
		},
	}
}

func reason(err error) string {
	var r RefusalError
	if errors.As(err, &r) {
		return r.Reason
	}
	return "<nil>"
}

// TestSellAt pins C122: 4 iron at rate 0.5 → 12 cr, the synergy raises it,
// no value refuses unsellable, a worn item refuses equipped, and a refusal
// leaves the row untouched.
func TestSellAt(t *testing.T) {
	reg := artisanReg()
	shop := defs.NPC{ID: "npc.quartermaster", Kind: "shop"}
	p := &store.Player{Credits: 100, Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 10}, {Item: "ammo.cell", Qty: 5}, {Item: "armor.plate.iron", Qty: 1}},
		Equipped: map[string]string{"chest": "armor.plate.iron"}}

	paid, err := SellAt(p, shop, "mat.ore.iron", 4, reg, 0)
	if err != nil || paid != 12 || p.Credits != 112 || CountItem(p, "mat.ore.iron") != 6 {
		t.Fatalf("sell 4 iron: paid=%d credits=%d left=%d err=%v", paid, p.Credits, CountItem(p, "mat.ore.iron"), err)
	}
	// Scavenging 20 → +1.9% at 0.001/level: floor(6 × 0.5 × 1.019) = 3 still; make it visible at a bigger bonus.
	if got := SellPrice(reg.Items["mat.ore.iron"], 0.5, 0.5); got != 4 {
		t.Fatalf("bonused unit price = %d, want 4", got)
	}
	cases := []struct {
		item, want string
		qty        int
	}{
		{"ammo.cell", ReasonUnsellable, 1},
		{"armor.plate.iron", ReasonEquipped, 1},
		{"mat.ore.iron", ReasonNotOwned, 7},
		{"mat.scrap", ReasonNotOwned, 1},
		{"nope", ReasonUnknownItem, 1},
		{"mat.ore.iron", ReasonBadQty, 0},
	}
	for _, c := range cases {
		before := p.Credits
		if _, err := SellAt(p, shop, c.item, c.qty, reg, 0); reason(err) != c.want || p.Credits != before {
			t.Errorf("sell %s×%d: got %q credits %d→%d, want %q untouched", c.item, c.qty, reason(err), before, p.Credits, c.want)
		}
	}
	if _, err := SellAt(p, defs.NPC{Kind: "bench"}, "mat.ore.iron", 1, reg, 0); reason(err) != ReasonNoStock {
		t.Errorf("bench buys: %q", reason(err))
	}
}

// TestCraftUnits pins the Phase 22 unit moves: CraftCheck refuses locked,
// then missing_materials for one unit, then no_space; TakeUnit is all or
// none and clears a consumed worn tool; LandUnit drops the bonus on a
// stack_max 1 output; ReturnUnit puts a cancelled unit back.
func TestCraftUnits(t *testing.T) {
	reg := artisanReg()
	cells := reg.Recipes["recipe.cells"]
	p := &store.Player{Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 3}, {Item: "mat.scrap", Qty: 1}}}

	if err := CraftCheck(p, cells, 1, reg); err != nil {
		t.Fatalf("check cells: %v", err)
	}
	if err := TakeUnit(p, cells); err != nil || CountItem(p, "mat.ore.iron") != 1 || CountItem(p, "mat.scrap") != 0 {
		t.Fatalf("take: err=%v inv=%v", err, p.Inventory)
	}
	if err := TakeUnit(p, cells); reason(err) != ReasonMissingMaterials || CountItem(p, "mat.ore.iron") != 1 {
		t.Fatalf("second take: %q inv=%v", reason(err), p.Inventory)
	}
	if made, err := LandUnit(p, cells, 1, reg); err != nil || made != 31 || CountItem(p, "ammo.cell") != 31 {
		t.Fatalf("land with bonus: made=%d err=%v", made, err)
	}
	ReturnUnit(p, cells, reg)
	if CountItem(p, "mat.ore.iron") != 3 || CountItem(p, "mat.scrap") != 1 {
		t.Fatalf("return: %v", p.Inventory)
	}

	plate := reg.Recipes["recipe.plate.iron"]
	p = &store.Player{Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 6}, {Item: "mat.scrap", Qty: 3}}}
	if err := CraftCheck(p, plate, 4, reg); reason(err) != ReasonLocked {
		t.Fatalf("plate at 4: %q", reason(err))
	}
	if made, err := LandUnit(p, plate, 1, reg); err != nil || made != 1 {
		t.Fatalf("plate bonus on a stack_max 1: made=%d err=%v", made, err)
	}
	// A full bag: the plate needs a slot of its own once the inputs leave.
	reg.InvSlots = 2
	p.Inventory = []store.Stack{{Item: "mat.ore.iron", Qty: 7}, {Item: "mat.scrap", Qty: 4}}
	if err := CraftCheck(p, plate, 5, reg); reason(err) != ReasonNoSpace {
		t.Fatalf("plate into a full bag: %q", reason(err))
	}
	reg.InvSlots = 20

	// mk2 eats the worn drill.
	mk2 := reg.Recipes["recipe.drill.mk2"]
	p = &store.Player{Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 8}, {Item: "mat.scrap", Qty: 6}, {Item: "tool.drill", Qty: 1}},
		Equipped: map[string]string{"tool": "tool.drill"}}
	if err := TakeUnit(p, mk2); err != nil {
		t.Fatalf("mk2: %v", err)
	}
	if _, err := LandUnit(p, mk2, 0, reg); err != nil || CountItem(p, "tool.drill") != 0 || CountItem(p, "tool.drill.mk2") != 1 || p.Equipped["tool"] != "" {
		t.Fatalf("mk2 aftermath: err=%v inv=%v equipped=%v", err, p.Inventory, p.Equipped)
	}
}
