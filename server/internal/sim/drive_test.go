package sim

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

func newRover(t *terrain.Field) *Ent {
	pos, quat := SpawnRover(t)
	return &Ent{
		ID:   9000,
		Kind: EntityKind(protocol.EntityTypeVehicle),
		Pos:  [3]float64(pos),
		Quat: [4]float64(quat),
		Data: NewVehicleState(),
	}
}

func driveTicks(e *Ent, t *terrain.Field, n int, throttle, steer float64) {
	v := e.Data.(*VehicleState)
	ctx := StepCtx{Terrain: t}
	for i := 0; i < n; i++ {
		v.Throttle, v.Steer = throttle, steer
		StepRover(e, DT, ctx)
	}
}

func tangentSpeed(e *Ent) float64 {
	up := terrain.Normalize(Vec(e.Pos))
	vt := Vec(e.Vel).Sub(up.Scale(Vec(e.Vel).Dot(up)))
	return vt.Len()
}

// GDD tuning target: ~2 s to vmax, top speed pins at vmax_drive.
func TestRoverAcceleratesToVmax(t *testing.T) {
	f := terrain.Generate(1337)
	e := newRover(f)
	driveTicks(e, f, 200, 1, 0) // 10 s, plenty
	got := tangentSpeed(e)
	if math.Abs(got-VmaxDrive) > VmaxDrive*0.05 {
		t.Fatalf("speed after 10 s full throttle = %.3f m/s, want ~%v", got, VmaxDrive)
	}
	// No grounded assertion: real terrain has crests, and airborne over a
	// crest at speed is the model's intent, not a failure.
}

// GDD tuning target: lifting throttle coasts to under 2 m/s in ~3 s.
func TestRoverCoastsToStop(t *testing.T) {
	f := terrain.Generate(1337)
	e := newRover(f)
	driveTicks(e, f, 200, 1, 0)
	driveTicks(e, f, 80, 0, 0) // 4 s coast
	if got := tangentSpeed(e); got > 2 {
		t.Fatalf("speed after 4 s coast = %.3f m/s, want < 2", got)
	}
}

// Skid-steer: a stationary grounded rover turns in place at steer_rate.
func TestRoverTurnsInPlace(t *testing.T) {
	f := terrain.Generate(1337)
	e := newRover(f)
	h0 := Rotate(Quat(e.Quat), Vec{0, 0, 1})
	driveTicks(e, f, 40, 0, 1) // 2 s full-lock right, no throttle
	h1 := Rotate(Quat(e.Quat), Vec{0, 0, 1})
	// 2 s at steer_rate 1.2 = 2.4 rad; cos(2.4) ≈ −0.74 (slope wobble allowed).
	if dot := h0.Dot(h1); dot > -0.5 {
		t.Fatalf("heading turned too little: cos = %.3f", dot)
	}
}

// At modest speed (mostly grounded) grip keeps lateral velocity a small
// fraction of the tangential speed through a full-lock turn.
func TestRoverGripBites(t *testing.T) {
	f := terrain.Generate(1337)
	e := newRover(f)
	driveTicks(e, f, 40, 0.4, 0)
	driveTicks(e, f, 40, 0.4, 1) // 2 s turning at ~5 m/s
	up := terrain.Normalize(Vec(e.Pos))
	h := terrain.Normalize(tangential(Rotate(Quat(e.Quat), Vec{0, 0, 1}), up))
	vt := Vec(e.Vel).Sub(up.Scale(Vec(e.Vel).Dot(up)))
	vlat := vt.Sub(h.Scale(vt.Dot(h)))
	if vlat.Len() > 0.5*vt.Len() {
		t.Fatalf("lateral speed %.2f of tangent %.2f — grip not biting", vlat.Len(), vt.Len())
	}
}

