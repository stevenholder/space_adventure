// Phase 12 artisan rules on the player row: selling to a shop and crafting
// at the bench (docs/GDD.md "shop_sell", "The workbench and recipes"). Both
// build the new inventory in full before assigning, like Buy — a refusal at
// any step leaves p untouched.
package sim

import (
	"math"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

// Phase 12 refusal reasons.
const (
	ReasonUnsellable       = "unsellable"
	ReasonEquipped         = "equipped"
	ReasonUnknownRecipe    = "unknown_recipe"
	ReasonMissingMaterials = "missing_materials"
	ReasonLocked           = "locked"
)

// SellPrice is what one unit of item fetches: floor(value × rate × (1 +
// bonus)). 0 when the item has no value.
func SellPrice(def defs.Item, rate, bonus float64) int64 {
	if def.Value <= 0 {
		return 0
	}
	return int64(math.Floor(float64(def.Value) * rate * (1 + bonus)))
}

// SellAt validates a shop_sell in the GDD's order — qty, item known, item
// has a value, qty owned, not worn — then shrinks the stacks and grows the
// credits atomically. bonus is the resolved sell_bonus synergy.
func SellAt(p *store.Player, npc defs.NPC, item string, qty int, reg *defs.Registry, bonus float64) (int64, error) {
	if npc.Kind != "shop" {
		return 0, refuse(ReasonNoStock)
	}
	if qty < 1 || qty > maxQty {
		return 0, refuse(ReasonBadQty)
	}
	def, ok := reg.Items[item]
	if !ok {
		return 0, refuse(ReasonUnknownItem)
	}
	if def.Value <= 0 {
		return 0, refuse(ReasonUnsellable)
	}
	if CountItem(p, item) < qty {
		return 0, refuse(ReasonNotOwned)
	}
	for _, held := range p.Equipped {
		if held == item {
			return 0, refuse(ReasonEquipped)
		}
	}
	paid := SellPrice(def, defs.SellRate, bonus) * int64(qty)
	if err := TakeItem(p, item, qty); err != nil {
		return 0, err
	}
	p.Credits += paid
	return paid, nil
}

// Craft validates a craft — recipe known, level, every input × qty owned,
// the output fits once the inputs leave — then applies it atomically. qty
// crafts happen as one transaction or none. bonus is extra output units
// already rolled by the caller (craft_extra). Returns the units granted.
func Craft(p *store.Player, r defs.Recipe, qty int, level int, reg *defs.Registry, bonus int) (int, error) {
	if qty < 1 || qty > maxQty {
		return 0, refuse(ReasonBadQty)
	}
	if level < r.Level {
		return 0, refuse(ReasonLocked)
	}
	for _, in := range r.Inputs {
		if CountItem(p, in.Item) < in.Qty*qty {
			return 0, refuse(ReasonMissingMaterials)
		}
	}
	outDef, ok := reg.Items[r.Output.Item]
	if !ok {
		return 0, refuse(ReasonUnknownItem)
	}
	// Work on a scratch row so a no_space at the end costs nothing.
	scratch := store.Player{Inventory: p.Inventory}
	for _, in := range r.Inputs {
		if err := TakeItem(&scratch, in.Item, in.Qty*qty); err != nil {
			return 0, err
		}
	}
	// The bonus unit only exists for stackables: a second chest plate has
	// nowhere to go, so craft_extra never rolls on a stack_max 1 output.
	if outDef.StackMax <= 1 {
		bonus = 0
	}
	made := r.Output.Qty*qty + bonus
	newInv, err := applyStack(scratch.Inventory, r.Output.Item, made, outDef.StackMax, reg.InvSlots)
	if err != nil {
		return 0, err
	}
	p.Inventory = newInv
	// A consumed input may have been worn (the old drill in recipe.drill.mk2):
	// a slot must not name an item the bag no longer holds.
	for slot, held := range p.Equipped {
		if CountItem(p, held) == 0 {
			delete(p.Equipped, slot)
		}
	}
	return made, nil
}
