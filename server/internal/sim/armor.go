// Armor (docs/GDD.md "Equipment"): the summed `armor.value` of everything
// worn cuts incoming damage with diminishing returns, so stacking never
// reaches immunity.
package sim

import (
	"math"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

// ArmorK is the armor at which damage is halved: a hit is scaled by
// ArmorK / (ArmorK + armor). Full Scout (23) takes ~19% off, full
// Bulwark (46) ~32%.
const ArmorK = 100.0

// ArmorOf sums the armor value of every item p wears.
func ArmorOf(reg *defs.Registry, p *store.Player) int {
	if p == nil || reg == nil {
		return 0
	}
	total := 0
	for _, id := range p.Equipped {
		if item, ok := reg.Items[id]; ok && item.Armor != nil {
			total += item.Armor.Value
		}
	}
	return total
}

// Mitigate scales a hit by armor, rounded, never below 1 so a hit always
// lands something.
func Mitigate(amount, armor int) int {
	if amount <= 0 || armor <= 0 {
		return amount
	}
	out := int(math.Round(float64(amount) * ArmorK / (ArmorK + float64(armor))))
	if out < 1 {
		out = 1
	}
	return out
}
