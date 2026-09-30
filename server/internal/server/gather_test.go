package server

import (
	"math"
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/skills"
	"space-adventure/server/internal/store"
)

// TestGatherChannel pins C121 and C126's rule halves: the channel counts
// whole ticks and yields on the last one, moving past the tolerance ends
// it `moved`, death `died`, an emptied node `depleted`; the duration
// shortens per level, takes the Engineering→Mining synergy for mining
// only, and floors at gather_min_channel.
func TestGatherChannel(t *testing.T) {
	from := [3]float64{0, 150, 0}
	g := gatherState{node: 3, ticks: 3, from: from}
	for i, want := range []string{"", "", "done"} {
		if got := stepGather(&g, from, false, 5); got != want {
			t.Fatalf("tick %d: %q, want %q", i, got, want)
		}
	}
	g = gatherState{node: 3, ticks: 3, from: from}
	if got := stepGather(&g, [3]float64{0.4, 150, 0.2}, false, 5); got != "" {
		t.Fatalf("inside the tolerance ended %q", got)
	}
	if got := stepGather(&g, [3]float64{0.6, 150, 0}, false, 5); got != "moved" {
		t.Fatalf("past the tolerance: %q", got)
	}
	g = gatherState{node: 3, ticks: 3, from: from}
	if got := stepGather(&g, from, true, 5); got != "died" {
		t.Fatalf("dead: %q", got)
	}
	if got := stepGather(&g, from, false, 0); got != "depleted" {
		t.Fatalf("empty node: %q", got)
	}
	if got := stepGather(&gatherState{}, from, false, 0); got != "" {
		t.Fatalf("idle stepped: %q", got)
	}

	reg := &defs.Registry{
		Skills:    []defs.Skill{{ID: "mining"}, {ID: "salvaging"}, {ID: "engineering"}},
		Synergies: []defs.Synergy{{Source: "engineering", Target: "mining", What: "gather_speed", PerLevel: 0.001}},
	}
	reg.Skills[0].Efficacy.Kind, reg.Skills[0].Efficacy.PerLevel = "gather_speed", 0.005
	reg.Skills[1].Efficacy.Kind, reg.Skills[1].Efficacy.PerLevel = "gather_speed", 0.005
	iron := defs.Node{Skill: "mining", Channel: 3.0}
	wreck := defs.Node{Skill: "salvaging", Channel: 3.0}
	p := &store.Player{}
	if d := gatherDuration(reg, p, iron); d != 3.0 {
		t.Fatalf("fresh: %v", d)
	}
	p.Skills.XP = map[string]int64{"mining": skills.PointsForLevel(21), "engineering": skills.PointsForLevel(11)}
	// mining 21 → −10%, engineering 11 → −1% on mining only.
	if d := gatherDuration(reg, p, iron); math.Abs(d-3.0*(1-0.10-0.01)) > 1e-9 {
		t.Fatalf("mining 21 + engineering 11: %v", d)
	}
	if d := gatherDuration(reg, p, wreck); d != 3.0 {
		t.Fatalf("synergy leaked onto salvaging: %v", d)
	}
	// Mining 99 halves a 1.8 s node to 0.9 s, which the floor lifts to 1.0.
	p.Skills.XP["mining"] = skills.PointsForLevel(99)
	if d := gatherDuration(reg, p, defs.Node{Skill: "mining", Channel: 1.8}); d != gatherMinChannel {
		t.Fatalf("floor: %v", d)
	}
}
