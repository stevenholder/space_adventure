// Package sim is the authoritative on-foot movement model (GDD "M1 on-foot
// movement"). The server steps it once per fixed tick; the client re-runs
// the same function against the same terrain field for prediction
// (ARCHITECTURE "Network model"). The state is a pure function of
// (terrain field, spawn, input stream) — both ends keep State and nothing
// else.
//
// The integrator below follows the GDD "Integrator" steps 1–7 in exactly
// that order, and the rule table values. Reordering any step, or changing a
// threshold, desyncs client and server, so do not "clean up" the order.
package sim

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// Vec is the shared 3-vector type (alias of terrain.Vec).
type Vec = terrain.Vec

// Rule table (GDD "M1 on-foot movement" rule table). SI units, metres.
const (
	TickHz         = 20
	DT             = 1.0 / TickHz
	WalkSpeed      = 4.5                  // walk_speed
	SprintSpeed    = 7.5                  // sprint_speed
	AccelGround    = 50.0                 // accel_ground
	FrictionGround = 8.0                  // friction_ground
	AccelAir       = 8.0                  // accel_air
	Gravity        = 9.8                  // gravity
	JumpSpeed      = 4.5                  // jump_speed
	TerminalSpeed  = 60.0                 // terminal_speed
	MaxStep        = 0.3                  // max_step
	MaxSlope       = 50.0 * math.Pi / 180 // max_slope
	GroundSnap     = 0.15                 // ground_snap
	LookClamp      = 1.0 * math.Pi / 180  // look_clamp
	FacingHold     = 0.1                  // facing_hold
	WallTol        = 1e-3                 // wall_tol (wallSlide bisection)
	EpsDeg         = 1e-6                 // eps_degen
)

// Action mask bits (GDD "Input": action_mask u16).
const (
	ActionSprint uint16 = 0x0001
	ActionJump   uint16 = 0x0002
)

// mode is the per-tick movement mode partition (GDD integrator step 3).
type mode int

const (
	modeGround mode = iota
	modeSlide
	modeAir
)

// State is the per-entity movement state (GDD "State"). Nothing else is
// carried: look and the last-applied input are input-stream concerns.
type State struct {
	Pos      Vec  // position (world, on the planet surface or above it)
	Vel      Vec  // velocity (m/s, world frame)
	Grounded bool // carried from the previous tick (GDD mode partition)
	Facing   Vec  // held facing (unit, ~tangent to the surface)
}

// Input is one tick of command state (GDD "Input"), exactly the fields of
// the 24-byte input wire payload.
type Input struct {
	MoveX      float64 // strafe, +right, clamped to [−1, 1]
	MoveY      float64 // forward, clamped to [−1, 1]
	Look       Vec     // desired look direction (arbitrary length)
	ActionMask uint16

	// Colliders is not part of the 24-byte wire payload: it is the zone's
	// static collider list (GDD "Static colliders"), threaded through Input
	// because it is state the step already has access to. Nil/empty is a
	// no-op (GDD integrator step 8 skips its loop; step 9's re-seat then
	// leaves step 7's result untouched too).
	Colliders []protocol.Collider
}

// Quat is a rotation quaternion in (x, y, z, w) component order — the wire
// order of the snapshot entity row (Three.js Quaternion native order).
type Quat [4]float64

// SpawnState builds the spawn state (GDD "Spawn"): the spawn_dir point of
// the field, zero velocity, grounded per slopeOK, look and facing set to
// the tangent projection of (1,0,0) onto the spawn plane. Deterministic:
// the same field gives the same state.
func SpawnState(t *terrain.Field) State {
	up := terrain.Normalize(terrain.SpawnDir)
	pos := up.Scale(t.SampleRadius(up))
	look := tangential(Vec{1, 0, 0}, up)
	if look.Len() < EpsDeg {
		look = tangential(Vec{0, 0, 1}, up)
	}
	look = terrain.Normalize(look)
	return State{
		Pos:      pos,
		Vel:      Vec{},
		Grounded: t.Walkable(up),
		Facing:   look,
	}
}

// tangential projects v onto the plane perpendicular to up.
func tangential(v, up Vec) Vec { return v.Sub(up.Scale(v.Dot(up))) }

