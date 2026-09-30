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

// TestCraft pins C123: cells consume 2 ore + 1 scrap for 30 cells, a
// shortage refuses with nothing consumed, the plate is locked below
// Engineering 5, qty 3 is three crafts or none, and the mk2 recipe eats the
// worn drill and clears its slot.
func TestCraft(t *testing.T) {
	reg := artisanReg()
	p := &store.Player{Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 10}, {Item: "mat.scrap", Qty: 3}}}

	made, err := Craft(p, reg.Recipes["recipe.cells"], 1, 1, reg, 0)
	if err != nil || made != 30 || CountItem(p, "ammo.cell") != 30 || CountItem(p, "mat.ore.iron") != 8 || CountItem(p, "mat.scrap") != 2 {
		t.Fatalf("cells: made=%d err=%v inv=%v", made, err, p.Inventory)
	}
	if _, err := Craft(p, reg.Recipes["recipe.cells"], 3, 1, reg, 0); reason(err) != ReasonMissingMaterials || CountItem(p, "mat.ore.iron") != 8 {
		t.Fatalf("qty 3 with 2 scrap: %q, ore=%d", reason(err), CountItem(p, "mat.ore.iron"))
	}
	if _, err := Craft(p, reg.Recipes["recipe.cells"], 2, 1, reg, 0); err != nil || CountItem(p, "ammo.cell") != 90 {
		t.Fatalf("qty 2: err=%v cells=%d", err, CountItem(p, "ammo.cell"))
	}
	p.Inventory = []store.Stack{{Item: "mat.ore.iron", Qty: 6}, {Item: "mat.scrap", Qty: 3}}
	if _, err := Craft(p, reg.Recipes["recipe.plate.iron"], 1, 4, reg, 0); reason(err) != ReasonLocked {
		t.Fatalf("plate at 4: %q", reason(err))
	}
	// A bonus unit on a stack_max 1 output is dropped, not refused.
	if made, err := Craft(p, reg.Recipes["recipe.plate.iron"], 1, 5, reg, 1); err != nil || made != 1 {
		t.Fatalf("plate at 5 with bonus: made=%d err=%v", made, err)
	}
	if _, err := Craft(p, reg.Recipes["recipe.plate.iron"], 1, 5, reg, 0); reason(err) != ReasonMissingMaterials {
		t.Fatalf("plate twice: %q", reason(err))
	}
	// mk2 eats the worn drill.
	p = &store.Player{Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 8}, {Item: "mat.scrap", Qty: 6}, {Item: "tool.drill", Qty: 1}},
		Equipped: map[string]string{"tool": "tool.drill"}}
	if _, err := Craft(p, reg.Recipes["recipe.drill.mk2"], 1, 10, reg, 0); err != nil {
		t.Fatalf("mk2: %v", err)
	}
	if CountItem(p, "tool.drill") != 0 || CountItem(p, "tool.drill.mk2") != 1 || p.Equipped["tool"] != "" {
		t.Fatalf("mk2 aftermath: inv=%v equipped=%v", p.Inventory, p.Equipped)
	}
}
