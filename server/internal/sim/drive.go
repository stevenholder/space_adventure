// Phase 4 — stepRover, the ground drive model (GDD "Phase 4 — ground drive
// model", steps 1–10, implemented in order and mirrored line-for-line by
// client/shared/Sim/Drive.cs; C30 diffs the two at ≤ 1e-6 over ≥ 1000
// ticks, so any change here must land in both).
//
// The model is deliberately simpler than flight: no angular velocity state,
// no boost. Yaw comes straight off the steer input, lateral grip does the
// wheels' job, and terrain does the suspension — the rover's origin follows
// the surface exactly like a body's foot point. Airborne it is a ballistic
// brick: no steer, no throttle, no grip.

package sim

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// GDD "Ground drive model" rule table.
const (
	AccelDrive     = 8.0                  // accel_drive, m/s²
	VmaxDrive      = 16.0                 // vmax_drive, m/s
	SteerRate      = 1.2                  // steer_rate, rad/s
	Grip           = 6.0                  // grip, 1/s — lateral half-life ~0.12 s
	DampDrive      = 0.8                  // damp_drive, 1/s — coast half-life ~0.87 s
	HoldSpeed      = 0.1                  // hold_speed, m/s — parked is parked
	DriveSlopeMax  = 40.0 * math.Pi / 180 // drive_slope_max
	RoverSpawnDist = 20.0                 // rover_spawn_dist, m
)

