package sim

import (
	"encoding/binary"
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

// TestProjectileHitEventCarriesItsPayload pins the `hit` body against
// PROTOCOL.md: u32 shooter | f32 point[3] | u16 damage | u16 health_after.
// This emitted the 10-byte header alone, and a client decoding the documented
// layout ran off the end of the frame.
func TestProjectileHitEventCarriesItsPayload(t *testing.T) {
	w, ctx, events := newProjectileWorld(nil)
	addProjTarget(w, 2, [3]float64{200, 0, 20})
	w.Add(newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 100))

	for i := 0; i < 12 && w.Ents[1] != nil; i++ {
		w.Step(DT, ctx)
	}

	var hit *protocol.Event
	for i := range *events {
		if (*events)[i].EntityID == 2 && (*events)[i].EventID == protocol.EventHit {
			hit = &(*events)[i]
		}
	}
	if hit == nil {
		t.Fatal("no EventHit for entity 2")
	}
	if len(hit.Data) != 20 {
		t.Fatalf("hit data = %d bytes, want 20 (PROTOCOL.md `event` payloads)", len(hit.Data))
	}
	if got := binary.LittleEndian.Uint32(hit.Data[0:]); got != 99 {
		t.Errorf("shooter = %d, want the projectile's owner 99", got)
	}
	if got := binary.LittleEndian.Uint16(hit.Data[16:]); got != 25 {
		t.Errorf("damage = %d, want 25", got)
	}
	if got := binary.LittleEndian.Uint16(hit.Data[18:]); got != 75 {
		t.Errorf("health_after = %d, want 75", got)
	}
}

// A wall between the gunner and the target stops the round: no damage, no
// hit event, projectile gone. Before the swept test the round covered more
// than the wall's thickness per tick and struck whoever stood behind it.
func TestProjectileStopsAtWall(t *testing.T) {
	wall := protocol.Collider{
		Kind:   protocol.ColliderBox,
		Center: [3]float32{200, 1, 10},
		Half:   [3]float32{3, 1.5, 0.4},
		Quat:   [4]float32{0, 0, 0, 1},
	}
	w, ctx, events := newProjectileWorld([]protocol.Collider{wall})
	target := addProjTarget(w, 2, [3]float64{200, 0, 20})
	proj := newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 25, 100)
	w.Add(proj)

	for i := 0; i < 12 && w.Ents[1] != nil; i++ {
		w.Step(DT, ctx)
	}

	if target.Health != 100 {
		t.Fatalf("target health = %d, want 100 (the wall took the round)", target.Health)
	}
	if w.Ents[1] != nil {
		t.Fatalf("expected projectile removed at the wall")
	}
	for _, ev := range *events {
		if ev.EventID == protocol.EventHit {
			t.Fatalf("expected no EventHit through a wall, got %v", *events)
		}
	}
}

// A player is not a World ent: the projectile strikes it as a Body and hands
// the hit to HitBody (the server's damagePlayer), touching no health itself.
func TestProjectileHitsPlayerBody(t *testing.T) {
	type hitRec struct {
		id, attacker uint32
		dmg          int
	}
	for _, c := range []struct {
		name     string
		walls    []protocol.Collider
		bodyID   uint32
		target   bool // a world target nearer than the body
		wantHits int
	}{
		{"hit", nil, 7, false, 1},
		{"owner never hit", nil, 99, false, 0},
		{"wall in between", []protocol.Collider{{Kind: protocol.ColliderBox, Center: [3]float32{200, 1, 10}, Half: [3]float32{3, 1.5, 0.4}, Quat: [4]float32{0, 0, 0, 1}}}, 7, false, 0},
		{"nearer world ent takes it", nil, 7, true, 0},
	} {
		w, ctx, _ := newProjectileWorld(c.walls)
		var hits []hitRec
		ctx.Bodies = []Body{{ID: c.bodyID, Feet: Vec{200, 0, 20}, Height: 1.8, Radius: 0.35}}
		ctx.HitBody = func(id, attacker uint32, dmg int) { hits = append(hits, hitRec{id, attacker, dmg}) }
		var target *Ent
		if c.target {
			target = addProjTarget(w, 2, [3]float64{200, 0, 15})
		}
		w.Add(newProjectile(1, 99, [3]float64{200, 0, 0}, [3]float64{0, 0, 1}, 45, 8, 100))
		for i := 0; i < 12 && w.Ents[1] != nil; i++ {
			w.Step(DT, ctx)
		}
		if len(hits) != c.wantHits {
			t.Fatalf("%s: %d body hits, want %d", c.name, len(hits), c.wantHits)
		}
		if c.wantHits == 1 && hits[0] != (hitRec{7, 99, 8}) {
			t.Fatalf("%s: hit %+v, want body 7 by 99 for 8", c.name, hits[0])
		}
		if c.target && target.Health != 92 {
			t.Fatalf("%s: target health %d, want 92", c.name, target.Health)
		}
		if c.name == "hit" && w.Ents[1] != nil {
			t.Fatalf("%s: projectile not removed", c.name)
		}
	}
}
