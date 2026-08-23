package sim

import "testing"

func recordSeq(h *History, id uint32, startTick uint32, n int, base float64) {
	for i := 0; i < n; i++ {
		tick := startTick + uint32(i)
		v := base + float64(i)
		pos := [3]float64{v, v + 1, v + 2}
		up := [3]float64{v + 3, v + 4, v + 5}
		h.Record(tick, id, pos, up)
	}
}

// 1. Record 20 ticks for one entity; At(tick-3) resolves; At(tick-15) is
// outside the 10-tick window.
func TestHistory_WindowBounds(t *testing.T) {
	h := NewHistory(HistoryTicks)
	const id = uint32(1)
	const start = uint32(100)
	recordSeq(h, id, start, 20, 0)

	latest := start + 19 // last tick recorded

	pos, up, ok := h.At(latest-3, id)
	if !ok {
		t.Fatalf("At(latest-3) ok = false, want true")
	}
	wantV := float64(latest - 3 - start)
	wantPos := [3]float64{wantV, wantV + 1, wantV + 2}
	wantUp := [3]float64{wantV + 3, wantV + 4, wantV + 5}
	if pos != wantPos || up != wantUp {
		t.Fatalf("At(latest-3) = pos=%v up=%v, want pos=%v up=%v", pos, up, wantPos, wantUp)
	}

	if _, _, ok := h.At(latest-15, id); ok {
		t.Fatalf("At(latest-15) ok = true, want false (outside %d-tick window)", HistoryTicks)
	}
}

// 2. Two entities recorded on the same ticks do not read each other's data.
func TestHistory_PerEntityIsolation(t *testing.T) {
	h := NewHistory(HistoryTicks)
	const start = uint32(50)
	recordSeq(h, 1, start, 10, 0)    // entity 1 values: 0..9
	recordSeq(h, 2, start, 10, 1000) // entity 2 values: 1000..1009

	tick := start + 5
	pos1, up1, ok1 := h.At(tick, 1)
	pos2, up2, ok2 := h.At(tick, 2)
	if !ok1 || !ok2 {
		t.Fatalf("expected both entities resolved: ok1=%v ok2=%v", ok1, ok2)
	}
	if pos1 == pos2 || up1 == up2 {
		t.Fatalf("entities read each other's data: pos1=%v pos2=%v up1=%v up2=%v", pos1, pos2, up1, up2)
	}
	wantPos1 := [3]float64{5, 6, 7}
	wantPos2 := [3]float64{1005, 1006, 1007}
	if pos1 != wantPos1 {
		t.Fatalf("entity 1 pos = %v, want %v", pos1, wantPos1)
	}
	if pos2 != wantPos2 {
		t.Fatalf("entity 2 pos = %v, want %v", pos2, wantPos2)
	}
}

// 3. An unknown entity id reports ok == false rather than panicking.
func TestHistory_UnknownEntity(t *testing.T) {
	h := NewHistory(HistoryTicks)
	recordSeq(h, 1, 0, 5, 0)

	if _, _, ok := h.At(2, 999); ok {
		t.Fatalf("At for unknown entity id ok = true, want false")
	}
	// Also unknown on a completely empty history.
	empty := NewHistory(HistoryTicks)
	if _, _, ok := empty.At(0, 1); ok {
		t.Fatalf("At on empty history ok = true, want false")
	}
}

// 4. A tick sequence that wraps past 2^32-1 still resolves correctly — the
// case a naive `<` comparison on raw uint32 values gets wrong.
func TestHistory_TickWraparound(t *testing.T) {
	h := NewHistory(HistoryTicks)
	const id = uint32(7)
	// Start 5 ticks before the uint32 max, so the sequence wraps through 0.
	start := ^uint32(0) - 5 // 4294967290
	recordSeq(h, id, start, 20, 0)

	latest := start + 19 // wrapped around: start(-5) + 19 => 13 past zero

	// Sanity: confirm we actually wrapped past the uint32 boundary.
	if latest >= start {
		t.Fatalf("test setup did not wrap: start=%d latest=%d", start, latest)
	}

	pos, up, ok := h.At(latest-3, id)
	if !ok {
		t.Fatalf("At(latest-3) across wraparound ok = false, want true")
	}
	wantV := float64(int32(latest - 3 - start))
	wantPos := [3]float64{wantV, wantV + 1, wantV + 2}
	wantUp := [3]float64{wantV + 3, wantV + 4, wantV + 5}
	if pos != wantPos || up != wantUp {
		t.Fatalf("At(latest-3) across wraparound = pos=%v up=%v, want pos=%v up=%v", pos, up, wantPos, wantUp)
	}

	if _, _, ok := h.At(latest-15, id); ok {
		t.Fatalf("At(latest-15) across wraparound ok = true, want false (outside window)")
	}
}

// TestHistory_ForgetReleasesEntity guards the despawn path: without Forget the
// map grows once per entity id for the life of the process, and ids are never
// reused.
func TestHistory_ForgetReleasesEntity(t *testing.T) {
	h := NewHistory(HistoryTicks)
	h.Record(1, 42, [3]float64{1, 2, 3}, [3]float64{0, 1, 0})
	if _, _, ok := h.At(1, 42); !ok {
		t.Fatal("precondition: recorded sample should resolve")
	}
	if got := len(h.entities); got != 1 {
		t.Fatalf("entities = %d, want 1", got)
	}

	h.Forget(42)
	if _, _, ok := h.At(1, 42); ok {
		t.Error("At resolved after Forget")
	}
	if got := len(h.entities); got != 0 {
		t.Errorf("entities = %d after Forget, want 0 (ring still retained)", got)
	}

	h.Forget(9999) // never recorded — must not panic
}
