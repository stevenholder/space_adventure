package server

import "testing"

func TestInterestVisibleInsideRadius(t *testing.T) {
	i := Interest{Radius: 120}
	client := [3]float64{0, 0, 0}
	near := [3]float64{50, 0, 0} // 50 m, inside 120 m radius
	if !i.Visible(client, near, false) {
		t.Fatalf("entity at 50m should be visible within a 120m radius")
	}
}

func TestInterestNotVisibleOutsideRadius(t *testing.T) {
	i := Interest{Radius: 120}
	client := [3]float64{0, 0, 0}
	far := [3]float64{200, 0, 0} // 200 m, outside 120 m radius
	if i.Visible(client, far, false) {
		t.Fatalf("entity at 200m should not be visible within a 120m radius")
	}
}

func TestInterestOwnEntityAlwaysVisible(t *testing.T) {
	i := Interest{Radius: 120}
	client := [3]float64{0, 0, 0}
	farAway := [3]float64{100000, 100000, 100000} // way beyond any radius
	if !i.Visible(client, farAway, true) {
		t.Fatalf("own entity must always be visible regardless of distance")
	}
}

func TestInterestFilterPreservesOrder(t *testing.T) {
	i := Interest{Radius: 120}
	client := [3]float64{0, 0, 0}
	ents := []InterestEnt{
		{ID: 3, Pos: [3]float64{10, 0, 0}},  // in range
		{ID: 1, Pos: [3]float64{500, 0, 0}}, // out of range
		{ID: 9, Pos: [3]float64{20, 0, 0}},  // in range
		{ID: 5, Pos: [3]float64{600, 0, 0}}, // out of range
		{ID: 2, Pos: [3]float64{30, 0, 0}},  // in range
	}
	got := i.Filter(client, ents)
	want := []uint32{3, 9, 2}
	if len(got) != len(want) {
		t.Fatalf("Filter() = %v, want %v", got, want)
	}
	for idx := range want {
		if got[idx] != want[idx] {
			t.Fatalf("Filter() = %v, want %v (order not preserved)", got, want)
		}
	}
}

func TestInterestFilterOwnEntityBeyondRadius(t *testing.T) {
	i := Interest{Radius: 120}
	client := [3]float64{0, 0, 0}
	ents := []InterestEnt{
		{ID: 1, Pos: [3]float64{9999, 0, 0}, Own: true}, // far, but own
		{ID: 2, Pos: [3]float64{9999, 0, 0}, Own: false},
	}
	got := i.Filter(client, ents)
	want := []uint32{1}
	if len(got) != 1 || got[0] != want[0] {
		t.Fatalf("Filter() = %v, want %v", got, want)
	}
}

func TestInterestFilterEmptyInputYieldsEmptyNotNil(t *testing.T) {
	i := Interest{Radius: 120}
	client := [3]float64{0, 0, 0}
	got := i.Filter(client, []InterestEnt{})
	if got == nil {
		t.Fatalf("Filter() on empty input returned nil, want empty non-nil slice")
	}
	if len(got) != 0 {
		t.Fatalf("Filter() on empty input = %v, want empty", got)
	}
}
