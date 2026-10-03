package server

import (
	"math"
	"testing"

	"space-adventure/server/internal/sim"
)

// An AI-steered NPC must face where it is heading, by the same convention as
// a player body (State.OrientationQuat: local +Z forward, right = up x
// facing). quatFromForward once built right = facing x up: a mirrored basis
// that rendered every chasing NPC with its back to its target.
func TestNPCQuatFacesItsHeading(t *testing.T) {
	pos := [3]float64{0, 100, 0}
	for _, f := range [][3]float64{{0, 0, 1}, {1, 0, 0}, {0, 0, -1}, {0.6, 0, -0.8}} {
		q := quatFromForward(f, pos)
		got := forwardOf(q)
		for i := range f {
			if math.Abs(got[i]-f[i]) > 1e-9 {
				t.Fatalf("facing %v: forwardOf(quatFromForward) = %v", f, got)
			}
		}
		s := sim.State{Pos: pos, Facing: f}
		want := s.OrientationQuat()
		dot := 0.0
		for i := range q {
			dot += q[i] * want[i]
		}
		if math.Abs(math.Abs(dot)-1) > 1e-9 {
			t.Fatalf("facing %v: npc quat %v != player convention %v", f, q, want)
		}
	}
}
