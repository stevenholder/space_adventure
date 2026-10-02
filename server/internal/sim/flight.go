// Phase 5 — stepShip: the GDD flight model ("The flight model — M2
// context", steps 1–4) plus the Phase 5 space regime and landing rules,
// mirrored line-for-line by client/shared/Sim/Flight.cs. C34 diffs
// the two at ≤ 1e-6 over ≥ 1000 ticks, so any change lands in both.
//
// One deviation from the pseudocode, deliberate and mirrored: the GDD line
// `q ← normalize(s.quat ⊗ axisAngle(rotate(s.quat, ω) · dt))` post-
// multiplies a WORLD-axis rotation, which contradicts its own comment
// ("rotate about local axes") — ω's components are about local X/Y/Z, and
// the local form is q ⊗ axisAngle(ω · dt). This file implements the local
// form; the GDD line carries the correction note.

package sim

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// GDD "Flight model" + "The flight model — M2 context" + "Phase 5" tables.
const (
	FlightAccel      = 12.0
	FlightAccelBoost = 24.0
	FlightVmax       = 40.0
	FlightVmaxBoost  = 80.0
	FlightDamp       = 0.5
	AngvelMax        = 4.0
	AngvelMaxRoll    = 2.0
	AngvelTau        = 0.15

	SpaceRadius  = 260.0
	SpaceHyst    = 10.0
	LandSpeedMax = 8.0
	BounceK      = 0.3
	LandGrip     = 3.0
)

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeShip), StepShip)
}

// StepShip advances one ship by dt. Input is read from ShipState, already
// sanitised by the gateway; a ship with no pilot steps with zero input and
// coasts (in atmosphere) or drifts (in space).
func StepShip(e *Ent, dt float64, ctx StepCtx) {
	s, ok := e.Data.(*ShipState)
	if !ok || ctx.Terrain == nil {
		return // tolerant, same rule as every other kind
	}

	thrust := sanitiseAxis(s.Thrust)
	roll := sanitiseAxis(s.Roll)
	yawRate := clampRate(s.YawRate)
	pitchRate := clampRate(s.PitchRate)

	pos, vel := Vec(e.Pos), Vec(e.Vel)
	q := Quat(e.Quat)
	up := terrain.Normalize(pos)

	// 1. Rotation — first-order toward the target, ship frame; ω carried.
	wt := Vec{pitchRate, yawRate, roll * AngvelMaxRoll}
	k := math.Exp(-dt / AngvelTau)
	w := Vec{
		wt[0] + (s.Omega[0]-wt[0])*k,
		wt[1] + (s.Omega[1]-wt[1])*k,
		wt[2] + (s.Omega[2]-wt[2])*k,
	}
	if ang := w.Len() * dt; ang > 1e-12 {
		q = quatNormalize(quatMulSim(q, quatAxisAngle(w.Scale(1/w.Len()), ang)))
	}

	// 2. Translation — thrust along ship forward; damp only in atmosphere.
	fwd := Rotate(q, Vec{0, 0, 1})
	accel := FlightAccel * effMult(s.EffMult)
	cap := FlightVmax
	if s.Boost {
		accel, cap = FlightAccelBoost*effMult(s.EffMult), FlightVmaxBoost
	}
	factor := thrust
	if thrust < 0 {
		factor = 0.5 * thrust
	}
	vel = vel.Add(fwd.Scale(accel * factor * dt))
	if thrust == 0 && !s.Space {
		vel = vel.Scale(math.Exp(-FlightDamp * dt))
	}
	if l := vel.Len(); l > cap {
		vel = vel.Scale(cap / l)
	}

	// 3. Gravity — airborne and in atmosphere only.
	if !s.Grounded && !s.Space {
		vel = vel.Sub(up.Scale(Gravity * dt))
	}

	// Grounded grip (GDD "Landing"): a landed ship slides to a stop and
	// then holds — thrust still works, which is the takeoff.
	if s.Grounded {
		vr := vel.Dot(up)
		vt := vel.Sub(up.Scale(vr))
		vt = vt.Scale(math.Exp(-LandGrip * dt))
		if vt.Len() < HoldSpeed {
			vt = Vec{}
		}
		vel = vt.Add(up.Scale(vr))
	}

	// 4. Integrate, then origin-point contact with the landing rules.
	pos = pos.Add(vel.Scale(dt))
	// 4a: the hull out of anything solid, by this tick's attitude (the
	// rover's step 8b, three spheres for the fuselage).
	if ctx.CollidersFor != nil {
		p2, v2 := ResolveHull([3]float64(pos), [3]float64(vel), q, ShipHull, ctx.CollidersFor(e.ID))
		pos, vel = Vec(p2), Vec(v2)
	}
	dir := terrain.Normalize(pos)
	r := ctx.Terrain.SampleRadius(dir)
	if pos.Len() < r {
		pos = dir.Scale(r)
		if vr := vel.Dot(dir); vr < 0 {
			if -vr <= LandSpeedMax {
				// Settle: kill the inward radial, grounded.
				vel = vel.Sub(dir.Scale(vr))
				s.Grounded = true
			} else {
				// Hard landing: reflect at bounce_k restitution — the
				// penalty is the bounce; tunnelling is impossible either
				// way, the clamp above already ran.
				vel = vel.Sub(dir.Scale(vr * (1 + BounceK)))
				s.Grounded = false
			}
		}
	}
	if pos.Len() > r+GroundSnap {
		s.Grounded = false
	}

	// Space regime, hysteresis on the radius (GDD "Space regime"): the
	// transition changes no state but the flag.
	switch {
	case pos.Len() >= SpaceRadius+SpaceHyst/2:
		s.Space = true
	case pos.Len() <= SpaceRadius-SpaceHyst/2:
		s.Space = false
	}

	e.Pos, e.Vel = [3]float64(pos), [3]float64(vel)
	e.Quat = [4]float64(q)
	s.Omega = w

	e.Flags = 0
	if s.Grounded {
		e.Flags |= protocol.FlagGrounded
	}
	if s.Space {
		e.Flags |= protocol.FlagSpace
	}
}

