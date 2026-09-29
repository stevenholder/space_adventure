// Inventory, credits and shop-purchase rules (docs/GDD.md, "Items and
// currency" and "Shop NPCs"). Buy and Equip are the validated entry points
// the cmd opcodes call (docs/tasks/phase2-wave2.md "W2-11"); every refusal
// is a machine-readable reason so cmd_result never carries prose
// (docs/PROTOCOL.md).
package sim

import (
	"math"
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
	"strings"
)

// RefusalError is a typed, machine-readable refusal from a validated
// inventory/shop operation. Its Reason is exactly the string cmd_result
// puts in {"reason": ...} — never prose for display.
type RefusalError struct{ Reason string }

func (e RefusalError) Error() string { return e.Reason }

// Refusal reasons (docs/GDD.md "Items and currency", "Shop NPCs").
const (
	ReasonBadQty              = "bad_qty"
	ReasonUnknownItem         = "unknown_item"
	ReasonNoStock             = "no_stock"
	ReasonInsufficientCredits = "insufficient_credits"
	ReasonNoSpace             = "no_space"
	ReasonNotOwned            = "not_owned"
	ReasonWrongSlot           = "wrong_slot"
)

func refuse(reason string) error { return RefusalError{Reason: reason} }

// maxQty is the per-call quantity ceiling (docs/GDD.md "Items and
// currency"): qty must be >= 1 and <= 1000.
const maxQty = 1000

// applyStack returns a NEW stack slice with qty of item added, honoring
// reg.InvSlots (max stack count) and the item's StackMax (max qty per
// stack). It never mutates stacks in place, so a caller can discard the
// result on error without touching the original slice — the atomicity Buy
// and AddItem depend on.
func applyStack(stacks []store.Stack, item string, qty int, stackMax int, invSlots int) ([]store.Stack, error) {
	out := make([]store.Stack, len(stacks))
	copy(out, stacks)

	for i, s := range out {
		if s.Item == item {
			newQty := s.Qty + qty
			if newQty > stackMax {
				return nil, refuse(ReasonNoSpace)
			}
			out[i] = store.Stack{Item: item, Qty: newQty}
			return out, nil
		}
	}

	// No existing stack for this item: a new one is needed, which costs a
	// slot.
	if len(out) >= invSlots {
		return nil, refuse(ReasonNoSpace)
	}
	if qty > stackMax {
		return nil, refuse(ReasonNoSpace)
	}
	out = append(out, store.Stack{Item: item, Qty: qty})
	return out, nil
}

// Buy validates a shop_buy against npc's stock, in the order docs/GDD.md
// "Shop NPCs" specifies (item existence, stock, affordability, space), then
// debits credits and grants the item ATOMICALLY: the new credits/inventory
// are built in full before anything is assigned onto p, so a failure at any
// step — including the last one — leaves p completely unchanged.
func Buy(p *store.Player, npc defs.NPC, item string, qty int, reg *defs.Registry) error {
	return BuyAt(p, npc, item, qty, reg, 1)
}

// BuyAt is Buy with the unit price scaled by priceMult and rounded to the
// nearest credit (Phase 11 Commerce discount). priceMult 1 is Buy exactly.
func BuyAt(p *store.Player, npc defs.NPC, item string, qty int, reg *defs.Registry, priceMult float64) error {
	if qty < 1 || qty > maxQty {
		return refuse(ReasonBadQty)
	}

	def, ok := reg.Items[item]
	if !ok {
		return refuse(ReasonUnknownItem)
	}

	if npc.Kind != "shop" {
		return refuse(ReasonNoStock)
	}
	var price int64 = -1
	for _, s := range npc.Stock {
		if s.Item == item {
			price = s.Price
			break
		}
	}
	if price < 0 {
		return refuse(ReasonNoStock)
	}

	if priceMult > 0 && priceMult != 1 {
		price = int64(math.Round(float64(price) * priceMult))
	}
	cost := price * int64(qty)
	if cost > p.Credits {
		return refuse(ReasonInsufficientCredits)
	}

	newInv, err := applyStack(p.Inventory, item, qty, def.StackMax, reg.InvSlots)
	if err != nil {
		return err
	}

	// Everything validated and built — swap in atomically.
	p.Credits -= cost
	p.Inventory = newInv
	return nil
}

