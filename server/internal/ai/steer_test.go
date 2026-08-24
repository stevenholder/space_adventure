package ai

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// flatField builds a Field whose surface radius is r everywhere, so every
// direction is walkable and SampleRadius is exactly r.
func flatField(r float64) *terrain.Field {
	f := &terrain.Field{}
	for face := range f.Radii {
		for i := range f.Radii[face] {
			f.Radii[face][i] = r
		}
	}
	return f
}

func finite(v [3]float64) bool {
	for _, c := range v {
		if math.IsNaN(c) || math.IsInf(c, 0) {
			return false
		}
	}
	return true
}

func length(v [3]float64) float64 {
	return math.Sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2])
}

func dist(a, b [3]float64) float64 {
	d := [3]float64{a[0] - b[0], a[1] - b[1], a[2] - b[2]}
	return length(d)
}

// TestStepWalksTowardTarget: an NPC on a flat field walks toward a target,
// reducing the distance every tick, and stays glued to the sampled surface
// radius throughout.
func TestStepWalksTowardTarget(t *testing.T) {
	const r = 150.0
	f := flatField(r)

	s := &Steerer{
		Pos:      [3]float64{0, 0, r},
		Facing:   [3]float64{0, 1, 0},
		Speed:    4.5,
		TurnRate: 3.0,
	}
	target := [3]float64{50, 0, r}
	const dt = 1.0 / 20

	prevDist := dist(s.Pos, target)
	for i := 0; i < 200; i++ {
		Step(s, target, dt, f, nil)

		if !finite(s.Pos) || !finite(s.Vel) || !finite(s.Facing) {
			t.Fatalf("tick %d: non-finite state pos=%v vel=%v facing=%v", i, s.Pos, s.Vel, s.Facing)
		}

		up := terrain.Normalize(terrain.Vec(s.Pos))
		gotR := length(s.Pos)
		wantR := f.SampleRadius(up)
		if math.Abs(gotR-wantR) > 1e-6 {
			t.Fatalf("tick %d: |pos| = %.12f, want sampled radius %.12f (glued)", i, gotR, wantR)
		}

		d := dist(s.Pos, target)
		if d > prevDist+1e-9 {
			t.Fatalf("tick %d: distance to target grew: %.6f -> %.6f", i, prevDist, d)
		}
		prevDist = d
	}
	if prevDist > 1.0 {
		t.Fatalf("after 200 ticks, distance to target still %.3f, want < 1.0", prevDist)
	}
}

// TestStepFacingTurnRateClamp: facing turns no faster than TurnRate*dt per
// tick.
func TestStepFacingTurnRateClamp(t *testing.T) {
	const r = 150.0
	f := flatField(r)

	const turnRate = 1.0
	const dt = 1.0 / 20
	s := &Steerer{
		Pos:      [3]float64{0, 0, r},
		Facing:   [3]float64{0, 1, 0},
		Speed:    4.5,
		TurnRate: turnRate,
	}
	target := [3]float64{50, 0, r} // desired dir ~ (1,0,0), 90° from facing

	prevFacing := s.Facing
	maxAngle := turnRate*dt + 1e-6
	for i := 0; i < 60; i++ {
		Step(s, target, dt, f, nil)
		if !finite(s.Facing) {
			t.Fatalf("tick %d: non-finite facing %v", i, s.Facing)
		}
		fl := length(s.Facing)
		if math.Abs(fl-1) > 1e-9 {
			t.Fatalf("tick %d: |facing| = %.12f, want ~1", i, fl)
		}
		c := prevFacing[0]*s.Facing[0] + prevFacing[1]*s.Facing[1] + prevFacing[2]*s.Facing[2]
		if c > 1 {
			c = 1
		} else if c < -1 {
			c = -1
		}
		angle := math.Acos(c)
		if angle > maxAngle {
			t.Fatalf("tick %d: facing turned %.6f rad, want <= %.6f", i, angle, maxAngle)
		}
		prevFacing = s.Facing
	}
}

// TestStepTargetAtPosition: a target exactly at the NPC's own position
// produces no NaN (toward has no defined direction).
func TestStepTargetAtPosition(t *testing.T) {
	const r = 150.0
	f := flatField(r)

	s := &Steerer{
		Pos:      [3]float64{0, 0, r},
		Facing:   [3]float64{0, 1, 0},
		Speed:    4.5,
		TurnRate: 3.0,
	}
	target := s.Pos

	for i := 0; i < 5; i++ {
		Step(s, target, 1.0/20, f, nil)
		if !finite(s.Pos) || !finite(s.Vel) || !finite(s.Facing) {
			t.Fatalf("tick %d: non-finite state pos=%v vel=%v facing=%v", i, s.Pos, s.Vel, s.Facing)
		}
	}
}

// TestStepFacingExactlyOpposite: a desired direction exactly opposite the
// current facing (no unique rotation axis) produces no NaN and still
// respects the turn-rate clamp.
func TestStepFacingExactlyOpposite(t *testing.T) {
	const r = 150.0
	f := flatField(r)

	const turnRate = 2.0
	const dt = 1.0 / 20
	s := &Steerer{
		Pos:      [3]float64{0, 0, r},
		Facing:   [3]float64{-1, 0, 0}, // exactly opposite the desired dir below
		Speed:    4.5,
		TurnRate: turnRate,
	}
	target := [3]float64{50, 0, r} // desired dir = (1,0,0)

	Step(s, target, dt, f, nil)

	if !finite(s.Pos) || !finite(s.Vel) || !finite(s.Facing) {
		t.Fatalf("non-finite state pos=%v vel=%v facing=%v", s.Pos, s.Vel, s.Facing)
	}
	fl := length(s.Facing)
	if math.Abs(fl-1) > 1e-9 {
		t.Fatalf("|facing| = %.12f, want ~1", fl)
	}
	c := -s.Facing[0] // dot with old facing (-1,0,0)
	if c > 1 {
		c = 1
	} else if c < -1 {
		c = -1
	}
	angle := math.Acos(c)
	if angle > turnRate*dt+1e-6 {
		t.Fatalf("facing turned %.6f rad in one tick, want <= %.6f", angle, turnRate*dt)
	}
}

// TestStepEmptyColliders is a smoke test that a nil/empty collider slice
// (the common case for an open camp) is a well-defined no-op path through
// sim.ResolveColliders, not just an untested branch.
func TestStepEmptyColliders(t *testing.T) {
	const r = 150.0
	f := flatField(r)
	s := &Steerer{Pos: [3]float64{0, 0, r}, Facing: [3]float64{0, 1, 0}, Speed: 4.5, TurnRate: 3.0}
	Step(s, [3]float64{10, 0, r}, 1.0/20, f, []protocol.Collider{})
	if !finite(s.Pos) {
		t.Fatalf("non-finite pos %v", s.Pos)
	}
}
