package server

import (
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// useWorld is testWorld plus Phase 13's items and fake vitals/cooldowns.
type useFakes struct {
	health   int
	dead     bool
	cool     map[string]float64
	cancels  int
	scanRng  float64
	scanHits []map[string]any
}

func useWorld(p *store.Player, f *useFakes) cmdWorld {
	w := testWorld(p, shopNPC(), 2.0)
	w.Reg.Items["consumable.medkit"] = defs.Item{ID: "consumable.medkit", Kind: "consumable", StackMax: 10, Consumable: &defs.Consumable{Heal: 50, Cooldown: 8}}
	w.Reg.Items["gadget.scanner"] = defs.Item{ID: "gadget.scanner", Kind: "gadget", Slot: "gadget", StackMax: 1, Ability: &defs.Ability{ID: "scan", Range: 120, Cooldown: 30}}
	w.Reg.Items["weapon.pulse"] = defs.Item{ID: "weapon.pulse", Slot: "primary", StackMax: 1, Weapon: &defs.Weapon{Damage: 25, Magazine: 30, AmmoItem: "ammo.cell"}}
	w.Reg.Items["ammo.cell"] = defs.Item{ID: "ammo.cell", StackMax: 300}
	w.Reg.Items["mod.mag"] = defs.Item{ID: "mod.mag", Kind: "mod", Slot: "mod", StackMax: 1, Mod: &defs.Mod{Magazine: 10}}
	f.cool = map[string]float64{}
	w.Vitals = func() (int, bool) { return f.health, f.dead }
	w.Heal = func(n int) int {
		f.health += n
		if f.health > sim.PlayerMaxHealth {
			f.health = sim.PlayerMaxHealth
		}
		return f.health
	}
	w.CoolingFor = func(item string) float64 { return f.cool[item] }
	w.StartCooldown = func(item string, secs float64) { f.cool[item] = secs }
	w.CancelGather = func() bool { f.cancels++; return true }
	w.Scan = func(rng float64) []map[string]any { f.scanRng = rng; return f.scanHits }
	return w
}

func use(w cmdWorld, item string) (uint8, map[string]any) {
	return run(w, protocol.OpUse, `{"item":"`+item+`"}`)
}

// TestUseMedkit pins C129: +50 capped, one unit consumed, cooldown 8 in the
// result and then refused with ready_in, no_effect at full health with
// nothing consumed, and a running channel is cancelled.
func TestUseMedkit(t *testing.T) {
	f := &useFakes{health: 30}
	p := &store.Player{Inventory: []store.Stack{{Item: "consumable.medkit", Qty: 2}}, Equipped: map[string]string{}}
	w := useWorld(p, f)

	st, m := use(w, "consumable.medkit")
	if st != protocol.StatusOK || m["effect"].(map[string]any)["health"].(float64) != 80 || m["cooldown"].(float64) != 8 {
		t.Fatalf("medkit at 30: status=%d body=%v", st, m)
	}
	if sim.CountItem(p, "consumable.medkit") != 1 || f.cancels != 1 {
		t.Fatalf("after use: kits=%d cancels=%d", sim.CountItem(p, "consumable.medkit"), f.cancels)
	}
	if st, m := use(w, "consumable.medkit"); st != protocol.StatusRefused || m["reason"] != "cooldown" || m["ready_in"].(float64) != 8 {
		t.Fatalf("inside the cooldown: status=%d body=%v", st, m)
	}
	f.cool = map[string]float64{}
	f.health = 100
	if st, m := use(w, "consumable.medkit"); st != protocol.StatusRefused || m["reason"] != "no_effect" || sim.CountItem(p, "consumable.medkit") != 1 {
		t.Fatalf("at full: status=%d body=%v kits=%d", st, m, sim.CountItem(p, "consumable.medkit"))
	}
	f.health = 90
	if st, m := use(w, "consumable.medkit"); st != protocol.StatusOK || m["effect"].(map[string]any)["health"].(float64) != 100 {
		t.Fatalf("capped: status=%d body=%v", st, m)
	}
	if st, m := use(w, "consumable.medkit"); st != protocol.StatusRefused || m["reason"] != "not_owned" {
		t.Fatalf("no kits left: status=%d body=%v", st, m)
	}
}

// TestUseScanner pins C130's wire half: worn → pings with the def's range
// and a 30 s cooldown; unworn → not_owned.
func TestUseScanner(t *testing.T) {
	f := &useFakes{health: 100, scanHits: []map[string]any{{"id": 7, "def": "node.ore.iron"}}}
	p := &store.Player{Inventory: []store.Stack{{Item: "gadget.scanner", Qty: 1}}, Equipped: map[string]string{}}
	w := useWorld(p, f)
	if st, m := use(w, "gadget.scanner"); st != protocol.StatusRefused || m["reason"] != "not_owned" {
		t.Fatalf("unworn: status=%d body=%v", st, m)
	}
	p.Equipped["gadget"] = "gadget.scanner"
	st, m := use(w, "gadget.scanner")
	if st != protocol.StatusOK || f.scanRng != 120 || m["cooldown"].(float64) != 30 || len(m["effect"].(map[string]any)["pings"].([]any)) != 1 {
		t.Fatalf("scan: status=%d body=%v rng=%v", st, m, f.scanRng)
	}
	if sim.CountItem(p, "gadget.scanner") != 1 {
		t.Fatal("using a gadget consumed it")
	}
}

// TestUseRefusals pins the rest of the order: unknown, unusable, dead.
func TestUseRefusals(t *testing.T) {
	f := &useFakes{health: 50}
	p := &store.Player{Inventory: []store.Stack{{Item: "weapon.pulse", Qty: 1}, {Item: "consumable.medkit", Qty: 1}}, Equipped: map[string]string{"primary": "weapon.pulse"}}
	w := useWorld(p, f)
	if st, m := use(w, "nope"); st != protocol.StatusRefused || m["reason"] != "unknown_item" {
		t.Fatalf("unknown: %d %v", st, m)
	}
	if st, m := use(w, "weapon.pulse"); st != protocol.StatusRefused || m["reason"] != "unusable" {
		t.Fatalf("rifle: %d %v", st, m)
	}
	f.dead = true
	if st, m := use(w, "consumable.medkit"); st != protocol.StatusRefused || m["reason"] != "dead" {
		t.Fatalf("dead: %d %v", st, m)
	}
	c := &client{cooldowns: map[string]time.Time{"x": time.Now().Add(3 * time.Second)}}
	if left := c.coolingFor("x", time.Now()); left < 2.5 || left > 3 {
		t.Fatalf("coolingFor = %v", left)
	}
	if left := c.coolingFor("y", time.Now()); left != 0 {
		t.Fatalf("unknown item cooling %v", left)
	}
}

// TestReloadWithMagMod pins C131's reload half: a worn mag mod reloads to
// 40; with the mod off a 40-round magazine clamps to 30 and the surplus
// goes back to the bag.
func TestReloadWithMagMod(t *testing.T) {
	f := &useFakes{health: 100}
	p := &store.Player{Inventory: []store.Stack{{Item: "weapon.pulse", Qty: 1}, {Item: "ammo.cell", Qty: 100}, {Item: "mod.mag", Qty: 1}},
		Equipped: map[string]string{"primary": "weapon.pulse", "mod": "mod.mag"}}
	w := useWorld(p, f)
	w.Ent = &entity{Magazine: 0}
	if st, m := run(w, protocol.OpReload, `{}`); st != protocol.StatusOK || m["magazine"].(float64) != 40 || sim.CountItem(p, "ammo.cell") != 60 {
		t.Fatalf("modded reload: %d %v cells=%d", st, m, sim.CountItem(p, "ammo.cell"))
	}
	delete(p.Equipped, "mod")
	if st, m := run(w, protocol.OpReload, `{}`); st != protocol.StatusOK || m["magazine"].(float64) != 30 || w.Ent.Magazine != 30 || sim.CountItem(p, "ammo.cell") != 70 {
		t.Fatalf("clamped reload: %d %v mag=%d cells=%d", st, m, w.Ent.Magazine, sim.CountItem(p, "ammo.cell"))
	}
}
