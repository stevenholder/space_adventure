package sim

import (
	"math"
	"testing"

	"space-adventure/server/internal/terrain"
)

// flatField returns a Field with every cell at radius r (no generation).
func flatField(r float64) *terrain.Field {
	f := &terrain.Field{Seed: 0}
	for face := range terrain.NumFaces {
		for i := range terrain.FaceGrid * terrain.FaceGrid {
			f.Radii[face][i] = r
		}
	}
	return f
}

// rampFieldY returns a Field with constant base everywhere except the +Y
// face, whose radius ramps linearly with v: r = base + gain·v. At the face
// centre the slope is atan(gain/base) (walkable up to gain ≈ 177.7 for the
// 50° max_slope).
func rampFieldY(base, gain float64) *terrain.Field {
	f := flatField(base)
	for row := range terrain.FaceGrid {
		v := 2*float64(row)/(terrain.FaceGrid-1) - 1
		for col := range terrain.FaceGrid {
			f.Radii[terrain.FacePY][row*terrain.FaceGrid+col] = base + gain*v
		}
	}
	return f
}

func dist(a, b Vec) float64 { return a.Sub(b).Len() }

func finite(v Vec) bool {
	for _, c := range v {
		if math.IsNaN(c) || math.IsInf(c, 0) {
			return false
		}
	}
	return true
}

func TestSpawn(t *testing.T) {
	f := terrain.Generate(1337)
	s := SpawnState(f)
	up := terrain.Normalize(terrain.SpawnDir)
	if h := s.Pos.Len() - f.SampleRadius(up); h < -1e-9 {
		t.Fatalf("spawn below surface: h=%f", h)
	}
	if !s.Grounded {
		t.Fatal("spawn must be grounded (flat spawn disc contract)")
	}
	if s.Vel != (Vec{}) {
		t.Fatalf("spawn velocity must be zero: %v", s.Vel)
	}
	if math.Abs(s.Facing.Dot(up)) > 1e-9 {
		t.Fatalf("facing not tangent to up: %f", s.Facing.Dot(up))
	}
	if d := dist(s.Facing, Vec{1, 0, 0}); d > 1e-9 {
		t.Fatalf("default spawn facing must be world +X: %v", s.Facing)
	}
}

func TestWalkSpeedFlat(t *testing.T) {
	f := flatField(150)
	s := SpawnState(f)
	prev := s.Facing
	for i := 0; i < 100; i++ {
		prev = Step(&s, Input{MoveY: 1, Look: s.Facing}, prev, f, DT)
	}
	if d := s.Vel.Len() - WalkSpeed; d < -1e-3 || d > 1e-9 {
		t.Fatalf("walk speed not converged: %f", s.Vel.Len())
	}
	up := terrain.Normalize(s.Pos)
	if h := s.Pos.Len() - f.SampleRadius(up); h < -1e-9 {
		t.Fatalf("below surface: h=%f", h)
	}
	if !s.Grounded {
		t.Fatal("must stay grounded on flat ground")
	}
	if s.Pos[0] < 21 || s.Pos[0] > 23 {
		t.Fatalf("5 s at walk speed should cover ~22 m: x=%f", s.Pos[0])
	}
}

func TestSprintSpeed(t *testing.T) {
	f := flatField(150)
	s := SpawnState(f)
	prev := s.Facing
	for i := 0; i < 30; i++ {
		prev = Step(&s, Input{MoveY: 1, Look: s.Facing, ActionMask: ActionSprint}, prev, f, DT)
	}
	if d := s.Vel.Len() - SprintSpeed; d < -1e-3 || d > 1e-9 {
		t.Fatalf("sprint speed not converged: %f", s.Vel.Len())
	}
}

func TestBackwardFullSpeed(t *testing.T) {
	f := flatField(150)
	s := SpawnState(f)
	prev := s.Facing
	for i := 0; i < 30; i++ {
		prev = Step(&s, Input{MoveY: -1, Look: s.Facing}, prev, f, DT)
	}
	if d := s.Vel.Len() - WalkSpeed; d < -1e-3 || d > 1e-9 {
		t.Fatalf("backward must be full speed: %f", s.Vel.Len())
	}
	if c := s.Vel.Dot(s.Facing); c > -WalkSpeed+0.5 {
		t.Fatalf("backward must be −facing·speed: dot=%f", c)
	}
}