// clampRate sanitises a rate input to ±angvel_max, non-finite → 0.
func clampRate(x float64) float64 {
	if math.IsNaN(x) || math.IsInf(x, 0) {
		return 0
	}
	if x > AngvelMax {
		return AngvelMax
	}
	if x < -AngvelMax {
		return -AngvelMax
	}
	return x
}

// quatAxisAngle builds a rotation of ang radians about unit axis. The same
// maths as defs' private copy, deliberately duplicated (Sim depends on
// nothing) and mirrored char-for-char in Flight.cs.
func quatAxisAngle(axis Vec, ang float64) Quat {
	half := ang / 2
	s := math.Sin(half)
	return Quat{axis[0] * s, axis[1] * s, axis[2] * s, math.Cos(half)}
}

// quatMulSim returns a⊗b (apply b first, then a — Hamilton product).
func quatMulSim(a, b Quat) Quat {
	return Quat{
		a[3]*b[0] + a[0]*b[3] + a[1]*b[2] - a[2]*b[1],
		a[3]*b[1] - a[0]*b[2] + a[1]*b[3] + a[2]*b[0],
		a[3]*b[2] + a[0]*b[1] - a[1]*b[0] + a[2]*b[3],
		a[3]*b[3] - a[0]*b[0] - a[1]*b[1] - a[2]*b[2],
	}
}

func quatNormalize(q Quat) Quat {
	n := math.Sqrt(q[0]*q[0] + q[1]*q[1] + q[2]*q[2] + q[3]*q[3])
	if n == 0 {
		return Quat{0, 0, 0, 1}
	}
	return Quat{q[0] / n, q[1] / n, q[2] / n, q[3] / n}
}
