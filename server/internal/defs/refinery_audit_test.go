package defs

// Phase 22 data audits (GDD "The refinery — make everything, from nothing";
// ROADMAP C183, C186, C187). These read the shipped data only. Recipes'
// station/skill/seconds and items' gather_mult are read straight from the
// JSON so the audit does not lean on which struct fields exist yet.

import (
	"encoding/json"
	"io/fs"
	"math"
	"testing"

	"space-adventure/server/data"
)

// The five raws: gathered or dropped, never made.
var refineryRaws = map[string]bool{
	"mat.ore.iron": true, "mat.ore.copper": true, "mat.scrap": true,
	"mat.hide": true, "mat.crystal": true,
}

type auditRecipe struct {
	ID      string    `json:"id"`
	Level   int       `json:"level"`
	Skill   string    `json:"skill"`
	Station string    `json:"station"`
	Seconds float64   `json:"seconds"`
	Inputs  []ItemQty `json:"inputs"`
	Output  ItemQty   `json:"output"`
}

func readAuditRecipes(t *testing.T) []auditRecipe {
	t.Helper()
	raw, err := fs.ReadFile(data.FS, "recipes.json")
	if err != nil {
		t.Fatal(err)
	}
	var f struct {
		Recipes []auditRecipe `json:"recipes"`
	}
	if err := json.Unmarshal(raw, &f); err != nil {
		t.Fatal(err)
	}
	return f.Recipes
}

func loadAudit(t *testing.T) *Registry {
	t.Helper()
	reg, err := Load()
	if err != nil {
		t.Fatalf("shipped data: %v", err)
	}
	return reg
}

// C183: every item past the five raws (vehicles excepted until Phase 24)
// is the output of exactly one recipe; a raw is the output of none.
func TestRefineryEveryItemHasOneRecipe(t *testing.T) {
	reg := loadAudit(t)
	makes := map[string]int{}
	for _, r := range readAuditRecipes(t) {
		makes[r.Output.Item]++
	}
	for id, it := range reg.Items {
		switch {
		case refineryRaws[id]:
			if makes[id] != 0 {
				t.Errorf("raw %s is the output of %d recipes, want 0", id, makes[id])
			}
		case it.Kind == "vehicle":
			// the shop keeps the ship until the dock (Phase 24)
		default:
			if makes[id] != 1 {
				t.Errorf("item %s is the output of %d recipes, want exactly 1", id, makes[id])
			}
		}
	}
	for raw := range refineryRaws {
		if _, ok := reg.Items[raw]; !ok {
			t.Errorf("raw %s missing from items.json", raw)
		}
	}
}

// Every recipe names a known station and skill, takes time, and eats and
// makes known items in positive counts.
func TestRefineryRecipesWellFormed(t *testing.T) {
	reg := loadAudit(t)
	skills := map[string]bool{}
	for _, s := range reg.Skills {
		skills[s.ID] = true
	}
	stations := map[string]bool{"hand": true, "bench": true, "forge": true}
	for _, r := range readAuditRecipes(t) {
		if !stations[r.Station] {
			t.Errorf("%s: station %q, want hand|bench|forge", r.ID, r.Station)
		}
		if !skills[r.Skill] {
			t.Errorf("%s: skill %q is not in skills.json", r.ID, r.Skill)
		}
		if !(r.Seconds > 0) || math.IsInf(r.Seconds, 0) {
			t.Errorf("%s: seconds %v, want > 0", r.ID, r.Seconds)
		}
		if r.Level < 1 {
			t.Errorf("%s: level %d, want >= 1", r.ID, r.Level)
		}
		if _, ok := reg.Items[r.Output.Item]; !ok || r.Output.Qty < 1 {
			t.Errorf("%s: output %s ×%d is not a known item in a positive count", r.ID, r.Output.Item, r.Output.Qty)
		}
		if len(r.Inputs) == 0 {
			t.Errorf("%s: no inputs", r.ID)
		}
		for _, in := range r.Inputs {
			if _, ok := reg.Items[in.Item]; !ok || in.Qty < 1 {
				t.Errorf("%s: input %s ×%d is not a known item in a positive count", r.ID, in.Item, in.Qty)
			}
		}
	}
}

