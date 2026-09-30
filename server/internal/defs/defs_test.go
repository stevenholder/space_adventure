package defs

import (
	"encoding/json"
	"testing"
)

func TestLoad(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatalf("Load() error: %v", err)
	}

	weapon, ok := reg.Items["weapon.pulse"]
	if !ok {
		t.Fatal("weapon.pulse not found in Items")
	}
	if weapon.Weapon == nil {
		t.Fatal("weapon.pulse has no weapon rule table")
	}
	if weapon.Weapon.Damage != 25 {
		t.Errorf("damage = %v, want 25", weapon.Weapon.Damage)
	}
	if weapon.Weapon.FireInterval != 0.15 {
		t.Errorf("fire_interval = %v, want 0.15", weapon.Weapon.FireInterval)
	}
	if weapon.Weapon.Magazine != 30 {
		t.Errorf("magazine = %v, want 30", weapon.Weapon.Magazine)
	}

	rangeZone, ok := reg.Zones["range"]
	if !ok {
		t.Fatal("range zone not found")
	}
	// 1 hand-authored sphere + 4 derived from the layout (3 solid wall
	// runs, 1 mast) — see layout.go.
	if len(rangeZone.Colliders) != 5 {
		t.Errorf("range colliders = %d, want 5", len(rangeZone.Colliders))
	}
	targetCount := 0
	for _, e := range rangeZone.Entities {
		if e.Type == "target" {
			targetCount++
		}
	}
	if targetCount != 5 {
		t.Errorf("range target placements = %d, want 5", targetCount)
	}

	if !json.Valid(reg.Payload) {
		t.Fatal("Payload is not valid JSON")
	}
	if len(reg.Payload) >= MaxPayload {
		t.Errorf("Payload is %d bytes, want < %d (64 KiB)", len(reg.Payload), MaxPayload)
	}

	var decoded map[string]json.RawMessage
	if err := json.Unmarshal(reg.Payload, &decoded); err != nil {
		t.Fatalf("Payload does not decode as a JSON object: %v", err)
	}
	for _, key := range []string{"items", "entities", "npcs", "constants", "nodes", "recipes"} {
		if _, ok := decoded[key]; !ok {
			t.Errorf("Payload missing key %q", key)
		}
	}

	// Phase 12 data audit: the three nodes, three recipes, the bench, the
	// tools in stock, and the placements the GDD names.
	if len(reg.Nodes) != 3 || len(reg.Recipes) != 3 {
		t.Errorf("nodes=%d recipes=%d, want 3/3", len(reg.Nodes), len(reg.Recipes))
	}
	if reg.NPCs["npc.workbench"].Kind != "bench" {
		t.Error("npc.workbench is not a bench")
	}
	if reg.Items["tool.drill.mk2"].Supersedes != "tool.drill" || reg.Items["mat.ore.iron"].Value != 6 || reg.Items["ammo.cell"].Value != 0 {
		t.Error("Phase 12 item fields did not parse")
	}
	placed := map[string]int{}
	for _, z := range reg.Zones {
		for _, e := range z.Entities {
			placed[e.Type+":"+e.Def]++
		}
	}
	for def, want := range map[string]int{"node:node.ore.iron": 3, "node:node.ore.copper": 1, "node:node.wreck": 2, "npc:npc.workbench": 1} {
		if placed[def] != want {
			t.Errorf("%s placed %d times, want %d", def, placed[def], want)
		}
	}
	for _, sk := range reg.Skills {
		if sk.Reserved {
			t.Errorf("skill %s still reserved", sk.ID)
		}
	}
}

// TestHostileNPCsAreShootable pins the shipped data invariant that made the
// Phase 3 camp unclearable: every EntityTypeNPC resolves to the one "npc"
// entity_def, and while it said damageable:false, ResolveShot skipped every
// grunt and gunner outright — the rifle could be emptied into the camp with no
// hit, no death and no loot, and nothing caught it because C20/C21 only measure
// NPCs damaging the PLAYER and C23's evidence is a unit test with its own
// fixtures.
//
// The shopkeeper's immunity does not come from this flag. It comes from its
// archetype having no max_health, so it spawns at 0 health and the
// dead-entity guard drops it first (sim.ResolveShot; TestResolveShot's "dead
// entity is skipped"). Both halves are asserted here because flipping the flag
// is only safe while the second half holds.
func TestHostileNPCsAreShootable(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatalf("Load() error: %v", err)
	}

	npcDef, ok := reg.Entities["npc"]
	if !ok {
		t.Fatal("no \"npc\" entity_def")
	}
	if !npcDef.Damageable {
		t.Error("entity_def npc is not damageable: camp grunts and gunners cannot be shot")
	}

	for _, id := range []string{"npc.grunt", "npc.gunner"} {
		n, ok := reg.NPCs[id]
		if !ok {
			t.Fatalf("archetype %s missing", id)
		}
		if n.MaxHealth <= 0 {
			t.Errorf("%s max_health = %d, want > 0 — a hostile spawned at 0 health is skipped as dead", id, n.MaxHealth)
		}
	}

	shop, ok := reg.NPCs["npc.quartermaster"]
	if !ok {
		t.Fatal("archetype npc.quartermaster missing")
	}
	if shop.MaxHealth != 0 {
		t.Errorf("npc.quartermaster max_health = %d, want 0 — the shopkeeper's immunity depends on it", shop.MaxHealth)
	}
}
