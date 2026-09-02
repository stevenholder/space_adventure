// Hitscan shot resolution (docs/GDD.md, "Weapons" and "Health and damage"
// -> "Lag compensation"). ResolveShot is the single entry point: rewind,
// spread, ray-vs-capsule, falloff, damage — in that order, matching the GDD
// verbatim so a reordering here is a doc-drift bug, not a refactor.
package sim

import (
	"math"
	"math/rand"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// eyeHeightMeters is terrain.EyeHeightMeters, aliased so call sites read
// locally. The GDD table calls eye_height client-only, but a hitscan shot's
// origin must be server-authoritative, so ResolveShot uses this constant
// rather than any client-supplied eye position. It is distinct from an
// entity's hitbox Height (the capsule used to hit *other* entities) — the
// shooter's own hitbox plays no part in where its eye sits.
const eyeHeightMeters = terrain.EyeHeightMeters

// Shot is a single hitscan shot request to resolve.
type Shot struct {
	// Shooter is the firing entity's id. Only ever used to look up the
	// shooter's OWN rewound history — never trusted as a position.
	Shooter uint32
	// Dir is the aim direction at the moment of firing, before spread. Need
	// not be pre-normalised.
	Dir [3]float64
	// Tick is the server tick the shot resolves on.
	Tick uint32
	// RewindTicks is how far back, in ticks, to rewind shooter and target
	// history for lag compensation. The CALLER must already have clamped
	// this to [0, HistoryTicks] (rewind_max at TickHz) from its own
	// smoothed RTT/2 for the connection. ResolveShot trusts it verbatim and
	// does not re-clamp or otherwise inspect it: it has no client-supplied
	// value of its own to validate against, and re-deriving a clamp here
	// from something the client controls is exactly the hole that lets a
	// client shoot into the past.
	RewindTicks int
	// ConeHalfAngle is the spread cone's current half-angle, in radians, to
	// draw this shot's deviation from. It is the caller's bloom state —
	// spread_base grown by spread_per_shot per shot fired and recovered by
	// spread_decay deg/s, clamped to spread_max (GDD "Weapons"). ResolveShot
	// only draws a random deviation within whatever half-angle it is given
	// here; tracking bloom across shots is the caller's job, not this
	// function's.
	ConeHalfAngle float64
}

// Ray is the shot as the SERVER actually resolved it: the rewound eye it left
// from and the direction it went after spread was applied.
//
// It is returned whether or not anything was hit, because the `shot fired`
// event needs it either way. Broadcasting the client's own aim instead would
// draw every player a tracer along a line the shot did not take — and since
// the server owns spread, that line disagrees with the hit markers, which
// reads as broken hit registration and sends you debugging the netcode.
type Ray struct {
	Origin [3]float64
	Dir    [3]float64
}

// Hit is the outcome of a shot that hit something.
type Hit struct {
	Victim      uint32
	Point       [3]float64
	Damage      int
	HealthAfter int
}

// ResolveShot resolves one hitscan shot against w, per the GDD:
//
//  1. The ray origin is the shooter's own rewound eye position from
//     History — never a client-supplied value. No history sample, no hit.
//  2. Dir is deviated by a random angle within s.ConeHalfAngle, drawn from
//     rng (the server's per-connection RNG — never the client's).
//  3. Every damageable, alive entity except the shooter is tested as a ray
//     against a vertical capsule (that entity's def hitbox radius/height)
//     standing along THAT entity's own rewound up — not the shooter's,
//     which differs on a round world.
//  4. The nearest hit within wp.MaxRange wins; beyond MaxRange there is no
//     hit at all.
//  5. damage = round(wp.Damage * falloff), falloff linear from 1.0 at
//     FalloffStart to FalloffMin at FalloffEnd, clamped at both ends.
//  6. Damage is applied and health is clamped at 0.
//
// defOf resolves an entity's EntityDef (hitbox, Damageable) — combat keeps
// no registry of its own, so the caller supplies the lookup.
//
// Iteration follows w.order, never a range over w.Ents: map order is
// randomised per run, which would make which of two overlapping targets is
// hit non-deterministic.
func ResolveShot(w *World, h *History, s Shot, wp defs.Weapon,
	defOf func(*Ent) defs.EntityDef, rng *rand.Rand) (Ray, Hit, bool) {

	rewindTick := s.Tick - uint32(s.RewindTicks)

	eyePos, eyeUp, ok := h.At(rewindTick, s.Shooter)
	if !ok {
		return Ray{}, Hit{}, false
	}
	origin := Vec(eyePos).Add(Vec(eyeUp).Scale(eyeHeightMeters))

	dir := terrain.Normalize(s.Dir)
	if s.ConeHalfAngle > 0 {
		dir = deviate(dir, s.ConeHalfAngle, rng)
	}
	ray := Ray{Origin: [3]float64(origin), Dir: [3]float64(dir)}

	bestT := math.Inf(1)
	var bestID uint32
	var bestPoint Vec
	found := false

	for _, id := range w.order {
		if id == s.Shooter {
			continue
		}
		e := w.Ents[id]
		if e == nil || e.Health <= 0 || e.Flags&protocol.FlagDead != 0 {
			continue
		}
		def := defOf(e)
		if !def.Damageable {
			continue
		}
		rpos, rup, ok := h.At(rewindTick, id)
		if !ok {
			continue
		}
		feet := Vec(rpos)
		head := feet.Add(Vec(rup).Scale(def.Hitbox.Height))
		t, hit := rayCapsule(origin, dir, feet, head, def.Hitbox.Radius)
		if !hit || t < 0 || t > wp.MaxRange || t >= bestT {
			continue
		}
		bestT = t
		bestID = id
		bestPoint = origin.Add(dir.Scale(t))
		found = true
	}

	if !found {
		return ray, Hit{}, false
	}

	damage := int(math.Round(float64(wp.Damage) * falloffAt(bestT, wp)))

	victim := w.Ents[bestID]
	victim.Health -= damage
	if victim.Health < 0 {
		victim.Health = 0
	}

	return ray, Hit{
		Victim:      bestID,
		Point:       bestPoint,
		Damage:      damage,
		HealthAfter: victim.Health,
	}, true
}

// falloffAt returns the damage multiplier at range dist (GDD "Weapons"):
// 1.0 at or below FalloffStart, FalloffMin at or beyond FalloffEnd, linear
// in between, clamped at both ends.
func falloffAt(dist float64, wp defs.Weapon) float64 {
	if dist <= wp.FalloffStart {
		return 1.0
	}
	if dist >= wp.FalloffEnd {
		return wp.FalloffMin
	}
	span := wp.FalloffEnd - wp.FalloffStart
	if span <= 0 {
		return wp.FalloffMin
	}
	frac := (dist - wp.FalloffStart) / span
	return 1.0 + frac*(wp.FalloffMin-1.0)
}

// deviate rotates the unit vector d by a random angle drawn uniformly from
// [0, halfAngle) around a random azimuth about d — sampling the cone's
// polar angle, per "an angle drawn ... within the current cone half-angle"
// (GDD "Weapons").
func deviate(d Vec, halfAngle float64, rng *rand.Rand) Vec {
	theta := rng.Float64() * halfAngle
	phi := rng.Float64() * 2 * math.Pi

	u, v := perpBasis(d)
	sinT, cosT := math.Sin(theta), math.Cos(theta)
	return d.Scale(cosT).
		Add(u.Scale(math.Cos(phi) * sinT)).
		Add(v.Scale(math.Sin(phi) * sinT))
}

// perpBasis returns two unit vectors orthogonal to d and to each other,
// completing a right-handed basis with d.
func perpBasis(d Vec) (Vec, Vec) {
	ref := Vec{0, 1, 0}
	if math.Abs(d.Dot(ref)) > 0.999 {
		ref = Vec{1, 0, 0}
	}
	u := terrain.Normalize(terrain.Cross(ref, d))
	v := terrain.Cross(d, u)
	return u, v
}

// rayCapsule intersects the ray (ro + t*rd, rd unit) against the capsule of
// radius r standing on segment pa-pb (feet to head), returning the nearest
// such t. t may be negative (capsule behind the ray origin); callers filter
// that, same as any other out-of-range result. Adapted from the standard
// analytic ray/capsule intersection (body cylinder plus two end-cap
// spheres).
func rayCapsule(ro, rd, pa, pb Vec, r float64) (t float64, hit bool) {
	ba := pb.Sub(pa)
	oa := ro.Sub(pa)
	baba := ba.Dot(ba)
	if baba < 1e-12 {
		// Zero-height capsule: a plain sphere at pa.
		return raySphere(ro, rd, pa, r)
	}

	bard := ba.Dot(rd)
	baoa := ba.Dot(oa)
	rdoa := rd.Dot(oa)
	oaoa := oa.Dot(oa)

	a := baba - bard*bard
	if math.Abs(a) < 1e-12 {
		// Ray parallel to the capsule axis: the body-cylinder equation
		// degenerates. Only the end caps can be hit.
		tPA, okPA := raySphere(ro, rd, pa, r)
		tPB, okPB := raySphere(ro, rd, pb, r)
		switch {
		case okPA && okPB:
			if tPA < tPB {
				return tPA, true
			}
			return tPB, true
		case okPA:
			return tPA, true
		case okPB:
			return tPB, true
		default:
			return 0, false
		}
	}

	b := baba*rdoa - baoa*bard
	c := baba*oaoa - baoa*baoa - r*r*baba
	disc := b*b - a*c
	if disc < 0 {
		return 0, false
	}
	tt := (-b - math.Sqrt(disc)) / a
	y := baoa + tt*bard
	if y > 0 && y < baba {
		return tt, true // body cylinder
	}
	if y <= 0 {
		return raySphere(ro, rd, pa, r)
	}
	return raySphere(ro, rd, pb, r)
}

// raySphere returns the near intersection t of ray (ro + t*rd, rd unit)
// with the sphere of radius r centred at c.
func raySphere(ro, rd, c Vec, r float64) (t float64, hit bool) {
	oc := ro.Sub(c)
	b := rd.Dot(oc)
	cc := oc.Dot(oc) - r*r
	disc := b*b - cc
	if disc < 0 {
		return 0, false
	}
	return -b - math.Sqrt(disc), true
}