func TestJumpApexAndAirtime(t *testing.T) {
	f := flatField(150)
	s := SpawnState(f)
	prev := s.Facing
	maxH := 0.0
	air := 0
	landedAt := -1
	for i := 0; i < 40; i++ {
		in := Input{Look: s.Facing}
		if i == 0 {
			in.ActionMask = ActionJump
		}
		prev = Step(&s, in, prev, f, DT)
		if h := s.Pos.Len() - 150; h > maxH {
			maxH = h
		}
		if i > 0 && !s.Grounded {
			air++
		}
		if i > 0 && s.Grounded && landedAt < 0 {
			landedAt = i
		}
	}
	// Analytic apex v²/2g = 1.03 m, hang time 2v/g = 0.92 s ≈ 18 ticks;
	// semi-implicit Euler overshoots the apex slightly.
	if maxH < 1.0 || maxH > 1.25 {
		t.Fatalf("apex out of range: %f m", maxH)
	}
	if landedAt < 16 || landedAt > 24 {
		t.Fatalf("airtime out of range: landed at tick %d", landedAt)
	}
	if air < 15 || air > 24 {
		t.Fatalf("airborne ticks out of range: %d", air)
	}
	if !s.Grounded {
		t.Fatal("must be grounded at the end")
	}
}

// TestSlideOverSteep: contact on ground steeper than max_slope is SLIDE —
// no walk accel, no friction, gravity along the downslope tangent.
//
// The GDD integrator zeroes radial velocity against the radial direction
// each tick; on an over-steep ramp the surface recedes faster than the
// body falls, so once the per-tick recession exceeds ground_snap the body
// hops (AIR) before re-landing further down. Both ends run the same code,
// so the hops are deterministic. We assert the GDD invariants: never below
// the surface, never grounded, accelerating downhill.
func TestSlideOverSteep(t *testing.T) {
	f := rampFieldY(150, 200) // 53° slope at the face centre — unwalkable
	up := Vec{0, 1, 0}
	r := f.SampleRadius(up)
	s := State{
		Pos:      up.Scale(r + 0.05), // in the glue band, not grounded
		Vel:      Vec{0, 0, -1},      // downhill is −Z on the +Y face
		Grounded: false,
		Facing:   Vec{0, 0, -1},
	}
	prev := s.Facing
	zStart := s.Pos[2]
	for i := 0; i < 10; i++ {
		prev = Step(&s, Input{Look: s.Facing}, prev, f, DT)
		if h := s.Pos.Len() - f.SampleRadius(terrain.Normalize(s.Pos)); h < -1e-9 {
			t.Fatalf("tick %d: below the surface: h=%f", i, h)
		}
		if s.Grounded {
			t.Fatal("53° slope must not be walkable")
		}
	}
	if s.Vel[2] > -2.5 {
		t.Fatalf("slide not accelerating downhill: vel=%v", s.Vel)
	}
	if s.Pos[2] >= zStart {
		t.Fatalf("slide must descend the ramp: z=%f start=%f", s.Pos[2], zStart)
	}
}

