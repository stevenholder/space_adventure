// Static collider push-out plus terrain re-seat (GDD "Static colliders" ->
// "Integrator addition", steps 8 and 9). This is a standalone function —
// wiring it into Step is a separate change (server/internal/sim/sim.go is
// not touched here).
package sim

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// Rule table constants for the player collision shape (GDD "Static
// colliders" rule table). MaxSlope is already exported by sim.go and is
// reused as-is.
const (
	BodyRadius  = 0.35 // body_radius: collision sphere, m
	BodySphereH = 0.9  // body_sphere_h: chest height above the feet, m
)

// Moving bodies as colliders (playtest 2026-10-02: "the car and players
// don't have collision"). Nothing new is solved: each tick a player is
// stepped against the static colliders PLUS these shapes for everyone else,
// so ResolveColliders pushes bodies out of each other and out of the rover
// exactly as it does out of a wall. The client predicts with the same
// shapes built from what it draws (Collide.cs).
//
// ponytail: one-sided and one tick stale -- a body is pushed by the others'
// positions at the start of the tick, and nothing pushes the rover or an
// NPC back. Good enough to stop walking through things; a mass-and-impulse
// solver is the upgrade if anyone ever needs to shove a rover.
const (
	// RoverHalfX/Y/Z: the rover's hull as one box (rover.v1: tub, fenders
	// and wheels ±0.91 m wide, bumper to cargo rack ±1.1 m long), its centre
	// RoverBoxH over the rover origin along the rover's up. The top sits at
	// the tub rim, so a body can stand on the rover but not in the cage.
	RoverHalfX = 0.85
	RoverHalfY = 0.55
	RoverHalfZ = 1.12
	RoverBoxH  = 0.75
)

// Ship hull box for bodies and rovers (gen_ship.py: fuselage ±3.55 m long,
// ~1.4 m half-width, cockpit to ~2.5 m), centre ShipBoxH over the origin.
// The wings are left out: a body ducks under them.
const (
	ShipHalfX = 1.40
	ShipHalfY = 1.30
	ShipHalfZ = 3.60
	ShipBoxH  = 1.30
)

// HullSphere is one sphere of a vehicle's collision hull: Off in the
// vehicle's local frame (+Y up, +Z its heading), radius R.
type HullSphere struct {
	Off Vec
	R   float64
}

// RoverHull and ShipHull are the vehicles' own collision shapes -- a few
// spheres strung along each, because a sphere is what nearest() resolves.
// The rover's two cover its 1.7 x 2.2 m tub; the ship's three its fuselage.
var (
	RoverHull = []HullSphere{{Vec{0, 0.75, 0.55}, 0.80}, {Vec{0, 0.75, -0.55}, 0.80}}
	ShipHull  = []HullSphere{{Vec{0, 1.30, 2.40}, 1.40}, {Vec{0, 1.30, 0}, 1.40}, {Vec{0, 1.30, -2.40}, 1.40}}
)

// ResolveHull pushes a vehicle out of colliders: each hull sphere, in order,
// against each collider, in order -- the body rule (step 8) with the
// sphere's centre carried by the vehicle's pose. Inbound velocity along the
// push is removed, so a rover slides along a wall instead of sticking.
// Orientation is not touched; the caller's own terrain step re-seats.
func ResolveHull(pos, vel [3]float64, q Quat, hull []HullSphere, cs []protocol.Collider) (outPos, outVel [3]float64) {
	if len(cs) == 0 {
		return pos, vel
	}
	p, v := Vec(pos), Vec(vel)
	up := terrain.Normalize(p)
	for _, h := range hull {
		off := Rotate(q, h.Off)
		for i := range cs {
			hit, n, depth := nearest(p.Add(off), h.R, cs[i], up)
			if !hit {
				continue
			}
			p = p.Add(n.Scale(depth))
			if d := v.Dot(n); d < 0 {
				v = v.Sub(n.Scale(d))
			}
		}
	}
	return [3]float64(p), [3]float64(v)
}

// ShipCollider is a ship's hull box at pos, oriented by q.
func ShipCollider(pos Vec, q Quat) protocol.Collider {
	c := pos.Add(Rotate(q, Vec{0, ShipBoxH, 0}))
	return protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{float32(c[0]), float32(c[1]), float32(c[2])},
		Half:   [3]float32{ShipHalfX, ShipHalfY, ShipHalfZ},
		Quat:   [4]float32{float32(q[0]), float32(q[1]), float32(q[2]), float32(q[3])},
	}
}

