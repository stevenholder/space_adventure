package server

// Task 6 (C80/C81/C82): server-side efficacy and synergy math off the sheet,
// and the data-driven unlock gate on purchases.

import (
	"encoding/json"
	"math"
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/skills"
	"space-adventure/server/internal/store"
)

func skillsTestRegistry() *defs.Registry {
	reg := &defs.Registry{}
	for _, r := range []struct {
		id   string
		kind string
		per  float64
	}{
		{"marksmanship", "damage_mult", 0.004},
		{"scavenging", "loot_extra_roll", 0.003},
		{"commerce", "buy_discount", 0.002},
		{"recon", "discovery_range", 0.005},
	} {
		sk := defs.Skill{ID: r.id}
		sk.Efficacy.Kind, sk.Efficacy.PerLevel = r.kind, r.per
		reg.Skills = append(reg.Skills, sk)
	}
	reg.Synergies = []defs.Synergy{
		{Source: "recon", Target: "scavenging", What: "loot_extra_roll", PerLevel: 0.002, Where: "poi"},
		{Source: "scavenging", Target: "commerce", What: "sell_bonus", PerLevel: 0.001},
	}
	return reg
}

func TestEfficacyAndSynergyMath(t *testing.T) {
	reg := skillsTestRegistry()
	near := func(got, want float64) bool { return math.Abs(got-want) < 1e-12 }

	fresh := &store.Player{}
	if efficacyMult(reg, fresh, "marksmanship") != 1 || efficacyBonus(reg, fresh, "commerce") != 0 ||
		synergyBonus(reg, fresh, "loot_extra_roll", "poi") != 0 {
		t.Fatal("fresh player must be the exact identity")
	}

	p := &store.Player{Skills: store.SkillsState{XP: map[string]int64{
		"marksmanship": skills.PointsForLevel(50),
		"scavenging":   skills.PointsForLevel(50),
		"commerce":     skills.PointsForLevel(99),
		"recon":        skills.PointsForLevel(20),
	}}}
	if got := efficacyMult(reg, p, "marksmanship"); !near(got, 1.196) {
		t.Fatalf("damage mult at 50 = %v, want 1.196", got)
	}
	if got := 1 - efficacyBonus(reg, p, "commerce"); !near(got, 0.804) {
		t.Fatalf("buy mult at 99 = %v, want 0.804 (the −19.6%% cap)", got)
	}
	if got := efficacyBonus(reg, p, "scavenging"); !near(got, 0.147) {
		t.Fatalf("loot extra at 50 = %v, want 0.147", got)
	}
	if got := synergyBonus(reg, p, "loot_extra_roll", "poi"); !near(got, 0.038) {
		t.Fatalf("recon→scavenging at 20 = %v, want 0.038", got)
	}
	if got := synergyBonus(reg, p, "loot_extra_roll", ""); got != 0 {
		t.Fatalf("poi synergy leaked outside a poi: %v", got)
	}
	if got := synergyBonus(reg, p, "sell_bonus", ""); !near(got, 0.049) {
		t.Fatalf("scavenging→commerce at 50 = %v, want 0.049", got)
	}
}

func TestUnlockGateOnPurchase(t *testing.T) {
	npc := defs.NPC{Kind: "shop"}
	npc.Stock = append(npc.Stock, struct {
		Item  string `json:"item"`
		Price int64  `json:"price"`
	}{Item: "widget", Price: 10})
	p := &store.Player{Credits: 100}
	w := testWorld(p, npc, 1)
	w.Reg.Unlocks = []defs.UnlockRequirement{{Item: "widget", Skill: "marksmanship", Level: 5}}
	req := protocol.Cmd{Seq: 1, Opcode: protocol.OpShopBuy, Data: []byte(`{"npc":1,"item":"widget","qty":1}`)}

	res := handleCmd(newCmdRate(time.Now()), time.Now(), req, w)
	var body struct {
		Reason string `json:"reason"`
	}
	_ = json.Unmarshal(res.Data, &body)
	if res.Status != protocol.StatusRefused || body.Reason != "locked" {
		t.Fatalf("below level: status=%v reason=%q, want refused/locked", res.Status, body.Reason)
	}
	if p.Credits != 100 {
		t.Fatalf("a locked purchase moved credits: %d", p.Credits)
	}

	p.Skills.XP = map[string]int64{"marksmanship": skills.PointsForLevel(5)}
	req.Seq = 2
	if res := handleCmd(newCmdRate(time.Now()), time.Now(), req, w); res.Status != protocol.StatusOK {
		t.Fatalf("at level: status=%v, want ok", res.Status)
	}
	if p.Credits != 90 {
		t.Fatalf("Credits = %d, want 90", p.Credits)
	}
}