// TestWallSlideCliff: a step above max_step is a wall — wallSlide stops the
// body at first contact instead of letting it pass through.
func TestWallSlideCliff(t *testing.T) {
	f := flatField(150)
	// Cliff on the +Y face: rows ≥ 32 (v ≥ 0) rise 10 m over one grid cell.
	for row := 32; row < terrain.FaceGrid; row++ {
		for col := range terrain.FaceGrid {
			f.Radii[terrain.FacePY][row*terrain.FaceGrid+col] = 160
		}
	}
	// Start 9 m short of the cliff, walking toward it (+Z on the +Y face).
	a := 0.06
	d := Vec{0, math.Cos(a), -math.Sin(a)}
	facing := terrain.Normalize(tangential(Vec{0, 0, 1}, d))
	s := State{
		Pos:      d.Scale(150),
		Vel:      Vec{},
		Grounded: true,
		Facing:   facing,
	}
	prev := facing
	for i := 0; i < 60; i++ {
		prev = Step(&s, Input{MoveY: 1, Look: facing}, prev, f, DT)
	}
	if s.Pos[2] > -4.2 {
		t.Fatalf("climbed over the cliff: z=%f", s.Pos[2])
	}
	if s.Pos[2] < -5.2 {
		t.Fatalf("did not reach the cliff: z=%f", s.Pos[2])
	}
	if !s.Grounded {
		t.Fatal("must stay grounded while wall-sliding")
	}
	if h := s.Pos.Len() - f.SampleRadius(terrain.Normalize(s.Pos)); h < -1e-9 || h > 0.5 {
		t.Fatalf("not at the cliff face: h=%f", h)
	}
}

// TestSeamCrossing: crossing a cube-face seam must be invisible (GDD edge
// cases). The player walks over the +Y/+Z seam on the generated field.
func TestSeamCrossing(t *testing.T) {
	f := terrain.Generate(1337)
	a := math.Atan(0.975) // just short of the v = 1 seam
	d := Vec{0, math.Cos(a), math.Sin(a)}
	r := f.SampleRadius(d)
	facing := terrain.Normalize(tangential(Vec{0, 0, 1}, d))
	s := State{
		Pos:      d.Scale(r),
		Vel:      Vec{},
		Grounded: f.Walkable(d),
		Facing:   facing,
	}
	prev := facing
	var prevPos Vec
	for i := 0; i < 40; i++ {
		prev = Step(&s, Input{MoveY: 1, Look: facing}, prev, f, DT)
		if !finite(s.Pos) || !finite(s.Vel) {
			t.Fatalf("tick %d: non-finite state after seam crossing", i)
		}
		if rad := s.Pos.Len(); rad < terrain.RadiusMin-1 || rad > terrain.RadiusMax+1 {
			t.Fatalf("tick %d: radius out of range: %f", i, rad)
		}
		if i > 0 {
			if step := s.Pos.Sub(prevPos).Len(); step > 1.0 {
				t.Fatalf("tick %d: teleport across seam: %f m", i, step)
			}
		}
		prevPos = s.Pos
	}
}

// TestDeterminism: same seed + same input stream → bit-identical
// trajectories, even across independently generated fields.
func TestDeterminism(t *testing.T) {
	a := terrain.Generate(42)
	b := terrain.Generate(42)
	inputs := make([]Input, 300)
	for i := range inputs {
		mask := uint16(0)
		if i%40 == 0 {
			mask |= ActionJump
		}
		if i%60 == 0 {
			mask |= ActionSprint
		}
		inputs[i] = Input{
			MoveX:      math.Sin(float64(i)*0.07) * 0.9,
			MoveY:      1.0,
			Look:       Vec{math.Cos(float64(i) * 0.03), 0.4, math.Sin(float64(i) * 0.03)},
			ActionMask: mask,
		}
	}
	run := func(f *terrain.Field) []State {
		s := SpawnState(f)
		prev := s.Facing
		out := make([]State, 0, len(inputs))
		for _, in := range inputs {
			prev = Step(&s, in, prev, f, DT)
			out = append(out, s)
		}
		return out
	}
	ra, rb := run(a), run(b)
	if len(ra) != len(rb) {
		t.Fatalf("trajectory length mismatch: %d vs %d", len(ra), len(rb))
	}
	for i := range ra {
		if ra[i] != rb[i] {
			t.Fatalf("tick %d diverged:\n%+v\n%+v", i+1, ra[i], rb[i])
		}
	}
}

