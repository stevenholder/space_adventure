package store

import "testing"

// A clone shares nothing mutable with its source: identity.Snapshot hands
// clones to readers that hold no lock while Mutate keeps writing.
func TestPlayerCloneIsIndependent(t *testing.T) {
	p := Player{
		Inventory: []Stack{{Item: "a", Qty: 1}},
		Equipped:  map[string]string{"head": "h"},
		Missions:  map[string]*MissionState{"m": {Count: 1}},
		Skills:    SkillsState{XP: map[string]int64{"x": 1}, Discovered: []string{"poi"}},
	}
	c := p.Clone()
	p.Inventory[0].Qty = 9
	p.Equipped["head"] = "other"
	p.Missions["m"].Count = 9
	p.Skills.XP["x"] = 9
	p.Skills.Discovered[0] = "other"
	if c.Inventory[0].Qty != 1 || c.Equipped["head"] != "h" || c.Missions["m"].Count != 1 ||
		c.Skills.XP["x"] != 1 || c.Skills.Discovered[0] != "poi" {
		t.Fatalf("clone changed with its source: %+v", c)
	}
	if (Player{}).Clone().Missions != nil {
		t.Fatal("nil missions must stay nil")
	}
}
