package sim

import (
	"testing"

	"space-adventure/server/internal/protocol"
)

// A zero-radius probe INSIDE a rotated box is a hit (the outpost's north
// wall, from the zone data, with a point 0.15 m inside its thin axis).
func TestZeroRadiusProbeInsideRotatedBox(t *testing.T) {
	wall := protocol.Collider{Kind: protocol.ColliderBox,
		Center: [3]float32{-98.83175, -26.253382, -134.63153},
		Half:   [3]float32{8.6, 1.55, 0.6},
		Quat:   [4]float32{-0.27096933, -0.35234612, 0.7100838, 0.546085}}
	q := Quat{float64(wall.Quat[0]), float64(wall.Quat[1]), float64(wall.Quat[2]), float64(wall.Quat[3])}
	center := Vec{float64(wall.Center[0]), float64(wall.Center[1]), float64(wall.Center[2])}
	p := center.Add(Rotate(q, Vec{0, 0, -0.15}))
	hit, n, depth := nearest(p, 0, wall, Vec{0, 1, 0})
	t.Logf("hit=%v n=%v depth=%v", hit, n, depth)
	if !hit {
		t.Fatalf("interior probe missed the box")
	}
	tt, ok := SegmentColliderHit([3]float64(center.Add(Rotate(q, Vec{0, 0, -1.75}))), [3]float64(Rotate(q, Vec{0, 0, 1})), 2.25, []protocol.Collider{wall})
	if !ok {
		t.Fatalf("swept segment through the box missed (t=%v)", tt)
	}
}
