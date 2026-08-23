package sim

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
)

func zeroRadius([3]float64) float64 { return 0 }

// A sphere driven into a box face stops at the face: after resolution the
// chest sphere no longer penetrates the box (penetration <= 1e-9).
func TestResolveColliders_BoxFaceStop(t *testing.T) {
	box := protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{0, 0, 0},
		Half:   [3]float32{1, 1, 1},
		Quat:   [4]float32{0, 0, 0, 1},
	}
	up := [3]float64{0, 1, 0}
	// chest = pos + up*BodySphereH = {1.2, 0, 0}: 0.2 m from the +X face,
	// which is inside the 0.35 m body radius (0.15 m penetration).
	pos := [3]float64{1.2, -BodySphereH, 0}
	vel := [3]float64{-1, 0, 0} // moving into the wall

	outPos, outVel, outGrounded := ResolveColliders(pos, vel, up, false,
		[]protocol.Collider{box}, zeroRadius)

	chest := Vec{outPos[0] + up[0]*BodySphereH, outPos[1] + up[1]*BodySphereH, outPos[2] + up[2]*BodySphereH}
	closest := Vec{clampf(chest[0], -1, 1), clampf(chest[1], -1, 1), clampf(chest[2], -1, 1)}
	dist := chest.Sub(closest).Len()
	if pen := BodyRadius - dist; pen > 1e-9 {
		t.Fatalf("penetration = %v, want <= 1e-9 (chest=%v, dist=%v)", pen, chest, dist)
	}
	if outVel != (Vec{0, 0, 0}) {
		t.Fatalf("outVel = %v, want {0,0,0} (velocity into the wall cancelled)", outVel)
	}
	if outGrounded {
		t.Fatalf("outGrounded = true, want false (side wall, not standing on top)")
	}
}

// A sphere clear of the box is returned untouched — bit-identical.
func TestResolveColliders_ClearOfBox(t *testing.T) {
	box := protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{100, 100, 100},
		Half:   [3]float32{1, 1, 1},
		Quat:   [4]float32{0, 0, 0, 1},
	}
	pos := [3]float64{0, 0, 0}
	vel := [3]float64{1, 2, 3}
	up := [3]float64{0, 1, 0}

	outPos, outVel, outGrounded := ResolveColliders(pos, vel, up, true,
		[]protocol.Collider{box}, zeroRadius)

	if outPos != pos {
		t.Fatalf("outPos = %v, want bit-identical %v", outPos, pos)
	}
	if outVel != vel {
		t.Fatalf("outVel = %v, want bit-identical %v", outVel, vel)
	}
	if outGrounded != true {
		t.Fatalf("outGrounded = %v, want true (unchanged)", outGrounded)
	}
}

// The degenerate centre-at-centre case (no defined exit normal) must return
// a finite position and velocity, never NaN.
func TestResolveColliders_DegenerateCentre(t *testing.T) {
	box := protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{0, 0, 0},
		Half:   [3]float32{1, 1, 1},
		Quat:   [4]float32{0, 0, 0, 1},
	}
	up := [3]float64{0, 1, 0}
	// chest = pos + up*BodySphereH = {0,0,0}: exactly the box centre.
	pos := [3]float64{0, -BodySphereH, 0}
	vel := [3]float64{1, 1, 1}

	outPos, outVel, outGrounded := ResolveColliders(pos, vel, up, false,
		[]protocol.Collider{box}, zeroRadius)

	for i := 0; i < 3; i++ {
		if math.IsNaN(outPos[i]) || math.IsInf(outPos[i], 0) {
			t.Fatalf("outPos = %v, want finite", outPos)
		}
		if math.IsNaN(outVel[i]) || math.IsInf(outVel[i], 0) {
			t.Fatalf("outVel = %v, want finite", outVel)
		}
	}
	// Degenerate fallback: pushed out along up by the full body radius.
	wantPos := [3]float64{0, -BodySphereH + BodyRadius, 0}
	if outPos != wantPos {
		t.Fatalf("outPos = %v, want %v", outPos, wantPos)
	}
	_ = outGrounded
}

// An empty collider slice is a bit-identical no-op for pos, vel and
// grounded — this protects the Phase 1 conformance test (t5): trajectories
// must not move by a single ulp when there are no colliders.
func TestResolveColliders_EmptyIsNoOp(t *testing.T) {
	pos := [3]float64{12.3, -4.5, 6.7}
	vel := [3]float64{0.1, -0.2, 0.3}
	up := [3]float64{0, 1, 0}

	outPos, outVel, outGrounded := ResolveColliders(pos, vel, up, true, nil, zeroRadius)

	if outPos != pos {
		t.Fatalf("outPos = %v, want bit-identical %v", outPos, pos)
	}
	if outVel != vel {
		t.Fatalf("outVel = %v, want bit-identical %v", outVel, vel)
	}
	if outGrounded != true {
		t.Fatalf("outGrounded = %v, want true (unchanged)", outGrounded)
	}
}

// TestResolveColliders_EmptyNoOpBelowSurface is the case the original
// empty-slice test missed.
//
// Step 9 (terrain re-seat) used to run even with no colliders, so whenever
// |pos| sat an ulp under the sampled radius it re-seated and re-rounded the
// position. That perturbed every trajectory by ~1e-13 per tick — far below
// C5's 5% bar, so the conformance test still passed, but it broke the
// byte-identical property of the committed dumps, which is the evidence that
// the Phase 2 wire work never touched the sim.
//
// The original test placed the body exactly ON the surface, where the re-seat
// branch never fires. This one puts it just under.
func TestResolveColliders_EmptyNoOpBelowSurface(t *testing.T) {
	const r = 150.0
	radiusAt := func([3]float64) float64 { return r }

	// One ulp below the surface along +Y — exactly where step 9 would bite.
	pos := [3]float64{0, math.Nextafter(r, 0), 0}
	vel := [3]float64{1.5, -0.25, 0.75}
	up := [3]float64{0, 1, 0}

	for _, cs := range [][]protocol.Collider{nil, {}} {
		gotPos, gotVel, gotGrounded := ResolveColliders(pos, vel, up, true, cs, radiusAt)
		if gotPos != pos {
			t.Errorf("pos moved with no colliders: %v -> %v", pos, gotPos)
		}
		if gotVel != vel {
			t.Errorf("vel changed with no colliders: %v -> %v", vel, gotVel)
		}
		if !gotGrounded {
			t.Error("grounded flipped with no colliders")
		}
	}
}
