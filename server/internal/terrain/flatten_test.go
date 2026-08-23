package terrain

import "testing"

// TestFlattenOrigin: the lattice sample at the zone origin itself takes the
// origin radius exactly (GDD "Static colliders" -> "The site is flattened,
// not assumed flat").
func TestFlattenOrigin(t *testing.T) {
	f := Generate(1337)

	const face, row, col = FacePZ, 10, 20
	v := 2*float64(row)/(FaceGrid-1) - 1
	u := 2*float64(col)/(FaceGrid-1) - 1
	originDir := DirOf(face, u, v)

	originRadius := f.SampleRadius(Normalize(originDir))

	Flatten(f, [3]float64(originDir), 15, 5)

	got := f.Radii[face][row*FaceGrid+col]
	if got != originRadius {
		t.Fatalf("origin sample = %.12f, want %.12f (origin radius)", got, originRadius)
	}
}

// TestFlattenBeyondFalloffUnchanged: a sample whose arc distance exceeds
// radius+falloff is bit-identical to its pre-flatten value.
func TestFlattenBeyondFalloffUnchanged(t *testing.T) {
	f := Generate(1337)

	const originFace, originRow, originCol = FacePZ, 32, 32
	ov := 2*float64(originRow)/(FaceGrid-1) - 1
	ou := 2*float64(originCol)/(FaceGrid-1) - 1
	originDir := DirOf(originFace, ou, ov)

	// A node on the opposite face, far away in arc distance.
	const farFace, farRow, farCol = FaceNZ, 32, 32
	before := f.Radii[farFace][farRow*FaceGrid+farCol]

	Flatten(f, [3]float64(originDir), 10, 5)

	after := f.Radii[farFace][farRow*FaceGrid+farCol]
	if after != before {
		t.Fatalf("far sample changed: before=%.12f after=%.12f", before, after)
	}
}

// TestFlattenSharedEdgeConsistency: the flatten is a function of direction
// only, so the two grid copies of a shared cube-face edge — here the
// +X/+Y edge, where DirOf(FacePX, 1, v) == DirOf(FacePY, 1, v) for every
// row v — must still agree after flattening a region that straddles it.
func TestFlattenSharedEdgeConsistency(t *testing.T) {
	f := Generate(1337)

	// Origin sits exactly on the shared edge (mid-row), so the flattened
	// region straddles both faces' copies of it.
	const edgeRow = 32
	ev := 2*float64(edgeRow)/(FaceGrid-1) - 1
	originDir := DirOf(FacePX, 1, ev)

	Flatten(f, [3]float64(originDir), 20, 10)

	const edgeCol = FaceGrid - 1 // u = 1
	for row := 0; row < FaceGrid; row++ {
		px := f.Radii[FacePX][row*FaceGrid+edgeCol]
		py := f.Radii[FacePY][row*FaceGrid+edgeCol]
		if diff := px - py; diff > 1e-9 || diff < -1e-9 {
			t.Fatalf("shared edge mismatch at row %d: PX=%.12f PY=%.12f diff=%.3g", row, px, py, diff)
		}
	}
}

// TestFlattenZeroRadiusNoOp: radius 0 (with falloff 0) must be a no-op —
// the field is bit-identical afterwards.
func TestFlattenZeroRadiusNoOp(t *testing.T) {
	f := Generate(1337)
	before := *f // Field is a plain value type of arrays/slices header; copy fields we compare.

	originDir := DirOf(FacePX, 0.3, -0.2)
	Flatten(f, [3]float64(originDir), 0, 0)

	for face := 0; face < NumFaces; face++ {
		for i := range f.Radii[face] {
			if f.Radii[face][i] != before.Radii[face][i] {
				t.Fatalf("face %d idx %d changed: before=%.12f after=%.12f", face, i, before.Radii[face][i], f.Radii[face][i])
			}
		}
	}
}
