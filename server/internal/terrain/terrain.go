// Package terrain owns the cube-sphere surface field: procedural generation
// from the world seed (GDD "M1 terrain generation") and the bilinear
// sampling / normal / gradient helpers the sim collides against (GDD
// "Terrain sampling").
//
// The field is six square grids of surface radii, face order +X, −X, +Y, −Y,
// +Z, −Z. A sample direction d (unit vector, planet centre at origin) picks
// its face by the largest-magnitude component; the other two components
// divided by that magnitude give face coordinates (u, v) in [−1, 1].
//
// Face coordinate convention (binding for the client's mirror of this
// sampling — PROTOCOL "terrain"): (u, v) are the two non-dominant components
// in ascending axis order (x, y, z), divided by the dominant magnitude:
//
//	+X/−X face: u = y/max, v = z/max
//	+Y/−Y face: u = x/max, v = z/max
//	+Z/−Z face: u = x/max, v = y/max
//
// Grid indexing (PROTOCOL "Terrain sampling (pinned)"): radii[face·grid² +
// row·grid + col], with col = (u+1)/2·(grid−1) and row = (v+1)/2·(grid−1).
// Shared edges are duplicated between adjacent faces and carry identical
// values: generation evaluates 3D noise over the direction vector, so a
// shared edge is the same direction and samples to the same radius in both
// faces.
package terrain

import (
	"math"
)

// Grid and radius constants (GDD "M1 on-foot movement" rule table).
const (
	FaceGrid     = 65
	RadiusMin    = 124.0
	RadiusMax    = 190.0
	PlanetRadius = 150.0

	// MaxSlope is max_slope 50° in radians: steeper ground is not
	// walkable (the body slides).
	MaxSlope = 50.0 * math.Pi / 180
)

type Field struct {
	Seed uint64
	// Radii[face][row*FaceGrid+col] with col = (u+1)/2·(grid−1),
	// row = (v+1)/2·(grid−1) (PROTOCOL "Terrain sampling (pinned)").
	Radii [NumFaces][FaceGrid * FaceGrid]float64
	// Landmarks records where the six placed landmarks landed (for the
	// separation constraint check and debugging).
	Landmarks []LandmarkPos
	// Clamped is the number of samples clamped to [RadiusMin, RadiusMax]
	// during generation.
	Clamped int
}

// Face indices in wire order.
const (
	FacePX = iota
	FaceNX
	FacePY
	FaceNY
	FacePZ
	FaceNZ
	NumFaces = 6
)

type Vec [3]float64

func (a Vec) Dot(b Vec) float64 { return a[0]*b[0] + a[1]*b[1] + a[2]*b[2] }

func (a Vec) Sub(b Vec) Vec { return Vec{a[0] - b[0], a[1] - b[1], a[2] - b[2]} }

func (a Vec) Scale(s float64) Vec { return Vec{a[0] * s, a[1] * s, a[2] * s} }

func (a Vec) Len() float64 { return math.Sqrt(a.Dot(a)) }

func Normalize(a Vec) Vec {
	l := a.Len()
	if l < 1e-12 {
		return Vec{}
	}
	return a.Scale(1 / l)
}

// FaceOf returns the face index and (u, v) in [−1, 1] for a direction d.
// d should be a unit vector; non-unit input is handled by the magnitude
// division, so normalisation is not required.
func FaceOf(d Vec) (face int, u, v float64) {
	ax, ay, az := math.Abs(d[0]), math.Abs(d[1]), math.Abs(d[2])
	switch {
	case ax >= ay && ax >= az:
		if d[0] > 0 {
			return FacePX, d[1] / ax, d[2] / ax
		}
		return FaceNX, d[1] / ax, d[2] / ax
	case ay >= ax && ay >= az:
		if d[1] > 0 {
			return FacePY, d[0] / ay, d[2] / ay
		}
		return FaceNY, d[0] / ay, d[2] / ay
	default:
		if d[2] > 0 {
			return FacePZ, d[0] / az, d[1] / az
		}
		return FaceNZ, d[0] / az, d[1] / az
	}
}

