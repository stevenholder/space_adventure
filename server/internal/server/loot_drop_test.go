package server

import (
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
)

// TestLootDropReachesWorld: a dead NPC must produce a loot entity in the world.
//
// The wire half of this is covered by syncWorldEnts announcing new entities,
// which was the bug that left gunner projectiles invisible: snapshot rows carry
// no entity_type, so an entity created at runtime and never announced is a row
// the client cannot render. t16 proves projectiles now arrive; a loot drop only
// happens when something dies, which that test does not do, so it is asserted
// here instead of left to chance.
func TestLootDropReachesWorld(t *testing.T) {
	s, _ := newTestServer(t)
	s.mu.Lock()
	defer s.mu.Unlock()

	var victim *npcAI
	for _, n := range s.npcAI {
		if n.arch.Loot != "" {
			victim = n
			break
		}
	}
	if victim == nil {
		t.Fatal("no combat NPC with a loot table in the world")
	}
	before := len(s.world.Order())
	victim.ent.Health = 0
	s.dropNPCLoot(victim)

	after := s.world.Order()
	if len(after) <= before {
		t.Fatalf("no loot entity created: %d -> %d", before, len(after))
	}
	found := false
	for _, id := range after {
		if e := s.world.Ents[id]; e != nil && e.Kind == sim.EntityKind(protocol.EntityTypeLoot) {
			found = true
		}
	}
	if !found {
		t.Error("world gained an entity but none of type loot")
	}
}