func TestOrientationQuat(t *testing.T) {
	f := flatField(150)
	s := SpawnState(f)
	q := s.OrientationQuat()
	if n := q[0]*q[0] + q[1]*q[1] + q[2]*q[2] + q[3]*q[3]; math.Abs(n-1) > 1e-12 {
		t.Fatalf("quaternion not unit: %f", n)
	}
	// Spawn: up (0,1,0), facing (1,0,0), right = up × facing = (0,0,−1).
	if d := dist(Rotate(q, Vec{1, 0, 0}), Vec{0, 0, -1}); d > 1e-9 {
		t.Fatalf("local X must map to right: %v", Rotate(q, Vec{1, 0, 0}))
	}
	if d := dist(Rotate(q, Vec{0, 1, 0}), Vec{0, 1, 0}); d > 1e-9 {
		t.Fatalf("local Y must map to up: %v", Rotate(q, Vec{0, 1, 0}))
	}
	if d := dist(Rotate(q, Vec{0, 0, 1}), Vec{1, 0, 0}); d > 1e-9 {
		t.Fatalf("local Z must map to facing: %v", Rotate(q, Vec{0, 0, 1}))
	}
	// Non-trivial azimuth.
	facing := Vec{math.Cos(0.6), 0, math.Sin(0.6)}
	s2 := State{Pos: Vec{0, 150, 0}, Facing: facing, Grounded: true}
	q2 := s2.OrientationQuat()
	right := terrain.Cross(Vec{0, 1, 0}, facing)
	if d := dist(Rotate(q2, Vec{1, 0, 0}), right); d > 1e-9 {
		t.Fatalf("right mismatch: %v", Rotate(q2, Vec{1, 0, 0}))
	}
	if d := dist(Rotate(q2, Vec{0, 1, 0}), Vec{0, 1, 0}); d > 1e-9 {
		t.Fatalf("up mismatch: %v", Rotate(q2, Vec{0, 1, 0}))
	}
	if d := dist(Rotate(q2, Vec{0, 0, 1}), facing); d > 1e-9 {
		t.Fatalf("facing mismatch: %v", Rotate(q2, Vec{0, 0, 1}))
	}
}

func TestClampLook(t *testing.T) {
	up := Vec{0, 1, 0}
	pf := Vec{1, 0, 0}
	// Exactly up: degenerate tangent → azimuth from previous facing.
	r := clampLook(Vec{0, 1, 0}, up, pf)
	want := up.Scale(math.Cos(LookClamp)).Add(Vec{1, 0, 0}.Scale(math.Sin(LookClamp)))
	if d := dist(r, want); d > 1e-12 {
		t.Fatalf("straight-up clamp: %v", r)
	}
	// 0.5° off up → inside the cone: pushed out to exactly 1°, azimuth
	// (+Z) preserved.
	l05 := up.Scale(math.Cos(0.5 * math.Pi / 180)).Add(Vec{0, 0, 1}.Scale(math.Sin(0.5 * math.Pi / 180)))
	r8 := clampLook(l05, up, pf)
	if math.Abs(r8.Dot(up)-math.Cos(LookClamp)) > 1e-9 {
		t.Fatalf("0.5° look not clamped to 1°: dot=%f", r8.Dot(up))
	}
	tang := terrain.Normalize(r8.Sub(up.Scale(r8.Dot(up))))
	if d := dist(tang, Vec{0, 0, 1}); d > 1e-9 {
		t.Fatalf("azimuth not preserved: %v", tang)
	}
	// 45° off up → unchanged.
	l45 := up.Scale(math.Sqrt(0.5)).Add(Vec{0, 0, 1}.Scale(math.Sqrt(0.5)))
	if d := dist(clampLook(l45, up, pf), l45); d > 1e-12 {
		t.Fatal("45° look must pass through unchanged")
	}
	// Exactly −up: clamped 1° off −up, azimuth from previous facing.
	rNeg := clampLook(Vec{0, -1, 0}, up, pf)
	wantNeg := up.Scale(-math.Cos(LookClamp)).Add(Vec{1, 0, 0}.Scale(math.Sin(LookClamp)))
	if d := dist(rNeg, wantNeg); d > 1e-12 {
		t.Fatalf("straight-down clamp: %v", rNeg)
	}
}

