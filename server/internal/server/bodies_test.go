package server

import (
	"testing"

	"space-adventure/server/internal/sim"
)

// Playtest 2026-10-02: bodies walked through each other and through the
// rover. Two players dropped on one spot end up a body-width apart, and a
// player dropped inside the rover ends up out of its hull box.
func TestBodiesCollide(t *testing.T) {
	s, url := newTestServer(t)
	a := dialWS(t, url)
	aID := joinSeat(t, a, "a")
	b := dialWS(t, url)
	bID := joinSeat(t, b, "b")
	rover := roverOf(t, s)

	// Both bodies onto the same point 5 m from the rover, b nudged 5 cm so
	// the push-out has a direction.
	teleportNear(s, aID, sim.Vec(rover.Pos), 5)
	teleportNear(s, bID, sim.Vec(rover.Pos), 5.05)
	for i := 0; i < 10; i++ {
		rowOf(t, a, aID)
	}
	ra, rb := rowOf(t, a, aID), rowOf(t, a, bID)
	if d := dist32(ra.Pos, rb.Pos); d < 2*sim.BodyRadius-0.05 {
		t.Errorf("two bodies on one spot are %.3f m apart, want ≥ %.2f", d, 2*sim.BodyRadius)
	}

	// a straight into the rover's origin: the hull box shoves it out the
	// shallowest way, which from the middle is up -- it ends standing on the
	// roof, feet at RoverBoxH+RoverHalfY over the origin, minus the sphere's
	// own offset. Anything less is still inside the hull.
	s.mu.Lock()
	s.clients[aID].entity.State.Pos = rover.Pos
	s.mu.Unlock()
	for i := 0; i < 10; i++ {
		rowOf(t, a, aID)
	}
	ra = rowOf(t, a, aID)
	out := sim.RoverBoxH + sim.RoverHalfY - sim.BodySphereH + sim.BodyRadius
	if d := dist32(ra.Pos, toF32(rover.Pos)); d < out-0.01 {
		t.Errorf("a body inside the rover is %.3f m from its origin, want ≥ %.2f (out of the hull)", d, out)
	}
}

func toF32(v [3]float64) [3]float32 { return [3]float32{float32(v[0]), float32(v[1]), float32(v[2])} }
