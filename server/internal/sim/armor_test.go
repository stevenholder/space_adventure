package sim

import (
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

func TestMitigate(t *testing.T) {
	for _, c := range []struct{ amount, armor, want int }{
		{20, 0, 20},   // no armor, untouched
		{20, 23, 16},  // full Scout: 20*100/123 = 16.26
		{20, 46, 14},  // full Bulwark: 13.70
		{20, 100, 10}, // ArmorK halves
		{1, 1000, 1},  // never below 1
		{0, 50, 0},    // nothing stays nothing
	} {
		if got := Mitigate(c.amount, c.armor); got != c.want {
			t.Errorf("Mitigate(%d, %d) = %d, want %d", c.amount, c.armor, got, c.want)
		}
	}
}

func TestArmorOfSumsWorn(t *testing.T) {
	reg := &defs.Registry{Items: map[string]defs.Item{
		"h": {ID: "h", Armor: &defs.Armor{Value: 3}},
		"c": {ID: "c", Armor: &defs.Armor{Value: 8}},
		"w": {ID: "w"},
	}}
	p := &store.Player{Equipped: map[string]string{"head": "h", "chest": "c", "primary": "w", "legs": "missing"}}
	if got := ArmorOf(reg, p); got != 11 {
		t.Fatalf("ArmorOf = %d, want 11", got)
	}
	if ArmorOf(reg, nil) != 0 || ArmorOf(nil, p) != 0 {
		t.Fatal("nil player or registry must be 0")
	}
}