// A rover with no driver state must not panic (the spawner.go rule), and a
// rover with no terrain must not move.
func TestRoverTolerantStep(t *testing.T) {
	e := &Ent{Kind: EntityKind(protocol.EntityTypeVehicle), Pos: [3]float64{0, 150, 0}}
	StepRover(e, DT, StepCtx{}) // no state, no terrain
	e.Data = &VehicleState{}
	StepRover(e, DT, StepCtx{}) // state, no terrain
	if e.Pos != [3]float64{0, 150, 0} {
		t.Fatal("rover moved with no terrain in ctx")
	}
}

// The rover never sinks: after any driving, ‖pos‖ ≥ sampled radius − eps.
func TestRoverStaysOnSurface(t *testing.T) {
	f := terrain.Generate(1337)
	e := newRover(f)
	for i := 0; i < 400; i++ {
		v := e.Data.(*VehicleState)
		v.Throttle, v.Steer = 1, math.Sin(float64(i)/20)
		StepRover(e, DT, StepCtx{Terrain: f})
		up := terrain.Normalize(Vec(e.Pos))
		if Vec(e.Pos).Len() < f.SampleRadius(up)-1e-9 {
			t.Fatalf("tick %d: rover under the surface", i)
		}
	}
}

func TestSpawnRoverDeterministic(t *testing.T) {
	f := terrain.Generate(1337)
	p1, q1 := SpawnRover(f)
	p2, q2 := SpawnRover(f)
	if p1 != p2 || q1 != q2 {
		t.Fatal("SpawnRover not deterministic")
	}
	up := terrain.Normalize(p1)
	if !f.Walkable(up) {
		t.Fatal("rover spawned on unwalkable ground")
	}
	spawn := SpawnState(f)
	if d := p1.Sub(spawn.Pos).Len(); d < 5 || d > 100 {
		t.Fatalf("rover %f m from spawn, want near rover_spawn_dist", d)
	}
}

func TestComposeSeatOffsets(t *testing.T) {
	f := terrain.Generate(1337)
	pos, quat := SpawnRover(f)
	driver := ComposeSeat(EntityKind(protocol.EntityTypeVehicle), pos, quat, 1)
	pass := ComposeSeat(EntityKind(protocol.EntityTypeVehicle), pos, quat, 2)
	if d := driver.Sub(pos).Len(); math.Abs(d-RoverSeatPos[1].Len()) > 1e-9 {
		t.Fatalf("driver offset %.4f, want %.4f", d, RoverSeatPos[1].Len())
	}
	if driver == pass {
		t.Fatal("driver and passenger composed to the same point")
	}
	// Seat 0 / out-of-range degrade to the vehicle origin, never panic.
	if ComposeSeat(EntityKind(protocol.EntityTypeVehicle), pos, quat, 0) != pos || ComposeSeat(EntityKind(protocol.EntityTypeVehicle), pos, quat, 9) != pos {
		t.Fatal("invalid seat did not degrade to vehicle origin")
	}
}

func TestDisembarkStateOnGround(t *testing.T) {
	f := terrain.Generate(1337)
	pos, quat := SpawnRover(f)
	s := DisembarkState(EntityKind(protocol.EntityTypeVehicle), f, pos, quat)
	up := terrain.Normalize(s.Pos)
	if math.Abs(s.Pos.Len()-f.SampleRadius(up)) > 1e-9 {
		t.Fatal("disembarked body not on the surface")
	}
	if s.Vel != (Vec{}) {
		t.Fatal("disembarked body kept velocity")
	}
	if d := s.Pos.Sub(pos).Len(); d > 10 {
		t.Fatalf("disembarked %f m from the rover, want ≤ 10 (C32)", d)
	}
	if math.Abs(s.Facing.Dot(up)) > 1e-9 || math.Abs(s.Facing.Len()-1) > 1e-9 {
		t.Fatal("facing not a unit tangent vector")
	}
}

func TestVehicleSeatOf(t *testing.T) {
	v := NewVehicleState()
	v.Seats[1] = 42
	if v.SeatOf(42) != 1 || v.SeatOf(7) != 0 {
		t.Fatal("SeatOf lookup wrong")
	}
}

