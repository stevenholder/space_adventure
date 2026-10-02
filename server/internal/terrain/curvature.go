package terrain

import "math"

// curvStep is the finite-difference step of Curvature, radians of arc.
const curvStep = 0.02

// Curvature is the discrete Laplacian of the radius field at d, m/rad^2:
// positive in a basin, negative on a ridge. A transliteration of the C#
// TerrainField.Curvature, which biases the rock scatter; the server needs
// it only to place the same rocks the client draws (sim/rocks.go).
func (f *Field) Curvature(d Vec) float64 {
	k := Vec{0, 1, 0}
	if math.Abs(d[0]) <= 0.9 {
		k = Vec{1, 0, 0}
	}
	e1 := Normalize(k.Sub(d.Scale(k.Dot(d))))
	e2 := Cross(d, e1)
	r0 := f.SampleRadius(d)
	d1 := Normalize(d.Add(e1.Scale(curvStep)))
	d2 := Normalize(d.Add(e2.Scale(curvStep)))
	d3 := Normalize(d.Sub(e1.Scale(curvStep)))
	d4 := Normalize(d.Sub(e2.Scale(curvStep)))
	sum := f.SampleRadius(d1) + f.SampleRadius(d2) + f.SampleRadius(d3) + f.SampleRadius(d4)
	return (sum - 4*r0) / (curvStep * curvStep)
}