// Step applies one fixed-dt tick to s in place, in the exact step order of
// the GDD "Integrator" (steps 1–7). in is the raw command state for this
// tick; prevLook is the look of the last applied input (the spawn look
// before any input). Step returns the look actually applied after
// sanitisation so the caller can carry it forward as the next tick's
// prevLook.
func Step(s *State, in Input, prevLook Vec, t *terrain.Field, dt float64) Vec {
	// 1. Sanitise input.
	mx := sanitiseAxis(in.MoveX)
	my := sanitiseAxis(in.MoveY)
	if m := math.Hypot(mx, my); m > 1 {
		mx, my = mx/m, my/m
	}
	sprint := in.ActionMask&ActionSprint != 0
	jump := in.ActionMask&ActionJump != 0
	look := in.Look
	if !finiteVec(look) || look.Len() < EpsDeg {
		look = prevLook
	}
	look = terrain.Normalize(look)

	// 2. Frame and facing.
	up := terrain.Normalize(s.Pos)
	look = clampLook(look, up, s.Facing)
	tang := look.Sub(up.Scale(look.Dot(up)))
	if tang.Len() >= FacingHold {
		s.Facing = terrain.Normalize(tang)
	}
	// right = facing × up, NOT up × facing. In a right-handed frame the latter
	// points LEFT, which inverted A and D (GDD "Axis mapping": move_x is +right).
	right := terrain.Cross(s.Facing, up)

	// 3. Mode (from the state carried in).
	inContact := s.Pos.Len()-t.SampleRadius(up) <= GroundSnap
	mode := modeAir
	if s.Grounded {
		mode = modeGround
	} else if inContact {
		mode = modeSlide
	}

	// 4. Acceleration.
	wish := s.Facing.Scale(my).Add(right.Scale(mx))
	hasTarget := wish.Len() > EpsDeg
	target := Vec{}
	if hasTarget {
		speed := WalkSpeed
		if sprint {
			speed = SprintSpeed
		}
		target = terrain.Normalize(wish).Scale(speed)
	}
	switch mode {
	case modeGround:
		if hasTarget {
			s.Vel = approach(s.Vel, target, AccelGround*dt)
		} else {
			s.Vel = s.Vel.Scale(math.Exp(-FrictionGround * dt))
		}
	case modeSlide:
		n := t.SurfaceNormal(up)
		g := up.Scale(-Gravity)
		s.Vel = s.Vel.Add(g.Sub(n.Scale(g.Dot(n))).Scale(dt))
	case modeAir:
		vr := s.Vel.Dot(up)
		vt := s.Vel.Sub(up.Scale(vr))
		if hasTarget {
			vt = approach(vt, target, AccelAir*dt)
		}
		s.Vel = up.Scale(vr).Add(vt).Sub(up.Scale(Gravity * dt))
		if v := s.Vel.Dot(up); v < -TerminalSpeed {
			// GDD integrator line 395: clamp the downward radial speed to
			// exactly −terminal_speed.
			s.Vel = s.Vel.Sub(up.Scale(v + TerminalSpeed))
		}
	}

	// 5. Jump.
	if mode == modeGround && jump {
		s.Vel = s.Vel.Add(up.Scale(JumpSpeed))
	}

	// 6. Integrate.
	pOld := s.Pos
	s.Pos = s.Pos.Add(s.Vel.Scale(dt))

	// 7. Terrain resolution.
	resolve(s, pOld, mode, t)

	// 8-9. Static collider resolution + terrain re-seat (GDD "Static
	// colliders" -> "Integrator addition"). Immediately after step 7, the
	// single writer of Grounded above; an empty/nil collider list is a
	// documented no-op so this leaves step 7's result bit-identical. up is
	// re-derived from step 7's (possibly wallSlide- or glue-adjusted)
	// position, matching resolve's own internal recomputation.
	up = terrain.Normalize(s.Pos)
	s.Pos, s.Vel, s.Grounded = ResolveColliders(s.Pos, s.Vel, up, s.Grounded, in.Colliders,
		func(d [3]float64) float64 { return t.SampleRadius(Vec(d)) })
	return look
}

// resolve applies GDD step 7 (terrain resolution) to s in place. pOld is
// the pre-integration position (wallSlide needs the step segment); mode is
// the tick's mode.
func resolve(s *State, pOld Vec, mode mode, t *terrain.Field) {
	up := terrain.Normalize(s.Pos)
	h := s.Pos.Len() - t.SampleRadius(up)
	if h < 0 {
		if -h <= MaxStep {
			s.Pos = up.Scale(t.SampleRadius(up))
		} else {
			s.Pos = wallSlide(pOld, s.Pos, t)
			up = terrain.Normalize(s.Pos)
		}
		s.Vel = s.Vel.Sub(up.Scale(s.Vel.Dot(up)))
		s.Grounded = t.Walkable(up)
		return
	}
	if mode == modeAir {
		s.Grounded = false
		return
	}
	// Glue band (GDD resolve, "was GROUND or SLIDE"): ground_snap plus the
	// per-step downhill drop on the contact slope, so a body on steep ground
	// stays glued while the surface recedes faster than ground_snap per tick.
	// Flat ground (θ ≈ 0) keeps the band exactly ground_snap. |vel| is the
	// tangential speed: radial velocity is zeroed every contact tick, and on
	// a jump tick the jump's radial rise must not widen the band — the jump
	// margin jump_speed·dt − ground_snap (GDD invariants) holds only then.
	slopeDrop := tangential(s.Vel, up).Len() * DT * math.Max(0, math.Sin(t.Slope(up)))
	band := GroundSnap + slopeDrop
	if h <= band {
		s.Pos = up.Scale(t.SampleRadius(up))
		s.Vel = s.Vel.Sub(up.Scale(s.Vel.Dot(up)))
		s.Grounded = t.Walkable(up)
	} else {
		s.Grounded = false
	}
}

