package defs

// Phase 9 zone layouts (docs/GDD.md "World art style guide").
//
// A zone with a `layout` describes its structures as KIT PLACEMENTS on the
// 4 m module grid, and this file expands them — in the zone's LOCAL frame,
// before ComposeZone runs — into both the colliders the sim resolves against
// and the props the client renders. One source, two derivations: the wall
// you see and the wall you hit cannot drift apart, which the hand-authored
// era could not promise.
//
// Pieces are modelled at final scale (art/tools/gen_kit.py), so every prop
// here has Scale 1 and the client tiles, never stretches.

import (
	"fmt"
	"math"
)

// ZoneLayout is the kit description of a zone's structures.
type ZoneLayout struct {
	// Faction picks the kit skin: "scrap" or "colony".
	Faction string        `json:"faction"`
	Pieces  []LayoutPiece `json:"pieces"`
}

// LayoutPiece is one placement. Kinds:
//
//	wall_run — posts at From and To, wall4 modules tiled between; the
//	           horizontal From→To distance must be a whole number of 4 m
//	           modules. GateAt (1-based module index) swaps one module for
//	           gate4 and SPLITS the derived collider around the opening.
//	tower | mast | hab | shack | cover4 — single pieces at Pos/Yaw.
type LayoutPiece struct {
	Kind   string     `json:"kind"`
	From   [3]float64 `json:"from,omitempty"`
	To     [3]float64 `json:"to,omitempty"`
	GateAt int        `json:"gate_at,omitempty"`
	Pos    [3]float64 `json:"pos,omitempty"`
	Yaw    float64    `json:"yaw,omitempty"`
}

const moduleLen = 4.0

// Derived collider shapes per single-piece kind. Center height and half
// extents; plan halves stay just inside the visual so a wall never reads
// thinner than it hits.
var pieceColliders = map[string]struct {
	centerY float64
	half    [3]float64
}{
	"tower":  {4.0, [3]float64{3.9, 4.0, 3.9}},
	"mast":   {6.3, [3]float64{0.5, 6.3, 0.5}},
	"hab":    {1.6, [3]float64{3.55, 1.6, 3.55}},
	"shack":  {1.5, [3]float64{3.4, 1.5, 3.4}},
	"cover4": {1.1, [3]float64{1.95, 1.1, 1.95}},
}

// factionOnly guards the pieces that exist in one skin.
var factionOnly = map[string]string{"hab": "colony", "shack": "scrap"}

// ExpandLayout appends the layout's derived colliders and props to the
// zone's own lists. Deterministic: pieces in order, then the deduplicated
// corner posts. Called by the registry loader before composition.
func ExpandLayout(z *Zone) error {
	if z.Layout == nil {
		return nil
	}
	l := z.Layout
	if l.Faction != "scrap" && l.Faction != "colony" {
		return fmt.Errorf("zone %s: layout faction %q (want scrap|colony)", z.ID, l.Faction)
	}
	skin := func(piece string) string { return "struct." + piece + "." + l.Faction }

	// Corner posts dedupe by rounded position: four wall runs meeting at a
	// compound's corners must yield ONE post per corner, not a z-fighting
	// pair.
	type postKey [3]int
	posts := map[postKey][3]float64{}
	postOrder := []postKey{}
	addPost := func(p [3]float64) {
		k := postKey{int(math.Round(p[0] * 10)), int(math.Round(p[1] * 10)), int(math.Round(p[2] * 10))}
		if _, ok := posts[k]; !ok {
			posts[k] = p
			postOrder = append(postOrder, k)
		}
	}

	for i, p := range l.Pieces {
		switch p.Kind {
		case "wall_run":
			dx, dz := p.To[0]-p.From[0], p.To[2]-p.From[2]
			length := math.Hypot(dx, dz)
			n := int(math.Round(length / moduleLen))
			if n < 1 || math.Abs(length-float64(n)*moduleLen) > 0.01 {
				return fmt.Errorf("zone %s: wall_run %d length %.2f is not a whole number of %g m modules",
					z.ID, i, length, moduleLen)
			}
			if p.GateAt < 0 || p.GateAt > n {
				return fmt.Errorf("zone %s: wall_run %d gate_at %d outside 1..%d", z.ID, i, p.GateAt, n)
			}
			ux, uz := dx/length, dz/length
			// Model +X must land on the run direction. Local yaw is about +y
			// with yaw 0 keeping +x at +x; rotating by θ maps +x to
			// (cos θ, 0, −sin θ), so θ = atan2(−uz, ux).
			yawDeg := math.Atan2(-uz, ux) * 180 / math.Pi

			at := func(d float64) [3]float64 {
				return [3]float64{p.From[0] + ux*d, p.From[1], p.From[2] + uz*d}
			}

			addPost(p.From)
			addPost(p.To)
			for m := 1; m <= n; m++ {
				center := at(float64(m)*moduleLen - moduleLen/2)
				asset := skin("wall4")
				if m == p.GateAt {
					asset = skin("gate4")
				}
				z.Props = append(z.Props, ZoneProp{Asset: asset, Pos: center, Yaw: yawDeg, Scale: 1})
			}

			// The collider: one box for a solid run; two flanking a 2.4 m
			// opening for a gated one. Runs overhang half a post at each end
			// so butting runs seal their corners.
			const overhang = 0.6
			seg := func(d0, d1 float64) {
				mid := at((d0 + d1) / 2)
				z.Colliders = append(z.Colliders, ZoneCollider{
					Kind: "box",
					Pos:  [3]float64{mid[0], p.From[1] + 1.55, mid[2]},
					Half: [3]float64{(d1 - d0) / 2, 1.55, 0.6},
					Yaw:  yawDeg,
				})
			}
			if p.GateAt == 0 {
				seg(-overhang, length+overhang)
			} else {
				gateCenter := float64(p.GateAt)*moduleLen - moduleLen/2
				seg(-overhang, gateCenter-1.2)
				seg(gateCenter+1.2, length+overhang)
			}

		case "tower", "mast", "hab", "shack", "cover4":
			if want, ok := factionOnly[p.Kind]; ok && want != l.Faction {
				return fmt.Errorf("zone %s: piece %d: %s exists only in the %s skin", z.ID, i, p.Kind, want)
			}
			c := pieceColliders[p.Kind]
			z.Props = append(z.Props, ZoneProp{Asset: skin(p.Kind), Pos: p.Pos, Yaw: p.Yaw, Scale: 1})
			z.Colliders = append(z.Colliders, ZoneCollider{
				Kind: "box",
				Pos:  [3]float64{p.Pos[0], p.Pos[1] + c.centerY, p.Pos[2]},
				Half: c.half,
				Yaw:  p.Yaw,
			})

		default:
			return fmt.Errorf("zone %s: piece %d: unknown layout kind %q", z.ID, i, p.Kind)
		}
	}

	for _, k := range postOrder {
		z.Props = append(z.Props, ZoneProp{Asset: skin("corner"), Pos: posts[k], Scale: 1})
	}
	return nil
}