func TestFacingHold(t *testing.T) {
	f := flatField(150)
	s := SpawnState(f) // facing (1,0,0)
	up := Vec{0, 1, 0}
	// 3° off vertical: |tang| = sin 3° = 0.052 < 0.1 → facing holds.
	deg3 := 3 * math.Pi / 180
	lookSmall := up.Scale(math.Cos(deg3)).Add(Vec{1, 0, 0}.Scale(math.Sin(deg3)))
	held := s.Facing
	prev := lookSmall
	prev = Step(&s, Input{Look: lookSmall}, prev, f, DT)
	if s.Facing != held {
		t.Fatalf("facing must hold for near-vertical look: %v → %v", held, s.Facing)
	}
	// 10° off vertical: |tang| = sin 10° = 0.174 ≥ 0.1 → facing updates.
	deg10 := 10 * math.Pi / 180
	lookBig := up.Scale(math.Cos(deg10)).Add(Vec{1, 0, 0}.Scale(math.Sin(deg10)))
	Step(&s, Input{Look: lookBig}, prev, f, DT)
	want := terrain.Normalize(lookBig.Sub(up.Scale(lookBig.Dot(up))))
	if d := dist(s.Facing, want); d > 1e-9 {
		t.Fatalf("facing must follow stable-azimuth look: %v vs %v", s.Facing, want)
	}
}

func TestInputSanitise(t *testing.T) {
	f := flatField(150)
	// Non-finite axes → 0: the body stands still.
	s := SpawnState(f)
	prev := s.Facing
	before := s.Pos
	prev = Step(&s, Input{MoveX: math.NaN(), MoveY: math.Inf(1), Look: s.Facing}, prev, f, DT)
	if s.Pos != before || s.Vel != (Vec{}) {
		t.Fatalf("non-finite input must not move the body: pos=%v vel=%v", s.Pos, s.Vel)
	}
	// Pair clamped to unit length: (2, 2) is a diagonal, still walk speed.
	s = SpawnState(f)
	prev = s.Facing
	for i := 0; i < 30; i++ {
		prev = Step(&s, Input{MoveX: 2, MoveY: 2, Look: s.Facing}, prev, f, DT)
	}
	if s.Vel.Len() > WalkSpeed+1e-9 {
		t.Fatalf("diagonal must not exceed walk speed: %f", s.Vel.Len())
	}
	if d := s.Vel.Len() - WalkSpeed; d < -1e-3 {
		t.Fatalf("diagonal speed not converged: %f", s.Vel.Len())
	}
	// Degenerate look → previous look fallback; no poisoning.
	s = SpawnState(f)
	prev = s.Facing
	for i := 0; i < 5; i++ {
		prev = Step(&s, Input{MoveY: 1, Look: Vec{0, 0, 0}}, prev, f, DT)
	}
	if !finite(s.Pos) || !finite(s.Vel) || !finite(s.Facing) {
		t.Fatal("degenerate look poisoned state")
	}
	up := terrain.Normalize(s.Pos)
	// The carried facing is re-projected against the previous tick's up, so
	// it lags the current up by one tick of rotation (≈ v·dt/r); the GDD
	// carries the held value without re-projecting it.
	if math.Abs(s.Facing.Dot(up)) > 1e-2 {
		t.Fatal("facing left the tangent plane")
	}
}

// TestTerminalVelocity: an M1-scale fall (100 m) never reaches terminal
// speed, so the clamp stays a safety valve (GDD rule table).
func TestTerminalVelocity(t *testing.T) {
	f := flatField(150)
	s := State{Pos: Vec{0, 250, 0}, Vel: Vec{}, Grounded: false, Facing: Vec{1, 0, 0}}
	prev := s.Facing
	maxFall, landed := 0.0, false
	for i := 0; i < 400 && !landed; i++ {
		prev = Step(&s, Input{Look: s.Facing}, prev, f, DT)
		up := terrain.Normalize(s.Pos)
		if vr := s.Vel.Dot(up); vr < -maxFall {
			maxFall = -vr
		}
		landed = s.Grounded
	}
	if !landed {
		t.Fatal("did not land")
	}
	if maxFall > TerminalSpeed+1e-6 {
		t.Fatalf("terminal speed exceeded: %f", maxFall)
	}
	want := math.Sqrt(2 * Gravity * 100) // ≈ 44.3 m/s
	if d := maxFall - want; d > 1.0 || d < -1.0 {
		t.Fatalf("fall speed off: %f (want %f)", maxFall, want)
	}
}

