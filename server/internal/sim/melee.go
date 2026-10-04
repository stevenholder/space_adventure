// Melee swings and thrown charges (docs/GDD.md "Melee", "Throwables"): no
// physics, no swept blade. A swing is an AREA check -- every body whose
// capsule comes within reach of the wielder's chest and lies inside the
// weapon's arc of the facing -- and a burst is a sphere. Both only pick
// who is touched; the caller applies the damage (world ents here, players
// through the server's damagePlayer), so armor and invulnerability stay
// behind their one door.
package sim

import (
	"math"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// chestHeight is where a swing is measured from: the middle of a standing
// body, so a short creature and a tall one are both in reach of a blade.
const chestHeight = 1.1

// Touched is one body a swing or burst reaches, with the point it was
// reached at (for the hit marker) and its distance from the centre.
type Touched struct {
	ID    uint32
	Point Vec
	Dist  float64
}

// InArc reports whether body capsule feet->feet+up*height (radius r) is
// touched by a swing from `from` (the wielder's feet, `up` its up) facing
// `facing`: its nearest point is within reach + r of the chest, and its
// direction, flattened onto the wielder's ground plane, is within arc/2 of
// the flattened facing. arc >= 360 skips the direction test. Returns the
// nearest point and its distance.
func InArc(from, up, facing Vec, m defs.Melee, feet Vec, height, r float64) (Vec, float64, bool) {
	chest := from.Add(up.Scale(chestHeight))
	bodyUp := terrain.Normalize(feet)
	p := closestOnSegment(chest, feet, feet.Add(bodyUp.Scale(height)))
	d := p.Sub(chest).Len()
	if d > m.Range+r {
		return p, d, false
	}
	if m.Arc >= 360 {
		return p, d, true
	}
	flat := func(v Vec) Vec { return v.Sub(up.Scale(v.Dot(up))) }
	f, to := flat(facing), flat(p.Sub(chest))
	if to.Len() < r || f.Len() < 1e-9 {
		return p, d, true // standing inside the wielder: always touched
	}
	cos := f.Dot(to) / (f.Len() * to.Len())
	return p, d, cos >= math.Cos(m.Arc/2*math.Pi/180)
}

// SwingTargets lists every damageable, alive world entity a swing touches,
// in w.order (deterministic), skipping the wielder.
func SwingTargets(w *World, self uint32, from, up, facing Vec, m defs.Melee, defOf func(*Ent) defs.EntityDef) []Touched {
	var out []Touched
	for _, id := range w.order {
		if id == self {
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
		if p, d, ok := InArc(from, up, facing, m, Vec(e.Pos), def.Hitbox.Height, def.Hitbox.Radius); ok {
			out = append(out, Touched{ID: id, Point: p, Dist: d})
		}
	}
	return out
}

// BurstDamage is a throw's damage at distance d from the centre: full at
// the centre, half at the radius, none past it.
func BurstDamage(t defs.Throw, d float64) int {
	if d > t.Radius {
		return 0
	}
	return int(math.Round(float64(t.Damage) * (1 - 0.5*d/t.Radius)))
}

// BurstTargets lists every damageable, alive world entity whose capsule
// comes within t.Radius of centre, in w.order.
func BurstTargets(w *World, centre Vec, t defs.Throw, defOf func(*Ent) defs.EntityDef) []Touched {
	var out []Touched
	for _, id := range w.order {
		e := w.Ents[id]
		if e == nil || e.Health <= 0 || e.Flags&protocol.FlagDead != 0 {
			continue
		}
		def := defOf(e)
		if !def.Damageable {
			continue
		}
		if p, d, ok := InBurst(centre, t.Radius, Vec(e.Pos), def.Hitbox.Height, def.Hitbox.Radius); ok {
			out = append(out, Touched{ID: id, Point: p, Dist: d})
		}
	}
	return out
}

// InBurst: the capsule's nearest point to centre, its distance (less the
// capsule radius, floored at 0), and whether that is within radius.
func InBurst(centre Vec, radius float64, feet Vec, height, r float64) (Vec, float64, bool) {
	p := closestOnSegment(centre, feet, feet.Add(terrain.Normalize(feet).Scale(height)))
	d := math.Max(0, p.Sub(centre).Len()-r)
	return p, d, d <= radius
}

// closestOnSegment is the point of segment a-b nearest p.
func closestOnSegment(p, a, b Vec) Vec {
	ab := b.Sub(a)
	l := ab.Dot(ab)
	if l < 1e-12 {
		return a
	}
	t := math.Max(0, math.Min(1, p.Sub(a).Dot(ab)/l))
	return a.Add(ab.Scale(t))
}
