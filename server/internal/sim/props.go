package sim

import "space-adventure/server/internal/protocol"

// PropBoxes are the solid dressing props' model-space bounds (min, max),
// measured from their art/ GLBs. A prop is a box of these bounds, posed the
// way the client draws it: rotated by its quat, uniformly scaled, at its
// position (Structures.BuildProps). Bones stay walk-through, and structures
// (struct.*) already ship their own colliders. Collide.cs PropBoxes is the
// same table.
var PropBoxes = map[string][2]Vec{
	"prop.barrel":     {{-0.32, 0, -0.32}, {0.32, 0.95, 0.32}},
	"prop.barrels":    {{-0.65, 0, -0.86}, {0.65, 0.95, 0.49}},
	"prop.generator":  {{-1.00, 0, -0.45}, {1.12, 1.34, 0.43}},
	"prop.dish":       {{-0.95, 0, -0.93}, {0.95, 2.09, 0.66}},
	"prop.loot.crate": {{-0.22, 0, -0.17}, {0.22, 0.50, 0.17}},
}

// PropCollider is a solid prop as a box collider; false for dressing a body
// walks through.
func PropCollider(p protocol.Prop) (protocol.Collider, bool) {
	b, ok := PropBoxes[p.Asset]
	if !ok {
		return protocol.Collider{}, false
	}
	s := float64(p.Scale)
	if s <= 0 {
		s = 1
	}
	q := Quat{float64(p.Quat[0]), float64(p.Quat[1]), float64(p.Quat[2]), float64(p.Quat[3])}
	mid := b[0].Add(b[1]).Scale(0.5 * s)
	half := b[1].Sub(b[0]).Scale(0.5 * s)
	c := Vec{float64(p.Pos[0]), float64(p.Pos[1]), float64(p.Pos[2])}.Add(Rotate(q, mid))
	return protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{float32(c[0]), float32(c[1]), float32(c[2])},
		Half:   [3]float32{float32(half[0]), float32(half[1]), float32(half[2])},
		Quat:   p.Quat,
	}, true
}