// DirOf inverts FaceOf: the unit direction at face coordinates (u, v).
func DirOf(face int, u, v float64) Vec {
	switch face {
	case FacePX:
		return Normalize(Vec{1, u, v})
	case FaceNX:
		return Normalize(Vec{-1, u, v})
	case FacePY:
		return Normalize(Vec{u, 1, v})
	case FaceNY:
		return Normalize(Vec{u, -1, v})
	case FacePZ:
		return Normalize(Vec{u, v, 1})
	default:
		return Normalize(Vec{u, v, -1})
	}
}

// SampleRadius bilinearly samples the surface radius (metres from the
// planet centre) for a direction d (GDD "Terrain sampling" steps 1–3).
func (f *Field) SampleRadius(d Vec) float64 {
	face, u, v := FaceOf(d)
	// Map [−1, 1] to [0, grid−1] so face edges sit exactly on grid lines
	// (PROTOCOL "Terrain sampling (pinned)"; generation fills the same way).
	gu := (u + 1) * 0.5 * (FaceGrid - 1)
	gv := (v + 1) * 0.5 * (FaceGrid - 1)
	if gu < 0 {
		gu = 0
	}
	if gu > float64(FaceGrid-1) {
		gu = float64(FaceGrid - 1)
	}
	if gv < 0 {
		gv = 0
	}
	if gv > float64(FaceGrid-1) {
		gv = float64(FaceGrid - 1)
	}
	c0 := int(gu)
	r0 := int(gv)
	if c0 > FaceGrid-2 {
		c0 = FaceGrid - 2
	}
	if r0 > FaceGrid-2 {
		r0 = FaceGrid - 2
	}
	fu := gu - float64(c0)
	fv := gv - float64(r0)
	grid := f.Radii[face]
	i00 := grid[r0*FaceGrid+c0]
	i10 := grid[r0*FaceGrid+c0+1]
	i01 := grid[(r0+1)*FaceGrid+c0]
	i11 := grid[(r0+1)*FaceGrid+c0+1]
	top := i00 + (i10-i00)*fu
	bot := i01 + (i11-i01)*fu
	return top + (bot-top)*fv
}

// Cross returns the cross product a × b.
func Cross(a, b Vec) Vec {
	return Vec{a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0]}
}

func (v Vec) Add(w Vec) Vec { return Vec{v[0] + w[0], v[1] + w[1], v[2] + w[2]} }

// NormalEps is normal_eps 2° (GDD rule table), the finite-difference
// angular step for SurfaceNormal. At the nominal radius it spans ~5 m,
// about 1.5 grid cells, so the slope test averages across the bilinear
// kinks at grid lines instead of snapping to them.
const NormalEps = 2.0 * math.Pi / 180

// SurfaceNormal computes the terrain surface normal at direction d (unit)
// by finite differences of the sampled radius, using the exact formula of
// GDD "Terrain sampling" step 5 (both ends must derive the same normal
// from the same field). The result points outward (positive dot with d).
func (f *Field) SurfaceNormal(d Vec) Vec {
	var k Vec
	if math.Abs(d[0]) <= 0.9 {
		k = Vec{1, 0, 0}
	} else {
		k = Vec{0, 1, 0}
	}
	e1 := Normalize(k.Sub(d.Scale(k.Dot(d))))
	e2 := Cross(d, e1)
	p0 := d.Scale(f.SampleRadius(d))
	d1 := Normalize(d.Add(e1.Scale(NormalEps)))
	d2 := Normalize(d.Add(e2.Scale(NormalEps)))
	p1 := d1.Scale(f.SampleRadius(d1))
	p2 := d2.Scale(f.SampleRadius(d2))
	n := Cross(p1.Sub(p0), p2.Sub(p0))
	if n.Dot(d) < 0 {
		n = n.Scale(-1)
	}
	return Normalize(n)
}

// Slope returns the angle (radians) between the surface normal and local up
// (d) at direction d.
func (f *Field) Slope(d Vec) float64 {
	n := f.SurfaceNormal(d)
	c := n.Dot(d)
	if c > 1 {
		c = 1
	}
	if c < -1 {
		c = -1
	}
	return math.Acos(c)
}

// Walkable reports whether the slope at d is within max_slope.
func (f *Field) Walkable(d Vec) bool {
	return f.Slope(d) <= MaxSlope
}
