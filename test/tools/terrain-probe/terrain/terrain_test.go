package terrain

import (
	"bytes"
	"math"
	"math/rand/v2"
	"testing"
)

// TestGenerateDeterminism: same seed → bit-identical wire bytes.
func TestGenerateDeterminism(t *testing.T) {
	a := Generate(1337).Encode()
	b := Generate(1337).Encode()
	if !bytes.Equal(a, b) {
		t.Fatal("same seed produced different terrain")
	}
	c := Generate(1338).Encode()
	if bytes.Equal(a, c) {
		t.Fatal("different seeds produced identical terrain")
	}
}

// TestGenerateConstraints checks the GDD "Must hold" shape contract on a
// few seeds: ≥70% walkable, flat spawn disc, radii in range, seamless face
// edges, six distinct separated landmarks.
func TestGenerateConstraints(t *testing.T) {
	for _, seed := range []uint64{1, 42, 1337} {
		f := Generate(seed)
		rep := f.Report()
		if rep.WalkableFraction < 0.70 {
			t.Errorf("seed %d: walkable fraction %.3f < 0.70", seed, rep.WalkableFraction)
		}
		if rep.SpawnFlatErr > 0.5 {
			t.Errorf("seed %d: spawn disc deviates %.3f m > 0.5", seed, rep.SpawnFlatErr)
		}
		if rep.MinR < RadiusMin || rep.MaxR > RadiusMax {
			t.Errorf("seed %d: radii out of range: [%.1f, %.1f]", seed, rep.MinR, rep.MaxR)
		}
		if rep.SeamMaxErr > 1e-9 {
			t.Errorf("seed %d: face-edge mismatch %.3g m", seed, rep.SeamMaxErr)
		}
		if len(f.Landmarks) != 6 {
			t.Fatalf("seed %d: %d landmarks, want 6", seed, len(f.Landmarks))
		}
		if rep.LandmarkMinSep < LandmarkMinSep {
			t.Errorf("seed %d: landmarks separated by %.1f°, want ≥ %.1f°",
				seed, rep.LandmarkMinSep*180/math.Pi, LandmarkMinSep*180/math.Pi)
		}
		if rep.Clamped > 0 {
			t.Logf("seed %d: %d samples hit the radius clamp (tune amplitudes)", seed, rep.Clamped)
		}
	}
}

// TestEncodeDecodeRoundTrip: a decoded copy of the encoded field samples
// identically, cell for cell.
func TestEncodeDecodeRoundTrip(t *testing.T) {
	f := Generate(777)
	buf := f.Encode()
	g, err := Decode(buf)
	if err != nil {
		t.Fatalf("Decode: %v", err)
	}
	for face := range NumFaces {
		for i := 0; i < FaceGrid*FaceGrid; i++ {
			if f.Radii[face][i] != g.Radii[face][i] {
				t.Fatalf("face %d cell %d: %.6f != %.6f", face, i, f.Radii[face][i], g.Radii[face][i])
			}
		}
	}
	// Spot-check bilinear sampling parity at arbitrary directions.
	pts := []Vec{{1, 2, 3}, {-3, 1, 4}, {0.1, -0.9, 0.2}, {2, -5, -1}}
	for i := range pts {
		d := Normalize(pts[i])
		if a, b := f.SampleRadius(d), g.SampleRadius(d); a != b {
			t.Fatalf("sample %v: %.6f != %.6f", d, a, b)
		}
	}
}

// TestNormalPointsOutward: on the flat spawn disc the finite-difference
// normal is exactly local up (to finite-difference tolerance), and on
// walkable ground it never points inward.
func TestNormalPointsOutward(t *testing.T) {
	f := Generate(1337)
	if d := f.SurfaceNormal(SpawnDir).Dot(SpawnDir); math.Abs(d-1) > 1e-3 {
		t.Errorf("spawn normal · up = %.4f, want 1", d)
	}
	rng := rand.New(rand.NewPCG(0, 0))
	for range 200 {
		d := randomDir(rng)
		if f.Walkable(d) && f.SurfaceNormal(d).Dot(d) <= 0 {
			t.Fatalf("normal points inward at %v", d)
		}
	}
}
