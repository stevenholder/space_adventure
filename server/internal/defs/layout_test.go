package defs

import (
	"math"
	"strings"
	"testing"
)

// A gated wall run must tile the right modules, put one gate where asked,
// seal its corners with deduplicated posts, and split its collider around
// the opening.
func TestExpandLayout_GatedRun(t *testing.T) {
	z := &Zone{ID: "t", Layout: &ZoneLayout{
		Faction: "scrap",
		Pieces: []LayoutPiece{
			{Kind: "wall_run", From: [3]float64{-16, 0, -14}, To: [3]float64{-16, 0, 14}, GateAt: 4},
			{Kind: "wall_run", From: [3]float64{-16, 0, 14}, To: [3]float64{16, 0, 14}},
		},
	}}
	if err := ExpandLayout(z); err != nil {
		t.Fatal(err)
	}

	var walls, gates, posts int
	for _, p := range z.Props {
		switch {
		case strings.HasPrefix(p.Asset, "struct.wall4"):
			walls++
		case strings.HasPrefix(p.Asset, "struct.gate4"):
			gates++
		case strings.HasPrefix(p.Asset, "struct.corner"):
			posts++
		}
	}
	if walls != 14 || gates != 1 { // 7 modules gated run has 6 walls, 8 solid
		t.Errorf("walls=%d gates=%d, want 14/1", walls, gates)
	}
	// Two runs share the (-16, 0, 14) corner: 4 endpoints, 3 posts.
	if posts != 3 {
		t.Errorf("posts=%d, want 3 (corner dedupe)", posts)
	}

	// Gated run: two collider segments flanking a 2.4 m opening centred on
	// module 4 (z = 0 for a run from z -14).
	if len(z.Colliders) != 3 {
		t.Fatalf("colliders=%d, want 3 (2 gate segments + 1 solid)", len(z.Colliders))
	}
	gapLo, gapHi := math.Inf(-1), math.Inf(1)
	for _, c := range z.Colliders[:2] {
		lo, hi := c.Pos[2]-c.Half[0], c.Pos[2]+c.Half[0] // run along z: half[0] is the run half-length
		if hi < 0.1 && hi > gapLo {
			gapLo = hi
		}
		if lo > -0.1 && lo < gapHi {
			gapHi = lo
		}
	}
	if math.Abs(gapLo-(-1.2)) > 1e-9 || math.Abs(gapHi-1.2) > 1e-9 {
		t.Errorf("gate opening [%v, %v], want [-1.2, 1.2]", gapLo, gapHi)
	}
}

func TestExpandLayout_Rejects(t *testing.T) {
	bad := []Zone{
		{ID: "a", Layout: &ZoneLayout{Faction: "scrap", Pieces: []LayoutPiece{
			{Kind: "wall_run", From: [3]float64{0, 0, 0}, To: [3]float64{7, 0, 0}}}}}, // not a module multiple
		{ID: "b", Layout: &ZoneLayout{Faction: "scrap", Pieces: []LayoutPiece{
			{Kind: "hab", Pos: [3]float64{0, 0, 0}}}}}, // hab is colony-only
		{ID: "c", Layout: &ZoneLayout{Faction: "chaos"}},
		{ID: "d", Layout: &ZoneLayout{Faction: "colony", Pieces: []LayoutPiece{
			{Kind: "pyramid"}}}},
	}
	for _, z := range bad {
		z := z
		if err := ExpandLayout(&z); err == nil {
			t.Errorf("zone %s: expansion accepted, want error", z.ID)
		}
	}
}