// Equip refuses unknown_item (not in the registry), not_owned (not in the
// player's inventory) and wrong_slot (the item's def slot does not match
// the requested slot). On success it sets p.Equipped[slot] = item.
func Equip(p *store.Player, slot, item string, reg *defs.Registry) error {
	// The slot set is data (items.json equip_slots). A registry with none
	// declared keeps the Phase 2 world: primary only.
	if !SlotKnown(reg, slot) {
		return refuse(ReasonWrongSlot)
	}
	// An empty item is the unequip: the slot goes back to bare.
	if item == "" {
		delete(p.Equipped, slot)
		return nil
	}
	def, ok := reg.Items[item]
	if !ok {
		return refuse(ReasonUnknownItem)
	}

	owned := false
	for _, s := range p.Inventory {
		if s.Item == item && s.Qty > 0 {
			owned = true
			break
		}
	}
	if !owned {
		return refuse(ReasonNotOwned)
	}

	if !SlotAccepts(def.Slot, slot) {
		return refuse(ReasonWrongSlot)
	}
	// One stack of one can be in one slot: a ring equipped in accessory1
	// leaves accessory1 when it goes to accessory2.
	for other, held := range p.Equipped {
		if held == item && other != slot && CountItem(p, item) < 2 {
			delete(p.Equipped, other)
		}
	}

	if p.Equipped == nil {
		p.Equipped = make(map[string]string, 1)
	}
	p.Equipped[slot] = item
	return nil
}

// SlotKnown reports whether slot is one the registry declares. With no
// declared set, "primary" is the whole set (Phase 2 registries and tests).
func SlotKnown(reg *defs.Registry, slot string) bool {
	if len(reg.EquipSlots) == 0 {
		return slot == "primary"
	}
	for _, s := range reg.EquipSlots {
		if s == slot {
			return true
		}
	}
	return false
}

// SlotAccepts reports whether an item declared for itemSlot may sit in
// slot. Exact match, except "accessory" items fit any accessoryN slot.
func SlotAccepts(itemSlot, slot string) bool {
	if itemSlot == slot {
		return true
	}
	return itemSlot == "accessory" && strings.HasPrefix(slot, "accessory")
}

// CountItem reports how many of item p is carrying, across its stacks.
func CountItem(p *store.Player, item string) int {
	n := 0
	for _, s := range p.Inventory {
		if s.Item == item {
			n += s.Qty
		}
	}
	return n
}

// TakeItem removes qty of item from p's inventory, emptying stacks as it
// goes and dropping any that reach zero. It is atomic: if p is not carrying
// enough, nothing is removed and the refusal names the shortage.
//
// Used by reload, which converts carried ammunition into rounds in the
// magazine — the one place inventory shrinks without a shop involved.
func TakeItem(p *store.Player, item string, qty int) error {
	if qty < 1 || qty > maxQty {
		return refuse(ReasonBadQty)
	}
	if CountItem(p, item) < qty {
		return refuse(ReasonNotOwned)
	}

	out := make([]store.Stack, 0, len(p.Inventory))
	left := qty
	for _, s := range p.Inventory {
		if left > 0 && s.Item == item {
			take := s.Qty
			if take > left {
				take = left
			}
			s.Qty -= take
			left -= take
		}
		if s.Qty > 0 {
			out = append(out, s)
		}
	}
	p.Inventory = out
	return nil
}

// AddItem grants qty of item to p's inventory unconditionally (no credits,
// no NPC/stock check) — e.g. start_items on a new player row. It still
// enforces InvSlots/StackMax atomically: on failure p is unchanged.
func AddItem(p *store.Player, item string, qty int, reg *defs.Registry) error {
	if qty < 1 || qty > maxQty {
		return refuse(ReasonBadQty)
	}
	def, ok := reg.Items[item]
	if !ok {
		return refuse(ReasonUnknownItem)
	}

	newInv, err := applyStack(p.Inventory, item, qty, def.StackMax, reg.InvSlots)
	if err != nil {
		return err
	}
	p.Inventory = newInv
	return nil
}
