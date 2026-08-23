package sim

import "testing"

// testKind is a private Kind used only by this test's step registration,
// so it can't collide with real protocol.EntityType* values.
const testKind EntityKind = 0xFFFF

func TestWorldStepVisitsInInsertionOrder(t *testing.T) {
	var visits []uint32
	RegisterStep(testKind, func(e *Ent, dt float64, ctx StepCtx) {
		visits = append(visits, e.ID)
	})

	w := NewWorld()
	w.Add(&Ent{ID: 1, Kind: testKind})
	w.Add(&Ent{ID: 2, Kind: testKind})
	w.Add(&Ent{ID: 3, Kind: testKind})

	w.Step(0.1, StepCtx{})
	w.Step(0.1, StepCtx{})

	want := []uint32{1, 2, 3, 1, 2, 3}
	if len(visits) != len(want) {
		t.Fatalf("visits = %v, want %v", visits, want)
	}
	for i := range want {
		if visits[i] != want[i] {
			t.Fatalf("visits = %v, want %v", visits, want)
		}
	}
}

func TestWorldRemoveKeepsOrderConsistent(t *testing.T) {
	var visits []uint32
	RegisterStep(testKind, func(e *Ent, dt float64, ctx StepCtx) {
		visits = append(visits, e.ID)
	})

	w := NewWorld()
	w.Add(&Ent{ID: 1, Kind: testKind})
	w.Add(&Ent{ID: 2, Kind: testKind})
	w.Add(&Ent{ID: 3, Kind: testKind})

	w.Remove(2)

	if _, ok := w.Ents[2]; ok {
		t.Fatalf("removed id 2 still present in Ents")
	}
	for _, id := range w.order {
		if id == 2 {
			t.Fatalf("removed id 2 still present in order: %v", w.order)
		}
	}
	if len(w.Ents) != 2 || len(w.order) != 2 {
		t.Fatalf("expected 2 entities remaining, got Ents=%v order=%v", w.Ents, w.order)
	}

	w.Step(0.1, StepCtx{})

	want := []uint32{1, 3}
	if len(visits) != len(want) {
		t.Fatalf("visits = %v, want %v", visits, want)
	}
	for i := range want {
		if visits[i] != want[i] {
			t.Fatalf("visits = %v, want %v", visits, want)
		}
	}
}

func TestWorldAddRemoveReAddDoesNotDuplicate(t *testing.T) {
	w := NewWorld()
	w.Add(&Ent{ID: 1, Kind: testKind})
	w.Add(&Ent{ID: 2, Kind: testKind})

	w.Remove(1)
	w.Add(&Ent{ID: 1, Kind: testKind})

	count := 0
	for _, id := range w.order {
		if id == 1 {
			count++
		}
	}
	if count != 1 {
		t.Fatalf("id 1 appears %d times in order: %v", count, w.order)
	}
	if len(w.order) != 2 {
		t.Fatalf("order = %v, want length 2", w.order)
	}
}
