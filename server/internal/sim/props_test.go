package sim

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
)

// A barrel turned 90 degrees about Y and doubled: its box is the model
// bounds, scaled and rotated, centred over its base. simdump's selftest pins
// Collide.PropCollider to these same numbers.
func TestPropCollider(t *testing.T) {
	s := math.Sqrt(0.5)
	c, ok := PropCollider(protocol.Prop{Asset: "prop.barrels", Pos: [3]float32{0, 150, 0},
		Quat: [4]float32{0, float32(s), 0, float32(s)}, Scale: 2})
	if !ok {
		t.Fatal("barrels are solid")
	}
	// mid (0, 0.475, -0.185)*2 rotated +90 about Y -> (-0.37, 0.95, 0).
	want := [3]float32{-0.37, 150.95, 0}
	for i := range want {
		if math.Abs(float64(c.Center[i]-want[i])) > 1e-5 {
			t.Fatalf("centre %v, want %v", c.Center, want)
		}
	}
	if c.Half != [3]float32{1.3, 0.95, 1.35} {
		t.Fatalf("half %v, want [1.3 0.95 1.35]", c.Half)
	}
	if _, ok := PropCollider(protocol.Prop{Asset: "prop.bones", Scale: 1}); ok {
		t.Fatal("bones are walk-through")
	}
}
