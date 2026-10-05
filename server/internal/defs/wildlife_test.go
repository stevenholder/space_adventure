package defs

import (
	"math"
	"strings"
	"testing"

	"space-adventure/server/internal/terrain"
)

// Each GDD audit rule rejects its own bad herd; the shipped file passes.
func TestWildlifeAudit(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatalf("shipped data: %v", err)
	}
	if len(reg.Herds) != 9 {
		t.Fatalf("shipped herds = %d, want 9", len(reg.Herds))
	}
	good := reg.Herds[0] // herd.blobs.east: 4 green blobs, spread 5, wander 10
	cases := []struct {
		name string
		mut  func(hs []Herd) []Herd
		want string
	}{
		{"id prefix", func(hs []Herd) []Herd { hs[0].ID = "blobs"; return hs }, "herd.*"},
		{"duplicate id", func(hs []Herd) []Herd { return append(hs, good) }, "defined twice"},
		{"unknown def", func(hs []Herd) []Herd { hs[0].Def = "mob.nope"; return hs }, "unknown npc"},
		{"not a combatant", func(hs []Herd) []Herd { hs[0].Def = "npc.quartermaster"; return hs }, "not a combatant"},
		{"count 0", func(hs []Herd) []Herd { hs[0].Count = 0; return hs }, "count"},
		{"count 13", func(hs []Herd) []Herd { hs[0].Count = 13; hs[0].Spread = 20; return hs }, "count"},
		{"zero dir", func(hs []Herd) []Herd { hs[0].OriginDir = [3]float64{}; return hs }, "origin_dir"},
		{"nan dir", func(hs []Herd) []Herd { hs[0].OriginDir[1] = math.NaN(); return hs }, "origin_dir"},
		{"negative spread", func(hs []Herd) []Herd { hs[0].Count = 1; hs[0].Spread = -1; return hs }, "spread"},
		{"stacked", func(hs []Herd) []Herd { hs[0].Spread = 0.5; return hs }, "stack"},
		{"negative wander", func(hs []Herd) []Herd { hs[0].Wander = -1; return hs }, "wander"},
		{"wander past leash", func(hs []Herd) []Herd { hs[0].Wander = 39; return hs }, "wander"},
		{"near spawn", func(hs []Herd) []Herd { hs[0].OriginDir = [3]float64{0.1, 1, 0}; return hs }, "from spawn"},
		{"near a zone", func(hs []Herd) []Herd {
			for _, z := range reg.Zones {
				hs[0].OriginDir = vNormalize(z.OriginDir)
				break
			}
			return hs
		}, "from zone"},
		{"over budget", func(hs []Herd) []Herd {
			for i := range hs {
				hs[i].Count = 8
				hs[i].Spread = 6
			}
			return hs
		}, "budget"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := *reg
			r.Herds = c.mut(append([]Herd(nil), reg.Herds...))
			err := auditHerds(&r)
			if err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("err = %v, want one containing %q", err, c.want)
			}
		})
	}
}

// Shipped herds compose inside their disc, on the surface, apart; a lone
// member stands at the centre facing east.
func TestComposeHerd(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	radius := func(d [3]float64) float64 { return terrain.PlanetRadius + 2*d[0] - d[2] } // not a sphere
	for _, h := range reg.Herds {
		ps := ComposeHerd(h, radius)
		if len(ps) != h.Count {
			t.Fatalf("%s: %d placements, want %d", h.ID, len(ps), h.Count)
		}
		rad := reg.NPCs[h.Def].Radius()
		c := vNormalize(h.OriginDir)
		for i, p := range ps {
			if p.Type != "npc" || p.Def != h.Def {
				t.Errorf("%s[%d]: %s %s", h.ID, i, p.Type, p.Def)
			}
			d := vNormalize(p.Pos)
			if got := vLen(p.Pos); math.Abs(got-radius(d)) > 1e-9 {
				t.Errorf("%s[%d]: |pos| %v, surface %v", h.ID, i, got, radius(d))
			}
			if s := math.Acos(math.Min(1, vDot(c, d))) * radius(c); s > h.Spread+1e-6 {
				t.Errorf("%s[%d]: %.2f m from centre, spread %v", h.ID, i, s, h.Spread)
			}
			for j := 0; j < i; j++ {
				if gap := vLen(vAdd(p.Pos, vScale(ps[j].Pos, -1))); gap < 2*rad {
					t.Errorf("%s: members %d,%d %.2f m apart, need %.2f", h.ID, j, i, gap, 2*rad)
				}
			}
		}
		if h.Count == 1 {
			if s := math.Acos(math.Min(1, vDot(c, vNormalize(ps[0].Pos)))); s > 1e-9 {
				t.Errorf("%s: lone member off centre by %v rad", h.ID, s)
			}
			// forward (local +Z) of the lone member is the frame's east
			q := ps[0].Quat
			fwd := [3]float64{2 * (q[0]*q[2] + q[3]*q[1]), 2 * (q[1]*q[2] - q[3]*q[0]), 1 - 2*(q[0]*q[0]+q[1]*q[1])}
			north := vNormalize(vAdd([3]float64{0, 0, 1}, vScale(c, -c[2])))
			east := vCross(c, north)
			if vDot(fwd, east) < 0.9999 {
				t.Errorf("%s: lone member faces %v, want east %v", h.ID, fwd, east)
			}
		}
	}
}
