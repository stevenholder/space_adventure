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

// Phase 22 crafts are channels of units (docs/GDD.md "The refinery"): the
// server takes one unit's inputs when the unit starts, lands its output
// when it ends, and hands the inputs back if it is cancelled. These are
// the three moves on the player row; the timing lives in the server.

// CraftCheck refuses in the cmd's order — locked, missing_materials (for
// one unit), no_space (the output once that unit's inputs are out).
func CraftCheck(p *store.Player, r defs.Recipe, level int, reg *defs.Registry) error {
	if level < r.Level {
		return refuse(ReasonLocked)
	}
	scratch := store.Player{Inventory: p.Inventory}
	if err := TakeUnit(&scratch, r); err != nil {
		return err
	}
	outDef, ok := reg.Items[r.Output.Item]
	if !ok {
		return refuse(ReasonUnknownItem)
	}
	_, err := applyStack(scratch.Inventory, r.Output.Item, r.Output.Qty, outDef.StackMax, reg.InvSlots)
	return err
}

// TakeUnit removes one unit's inputs, all or none (missing_materials).
// A consumed input may have been worn (the old drill in the mk2 recipe):
// a slot must not name an item the bag no longer holds.
func TakeUnit(p *store.Player, r defs.Recipe) error {
	for _, in := range r.Inputs {
		if CountItem(p, in.Item) < in.Qty {
			return refuse(ReasonMissingMaterials)
		}
	}
	for _, in := range r.Inputs {
		if err := TakeItem(p, in.Item, in.Qty); err != nil {
			return err
		}
	}
	for slot, held := range p.Equipped {
		if CountItem(p, held) == 0 {
			delete(p.Equipped, slot)
		}
	}
	return nil
}

// LandUnit adds one unit's output plus bonus (craft_extra) and returns how
// many landed; no_space leaves p untouched. The bonus unit only exists for
// stackables: a second chest plate has nowhere to go.
func LandUnit(p *store.Player, r defs.Recipe, bonus int, reg *defs.Registry) (int, error) {
	outDef, ok := reg.Items[r.Output.Item]
	if !ok {
		return 0, refuse(ReasonUnknownItem)
	}
	if outDef.StackMax <= 1 {
		bonus = 0
	}
	made := r.Output.Qty + bonus
	inv, err := applyStack(p.Inventory, r.Output.Item, made, outDef.StackMax, reg.InvSlots)
	if err != nil {
		return 0, err
	}
	p.Inventory = inv
	return made, nil
}

// ReturnUnit hands a cancelled unit's inputs back, best effort: the slots
// they left are free unless something landed in them since.
func ReturnUnit(p *store.Player, r defs.Recipe, reg *defs.Registry) {
	for _, in := range r.Inputs {
		_ = AddItem(p, in.Item, in.Qty, reg)
	}
}
