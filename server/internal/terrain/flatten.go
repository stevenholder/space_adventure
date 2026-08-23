package terrain

import "math"

// Flatten levels the radius field under a zone before it is encoded for the
// wire (GDD "Static colliders" -> "The site is flattened, not assumed
// flat"). originDir need not be unit length; it is normalised internally,
// as is every lattice direction sampled below.
//
// originRadius is sampled once, before any writes, so the result does not
// depend on grid iteration order.
func Flatten(f *Field, originDir [3]float64, radius, falloff float64) {
	if radius <= 0 && falloff <= 0 {
		return
	}

	origin := Normalize(Vec(originDir))
	originRadius := f.SampleRadius(origin)
	outer := radius + falloff

	for face := 0; face < NumFaces; face++ {
		grid := &f.Radii[face]
		for row := 0; row < FaceGrid; row++ {
			v := 2*float64(row)/(FaceGrid-1) - 1
			for col := 0; col < FaceGrid; col++ {
				u := 2*float64(col)/(FaceGrid-1) - 1
				d := Normalize(DirOf(face, u, v))

				c := d.Dot(origin)
				if c > 1 {
					c = 1
				} else if c < -1 {
					c = -1
				}
				a := math.Acos(c) * PlanetRadius

				idx := row*FaceGrid + col
				switch {
				case a <= radius:
					grid[idx] = originRadius
				case a <= outer:
					t := (a - radius) / falloff
					w := t * t * (3 - 2*t)
					grid[idx] = originRadius*(1-w) + grid[idx]*w
				}
				// a > outer: unchanged.
			}
		}
	}
}