// Nodes name real tools, real skills and real loot tables; every loot
// table names real items. Iron and crystal take the crude drill (the
// drill and the mk2 stand in for it), copper keeps the mk2.
func TestRefineryNodesAndLootReferenceKnownIDs(t *testing.T) {
	reg := loadAudit(t)
	skills := map[string]bool{}
	for _, s := range reg.Skills {
		skills[s.ID] = true
	}
	for id, n := range reg.Nodes {
		if it, ok := reg.Items[n.Tool]; !ok || it.Kind != "tool" {
			t.Errorf("node %s: tool %q is not a tool item", id, n.Tool)
		}
		if !skills[n.Skill] {
			t.Errorf("node %s: skill %q unknown", id, n.Skill)
		}
		if _, ok := reg.Loot[n.Loot]; !ok {
			t.Errorf("node %s: loot table %q unknown", id, n.Loot)
		}
	}
	wantTool := map[string]string{
		"node.ore.iron": "tool.drill.crude", "node.crystal": "tool.drill.crude",
		"node.ore.copper": "tool.drill.mk2", "node.wreck": "tool.cutter",
	}
	for id, tool := range wantTool {
		if n, ok := reg.Nodes[id]; !ok || n.Tool != tool {
			t.Errorf("node %s: tool %q, want %q", id, reg.Nodes[id].Tool, tool)
		}
	}
	c := reg.Nodes["node.crystal"]
	if c.Skill != "mining" || c.Level != 1 || c.Channel != 3.5 || c.Yields != 4 || c.Respawn != 150 || c.XP != 40 || c.Asset != "prop.node.crystal" {
		t.Errorf("node.crystal = %+v, want mining 1, channel 3.5, 4 yields, respawn 150, xp 40, prop.node.crystal", c)
	}
	if l := reg.Loot[c.Loot]; len(l) != 1 || l[0].Item != "mat.crystal" || l[0].Qty != 2 || l[0].Chance != 1 {
		t.Errorf("crystal loot = %+v, want mat.crystal ×2 at 1.0", l)
	}
	for tid, tbl := range reg.Loot {
		for _, e := range tbl {
			if _, ok := reg.Items[e.Item]; !ok {
				t.Errorf("loot %s: unknown item %q", tid, e.Item)
			}
			if e.Qty < 1 || !(e.Chance > 0 && e.Chance <= 1) {
				t.Errorf("loot %s: %s ×%d at %v, want qty >= 1 and 0 < chance <= 1", tid, e.Item, e.Qty, e.Chance)
			}
		}
	}
	has := func(table, item string, qty int, chance float64) {
		for _, e := range reg.Loot[table] {
			if e.Item == item && e.Qty == qty && e.Chance == chance {
				return
			}
		}
		t.Errorf("loot %s: no %s ×%d at %v", table, item, qty, chance)
	}
	has("loot.wild.small", "mat.hide", 2, 0.6)
	has("loot.wild.big", "mat.hide", 2, 1.0)
	has("loot.wild.mech", "mat.wiring", 2, 0.3)
	has("loot.wild.mech", "mat.core", 1, 0.03)
	for _, tb := range []string{"loot.grunt", "loot.gunner", "loot.warlord"} {
		has(tb, "mat.parts", 2, 0.25)
	}
}

// C187 from nothing: no credits, an empty bag, the crude drill gathers
// slower than the drill, and the first board mission pays the cutter.
func TestRefineryStartsFromNothing(t *testing.T) {
	reg := loadAudit(t)
	if reg.StartCredits != 0 {
		t.Errorf("start_credits = %d, want 0", reg.StartCredits)
	}
	if len(reg.StartItems) != 0 {
		t.Errorf("start_items = %v, want []", reg.StartItems)
	}
	raw, err := fs.ReadFile(data.FS, "items.json")
	if err != nil {
		t.Fatal(err)
	}
	var f struct {
		Items []struct {
			ID         string  `json:"id"`
			GatherMult float64 `json:"gather_mult"`
		} `json:"items"`
	}
	if err := json.Unmarshal(raw, &f); err != nil {
		t.Fatal(err)
	}
	for _, it := range f.Items {
		if it.ID == "tool.drill.crude" && it.GatherMult != 1.5 {
			t.Errorf("tool.drill.crude gather_mult = %v, want 1.5", it.GatherMult)
		}
	}
	m, ok := reg.Missions["mission.first_scrap"]
	if !ok || m.Type != "fetch" || m.Item != "mat.scrap" || m.Count != 10 || m.Reward != 180 || !m.Board {
		t.Errorf("mission.first_scrap = %+v, want a board fetch of 10 mat.scrap for 180", m)
	}
	for _, n := range reg.NPCs {
		for _, s := range n.Stock {
			if s.Item == "tool.cutter" && s.Price > m.Reward {
				t.Errorf("%s sells the cutter at %d, more than first_scrap's %d", n.ID, s.Price, m.Reward)
			}
		}
	}
}