// BodyCollider is a standing body's collision sphere as a collider: the
// same sphere ResolveColliders gives the stepping body, so two bodies rest
// 2·BodyRadius apart. radius is BodyRadius for a player, the archetype's
// for an NPC.
func BodyCollider(pos Vec, radius float64) protocol.Collider {
	c := pos.Add(terrain.Normalize(pos).Scale(BodySphereH))
	return protocol.Collider{
		Kind:   protocol.ColliderSphere,
		Center: [3]float32{float32(c[0]), float32(c[1]), float32(c[2])},
		Half:   [3]float32{float32(radius), 0, 0},
		Quat:   [4]float32{0, 0, 0, 1},
	}
}

// RoverCollider is a rover's hull box at pos, oriented by q.
func RoverCollider(pos Vec, q Quat) protocol.Collider {
	c := pos.Add(Rotate(q, Vec{0, RoverBoxH, 0}))
	return protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{float32(c[0]), float32(c[1]), float32(c[2])},
		Half:   [3]float32{RoverHalfX, RoverHalfY, RoverHalfZ},
		Quat:   [4]float32{float32(q[0]), float32(q[1]), float32(q[2]), float32(q[3])},
	}
}

// degenEps is the distance below which a sphere-vs-collider closest point is
// treated as coincident with the sphere centre — the exit normal is then
// undefined by construction, not just numerically noisy.
const degenEps = 1e-9

// ResolveColliders applies GDD integrator steps 8 (static collider
// push-out) and 9 (terrain re-seat), in that order, verbatim.
func ResolveColliders(pos, vel, up [3]float64, grounded bool,
	cs []protocol.Collider,
	radiusAt func([3]float64) float64) (outPos, outVel [3]float64, outGrounded bool) {
	// No colliders: nothing to push out of, and step 9 exists ONLY to repair a
	// push-out that sank the feet below the terrain (GDD "Integrator
	// addition"). Running it anyway re-seats whenever |pos| sits an ulp under
	// the sampled radius, which re-rounds pos and perturbs the trajectory by
	// ~1e-13 per tick. Harmless against C5's 5% bar, but it silently voids the
	// byte-identical property that is the evidence the wire work never touched
	// the sim -- and a drift you cannot distinguish from noise is a drift you
	// stop noticing.
	if len(cs) == 0 {
		return pos, vel, grounded
	}

	p := Vec(pos)
	v := Vec(vel)
	u := Vec(up)
	g := grounded
	cosMaxSlope := math.Cos(MaxSlope)

	// Step 8 — static collider resolution, in the order received.
	c := p.Add(u.Scale(BodySphereH))
	for i := range cs {
		hit, n, depth := nearest(c, BodyRadius, cs[i], u)
		if !hit {
			continue
		}
		p = p.Add(n.Scale(depth))
		c = p.Add(u.Scale(BodySphereH))
		if d := v.Dot(n); d < 0 {
			v = v.Sub(n.Scale(d))
		}
		if n.Dot(u) >= cosMaxSlope {
			g = true
		}
	}

	// Step 9 — re-seat on the terrain if step 8 pushed the feet under it.
	r := radiusAt(terrain.Normalize(p))
	if p.Len() < r {
		p = terrain.Normalize(p).Scale(r)
		if d := v.Dot(u); d < 0 {
			v = v.Sub(u.Scale(d))
		}
	}

	return p, v, g
}

// nearest is the sphere-vs-collider closest-point test: sphere centre c,
// radius, against collider col. Total (every input yields a defined,
// finite result) and non-allocating. up feeds only the degenerate-case
// fallback normal.
func nearest(c Vec, radius float64, col protocol.Collider, up Vec) (hit bool, n Vec, depth float64) {
	switch col.Kind {
	case protocol.ColliderSphere:
		return nearestSphere(c, radius, col, up)
	default: // protocol.ColliderBox and anything unrecognised resolve as a box
		return nearestBox(c, radius, col, up)
	}
}

