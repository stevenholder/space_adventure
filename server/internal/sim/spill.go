// The death spill (Phase 12, docs/GDD.md "Death spills the raw"): every
// material stack leaves the bag; the caller drops each where the body fell.
package sim

import (
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

// SpillMaterials strips every stack whose item kind is "material" from p
// and returns them. Equipment, credits, tools, ammo — everything else —
// stays. Nothing worn is ever a material, so the equipped map is untouched.
func SpillMaterials(p *store.Player, reg *defs.Registry) []defs.ItemQty {
	var spilled []defs.ItemQty
	kept := make([]store.Stack, 0, len(p.Inventory))
	for _, st := range p.Inventory {
		if reg.Items[st.Item].Kind == "material" && st.Qty > 0 {
			spilled = append(spilled, defs.ItemQty{Item: st.Item, Qty: st.Qty})
			continue
		}
		kept = append(kept, st)
	}
	p.Inventory = kept
	return spilled
}