// TestTerminalClamp pins the GDD integrator's clamp (lines 394–395): when
// the post-gravity radial speed is below −terminal_speed,
// vel ← vel − up·(dot(vel,up) + terminal_speed) clamps it to exactly
// −terminal_speed. Inert in M1 (max fall speed ~27 m/s < 60).
func TestTerminalClamp(t *testing.T) {
	f := flatField(150)
	s := State{Pos: Vec{0, 250, 0}, Vel: Vec{0, -61, 0}, Grounded: false, Facing: Vec{1, 0, 0}}
	Step(&s, Input{Look: s.Facing}, s.Facing, f, DT)
	up := terrain.Normalize(s.Pos)
	// Gravity first: vr = −61 − 9.8·dt = −61.49 < −60; clamp:
	// vr′ = vr − (vr + 60) = exactly −60.
	if d := s.Vel.Dot(up) + TerminalSpeed; math.Abs(d) > 1e-9 {
		t.Fatalf("downward radial speed not clamped to −terminal_speed: %f", s.Vel.Dot(up))
	}
	if d := s.Vel.Sub(up.Scale(s.Vel.Dot(up))).Len(); d > 1e-9 {
		t.Fatalf("clamp must not add tangential speed: %v", s.Vel)
	}
}

// TestInvariantsRandomWalk: property checks after every step (GDD
// "Invariants, checked after every step") over a deterministic pseudo-random
// input stream on the generated field.
func TestInvariantsRandomWalk(t *testing.T) {
	f := terrain.Generate(1337)
	s := SpawnState(f)
	prev := s.Facing
	var rng uint64 = 0x12345678
	next := func() float64 {
		rng ^= rng << 13
		rng ^= rng >> 7
		rng ^= rng << 17
		return float64(rng>>11)/float64(uint64(1)<<53) - 1 // [−1, 1)
	}
	var prevPos Vec
	for i := 0; i < 600; i++ {
		mask := uint16(0)
		if i%37 == 0 {
			mask |= ActionJump
		}
		if i%23 == 0 {
			mask |= ActionSprint
		}
		in := Input{
			MoveX:      next(),
			MoveY:      next(),
			Look:       Vec{next(), next() * 0.5, next()},
			ActionMask: mask,
		}
		// Pre-step mode (GDD step 3 uses the state carried in).
		upOld := terrain.Normalize(s.Pos)
		hOld := s.Pos.Len() - f.SampleRadius(upOld)
		preModeAir := !s.Grounded && hOld > GroundSnap
		prev = Step(&s, in, prev, f, DT)
		if !finite(s.Pos) || !finite(s.Vel) || !finite(s.Facing) {
			t.Fatalf("tick %d: non-finite state", i)
		}
		up := terrain.Normalize(s.Pos)
		h := s.Pos.Len() - f.SampleRadius(up)
		if h < -1e-9 {
			t.Fatalf("tick %d: below surface: h=%f", i, h)
		}
		// Tangent velocity in contact (GDD invariant): snapped to the
		// surface, or GROUND/SLIDE inside the glue band. An AIR body inside
		// the glue band keeps its radial velocity by design (resolve: AIR →
		// no re-projection).
		inContact := h < 1e-9 || (h <= GroundSnap && !preModeAir)
		if inContact && math.Abs(s.Vel.Dot(up)) > 1e-9 {
			t.Fatalf("tick %d: radial velocity while in contact: %f", i, s.Vel.Dot(up))
		}
		if s.Grounded && f.Slope(up) > MaxSlope+1e-9 {
			t.Fatalf("tick %d: grounded on unwalkable slope", i)
		}
		if i > 0 {
			if step := s.Pos.Sub(prevPos).Len(); step > 1.0 {
				t.Fatalf("tick %d: teleport: %f m", i, step)
			}
		}
		prevPos = s.Pos
	}
}
