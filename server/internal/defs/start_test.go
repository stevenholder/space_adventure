package defs

import "testing"

// TestApplyStartOverride pins SA_START's grammar: credits and item=qty
// pairs replace the file's start, a bad pair changes nothing.
func TestApplyStartOverride(t *testing.T) {
	reg := &Registry{InvSlots: 20, Items: map[string]Item{"ammo.cell": {ID: "ammo.cell", StackMax: 300}}}
	if err := ApplyStartOverride(reg, "credits=1000, ammo.cell=120"); err != nil {
		t.Fatal(err)
	}
	if reg.StartCredits != 1000 || len(reg.StartItems) != 1 || reg.StartItems[0].Item != "ammo.cell" || reg.StartItems[0].Qty != 120 {
		t.Fatalf("override: %d %v", reg.StartCredits, reg.StartItems)
	}
	for _, bad := range []string{"credits", "credits=-1", "credits=x", "widget=1", "ammo.cell=0"} {
		if err := ApplyStartOverride(reg, bad); err == nil {
			t.Errorf("%q accepted", bad)
		}
		if reg.StartCredits != 1000 || len(reg.StartItems) != 1 {
			t.Fatalf("%q changed the start: %d %v", bad, reg.StartCredits, reg.StartItems)
		}
	}
}
