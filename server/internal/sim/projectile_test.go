package sim

import (
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
)

// newProjectile builds a projectile Ent travelling at speed m/s along dir
// (need not be pre-normalised), far from the flatField(150) terrain surface
// (|pos| stays >= 200 the whole test) so terrain never interferes.
func newProjectile(id, owner uint32, pos, dir [3]float64, speed float64, damage, lifeTicks int) *Ent {
	d := terrainNormalizeTest(dir)
	return &Ent{
		ID:   id,
		Kind: EntityKind(protocol.EntityTypeProjectile),
		Pos:  pos,
		Vel:  [3]float64{d[0] * speed, d[1] * speed, d[2] * speed},
		Data: &ProjectileState{Owner: owner, Damage: damage, Speed: speed, LifeTicks: lifeTicks},
	}
}

// terrainNormalizeTest avoids importing terrain twice under a different
// alias in this file — Vec IS terrain.Vec (sim.go), so this just wraps
// Normalize for [3]float64 literals.
func terrainNormalizeTest(v [3]float64) [3]float64 {
	n := Vec(v)
	l := n.Len()
	if l < 1e-12 {
		return [3]float64{}
	}
	return [3]float64{n[0] / l, n[1] / l, n[2] / l}
}

func newProjectileWorld(colliders []protocol.Collider) (*World, StepCtx, *[]protocol.Event) {
	w := NewWorld()
	events := []protocol.Event{}
	defsByKey := map[string]defs.EntityDef{"target": targetDef()}
	ctx := StepCtx{
		World:     w,
		Events:    &events,
		Terrain:   flatField(150),
		Colliders: colliders,
		DefOf:     defOf(defsByKey),
	}
	return w, ctx, &events
}

func addProjTarget(w *World, id uint32, pos [3]float64) *Ent {
	e := &Ent{ID: id, Kind: EntityKind(protocol.EntityTypeTarget), Health: 100, Def: "target", Pos: pos}
	w.Add(e)
	return e
}

// TestProjectileHitsTargetInRange fires a projectile at a target 20 m away
// and expects a hit: damage applied, EventHit emitted, projectile removed.
func TestProjectileHitsTargetInRange(t *testing.T) {
	w, ctx, events := newProjectileWorld(nil)
	target := addProjTarget(w, 2, [3]float64{200, 0, 20})
	proj := newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 100)
	w.Add(proj)

	for i := 0; i < 12 && w.Ents[1] != nil; i++ {
		w.Step(DT, ctx)
	}

	if target.Health != 75 {
		t.Fatalf("target health = %d, want 75", target.Health)
	}
	if w.Ents[1] != nil {
		t.Fatalf("expected projectile removed after hit")
	}
	found := false
	for _, ev := range *events {
		if ev.EntityID == 2 && ev.EventID == protocol.EventHit {
			found = true
		}
	}
	if !found {
		t.Fatalf("expected an EventHit for entity 2, got %v", *events)
	}
}

// TestProjectileMissesOffAxisTarget fires past a target that sits well off
// the flight line — the ray never comes within hitbox radius, so it misses
// entirely (not merely a range issue).
func TestProjectileMissesOffAxisTarget(t *testing.T) {
	w, ctx, events := newProjectileWorld(nil)
	target := addProjTarget(w, 2, [3]float64{200, 5, 20}) // 5 m off the line of fire
	proj := newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 100)
	w.Add(proj)

	for i := 0; i < 12; i++ {
		w.Step(DT, ctx)
	}

	if target.Health != 100 {
		t.Fatalf("target health = %d, want 100 (miss)", target.Health)
	}
	for _, ev := range *events {
		if ev.EventID == protocol.EventHit {
			t.Fatalf("expected no EventHit, got %v", *events)
		}
	}
}

// TestProjectileSweptCollisionCatchesInBetweenTarget places a target
// strictly between two consecutive tick positions (closer than one tick of
// travel: 45 m/s / 20 Hz = 2.25 m per tick) — a point test at only the new
// position would miss it, but the swept segment test must not.
func TestProjectileSweptCollisionCatchesInBetweenTarget(t *testing.T) {
	w, ctx, events := newProjectileWorld(nil)
	// One tick moves the projectile from z=0 to z=2.25; the target sits at
	// z=1.1, inside that span but farther than its 0.5 m hitbox radius from
	// EITHER endpoint (1.1 from z=0, 1.15 from z=2.25).
	target := addProjTarget(w, 2, [3]float64{200, 0, 1.1})
	proj := newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 100)
	w.Add(proj)

	w.Step(DT, ctx)

	if target.Health != 75 {
		t.Fatalf("target health = %d, want 75 (swept hit)", target.Health)
	}
	if w.Ents[1] != nil {
		t.Fatalf("expected projectile removed after swept hit")
	}
	if len(*events) != 1 || (*events)[0].EventID != protocol.EventHit {
		t.Fatalf("expected exactly one EventHit, got %v", *events)
	}
}

// TestProjectileNeverHitsOwner fires straight through its own owner's body
// and expects no damage, no event, and the projectile to survive the pass.
func TestProjectileNeverHitsOwner(t *testing.T) {
	w, ctx, events := newProjectileWorld(nil)
	owner := addProjTarget(w, 2, [3]float64{200, 0, 1.1}) // same spot as the swept-hit test
	owner.Def = "target"                                  // damageable, so a miss here is because of Owner, not Damageable
	proj := newProjectile(1, 2 /* Owner = the entity in its own path */, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 100)
	w.Add(proj)

	w.Step(DT, ctx)

	if owner.Health != 100 {
		t.Fatalf("owner health = %d, want 100 (never self-damaged)", owner.Health)
	}
	for _, ev := range *events {
		if ev.EventID == protocol.EventHit {
			t.Fatalf("expected no EventHit against owner, got %v", *events)
		}
	}
	if w.Ents[1] == nil {
		t.Fatalf("expected projectile to survive passing through its owner")
	}
}

// TestProjectileExpires runs a projectile with no target in range for
// exactly LifeTicks ticks and expects removal on (and only on) the tick
// LifeTicks reaches 0 — silently, with no event emitted.
func TestProjectileExpires(t *testing.T) {
	w, ctx, events := newProjectileWorld(nil)
	proj := newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 3)
	w.Add(proj)

	w.Step(DT, ctx)
	if w.Ents[1] == nil {
		t.Fatalf("projectile removed too early (after 1 of 3 ticks)")
	}
	w.Step(DT, ctx)
	if w.Ents[1] == nil {
		t.Fatalf("projectile removed too early (after 2 of 3 ticks)")
	}
	w.Step(DT, ctx)
	if w.Ents[1] != nil {
		t.Fatalf("expected projectile removed after LifeTicks reached 0")
	}
	if len(*events) != 0 {
		t.Fatalf("expected no events on silent expiry, got %v", *events)
	}
}
