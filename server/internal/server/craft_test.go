package server

import (
	"encoding/json"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// craftClient joins one player on a live test server and fills their bag.
func craftClient(t *testing.T, bag []store.Stack) (*Server, *pClient, *client) {
	t.Helper()
	s, url := newTestServer(t)
	a := joinPlayer(t, url, "smith")
	a.awaitPublished(t)
	s.mu.Lock()
	c := s.clients[a.id]
	s.mu.Unlock()
	c.ident.Mutate(func(p *store.Player) { p.Inventory = bag })
	return s, a, c
}

type craftEnd struct {
	Recipe, Reason, Item string
	Qty                  int
}

func nextCraftEnd(t *testing.T, a *pClient) craftEnd {
	t.Helper()
	var e craftEnd
	if err := json.Unmarshal(a.event(t, protocol.EventCraftEnd).Data, &e); err != nil {
		t.Fatal(err)
	}
	return e
}

func count(c *client, item string) int {
	n := 0
	c.ident.Mutate(func(p *store.Player) { n = sim.CountItem(p, item) })
	return n
}

// TestCraftChannelUnits pins C185's server half: a hand craft ×3 answers
// with one unit's duration, takes the first unit's inputs with the reply,
// lands three outputs as three craft_end `done`, and pays the recipe's
// skill per unit; a 2-unit craft short for the second ends
// missing_materials after one output.
func TestCraftChannelUnits(t *testing.T) {
	s, a, c := craftClient(t, []store.Stack{{Item: "mat.scrap", Qty: 9}})
	r := s.reg.Recipes["recipe.mat.parts"]
	if r.Station != "hand" || r.Seconds != 1 || r.Skill != "smithing" {
		t.Fatalf("recipe.mat.parts is %+v, the test assumes hand/1 s/smithing", r)
	}
	res := a.cmdOK(t, 1, protocol.OpCraft, `{"npc":0,"recipe":"recipe.mat.parts","qty":3}`)
	var body struct {
		Recipe   string  `json:"recipe"`
		Qty      int     `json:"qty"`
		Duration float64 `json:"duration"`
	}
	_ = json.Unmarshal(res.Data, &body)
	if body.Recipe != r.ID || body.Qty != 3 || body.Duration != 1 {
		t.Fatalf("craft result %s", res.Data)
	}
	if n := count(c, "mat.scrap"); n != 6 {
		t.Fatalf("scrap after the first unit started = %d, want 6", n)
	}
	if r := a.cmd(t, 2, protocol.OpGather, `{"node":1}`); r.Status == protocol.StatusOK {
		t.Fatalf("a second channel started while crafting")
	}
	for i := 0; i < 3; i++ {
		if e := nextCraftEnd(t, a); e.Reason != "done" || e.Item != "mat.parts" || e.Qty != 1 || e.Recipe != r.ID {
			t.Fatalf("unit %d: %+v", i, e)
		}
	}
	if count(c, "mat.parts") != 3 || count(c, "mat.scrap") != 0 {
		t.Fatalf("after three units: parts=%d scrap=%d", count(c, "mat.parts"), count(c, "mat.scrap"))
	}
	var xp struct {
		Skill string `json:"skill"`
		XP    int64  `json:"xp"`
	}
	for xp.Skill != "smithing" {
		_ = json.Unmarshal(a.event(t, protocol.EventSkillXP).Data, &xp)
	}
	if xp.XP < r.XP {
		t.Fatalf("smithing xp %d after three units of %d", xp.XP, r.XP)
	}

	c.ident.Mutate(func(p *store.Player) { p.Inventory = []store.Stack{{Item: "mat.scrap", Qty: 4}} })
	a.cmdOK(t, 3, protocol.OpCraft, `{"npc":0,"recipe":"recipe.mat.parts","qty":2}`)
	if e := nextCraftEnd(t, a); e.Reason != "done" {
		t.Fatalf("first of two: %+v", e)
	}
	if e := nextCraftEnd(t, a); e.Reason != "missing_materials" || e.Item != "mat.parts" || e.Qty != 1 {
		t.Fatalf("second of two: %+v", e)
	}
	if count(c, "mat.parts") != 1 || count(c, "mat.scrap") != 1 {
		t.Fatalf("short craft left parts=%d scrap=%d", count(c, "mat.parts"), count(c, "mat.scrap"))
	}
}

// TestCraftChannelCancels pins the refunds: gather_cancel mid-unit puts the
// unit's inputs back in its reply's bag; a step past 0.5 m ends it `moved`
// with the inputs back after the tick.
func TestCraftChannelCancels(t *testing.T) {
	_, a, c := craftClient(t, []store.Stack{{Item: "mat.scrap", Qty: 8}, {Item: "mat.parts", Qty: 4}})
	s := c.srv
	a.cmdOK(t, 1, protocol.OpCraft, `{"npc":0,"recipe":"recipe.tool.drill.crude","qty":1}`)
	if count(c, "mat.scrap") != 4 || count(c, "mat.parts") != 2 {
		t.Fatalf("inputs not taken at start")
	}
	a.cmdOK(t, 2, protocol.OpGatherCancel, `{}`)
	if count(c, "mat.scrap") != 8 || count(c, "mat.parts") != 4 {
		t.Fatalf("gather_cancel did not refund: scrap=%d parts=%d", count(c, "mat.scrap"), count(c, "mat.parts"))
	}
	if e := nextCraftEnd(t, a); e.Reason != "cancel" || e.Item != "tool.drill.crude" {
		t.Fatalf("cancel end: %+v", e)
	}

	a.cmdOK(t, 3, protocol.OpCraft, `{"npc":0,"recipe":"recipe.tool.drill.crude","qty":1}`)
	s.mu.Lock()
	c.entity.State.Pos[0] += 2
	c.craft.from = c.entity.State.Pos
	c.craft.from[0] -= 2
	s.mu.Unlock()
	if e := nextCraftEnd(t, a); e.Reason != "moved" {
		t.Fatalf("moved end: %+v", e)
	}
	a.awaitPublished(t) // a round trip: the tick's refund drain has run
	if count(c, "mat.scrap") != 8 || count(c, "mat.parts") != 4 || count(c, "tool.drill.crude") != 0 {
		t.Fatalf("moved did not refund: scrap=%d parts=%d", count(c, "mat.scrap"), count(c, "mat.parts"))
	}
}

// TestCraftWrongStation pins the hands refusing a forge recipe and a
// station recipe's npc 0.
func TestCraftWrongStation(t *testing.T) {
	_, a, _ := craftClient(t, []store.Stack{{Item: "mat.ore.iron", Qty: 4}})
	r := a.cmd(t, 1, protocol.OpCraft, `{"npc":0,"recipe":"recipe.mat.ingot.iron","qty":1}`)
	if r.Status != protocol.StatusRefused || string(r.Data) != `{"reason":"wrong_station"}` {
		t.Fatalf("ingot in the hands: %d %s", r.Status, r.Data)
	}
}

// TestStartOverrideJoin: SA_START's credits reach a joining guest.
func TestStartOverrideJoin(t *testing.T) {
	s, url := newTestServer(t)
	if err := s.SetStart("credits=1000,ammo.cell=120"); err != nil {
		t.Fatal(err)
	}
	a := joinPlayer(t, url, "rich")
	var inv struct {
		Credits int64 `json:"credits"`
	}
	_ = json.Unmarshal(a.cmdOK(t, 1, protocol.OpInventory, `{}`).Data, &inv)
	if inv.Credits != 1000 {
		t.Fatalf("credits %d, want 1000", inv.Credits)
	}
}
