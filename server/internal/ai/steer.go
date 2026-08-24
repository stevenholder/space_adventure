// Package ai implements NPC steering on the sphere (GDD "Phase 3 — NPC
// combat at an encampment" -> "Steering (binding)"). NPCs are GLUED to the
// surface rather than gravity-simulated: no jump, no air control, no
// grounded flag — a walking enemy has no reason to leave the ground, and
// the moment it does every airborne edge case in the player integrator
// becomes an NPC bug too. They resolve against static colliders with the
// same sim.ResolveColliders the player uses — one wall rule, not two.
package ai

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// Vec is the shared 3-vector type (alias of terrain.Vec).
type Vec = terrain.Vec

// Steerer is one NPC's steering state.
type Steerer struct {
	Pos, Vel, Facing [3]float64
	Speed            float64 // m/s
	TurnRate         float64 // rad/s
}

// degenEps is the length/dot-product margin below which a direction is
// treated as undefined (zero, or exactly opposite) rather than
// numerically noisy — guards Normalize's own 1e-12 floor with margin for
// the dot and cross products built on top of it.
const degenEps = 1e-9

// Step moves one NPC toward target for dt, glued to the surface (GDD
// "Steering (binding)", implemented verbatim):
//
//	up      = normalize(pos)
//	toward  = tangent(target − pos, up)
//	desired = normalize(toward) * Speed
//	facing  = rotateToward(facing, normalize(toward), TurnRate*dt)
//	vel     = desired if the ground is walkable there, else slide downslope
//	pos     = pos + vel*dt
//	pos     = normalize(pos) * radius(terrain, normalize(pos))   // glued
//
// then resolved against the static colliders with sim.ResolveColliders.
func Step(s *Steerer, target [3]float64, dt float64, t *terrain.Field, cs []protocol.Collider) {
	pos := terrain.Vec(s.Pos)
	up := terrain.Normalize(pos)

	// toward = tangent(target - pos, up), projected into the tangent
	// plane: on a sphere the straight line to a target points through the
	// ground, and steering along it drives the NPC into the surface.
	toward := tangent(terrain.Vec(target).Sub(pos), up)

	var desired terrain.Vec
	if l := toward.Len(); l > degenEps {
		dir := toward.Scale(1 / l)
		desired = dir.Scale(s.Speed)
		s.Facing = [3]float64(rotateToward(terrain.Vec(s.Facing), dir, s.TurnRate*dt))
	}
	// toward ~ 0 (target coincides with pos, or lies exactly along up from
	// it): no direction to steer toward or face — desired stays zero and
	// facing holds, rather than feeding a degenerate direction downstream.

	var vel terrain.Vec
	if t.Walkable(up) {
		vel = desired
	} else {
		vel = slide(terrain.Vec(s.Vel), up, t)
	}

	// pos = pos + vel*dt, then glued back to the sampled surface radius.
	pos = pos.Add(vel.Scale(dt))
	up = terrain.Normalize(pos)
	pos = up.Scale(t.SampleRadius(up))

	// Static collider resolution — the player's own wall rule, not a
	// second one. NPCs have no airborne state, so "grounded" going in is
	// always true; the returned bit is not meaningful for a glued NPC and
	// is discarded.
	outPos, outVel, _ := sim.ResolveColliders(
		[3]float64(pos), [3]float64(vel), [3]float64(up), true, cs,
		func(d [3]float64) float64 { return t.SampleRadius(terrain.Vec(d)) })

	s.Pos = outPos
	s.Vel = outVel
}

// tangent projects v onto the plane perpendicular to up.
func tangent(v, up terrain.Vec) terrain.Vec { return v.Sub(up.Scale(v.Dot(up))) }

// slide redirects vel along the downhill tangent of the actual slope at up
// when the ground there is too steep to walk (GDD "Steering (binding)":
// vel = desired if slopeOK else slide(vel, up, terrain)). NPCs carry no
// gravity constant — they are glued, not simulated — so slide preserves
// vel's existing magnitude and only redirects its direction: an NPC that
// arrives at a steep patch with no momentum has nothing to slide with and
// holds still rather than accelerating from nothing.
func slide(vel, up terrain.Vec, t *terrain.Field) terrain.Vec {
	n := t.SurfaceNormal(up)
	down := up.Scale(-1)
	downhill := down.Sub(n.Scale(down.Dot(n)))
	if downhill.Len() <= degenEps {
		// n ~= up: no defined downhill direction. Should not arise when
		// Walkable(up) is already false, but stay finite regardless.
		return tangent(vel, up)
	}
	return terrain.Normalize(downhill).Scale(vel.Len())
}

// rotateToward turns unit vector `from` toward unit vector `to` by at most
// maxAngle radians (GDD "Steering (binding)": facing = rotateToward(facing,
// normalize(toward), turn_rate*dt)). Stable when from and to are already
// equal (returns immediately) or exactly opposite (no unique rotation
// axis — a deterministic perpendicular is chosen instead of dividing by a
// near-zero cross product).
func rotateToward(from, to terrain.Vec, maxAngle float64) terrain.Vec {
	from = terrain.Normalize(from)
	if from.Len() == 0 {
		// No prior facing to turn from (e.g. a freshly spawned NPC) —
		// snap straight to the target direction.
		return to
	}

	d := from.Dot(to)
	if d > 1 {
		d = 1
	} else if d < -1 {
		d = -1
	}
	angle := math.Acos(d)
	if angle <= maxAngle || angle < degenEps {
		return to
	}

	var axis terrain.Vec
	if angle >= math.Pi-degenEps {
		axis = arbitraryPerp(from)
	} else {
		axis = terrain.Normalize(terrain.Cross(from, to))
	}
	return rotateAroundAxis(from, axis, maxAngle)
}

// arbitraryPerp returns a unit vector perpendicular to v, chosen
// deterministically (same construction as terrain.Field.SurfaceNormal's
// tangent basis).
func arbitraryPerp(v terrain.Vec) terrain.Vec {
	k := terrain.Vec{1, 0, 0}
	if math.Abs(v[0]) > 0.9 {
		k = terrain.Vec{0, 1, 0}
	}
	return terrain.Normalize(k.Sub(v.Scale(k.Dot(v))))
}

// rotateAroundAxis rotates v by angle radians about unit axis (Rodrigues'
// rotation formula).
func rotateAroundAxis(v, axis terrain.Vec, angle float64) terrain.Vec {
	c, sn := math.Cos(angle), math.Sin(angle)
	return v.Scale(c).
		Add(terrain.Cross(axis, v).Scale(sn)).
		Add(axis.Scale(axis.Dot(v) * (1 - c)))
}