func nearestSphere(c Vec, radius float64, col protocol.Collider, up Vec) (hit bool, n Vec, depth float64) {
	center := Vec{float64(col.Center[0]), float64(col.Center[1]), float64(col.Center[2])}
	colRadius := float64(col.Half[0])
	combined := radius + colRadius

	diff := c.Sub(center)
	dist := diff.Len()
	if dist >= combined {
		return false, Vec{}, 0
	}
	if dist < degenEps {
		return true, up, combined
	}
	return true, diff.Scale(1 / dist), combined - dist
}

func nearestBox(c Vec, radius float64, col protocol.Collider, up Vec) (hit bool, n Vec, depth float64) {
	center := Vec{float64(col.Center[0]), float64(col.Center[1]), float64(col.Center[2])}
	half := Vec{float64(col.Half[0]), float64(col.Half[1]), float64(col.Half[2])}
	q := Quat{float64(col.Quat[0]), float64(col.Quat[1]), float64(col.Quat[2]), float64(col.Quat[3])}
	qConj := Quat{-q[0], -q[1], -q[2], q[3]}

	local := Rotate(qConj, c.Sub(center))

	// A centre INSIDE the box is decided here, in the box's own frame,
	// not by the distance below: the rotate round trip leaves ~1e-8 of
	// noise, so "dist <= radius" with a zero radius (line of sight, a
	// projectile's path) missed interior points and every NPC saw and shot
	// through every wall. Exit along the nearest face.
	if math.Abs(local[0]) <= half[0] && math.Abs(local[1]) <= half[1] && math.Abs(local[2]) <= half[2] {
		axis, pen := 0, half[0]-math.Abs(local[0])
		for i := 1; i < 3; i++ {
			if p := half[i] - math.Abs(local[i]); p < pen {
				axis, pen = i, p
			}
		}
		var out Vec
		out[axis] = 1
		if local[axis] < 0 {
			out[axis] = -1
		}
		return true, Rotate(q, out), radius + pen
	}

	clamped := Vec{
		clampf(local[0], -half[0], half[0]),
		clampf(local[1], -half[1], half[1]),
		clampf(local[2], -half[2], half[2]),
	}
	closest := Rotate(q, clamped).Add(center)

	diff := c.Sub(closest)
	dist := diff.Len()
	if dist >= radius {
		return false, Vec{}, 0
	}
	if dist < degenEps {
		// Sphere centre coincides with its closest point in the box (e.g.
		// exactly at the box centre): no defined exit normal. Push out
		// along up by the full body radius rather than propagate a NaN.
		return true, up, radius
	}
	return true, diff.Scale(1 / dist), radius - dist
}

func clampf(x, lo, hi float64) float64 {
	if x < lo {
		return lo
	}
	if x > hi {
		return hi
	}
	return x
}

// SegmentHitsColliders reports whether the segment from `from` along `dir` for
// `length` metres is blocked by any static collider.
//
// Used for NPC line of sight (GDD, "AI state machine"). It tests the static
// colliders only — never the terrain: at combat ranges the horizon does not
// occlude, and marching the radius field along a ray costs far more than the
// handful of box tests it replaces.
func SegmentHitsColliders(from, dir [3]float64, length float64, cs []protocol.Collider) bool {
	_, hit := SegmentColliderHit(from, dir, length, cs)
	return hit
}

// SegmentColliderHit is SegmentHitsColliders with the distance: how far along
// the segment the first static collider is entered. A projectile uses it to
// stop at a wall BEFORE it reaches whoever stands behind it.
func SegmentColliderHit(from, dir [3]float64, length float64, cs []protocol.Collider) (float64, bool) {
	if len(cs) == 0 || length <= 0 {
		return 0, false
	}
	// Sample along the segment against the same sphere-vs-shape test the
	// push-out uses, with a probe radius of zero. Step by half the smallest
	// collider extent so nothing thinner than a step is skipped.
	const step = 0.4
	steps := int(length/step) + 1
	for i := 0; i <= steps; i++ {
		t := float64(i) * step
		if t > length {
			t = length
		}
		p := [3]float64{from[0] + dir[0]*t, from[1] + dir[1]*t, from[2] + dir[2]*t}
		for _, c := range cs {
			if hit, _, _ := nearest(p, 0, c, Vec{0, 1, 0}); hit {
				return t, true
			}
		}
	}
	return 0, false
}