// Full collision (playtest 2026-10-02): a rover driven flat out at a wall
// 6 m ahead stops at it -- its hull never reaches past the wall's face --
// instead of driving through as it did when only the terrain stopped it.
func TestRoverStopsAtWall(t *testing.T) {
	f := flatField(150)
	e := newRover(f)
	fwd := Rotate(Quat(e.Quat), Vec{0, 0, 1})
	up := terrain.Normalize(Vec(e.Pos))
	fwd = terrain.Normalize(fwd.Sub(up.Scale(fwd.Dot(up))))
	start := Vec(e.Pos)
	wallC := start.Add(fwd.Scale(6)).Add(up.Scale(1))
	// A thin box across the path, faced along fwd: half 0.2 m along fwd.
	right := terrain.Normalize(terrain.Cross(up, fwd))
	q := quatFromBasis(right, up, fwd)
	wall := protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{float32(wallC[0]), float32(wallC[1]), float32(wallC[2])},
		Half:   [3]float32{5, 2, 0.2},
		Quat:   [4]float32{float32(q[0]), float32(q[1]), float32(q[2]), float32(q[3])},
	}
	v := e.Data.(*VehicleState)
	ctx := StepCtx{Terrain: f, CollidersFor: func(uint32) []protocol.Collider { return []protocol.Collider{wall} }}
	for i := 0; i < 120; i++ {
		v.Throttle, v.Steer = 1, 0
		StepRover(e, DT, ctx)
	}
	along := Vec(e.Pos).Sub(start).Dot(fwd)
	// Wall face at 6 - 0.2; the hull's front sphere reaches 0.55 + 0.80 ahead.
	if limit := 6 - 0.2 - (0.55 + 0.80) + 0.01; along > limit {
		t.Fatalf("rover reached %.2f m along its path, want ≤ %.2f (stopped at the wall)", along, limit)
	}
	if along < 3 {
		t.Fatalf("rover only moved %.2f m, want it to drive up to the wall", along)
	}
}

// A rover never wedges: on a slope past drive_slope_max, throttle uphill is
// refused but throttle that carries it downhill works — forward and reverse.
func TestRoverDrivesOffSteepSlope(t *testing.T) {
	f := slopeFieldY(1000, 45*math.Pi/180)
	up := Vec{0, 1, 0}
	if s := f.Slope(up); s <= DriveSlopeMax {
		t.Fatalf("test field slope %.1f° not past drive_slope_max", s*180/math.Pi)
	}
	park := func(facing Vec) *Ent {
		e := &Ent{
			Kind: EntityKind(protocol.EntityTypeVehicle),
			Pos:  [3]float64(up.Scale(f.SampleRadius(up))),
			Quat: [4]float64(QuatFromBasis(terrain.Cross(up, facing), up, facing)),
			Data: &VehicleState{Grounded: true},
		}
		return e
	}
	// Uphill is +Z on the +Y face (r grows with v = z/y).
	cases := []struct {
		name     string
		facing   Vec
		throttle float64
		moves    bool
	}{
		{"forward uphill", Vec{0, 0, 1}, 1, false},
		{"reverse uphill", Vec{0, 0, -1}, -1, false},
		{"forward downhill", Vec{0, 0, -1}, 1, true},
		{"reverse downhill", Vec{0, 0, 1}, -1, true},
	}
	for _, c := range cases {
		e := park(c.facing)
		driveTicks(e, f, 10, c.throttle, 0)
		sp := tangentSpeed(e)
		if c.moves && sp < 1 {
			t.Errorf("%s: speed %.3f m/s after 0.5 s, want it to drive off", c.name, sp)
		}
		if !c.moves && sp > 1e-6 {
			t.Errorf("%s: speed %.3g m/s, want uphill throttle refused", c.name, sp)
		}
	}
}