// StepRover advances one rover by dt. Input (Throttle/Steer) is read from
// VehicleState, already sanitised by the gateway; a rover with no driver
// steps with zero input and coasts to a stop (GDD "Control handoff").
func StepRover(e *Ent, dt float64, ctx StepCtx) {
	v, ok := e.Data.(*VehicleState)
	if !ok || ctx.Terrain == nil {
		// Tolerant on purpose (see spawner.go): an Ent of the right Kind
		// with no state must not panic the tick loop.
		return
	}

	pos, vel := Vec(e.Pos), Vec(e.Vel)
	// sanitiseAxis clamps to [−1, 1] and zeroes non-finite, same as mode 0.
	throttle, steer := sanitiseAxis(v.Throttle), sanitiseAxis(v.Steer)

	// 1–2: local up, heading from the quat's forward projected to the
	// tangent plane. Fallback per the GDD: rotate(quat, +Y) projected.
	up := terrain.Normalize(pos)
	h := tangential(Rotate(Quat(e.Quat), Vec{0, 0, 1}), up)
	if h.Len() < EpsDeg {
		h = tangential(Rotate(Quat(e.Quat), Vec{0, 1, 0}), up)
	}
	h = terrain.Normalize(h)

	// 3: steer, grounded only. Positive steer turns toward local +X
	// (right), which is a negative rotation about up by the right-hand
	// rule. Rodrigues, spelled out so the C# mirror is char-for-char.
	if v.Grounded && steer != 0 {
		h = rotateAboutAxis(h, up, -steer*SteerRate*dt)
	}

	// 4: throttle, grounded only, and under the slope cutoff OR pointed
	// downhill: past drive_slope_max uphill throttle is refused, but throttle
	// that would carry the rover downhill still works, so a rover that comes
	// to rest on a scarp can always drive off it ("a rover never wedges").
	// Reverse at half accel, same rule as the flight model's backward thrust.
	if v.Grounded && throttle != 0 && driveAllowed(ctx.Terrain, up, h, throttle) {
		a := AccelDrive * effMult(v.EffMult)
		if throttle < 0 {
			a = AccelDrive * 0.5 * effMult(v.EffMult)
		}
		vel = vel.Add(h.Scale(throttle * a * dt))
	}

	// 5: gravity, always, along local −up.
	vel = vel.Sub(up.Scale(Gravity * dt))

	// 6: grip and coast, tangent-frame split, grounded only.
	if v.Grounded {
		vr := vel.Dot(up)
		vt := vel.Sub(up.Scale(vr))
		vf := vt.Dot(h)
		vlat := vt.Sub(h.Scale(vf))
		vlat = vlat.Scale(math.Exp(-Grip * dt))
		if throttle == 0 {
			vf *= math.Exp(-DampDrive * dt)
			// Static friction: exponential decay never reaches zero, and a
			// parked rover on any grade would creep downhill forever.
			if vf*vf+vlat.Dot(vlat) < HoldSpeed*HoldSpeed {
				vf = 0
				vlat = Vec{}
			}
		}
		vel = up.Scale(vr).Add(h.Scale(vf)).Add(vlat)
	}

	// 7: clamp tangential speed.
	vr := vel.Dot(up)
	vt := vel.Sub(up.Scale(vr))
	if l := vt.Len(); l > VmaxDrive {
		vel = vt.Scale(VmaxDrive / l).Add(up.Scale(vr))
	}

	// 8: integrate.
	pos = pos.Add(vel.Scale(dt))

	// 8b: the hull out of anything solid (structures, rocks, props, bodies,
	// other vehicles), by the pose it started the tick with. Before step 9,
	// so the terrain follow re-seats whatever the push did to the height.
	if ctx.CollidersFor != nil {
		p2, v2 := ResolveHull([3]float64(pos), [3]float64(vel), Quat(e.Quat), RoverHull, ctx.CollidersFor(e.ID))
		pos, vel = Vec(p2), Vec(v2)
	}

	// 9: terrain following — the origin point, like a body's foot.
	u := terrain.Normalize(pos)
	r := ctx.Terrain.SampleRadius(u)
	if pos.Len() <= r+GroundSnap {
		pos = u.Scale(r)
		if rad := vel.Dot(u); rad < 0 {
			vel = vel.Sub(u.Scale(rad))
		}
		v.Grounded = true
	} else {
		v.Grounded = false
	}

	// 10: orientation from the ground normal (radial up when airborne).
	n := u
	if v.Grounded {
		n = ctx.Terrain.SurfaceNormal(u)
	}
	h2 := tangential(h, n)
	if h2.Len() < EpsDeg {
		h2 = tangential(Rotate(Quat(e.Quat), Vec{0, 1, 0}), n)
	}
	h2 = terrain.Normalize(h2)

	e.Pos, e.Vel = [3]float64(pos), [3]float64(vel)
	e.Quat = [4]float64(QuatFromBasis(terrain.Cross(n, h2), n, h2))

	// The wire's grounded flag mirrors the carried state so the client's
	// rover predictor can reconcile it instead of re-deriving it at the
	// snap boundary, where a re-derivation can disagree by one tick.
	if v.Grounded {
		e.Flags |= protocol.FlagGrounded
	} else {
		e.Flags &^= protocol.FlagGrounded
	}
}

// driveAllowed is step 4's slope rule: slope ≤ drive_slope_max, or the
// drive direction (h, reversed for negative throttle) has a positive
// component along the downhill tangent — gravity-down projected onto the
// ground plane at the surface normal n:
//
//	downhill = normalize(g − n·dot(g, n)),  g = −up
//
// Drive.cs DriveAllowed is the same expression in the same order.
func driveAllowed(t *terrain.Field, up, h Vec, throttle float64) bool {
	if t.Slope(up) <= DriveSlopeMax {
		return true
	}
	n := t.SurfaceNormal(up)
	g := up.Scale(-1)
	downhill := terrain.Normalize(g.Sub(n.Scale(g.Dot(n))))
	dir := h
	if throttle < 0 {
		dir = h.Scale(-1)
	}
	return dir.Dot(downhill) > 0
}

// rotateAboutAxis rotates v about unit axis k by ang (Rodrigues). The C#
// mirror must use this exact expression — not a quaternion — so the two
// sims agree to C30's bar.
func rotateAboutAxis(v, k Vec, ang float64) Vec {
	c, s := math.Cos(ang), math.Sin(ang)
	return v.Scale(c).Add(terrain.Cross(k, v).Scale(s)).Add(k.Scale(k.Dot(v) * (1 - c)))
}