// The three new roster rows, their speed, the synergy, and the mk2 gate.
func TestRefinerySkills(t *testing.T) {
	reg := loadAudit(t)
	got := map[string]Skill{}
	for _, s := range reg.Skills {
		got[s.ID] = s
	}
	for _, id := range []string{"smithing", "chemistry", "construction"} {
		s, ok := got[id]
		if !ok || s.Efficacy.Kind != "craft_speed" || s.Efficacy.PerLevel != 0.005 {
			t.Errorf("skill %s = %+v, want craft_speed 0.005/lvl", id, s)
		}
	}
	syn := false
	for _, s := range reg.Synergies {
		if s.Source == "smithing" && s.Target == "engineering" && s.What == "craft_speed" && s.PerLevel == 0.001 {
			syn = true
		}
	}
	if !syn {
		t.Error("no smithing -> engineering craft_speed 0.001 synergy")
	}
	gate := false
	for _, u := range reg.Unlocks {
		if u.Item == "tool.drill.mk2" && u.Skill == "engineering" && u.Level == 10 {
			gate = true
		}
	}
	if !gate {
		t.Error("tool.drill.mk2 lost its engineering 10 unlock")
	}
}

// Stations and crystal are placed: one forge (kind forge) at the spawn pad,
// four crystal nodes (two at spawn, two at the relay).
func TestRefineryPlacements(t *testing.T) {
	reg := loadAudit(t)
	if n, ok := reg.NPCs["npc.forge"]; !ok || n.Kind != "forge" || n.MoveSpeed != 0 || n.Asset != "prop.forge" {
		t.Errorf("npc.forge = %+v, want kind forge, asset prop.forge, non-combat", n)
	}
	forges := map[string]int{}
	crystals := map[string]int{}
	for zid, z := range reg.Zones {
		for _, e := range z.Entities {
			if e.Type == "npc" && reg.NPCs[e.Def].Kind == "forge" {
				forges[zid]++
			}
			if e.Type == "node" && e.Def == "node.crystal" {
				crystals[zid]++
			}
		}
	}
	if len(forges) != 1 || forges["spawn"] != 1 {
		t.Errorf("forges placed = %v, want exactly one, at the spawn pad", forges)
	}
	if crystals["spawn"] != 2 || crystals["relay"] != 2 || len(crystals) != 2 {
		t.Errorf("crystal nodes placed = %v, want spawn 2, relay 2", crystals)
	}
}

// The shop is convenience at a markup: everything Vex stocks that has a
// recipe sells for at least 1.5× its material value — the recipe's inputs
// valued recursively down to the raws (a raw at its own value), per unit of
// output.
func TestRefineryShopPriceVsRecipe(t *testing.T) {
	reg := loadAudit(t)
	byOut := map[string]auditRecipe{}
	for _, r := range readAuditRecipes(t) {
		byOut[r.Output.Item] = r
	}
	memo := map[string]float64{}
	var unit func(id string, depth int) float64
	unit = func(id string, depth int) float64 {
		if v, ok := memo[id]; ok {
			return v
		}
		if depth > 32 {
			t.Fatalf("recipe cycle through %s", id)
		}
		r, ok := byOut[id]
		if !ok {
			v := float64(reg.Items[id].Value)
			memo[id] = v
			return v
		}
		sum := 0.0
		for _, in := range r.Inputs {
			sum += unit(in.Item, depth+1) * float64(in.Qty)
		}
		v := sum / float64(r.Output.Qty)
		memo[id] = v
		return v
	}
	vex, ok := reg.NPCs["npc.quartermaster"]
	if !ok || len(vex.Stock) == 0 {
		t.Fatal("npc.quartermaster has no stock")
	}
	for _, s := range vex.Stock {
		if _, ok := byOut[s.Item]; !ok {
			if reg.Items[s.Item].Kind != "vehicle" {
				t.Errorf("Vex stocks %s, which has no recipe", s.Item)
			}
			continue
		}
		need := 1.5 * unit(s.Item, 0)
		if float64(s.Price) < need {
			t.Errorf("Vex sells %s at %d, want >= %.2f (1.5 × material value %.2f)", s.Item, s.Price, need, unit(s.Item, 0))
		}
	}
	for _, want := range []string{"tool.drill", "tool.cutter", "ammo.cell", "consumable.medkit", "consumable.potion.heal"} {
		found := false
		for _, s := range vex.Stock {
			found = found || s.Item == want
		}
		if !found {
			t.Errorf("Vex no longer stocks %s", want)
		}
	}
	for _, s := range vex.Stock {
		if s.Item == "tool.drill.crude" {
			t.Error("Vex stocks tool.drill.crude: the crude drill is made, not sold")
		}
	}
}
