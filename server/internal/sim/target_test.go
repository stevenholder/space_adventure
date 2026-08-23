package sim

import (
	"testing"

	"space-adventure/server/internal/protocol"
)

func newTestTarget(id uint32) *Ent {
	return &Ent{
		ID:     id,
		Kind:   EntityKind(protocol.EntityTypeTarget),
		Pos:    [3]float64{1, 2, 3},
		Health: TargetMaxHealth,
	}
}

// TestTargetDeathAndRespawn covers the full brief in one run: damage to 0
// sets FlagDead and emits exactly one death event; the target is not
// damageable/hit-testable during the dead window; exactly 3.0 s later it is
// back to full health with FlagDead cleared, the SAME id and position; and
// the respawn fires once, not repeatedly.
func TestTargetDeathAndRespawn(t *testing.T) {
	w := NewWorld()
	e := newTestTarget(42)
	w.Add(e)

	// Deal lethal damage, then step once to resolve death.
	e.Health = 0
	var events []protocol.Event
	stepTicks(w, 1, StepCtx{Events: &events})

	if e.Flags&protocol.FlagDead == 0 {
		t.Fatalf("expected FlagDead set after lethal damage")
	}
	if len(events) != 1 {
		t.Fatalf("expected exactly 1 death event, got %d: %v", len(events), events)
	}
	if events[0].EntityID != 42 || events[0].EventID != protocol.EventDeath {
		t.Fatalf("unexpected event: %+v", events[0])
	}
	if TargetIsDamageable(e) {
		t.Fatalf("dead target must not be damageable")
	}

	// Mid-window: not yet respawned, no extra events, same id/pos.
	events = nil
	stepTicks(w, 20, StepCtx{Events: &events})
	if e.Flags&protocol.FlagDead == 0 {
		t.Fatalf("target respawned early")
	}
	if len(events) != 0 {
		t.Fatalf("expected no events mid-respawn window, got %v", events)
	}
	if e.ID != 42 {
		t.Fatalf("entity id changed: got %d", e.ID)
	}

	// Cross the 3.0 s boundary (1.0 already elapsed above, 2.0 more here).
	events = nil
	stepTicks(w, 40, StepCtx{Events: &events})

	if e.Flags&protocol.FlagDead != 0 {
		t.Fatalf("expected FlagDead cleared after respawn window")
	}
	if e.Health != TargetMaxHealth {
		t.Fatalf("expected health restored to %d, got %d", TargetMaxHealth, e.Health)
	}
	if e.ID != 42 {
		t.Fatalf("entity id changed across respawn: got %d", e.ID)
	}
	if e.Pos != [3]float64{1, 2, 3} {
		t.Fatalf("position changed across respawn: got %v", e.Pos)
	}
	if !TargetIsDamageable(e) {
		t.Fatalf("respawned target should be damageable again")
	}
	if len(events) != 0 {
		t.Fatalf("respawn itself must not emit an event, got %v", events)
	}

	// Respawn must fire once, not repeatedly: further steps with health
	// already full and FlagDead clear must not re-trigger anything.
	events = nil
	stepTicks(w, 100, StepCtx{Events: &events})
	if e.Flags&protocol.FlagDead != 0 {
		t.Fatalf("target died again with no damage applied")
	}
	if e.Health != TargetMaxHealth {
		t.Fatalf("health drifted after extra steps: got %d", e.Health)
	}
	if len(events) != 0 {
		t.Fatalf("expected no events on a healthy target, got %v", events)
	}
}

// TestTargetRespawnExactBoundary confirms the respawn timer needs the full
// 3.0 s — not a partial tick — before it restores the target.
func TestTargetRespawnExactBoundary(t *testing.T) {
	w := NewWorld()
	e := newTestTarget(7)
	w.Add(e)

	e.Health = 0
	stepTicks(w, 1, StepCtx{})
	if e.Flags&protocol.FlagDead == 0 {
		t.Fatalf("expected death")
	}

	// Just under 3.0 s total: still dead.
	stepTicks(w, TargetRespawnTicks-1, StepCtx{}) // one tick short of 3.0 s
	if e.Flags&protocol.FlagDead == 0 {
		t.Fatalf("target respawned before 3.0 s elapsed")
	}

	// The remaining sliver crosses the boundary: respawned.
	stepTicks(w, 1, StepCtx{}) // the tick that crosses 3.0 s exactly
	if e.Flags&protocol.FlagDead != 0 {
		t.Fatalf("target did not respawn at 3.0 s")
	}
	if e.Health != TargetMaxHealth {
		t.Fatalf("expected full health at respawn, got %d", e.Health)
	}
}

// stepTicks advances the world by n whole ticks at the sim's fixed dt.
//
// The respawn countdown is in ticks, not accumulated seconds (the tick rate is
// fixed at 20 Hz, so 3.0 s is exactly 60 ticks and no epsilon is needed). A
// test that passes one big dt to a single Step call would advance the
// countdown by ONE tick regardless of the number, which is not how the server
// runs.
func stepTicks(w *World, n int, ctx StepCtx) {
	for i := 0; i < n; i++ {
		w.Step(DT, ctx)
	}
}
