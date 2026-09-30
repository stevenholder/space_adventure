// Weapon mods (Phase 13, docs/GDD.md "Weapon mods"): additive deltas from
// the worn `mod` item onto the primary's weapon table. Every reader of a
// weapon table goes through WeaponWith so a mod cannot be applied in one
// place and forgotten in another.
package sim

import (
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

// ApplyMod returns w with m's deltas added. A nil m is w unchanged.
func ApplyMod(w defs.Weapon, m *defs.Mod) defs.Weapon {
	if m == nil {
		return w
	}
	w.Damage += m.Damage
	w.Magazine += m.Magazine
	w.MaxRange += m.MaxRange
	w.FalloffStart += m.FalloffStart
	w.FalloffEnd += m.FalloffEnd
	return w
}

// WeaponWith resolves p's worn primary to its weapon table with the worn
// mod applied. false when nothing with a weapon table is worn.
func WeaponWith(reg *defs.Registry, p *store.Player) (defs.Weapon, bool) {
	if p == nil || reg == nil {
		return defs.Weapon{}, false
	}
	item, ok := reg.Items[p.Equipped["primary"]]
	if !ok || item.Weapon == nil {
		return defs.Weapon{}, false
	}
	var m *defs.Mod
	if md, ok := reg.Items[p.Equipped["mod"]]; ok {
		m = md.Mod
	}
	return ApplyMod(*item.Weapon, m), true
}
