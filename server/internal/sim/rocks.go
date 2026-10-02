// The rock scatter, server side, so rocks are solid (playtest 2026-10-02:
// "full collision"). Rocks were client-only dressing (GDD "Rocks stay
// client-scattered and non-collidable"); now both sides run the same
// seeded scatter -- the client's RockScatter.cs, transliterated -- on the
// same wire-quantized field, and both turn each rock into the same sphere.
// test/t40-rock-parity.mjs holds the two to identical placements.
package sim

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// Mulberry32 is the client's Rng.Mulberry32: doubles in [0, 1), value for
// value from the same seed. uint32 arithmetic wraps exactly as Math.imul.
func Mulberry32(seed uint32) func() float64 {
	a := seed
	return func() float64 {
		a += 0x6d2b79f5
		t := (a ^ (a >> 15)) * (1 | a)
		t = (t + (t^(t>>7))*(61|t)) ^ t
		return float64(t^(t>>14)) / 4294967296.0
	}
}

// RockPlacement is one rock: seated position, surface direction (its +Y),
// spin about it, per-axis scale, model variant.
type RockPlacement struct {
	Pos, Dir Vec
	Spin     float64
	Scale    Vec
	Variant  int
}

const (
	rockSlopeMax    = 35.0 * math.Pi / 180.0
	rockTargetCount = 400
	rockMinSize     = 0.6
	rockMaxSize     = 1.5
	rockBasinT      = 20
	rockRidgeT      = -20
	rockPFlat       = 0.1
	rockPFeature    = 0.9
	rockMaxTries    = rockTargetCount * 60
)

// RockScatter places this world's rocks: RockScatter.cs line for line.
func RockScatter(t *terrain.Field, worldSeed uint32) []RockPlacement {
	rng := Mulberry32(worldSeed ^ 0x5eed)
	out := make([]RockPlacement, 0, rockTargetCount)
	for tries := 0; len(out) < rockTargetCount && tries < rockMaxTries; tries++ {
		z := rng()*2 - 1
		th := rng() * math.Pi * 2
		r := math.Sqrt(1 - z*z)
		d := terrain.Normalize(Vec{r * math.Cos(th), z, r * math.Sin(th)})

		if t.Slope(d) > rockSlopeMax {
			continue
		}
		c := t.Curvature(d)
		p := rockPFlat
		if c > rockBasinT || c < rockRidgeT {
			p = rockPFeature
		}
		if rng() > p {
			continue
		}
		size := rockMinSize + rng()*(rockMaxSize-rockMinSize)
		surf := t.SampleRadius(d)
		pl := RockPlacement{Pos: d.Scale(surf + size*0.05), Dir: d}
		pl.Spin = rng() * math.Pi * 2
		sx := size * (0.8 + rng()*0.4)
		sy := size * (0.55 + rng()*0.5)
		sz := size * (0.8 + rng()*0.4)
		pl.Scale = Vec{sx, sy, sz}
		pl.Variant = int(math.Min(2, math.Floor(rng()*3)))
		out = append(out, pl)
	}
	return out
}

// RockCollider is a rock as a sphere: centre 0.4 of its height up its own
// axis, radius 0.45 of its narrower footprint. A body steps over the small
// ones (the sphere stays under its chest sphere); a rover hits them all.
func RockCollider(r RockPlacement) protocol.Collider {
	c := r.Pos.Add(r.Dir.Scale(r.Scale[1] * 0.4))
	return protocol.Collider{
		Kind:   protocol.ColliderSphere,
		Center: [3]float32{float32(c[0]), float32(c[1]), float32(c[2])},
		Half:   [3]float32{float32(0.45 * math.Min(r.Scale[0], r.Scale[2])), 0, 0},
		Quat:   [4]float32{0, 0, 0, 1},
	}
}
