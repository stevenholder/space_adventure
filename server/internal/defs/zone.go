package defs

import (
	"fmt"
	"math"

	"space-adventure/server/internal/protocol"
)

// Placement is one entity (npc or target) composed from a zone's local
// tangent frame into world space.
type Placement struct {
	Type string // "npc" | "target"
	Def  string
	Pos  [3]float64
	Quat [4]float64
}

// vec3 helpers — the composition below follows docs/GDD.md "Static
// colliders" -> "Authoring frame" verbatim.

func vAdd(a, b [3]float64) [3]float64 {
	return [3]float64{a[0] + b[0], a[1] + b[1], a[2] + b[2]}
}

func vScale(a [3]float64, s float64) [3]float64 {
	return [3]float64{a[0] * s, a[1] * s, a[2] * s}
}

func vDot(a, b [3]float64) float64 {
	return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]
}

func vCross(a, b [3]float64) [3]float64 {
	return [3]float64{
		a[1]*b[2] - a[2]*b[1],
		a[2]*b[0] - a[0]*b[2],
		a[0]*b[1] - a[1]*b[0],
	}
}

func vLen(a [3]float64) float64 {
	return math.Sqrt(vDot(a, a))
}

func vNormalize(a [3]float64) [3]float64 {
	l := vLen(a)
	if l == 0 {
		return a
	}
	return vScale(a, 1/l)
}

// quat is (x, y, z, w).
type quat [4]float64

// quatFromBasis builds the rotation whose local +x, +y, +z axes map to the
// given world-space basis vectors (which must be orthonormal and
// right-handed).
func quatFromBasis(ex, ey, ez [3]float64) quat {
	// Standard rotation-matrix-to-quaternion conversion. Columns of the
	// matrix are ex, ey, ez.
	m00, m01, m02 := ex[0], ey[0], ez[0]
	m10, m11, m12 := ex[1], ey[1], ez[1]
	m20, m21, m22 := ex[2], ey[2], ez[2]

	trace := m00 + m11 + m22
	var q quat
	if trace > 0 {
		s := 0.5 / math.Sqrt(trace+1.0)
		q[3] = 0.25 / s
		q[0] = (m21 - m12) * s
		q[1] = (m02 - m20) * s
		q[2] = (m10 - m01) * s
	} else if m00 > m11 && m00 > m22 {
		s := 2.0 * math.Sqrt(1.0+m00-m11-m22)
		q[3] = (m21 - m12) / s
		q[0] = 0.25 * s
		q[1] = (m01 + m10) / s
		q[2] = (m02 + m20) / s
	} else if m11 > m22 {
		s := 2.0 * math.Sqrt(1.0+m11-m00-m22)
		q[3] = (m02 - m20) / s
		q[0] = (m01 + m10) / s
		q[1] = 0.25 * s
		q[2] = (m12 + m21) / s
	} else {
		s := 2.0 * math.Sqrt(1.0+m22-m00-m11)
		q[3] = (m10 - m01) / s
		q[0] = (m02 + m20) / s
		q[1] = (m12 + m21) / s
		q[2] = 0.25 * s
	}
	return q
}

// quatFromAxisAngle builds a rotation of angleRad radians about axis
// (assumed unit length).
func quatFromAxisAngle(axis [3]float64, angleRad float64) quat {
	half := angleRad / 2
	s := math.Sin(half)
	return quat{axis[0] * s, axis[1] * s, axis[2] * s, math.Cos(half)}
}

// quatMul returns a*b (apply b first, then a — standard Hamilton product).
func quatMul(a, b quat) quat {
	return quat{
		a[3]*b[0] + a[0]*b[3] + a[1]*b[2] - a[2]*b[1],
		a[3]*b[1] - a[0]*b[2] + a[1]*b[3] + a[2]*b[0],
		a[3]*b[2] + a[0]*b[1] - a[1]*b[0] + a[2]*b[3],
		a[3]*b[3] - a[0]*b[0] - a[1]*b[1] - a[2]*b[2],
	}
}

// ComposeZone transforms a zone's locally-authored colliders and entities
// (local tangent frame) into world-space colliders and placements, per
// docs/GDD.md "Static colliders" -> "Authoring frame".
func ComposeZone(z Zone, radius func([3]float64) float64) (colliders []protocol.Collider, placements []Placement, err error) {
	// zone frame, once per zone
	up := vNormalize(z.OriginDir)
	ref := [3]float64{0, 0, 1}
	if math.Abs(vDot(ref, up)) > 0.999 {
		ref = [3]float64{1, 0, 0}
	}
	north := vNormalize(vAdd(ref, vScale(up, -vDot(ref, up))))
	east := vCross(up, north)
	origin := vScale(up, radius(up))

	// worldTransform composes one local point/yaw into a world position and
	// quaternion, per object, re-deriving its own frame at its own position.
	worldTransform := func(p [3]float64, yawDeg float64) ([3]float64, quat) {
		dir := vNormalize(vAdd(origin, vAdd(vScale(east, p[0]), vScale(north, p[2]))))
		worldPos := vScale(dir, radius(dir)+p[1])

		northP := vNormalize(vAdd(north, vScale(dir, -vDot(north, dir))))
		eastP := vCross(dir, northP)

		frameQuat := quatFromBasis(eastP, dir, northP)
		localQuat := quatFromAxisAngle([3]float64{0, 1, 0}, yawDeg*math.Pi/180)
		worldQuat := quatMul(frameQuat, localQuat)

		return worldPos, worldQuat
	}

	colliders = make([]protocol.Collider, 0, len(z.Colliders))
	for _, c := range z.Colliders {
		var kind uint8
		switch c.Kind {
		case "box":
			kind = protocol.ColliderBox
		case "sphere":
			kind = protocol.ColliderSphere
		default:
			return nil, nil, fmt.Errorf("defs: zone %q: unknown collider kind %q", z.ID, c.Kind)
		}

		pos, q := worldTransform(c.Pos, c.Yaw)
		colliders = append(colliders, protocol.Collider{
			Kind:   kind,
			Center: [3]float32{float32(pos[0]), float32(pos[1]), float32(pos[2])},
			Half:   [3]float32{float32(c.Half[0]), float32(c.Half[1]), float32(c.Half[2])},
			Quat:   [4]float32{float32(q[0]), float32(q[1]), float32(q[2]), float32(q[3])},
		})
	}

	placements = make([]Placement, 0, len(z.Entities))
	for _, e := range z.Entities {
		pos, q := worldTransform(e.Pos, e.Yaw)
		placements = append(placements, Placement{
			Type: e.Type,
			Def:  e.Def,
			Pos:  pos,
			Quat: [4]float64(q),
		})
	}

	return colliders, placements, nil
}
