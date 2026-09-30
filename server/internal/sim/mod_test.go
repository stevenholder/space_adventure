package sim

import (
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

// TestApplyMod pins C131's arithmetic: deltas add, nothing multiplies, a
// missing mod is the bare table, and WeaponWith reads the worn slots.
func TestApplyMod(t *testing.T) {
	base := defs.Weapon{Damage: 25, Magazine: 30, MaxRange: 120, FalloffStart: 40, FalloffEnd: 120}
	got := ApplyMod(base, &defs.Mod{Damage: 5, Magazine: 10, MaxRange: 40, FalloffStart: 20, FalloffEnd: 40})
	if got.Damage != 30 || got.Magazine != 40 || got.MaxRange != 160 || got.FalloffStart != 60 || got.FalloffEnd != 160 {
		t.Fatalf("modded = %+v", got)
	}
	if ApplyMod(base, nil) != base {
		t.Fatal("nil mod changed the table")
	}
	reg := &defs.Registry{Items: map[string]defs.Item{
		"weapon.pulse": {ID: "weapon.pulse", Weapon: &base},
		"mod.coil":     {ID: "mod.coil", Slot: "mod", Mod: &defs.Mod{Damage: 5}},
	}}
	p := &store.Player{Equipped: map[string]string{"primary": "weapon.pulse", "mod": "mod.coil"}}
	if w, ok := WeaponWith(reg, p); !ok || w.Damage != 30 || w.Magazine != 30 {
		t.Fatalf("worn coil: ok=%v %+v", ok, w)
	}
	delete(p.Equipped, "primary")
	if _, ok := WeaponWith(reg, p); ok {
		t.Fatal("a mod with no rifle has a weapon table")
	}
}
