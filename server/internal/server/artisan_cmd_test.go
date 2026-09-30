package server

import (
	"encoding/json"
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// artisanWorld is testWorld plus the Phase 12 data: sellable ore, a bench at
// id 2, an iron node at id 3 (health = yields) and a copper node at id 4
// (Mining 10, mk2 drill).
func artisanWorld(p *store.Player, npc defs.NPC, nodeHealth int) cmdWorld {
	w := testWorld(p, npc, 2.0)
	w.Reg.Items["mat.ore.iron"] = defs.Item{ID: "mat.ore.iron", StackMax: 50, Value: 6}
	w.Reg.Items["mat.scrap"] = defs.Item{ID: "mat.scrap", StackMax: 50, Value: 8}
	w.Reg.Items["ammo.cell"] = defs.Item{ID: "ammo.cell", StackMax: 300}
	w.Reg.Items["tool.drill"] = defs.Item{ID: "tool.drill", StackMax: 1, Slot: "tool"}
	w.Reg.Items["tool.drill.mk2"] = defs.Item{ID: "tool.drill.mk2", StackMax: 1, Slot: "tool", Supersedes: "tool.drill"}
	w.Reg.Recipes = map[string]defs.Recipe{"recipe.cells": {ID: "recipe.cells", Level: 1,
		Inputs: []defs.ItemQty{{Item: "mat.ore.iron", Qty: 2}, {Item: "mat.scrap", Qty: 1}},
		Output: defs.ItemQty{Item: "ammo.cell", Qty: 30}}}
	w.Reg.Synergies = []defs.Synergy{{Source: "scavenging", Target: "commerce", What: "sell_bonus", PerLevel: 0.001}}
	w.Reg.Loot = map[string][]defs.LootEntry{"loot.node.iron": {{Item: "mat.ore.iron", Qty: 2, Chance: 1}}, "loot.node.copper": {{Item: "mat.ore.iron", Qty: 2, Chance: 1}}}
	bench := defs.NPC{ID: "npc.workbench", Kind: "bench"}
	inner := w.FindNPC
	w.FindNPC = func(id uint32) (defs.NPC, sim.Vec, bool) {
		if id == 2 {
			return bench, sim.Vec{2, planetSurfaceY, 0}, true
		}
		return inner(id)
	}
	iron := defs.Node{ID: "node.ore.iron", Skill: "mining", Level: 1, Tool: "tool.drill", Channel: 3, Yields: 5, Loot: "loot.node.iron"}
	copper := defs.Node{ID: "node.ore.copper", Skill: "mining", Level: 10, Tool: "tool.drill.mk2", Channel: 4, Yields: 4, Loot: "loot.node.copper"}
	w.FindNode = func(id uint32) (defs.Node, sim.Vec, int, bool) {
		switch id {
		case 3:
			return iron, sim.Vec{2, planetSurfaceY, 0}, nodeHealth, true
		case 4:
			return copper, sim.Vec{2, planetSurfaceY, 0}, 4, true
		}
		return defs.Node{}, sim.Vec{}, 0, false
	}
	return w
}

func run(w cmdWorld, op uint16, body string) (uint8, map[string]any) {
	res := handleCmd(newCmdRate(time.Now()), time.Now(), protocol.Cmd{Seq: 1, Opcode: op, Data: []byte(body)}, w)
	var m map[string]any
	if len(res.Data) > 0 {
		_ = json.Unmarshal(res.Data, &m)
	}
	return res.Status, m
}

// TestHandleCmdPhase12Skeleton pins the verbs' validation: shop_sell pays
// and refuses, craft consumes and gates on the bench, gather walks every
// pre-channel refusal in the GDD's order and then starts the channel with
// its duration, gather_cancel answers by whether one is running.
func TestHandleCmdPhase12Skeleton(t *testing.T) {
	p := &store.Player{Credits: 100, Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 10}, {Item: "mat.scrap", Qty: 1}}, Equipped: map[string]string{}}
	w := artisanWorld(p, shopNPC(), 5)

	if st, m := run(w, protocol.OpShopSell, `{"npc":1,"item":"mat.ore.iron","qty":4}`); st != protocol.StatusOK || m["credits"].(float64) != 112 {
		t.Fatalf("sell: status=%d body=%v", st, m)
	}
	if st, m := run(w, protocol.OpShopSell, `{"npc":1,"item":"widget","qty":1}`); st != protocol.StatusRefused || m["reason"] != "unsellable" {
		t.Fatalf("sell widget: status=%d body=%v", st, m)
	}
	if st, m := run(w, protocol.OpShopSell, `{"npc":2,"item":"mat.ore.iron","qty":1}`); st != protocol.StatusRefused || m["reason"] != "no_stock" {
		t.Fatalf("sell at bench: status=%d body=%v", st, m)
	}
	if st, _ := run(w, protocol.OpShopSell, `{"npc":9,"item":"mat.ore.iron","qty":1}`); st != protocol.StatusNotFound {
		t.Fatalf("sell at nobody: status=%d", st)
	}

	if st, m := run(w, protocol.OpCraft, `{"npc":2,"recipe":"recipe.cells","qty":1}`); st != protocol.StatusOK || m["crafted"].(map[string]any)["qty"].(float64) != 30 {
		t.Fatalf("craft: status=%d body=%v", st, m)
	}
	if sim.CountItem(p, "mat.ore.iron") != 4 || sim.CountItem(p, "ammo.cell") != 30 {
		t.Fatalf("craft aftermath: %v", p.Inventory)
	}
	if st, m := run(w, protocol.OpCraft, `{"npc":1,"recipe":"recipe.cells","qty":1}`); st != protocol.StatusRefused || m["reason"] != "unknown_recipe" {
		t.Fatalf("craft at the shop: status=%d body=%v", st, m)
	}
	if st, m := run(w, protocol.OpCraft, `{"npc":2,"recipe":"recipe.cells","qty":1}`); st != protocol.StatusRefused || m["reason"] != "missing_materials" {
		t.Fatalf("craft short: status=%d body=%v", st, m)
	}

	started := 0
	w.Gather = func(node uint32, ticks int) bool { started++; return true }
	gatherOK := func(node int, wantDur float64) {
		t.Helper()
		st, m := run(w, protocol.OpGather, `{"node":`+string(rune('0'+node))+`}`)
		if st != protocol.StatusOK || m["duration"].(float64) != wantDur {
			t.Fatalf("gather node %d: status=%d body=%v, want ok/%v", node, st, m, wantDur)
		}
	}
	gather := func(node int, want string) {
		t.Helper()
		st, m := run(w, protocol.OpGather, `{"node":`+string(rune('0'+node))+`}`)
		if st != protocol.StatusRefused || m["reason"] != want {
			t.Fatalf("gather node %d: status=%d body=%v, want %q", node, st, m, want)
		}
	}
	gather(3, "no_tool")
	p.Equipped["tool"] = "tool.drill"
	gatherOK(3, 3.0)
	gather(4, "no_tool")
	p.Equipped["tool"] = "tool.drill.mk2"
	gatherOK(3, 3.0) // mk2 supersedes the drill
	gather(4, "locked")
	if started != 2 {
		t.Fatalf("channels started = %d, want 2", started)
	}
	w.Busy = func() bool { return true }
	gather(3, "busy")
	w.Busy = nil
	// A bag that cannot take the yield refuses before the bar starts.
	full := &store.Player{Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 50}}, Equipped: map[string]string{"tool": "tool.drill"}}
	wf := artisanWorld(full, shopNPC(), 5)
	wf.Reg.Loot = map[string][]defs.LootEntry{"loot.node.iron": {{Item: "mat.ore.iron", Qty: 2, Chance: 1}}}
	wf.Reg.InvSlots = 1
	if st, m := run(wf, protocol.OpGather, `{"node":3}`); st != protocol.StatusRefused || m["reason"] != "no_space" {
		t.Fatalf("gather with a full bag: status=%d body=%v", st, m)
	}
	if st, _ := run(w, protocol.OpGather, `{"node":8}`); st != protocol.StatusNotFound {
		t.Fatalf("gather nothing: status=%d", st)
	}
	w2 := artisanWorld(p, shopNPC(), 0)
	if st, m := run(w2, protocol.OpGather, `{"node":3}`); st != protocol.StatusRefused || m["reason"] != "depleted" {
		t.Fatalf("gather depleted: status=%d body=%v", st, m)
	}
	if st, m := run(w, protocol.OpGatherCancel, `{}`); st != protocol.StatusRefused || m["reason"] != "not_gathering" {
		t.Fatalf("cancel: status=%d body=%v", st, m)
	}
	w.CancelGather = func() bool { return true }
	if st, _ := run(w, protocol.OpGatherCancel, `{}`); st != protocol.StatusOK {
		t.Fatalf("cancel running: status=%d", st)
	}
}
