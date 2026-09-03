package defs

// C69 — the clearance audit. Every committed zone's site must clear every
// world feature by its own reach, and every pair of zones must clear each
// other. The rule the camp and range each broke once by hand ("anything the
// terrain audit checks for has to be in the avoid set") is enforced here
// against the SHIPPED data, not promised by the solver that proposed it.

import (
	"math"
	"testing"

	"space-adventure/server/internal/terrain"
)

func surfDist(a, b terrain.Vec) float64 {
	dot := a.Dot(b)
	if dot > 1 {
		dot = 1
	} else if dot < -1 {
		dot = -1
	}
	return math.Acos(dot) * terrain.PlanetRadius
}

func TestZoneSiteClearance(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	field := terrain.Generate(1337)

	type feature struct {
		name   string
		dir    terrain.Vec
		extent float64
	}
	features := []feature{{"spawn", terrain.SpawnDir, 25 + 12}}
	for _, l := range field.Landmarks {
		features = append(features, feature{"landmark:" + l.Name, l.Dir, 25})
	}
	for i, c := range field.Craters {
		features = append(features, feature{name: "crater", dir: c.Dir, extent: c.Radius})
		_ = i
	}

	// The legacy sites were swept by hand against a smaller rulebook, and
	// they stand where they stand — moving the camp breaks every committed
	// route and test position. They are audited with no daylight margin;
	// zones the SOLVER placed owe the style guide's full 10 m.
	margin := map[string]float64{"camp": 0, "range": 0}

	zoneReach := func(z Zone) float64 { return z.FlattenRadius + z.FlattenFalloff }

	for id, z := range reg.Zones {
		if z.FlattenRadius == 0 {
			continue
		}
		m, ok := margin[id]
		if !ok {
			m = 10
		}
		dir := terrain.Vec{z.OriginDir[0], z.OriginDir[1], z.OriginDir[2]}
		for _, f := range features {
			if got := surfDist(dir, f.dir); got < zoneReach(z)+f.extent+m {
				t.Errorf("zone %s vs %s: %.1f m, want >= %.1f", id, f.name, got, zoneReach(z)+f.extent+m)
			}
		}
		for otherID, o := range reg.Zones {
			if otherID == id || o.FlattenRadius == 0 {
				continue
			}
			oDir := terrain.Vec{o.OriginDir[0], o.OriginDir[1], o.OriginDir[2]}
			if got := surfDist(dir, oDir); got < zoneReach(z)+zoneReach(o)+m {
				t.Errorf("zone %s vs zone %s: %.1f m, want >= %.1f", id, otherID, got, zoneReach(z)+zoneReach(o)+m)
			}
		}
	}
}

// C68 — every POI layout carries exactly one mast, and mast spacing keeps
// at most one skyline spike per view (GDD: 120 m).
func TestPOIMasts(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	type mastAt struct {
		zone string
		dir  terrain.Vec
	}
	var masts []mastAt
	for id, z := range reg.Zones {
		if z.Layout == nil {
			continue
		}
		count := 0
		for _, p := range z.Layout.Pieces {
			if p.Kind == "mast" {
				count++
			}
		}
		if count != 1 {
			t.Errorf("zone %s: %d masts, want exactly 1", id, count)
		}
		masts = append(masts, mastAt{id, terrain.Vec{z.OriginDir[0], z.OriginDir[1], z.OriginDir[2]}})
	}
	for i := range masts {
		for j := i + 1; j < len(masts); j++ {
			if got := surfDist(masts[i].dir, masts[j].dir); got < 120 {
				t.Errorf("masts %s and %s only %.1f m apart, want >= 120", masts[i].zone, masts[j].zone, got)
			}
		}
	}
}
