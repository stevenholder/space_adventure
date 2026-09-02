package sim

import (
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
)

func spawnerTestRegistry() *defs.Registry {
	return &defs.Registry{
		NPCs: map[string]defs.NPC{
			"npc.grunt":  {ID: "npc.grunt", Kind: "melee", MaxHealth: 60},
			"npc.gunner": {ID: "npc.gunner", Kind: "ranged", MaxHealth: 40},
		},
	}
}

func testPlacements() []defs.Placement {
	return []defs.Placement{
		{Type: "npc", Def: "npc.grunt", Pos: [3]float64{1, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
		{Type: "npc", Def: "npc.gunner", Pos: [3]float64{2, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
		{Type: "target", Def: "target.basic", Pos: [3]float64{3, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
		{Type: "npc", Def: "npc.grunt", Pos: [3]float64{4, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
	}
}

func newIDGen(start uint32) func() uint32 {
	next := start
	return func() uint32 {
		id := next
		next++
		return id
	}
}

// TestSpawnZoneNPCs_OneEntityPerNPCPlacement covers: one entity per "npc"
// placement (non-npc placements, e.g. a target, are ignored), each at its
// placement's position with its archetype's max health from reg.NPCs — not
// a hardcoded number.
func TestSpawnZoneNPCs_OneEntityPerNPCPlacement(t *testing.T) {
	w := NewWorld()
	reg := spawnerTestRegistry()
	placements := testPlacements()

	ids := SpawnZoneNPCs(w, reg, placements, newIDGen(100))

	if len(ids) != 3 {
		t.Fatalf("expected 3 NPC entities (target placement ignored), got %d: %v", len(ids), ids)
	}
	if len(w.Ents) != 3 {
		t.Fatalf("expected 3 entities in the world, got %d", len(w.Ents))
	}

	wantDef := []string{"npc.grunt", "npc.gunner", "npc.grunt"}
	wantHealth := []int{60, 40, 60}
	wantPos := [][3]float64{{1, 0, 0}, {2, 0, 0}, {4, 0, 0}}

	for i, id := range ids {
		e, ok := w.Ents[id]
		if !ok {
			t.Fatalf("id %d (index %d) not found in world", id, i)
		}
		if e.Kind != EntityKind(protocol.EntityTypeNPC) {
			t.Fatalf("entity %d: kind = %v, want EntityTypeNPC", id, e.Kind)
		}
		if e.Def != wantDef[i] {
			t.Fatalf("entity %d: def = %q, want %q", id, e.Def, wantDef[i])
		}
		if e.Health != wantHealth[i] {
			t.Fatalf("entity %d: health = %d, want %d (from reg.NPCs, not hardcoded)", id, e.Health, wantHealth[i])
		}
		if e.Pos != wantPos[i] {
			t.Fatalf("entity %d: pos = %v, want %v", id, e.Pos, wantPos[i])
		}
		state, ok := e.Data.(*NPCState)
		if !ok {
			t.Fatalf("entity %d: Data is not *NPCState: %T", id, e.Data)
		}
		if state.Post != wantPos[i] {
			t.Fatalf("entity %d: Post = %v, want %v", id, state.Post, wantPos[i])
		}
		if state.Archetype != wantDef[i] {
			t.Fatalf("entity %d: Archetype = %q, want %q", id, state.Archetype, wantDef[i])
		}
	}

	// Placement order preserved in the returned ids.
	if ids[0] >= ids[1] || ids[1] >= ids[2] {
		t.Fatalf("expected ids in placement order, got %v", ids)
	}
}

// TestNPCRespawn_ExactBoundaryAndReturnsToPost covers the death/respawn
// cycle: a killed NPC respawns after EXACTLY 400 ticks (not sooner), at full
// archetype health, with FlagDead cleared, AT ITS POST even if moved far
// away first — and its entity id never changes.
func TestNPCRespawn_ExactBoundaryAndReturnsToPost(t *testing.T) {
	w := NewWorld()
	reg := spawnerTestRegistry()
	placements := []defs.Placement{
		{Type: "npc", Def: "npc.grunt", Pos: [3]float64{5, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
	}
	ids := SpawnZoneNPCs(w, reg, placements, newIDGen(1))
	id := ids[0]
	e := w.Ents[id]

	// Kill it, then drag it far from its post (as if it chased and died
	// elsewhere) before the respawn resolves.
	e.Health = 0
	var events []protocol.Event
	stepTicks(w, 1, StepCtx{Events: &events})

	if e.Flags&protocol.FlagDead == 0 {
		t.Fatalf("expected FlagDead set after lethal damage")
	}
	if len(events) != 1 || events[0].EntityID != id || events[0].EventID != protocol.EventDeath {
		t.Fatalf("expected exactly 1 death event for %d, got %v", id, events)
	}

	e.Pos = [3]float64{999, 999, 999} // moved far from its post while dead

	// 400 ticks total is npc_respawn (20 s at 20 Hz). One tick short: still
	// dead, still displaced.
	stepTicks(w, NPCRespawnTicks-1, StepCtx{})
	if e.Flags&protocol.FlagDead == 0 {
		t.Fatalf("NPC respawned before 400 ticks elapsed")
	}
	if e.ID != id {
		t.Fatalf("entity id changed: got %d, want %d", e.ID, id)
	}

	// The tick that crosses the boundary exactly: respawned, at its POST,
	// full archetype health, same id.
	events = nil
	stepTicks(w, 1, StepCtx{Events: &events})

	if e.Flags&protocol.FlagDead != 0 {
		t.Fatalf("expected FlagDead cleared after 400 ticks")
	}
	if e.Health != 60 {
		t.Fatalf("expected health restored to archetype max 60, got %d", e.Health)
	}
	if e.Pos != [3]float64{5, 0, 0} {
		t.Fatalf("expected NPC returned to its post %v, got %v", [3]float64{5, 0, 0}, e.Pos)
	}
	if e.ID != id {
		t.Fatalf("entity id changed across respawn: got %d, want %d", e.ID, id)
	}
	if len(events) != 0 {
		t.Fatalf("respawn itself must not emit an event, got %v", events)
	}
}

// TestShopNPCIsNotACombatant pins the bug that hid the quartermaster.
//
// A shop archetype has no max_health, so it spawns at 0 health. Giving it
// NPCState made StepNPCRespawn treat it as a corpse: it set the dead flag,
// emitted a death event, and started a respawn countdown that returned it to
// 0 health to die again every npc_respawn seconds. The dead flag is honoured
// by any correct client, so the only NPC a new player must talk to was
// invisible, 3.6 m from the spawn point, while the event log filled with its
// deaths.
func TestShopNPCIsNotACombatant(t *testing.T) {
	w := NewWorld()
	reg := spawnerTestRegistry()
	reg.NPCs["npc.quartermaster"] = defs.NPC{ID: "npc.quartermaster", Kind: "shop"} // no MaxHealth

	ids := SpawnZoneNPCs(w, reg, []defs.Placement{
		{Type: "npc", Def: "npc.quartermaster", Pos: [3]float64{1, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
		{Type: "npc", Def: "npc.grunt", Pos: [3]float64{2, 0, 0}, Quat: [4]float64{0, 0, 0, 1}},
	}, newIDGen(1))
	if len(ids) != 2 {
		t.Fatalf("spawned %d entities, want 2", len(ids))
	}
	shop, grunt := w.Ents[ids[0]], w.Ents[ids[1]]

	if _, ok := shop.Data.(*NPCState); ok {
		t.Error("the shop NPC carries combat state; StepNPCRespawn will kill it on a timer")
	}
	if _, ok := grunt.Data.(*NPCState); !ok {
		t.Error("the grunt has no combat state, so it can never respawn")
	}

	// Step well past npc_respawn: the shop NPC must never be flagged dead and
	// must never emit an event.
	var events []protocol.Event
	ctx := StepCtx{Events: &events, World: w}
	for i := 0; i < NPCRespawnTicks*3; i++ {
		StepNPCRespawn(shop, DT, ctx)
	}
	if shop.Flags&protocol.FlagDead != 0 {
		t.Error("the shop NPC is flagged dead, so every client hides it")
	}
	for _, e := range events {
		if e.EntityID == shop.ID {
			t.Errorf("the shop NPC emitted event %#04x; it is not a combatant", e.EventID)
		}
	}
}
