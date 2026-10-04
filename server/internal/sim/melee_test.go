package sim

import (
	"testing"

	"space-adventure/server/internal/defs"
)

// A swing reaches what is in front within range and arc; a full circle
// reaches behind too; nothing past reach.
func TestInArc(t *testing.T) {
	from := Vec{0, 1000, 0}
	up := Vec{0, 1, 0}
	fwd := Vec{0, 0, -1}
	sword := defs.Melee{Damage: 10, Interval: 1, Range: 2, Arc: 100}
	maul := sword
	maul.Arc = 360
	body := func(x, z float64) Vec { return Vec{x, 1000, z} }
	for _, c := range []struct {
		name string
		m    defs.Melee
		at   Vec
		want bool
	}{
		{"ahead", sword, body(0, -1.8), true},
		{"ahead, past reach", sword, body(0, -2.8), false},
		{"beside, outside 100 deg", sword, body(1.8, 0), false},
		{"45 deg off, inside", sword, body(1.2, -1.2), true},
		{"behind", sword, body(0, 1.5), false},
		{"behind, full circle", maul, body(0, 1.5), true},
	} {
		if _, _, got := InArc(from, up, fwd, c.m, c.at, 1.8, 0.35); got != c.want {
			t.Errorf("%s: touched=%v want %v", c.name, got, c.want)
		}
	}
}

func TestBurstDamage(t *testing.T) {
	th := defs.Throw{Damage: 60, Radius: 5, Speed: 18}
	for _, c := range []struct {
		d    float64
		want int
	}{{0, 60}, {2.5, 45}, {5, 30}, {5.01, 0}} {
		if got := BurstDamage(th, c.d); got != c.want {
			t.Errorf("d=%v: %d want %d", c.d, got, c.want)
		}
	}
}

// A thrown charge falls (its path bends toward the ground) and bursts,
// through ctx.Burst, when its life runs out in the air.
func TestThrownChargeFallsThenBursts(t *testing.T) {
	w := NewWorld()
	e := &Ent{ID: 7, Kind: 7, Pos: [3]float64{0, 1000, 0}, Vel: [3]float64{0, 0, -10},
		Data: &ProjectileState{Owner: 1, Speed: 10, Falls: true, Burst: "throw.frag", LifeTicks: 10}}
	w.Add(e)
	var burstAt Vec
	bursts := 0
	ctx := StepCtx{World: w, Burst: func(at Vec, owner uint32, item string) {
		bursts++
		burstAt = at
		if owner != 1 || item != "throw.frag" {
			t.Fatalf("burst owner=%d item=%q", owner, item)
		}
	}}
	for i := 0; i < 20 && w.Ents[7] != nil; i++ {
		StepProjectile(e, DT, ctx)
	}
	if bursts != 1 || w.Ents[7] != nil {
		t.Fatalf("bursts=%d, still in world=%v", bursts, w.Ents[7] != nil)
	}
	if burstAt[1] >= 1000 || burstAt[2] >= -1 {
		t.Fatalf("burst at %v: want below the throw height and down range", burstAt)
	}
}
