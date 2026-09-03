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