// wallSlide bisection-locates the last point of the segment [pOld, pNew]
// that is at or above the surface (GDD "wallSlide") and returns it. pOld
// is at or above the surface and pNew is embedded deeper than max_step.
func wallSlide(pOld, pNew Vec, t *terrain.Field) Vec {
	dir := pNew.Sub(pOld)
	lo, hi := 0.0, 1.0
	for hi-lo > WallTol {
		mid := (lo + hi) / 2
		p := pOld.Add(dir.Scale(mid))
		d := terrain.Normalize(p)
		if p.Len()-t.SampleRadius(d) >= 0 {
			lo = mid
		} else {
			hi = mid
		}
	}
	return pOld.Add(dir.Scale(lo))
}

// clampLook clamps l away from local up/down by look_clamp (GDD
// "clampLook"). prevFacing is the azimuth fallback when the look points
// exactly along ±up.
func clampLook(l, up, prevFacing Vec) Vec {
	l = terrain.Normalize(l)
	c := l.Dot(up)
	cl := math.Cos(LookClamp)
	if c >= cl {
		t := l.Sub(up.Scale(c))
		if t.Len() < EpsDeg {
			t = prevFacing
		}
		return up.Scale(cl).Add(terrain.Normalize(t).Scale(math.Sin(LookClamp)))
	}
	if c <= -cl {
		t := l.Sub(up.Scale(c))
		if t.Len() < EpsDeg {
			t = prevFacing
		}
		return up.Scale(-cl).Add(terrain.Normalize(t).Scale(math.Sin(LookClamp)))
	}
	return l
}

// approach moves v toward target by at most maxDelta (GDD "approach").
func approach(v, target Vec, maxDelta float64) Vec {
	d := target.Sub(v)
	if d.Len() <= maxDelta {
		return target
	}
	return v.Add(terrain.Normalize(d).Scale(maxDelta))
}

// OrientationQuat returns the entity's rotation as a (x, y, z, w)
// quaternion mapping the local frame (+X right, +Y up, +Z forward) to
// (right = up × facing, up = normalize(pos), forward = facing) (GDD
// "Snapshot encoding").
func (s *State) OrientationQuat() Quat {
	up := terrain.Normalize(s.Pos)
	return quatFromBasis(terrain.Cross(up, s.Facing), up, s.Facing)
}

// quatFromBasis builds the quaternion whose rotation has column vectors
// x, y, z (local X → x, local Y → y, local Z → z).
func quatFromBasis(x, y, z Vec) Quat {
	m00, m11, m22 := x[0], y[1], z[2]
	tr := m00 + m11 + m22
	var q Quat
	switch {
	case tr > 0:
		s := math.Sqrt(tr+1) * 2
		q = Quat{(y[2] - z[1]) / s, (z[0] - x[2]) / s, (x[1] - y[0]) / s, 0.25 * s}
	case m00 > m11 && m00 > m22:
		s := math.Sqrt(1+m00-m11-m22) * 2
		q = Quat{0.25 * s, (x[1] + y[0]) / s, (z[0] + x[2]) / s, (y[2] - z[1]) / s}
	case m11 > m22:
		s := math.Sqrt(1+m11-m00-m22) * 2
		q = Quat{(y[0] + x[1]) / s, 0.25 * s, (y[2] + z[1]) / s, (z[0] - x[2]) / s}
	default:
		s := math.Sqrt(1+m22-m00-m11) * 2
		q = Quat{(z[0] + x[2]) / s, (z[1] + y[2]) / s, 0.25 * s, (x[1] - y[0]) / s}
	}
	n := math.Sqrt(q[0]*q[0] + q[1]*q[1] + q[2]*q[2] + q[3]*q[3])
	if n > 0 {
		q = Quat{q[0] / n, q[1] / n, q[2] / n, q[3] / n}
	}
	return q
}

// Rotate applies quaternion q (x, y, z, w) to vector v.
func Rotate(q Quat, v Vec) Vec {
	x, y, z, w := q[0], q[1], q[2], q[3]
	// t = 2 * cross(qv, v)
	tx := 2 * (y*v[2] - z*v[1])
	ty := 2 * (z*v[0] - x*v[2])
	tz := 2 * (x*v[1] - y*v[0])
	return Vec{
		v[0] + w*tx + (y*tz - z*ty),
		v[1] + w*ty + (z*tx - x*tz),
		v[2] + w*tz + (x*ty - y*tx),
	}
}

func sanitiseAxis(x float64) float64 {
	if math.IsNaN(x) || math.IsInf(x, 0) {
		return 0
	}
	if x < -1 {
		return -1
	}
	if x > 1 {
		return 1
	}
	return x
}

func finiteVec(v Vec) bool {
	for _, c := range v {
		if math.IsNaN(c) || math.IsInf(c, 0) {
			return false
		}
	}
	return true
}
