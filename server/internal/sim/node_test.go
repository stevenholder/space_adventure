package sim

import (
	"testing"

	"space-adventure/server/internal/protocol"
)

// TestNodeDepletesAndRespawns pins C120: five yields dark an iron node, the
// respawn countdown runs in whole ticks, and the node refills in place with
// the same id.
func TestNodeDepletesAndRespawns(t *testing.T) {
	w := NewWorld()
	n := &Ent{ID: 7, Kind: EntityKind(protocol.EntityTypeNode), Health: 5, Def: "node.ore.iron",
		Data: NewNodeState(5, 1.0)}
	w.Add(n)

	for i := 0; i < 5; i++ {
		if !TakeYield(n) {
			t.Fatalf("yield %d refused", i)
		}
	}
	if n.Health != 0 || TakeYield(n) {
		t.Fatalf("node should be depleted at 0, health=%d", n.Health)
	}

	dt := 1.0 / TickHz
	w.Step(dt, StepCtx{})     // starts the countdown (20 ticks)
	for i := 0; i < 19; i++ { // 19 more: still depleted
		w.Step(dt, StepCtx{})
	}
	if n.Health != 0 {
		t.Fatalf("respawned early: health=%d", n.Health)
	}
	w.Step(dt, StepCtx{})
	if n.Health != 5 {
		t.Fatalf("after respawn health=%d, want 5", n.Health)
	}
	if w.Ents[7] != n {
		t.Fatal("node identity changed across respawn")
	}
}
