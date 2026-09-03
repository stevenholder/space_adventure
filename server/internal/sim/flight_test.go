package sim

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

func newShip(t *terrain.Field) *Ent {
	pos, quat := SpawnShip(t, nil)
	return &Ent{
		ID:   9500,
		Kind: EntityKind(protocol.EntityTypeShip),
		Pos:  [3]float64(pos),
		Quat: [4]float64(quat),
		Data: NewShipState(""),
	}
}

func flyTicks(e *Ent, f *terrain.Field, n int, thrust, roll, yaw, pitch float64, boost bool) {
	s := e.Data.(*ShipState)
	ctx := StepCtx{Terrain: f}
	for i := 0; i < n; i++ {
		s.Thrust, s.Roll, s.YawRate, s.PitchRate, s.Boost = thrust, roll, yaw, pitch, boost
		StepShip(e, DT, ctx)
	}
}

func radiusOf(e *Ent) float64 { return Vec(e.Pos).Len() }

// Pitch up and thrust: the ship must leave the ground, climb through the
// boundary, flip to the space regime, and see gravity/drag stop.
func TestShipClimbsToSpace(t *testing.T) {
	f := terrain.Generate(1337)
	e := newShip(f)
	s := e.Data.(*ShipState)

	// Nose up: NEGATIVE pitch about local +X (GDD input map, as corrected —
	// positive is nose down by the right-hand rule). ~1.3 rad of pitch, a
	// steep climb without over-rotating past vertical.
	flyTicks(e, f, 25, 1, 0, 0, -1.3, false)
	flyTicks(e, f, 35, 1, 0, 0, 0, false)
	if s.Grounded {
		t.Fatal("still grounded after 3 s of nose-up thrust")
	}
	// Keep thrusting: boundary is 260 m, start ~150.
	flyTicks(e, f, 400, 1, 0, 0, 0, false)
	if !s.Space {
		t.Fatalf("not in space at radius %.1f m", radiusOf(e))
	}
	if e.Flags&protocol.FlagSpace == 0 {
		t.Fatal("space flag not mirrored onto the wire byte")
	}

	// In space, unthrottled: no drag, no gravity — speed holds.
	flyTicks(e, f, 1, 0, 0, 0, 0, false)
	v0 := Vec(e.Vel).Len()
	flyTicks(e, f, 100, 0, 0, 0, 0, false)
	v1 := Vec(e.Vel).Len()
	if math.Abs(v1-v0) > 1e-9 {
		t.Fatalf("space drift changed speed: %.6f -> %.6f", v0, v1)
	}
}

// Hysteresis: hovering exactly at the boundary radius must not flap the
// regime (C35).
func TestSpaceHysteresis(t *testing.T) {
	f := terrain.Generate(1337)
	e := newShip(f)
	s := e.Data.(*ShipState)
	up := terrain.Normalize(Vec(e.Pos))

	// Park the state exactly at the boundary, airborne, stationary.
	e.Pos = [3]float64(up.Scale(SpaceRadius))
	e.Vel = [3]float64{}
	s.Grounded = false

	flips := 0
	last := s.Space
	// Gravity pulls it down through the band; count transitions.
	for i := 0; i < 200; i++ {
		flyTicks(e, f, 1, 0, 0, 0, 0, false)
		if s.Space != last {
			flips++
			last = s.Space
		}
	}
	if flips > 1 {
		t.Fatalf("regime flapped %d times at the boundary", flips)
	}
}

// Soft landing settles; hard landing bounces (C37).
func TestLandingSettleAndBounce(t *testing.T) {
	f := terrain.Generate(1337)

	drop := func(speed float64) (*ShipState, *Ent) {
		e := newShip(f)
		s := e.Data.(*ShipState)
		up := terrain.Normalize(Vec(e.Pos))
		e.Pos = [3]float64(up.Scale(Vec(e.Pos).Len() + 0.5))
		e.Vel = [3]float64(up.Scale(-speed))
		s.Grounded = false
		flyTicks(e, f, 3, 0, 0, 0, 0, false)
		return s, e
	}

	s, e := drop(4) // under land_speed_max
	if !s.Grounded {
		t.Fatal("soft landing did not settle")
	}
	up := terrain.Normalize(Vec(e.Pos))
	if Vec(e.Pos).Len() < f.SampleRadius(up)-1e-9 {
		t.Fatal("settled under the surface")
	}

	s, e = drop(20) // over land_speed_max
	if s.Grounded {
		t.Fatal("hard landing settled instead of bouncing")
	}
	up = terrain.Normalize(Vec(e.Pos))
	if vr := Vec(e.Vel).Dot(up); vr <= 0 {
		t.Fatalf("hard landing did not reflect: radial vel %.2f", vr)
	}
	if Vec(e.Pos).Len() < f.SampleRadius(up)-1e-9 {
		t.Fatal("hard landing tunnelled")
	}
}

// A grounded ship holds still (land_grip + hold_speed) and an unpiloted
// airborne ship in atmosphere damps to a stop and lands.
func TestUnpilotedShipComesToRest(t *testing.T) {
	f := terrain.Generate(1337)
	e := newShip(f)
	s := e.Data.(*ShipState)
	flyTicks(e, f, 40, 1, 0, 0, 1.0, false) // messy climb-out
	flyTicks(e, f, 2400, 0, 0, 0, 0, false) // 2 min of nobody at the stick
	if !s.Grounded {
		t.Fatalf("unpiloted ship never landed: radius %.1f, |v| %.2f", radiusOf(e), Vec(e.Vel).Len())
	}
	if v := Vec(e.Vel).Len(); v > 0.01 {
		t.Fatalf("landed ship still moving at %.3f m/s", v)
	}
}

func TestSpawnShipPadAndSeparation(t *testing.T) {
	f := terrain.Generate(1337)
	p1, q1 := SpawnShip(f, nil)
	p2, q2 := SpawnShip(f, nil)
	if p1 != p2 || q1 != q2 {
		t.Fatal("SpawnShip not deterministic")
	}
	if !f.Walkable(terrain.Normalize(p1)) {
		t.Fatal("pad on unwalkable ground")
	}
	spawn := SpawnState(f).Pos
	if d := p1.Sub(spawn).Len(); d < 10 || d > 100 {
		t.Fatalf("pad %f m from spawn", d)
	}
	// A second owner's ship takes a different slot.
	p3, _ := SpawnShip(f, []Vec{p1})
	if d := p3.Sub(p1).Len(); d < 6 {
		t.Fatalf("second ship %f m from the first, want >= 6", d)
	}
	// The rover parks elsewhere.
	rp, _ := SpawnRover(f)
	if d := rp.Sub(p1).Len(); d < 6 {
		t.Fatalf("pad %f m from the rover's parking spot", d)
	}
}

// The ship's step tolerates missing state or terrain, like every kind.
func TestShipTolerantStep(t *testing.T) {
	e := &Ent{Kind: EntityKind(protocol.EntityTypeShip), Pos: [3]float64{0, 150, 0}}
	StepShip(e, DT, StepCtx{})
	e.Data = NewShipState("")
	StepShip(e, DT, StepCtx{})
	if e.Pos != [3]float64{0, 150, 0} {
		t.Fatal("ship moved with no terrain in ctx")
	}
}
