package server

import (
	"math"
	"testing"
	"time"

	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// TestSecondJoinStandsBesideTheFirst: two players on one spawn point used to
// stack -- the second was pushed 2*BodyRadius straight up onto the first's
// head (t2 clause c). The second now lands on the ground beside the first.
func TestSecondJoinStandsBesideTheFirst(t *testing.T) {
	srv, url := newTestServer(t)
	_, aID := joinClient(t, url, "a")
	published(srv, aID)
	_, bID := joinClient(t, url, "b")
	published(srv, bID)
	time.Sleep(200 * time.Millisecond) // a few ticks of collision

	srv.mu.Lock()
	a, b := srv.clients[aID].entity.State.Pos, srv.clients[bID].entity.State.Pos
	srv.mu.Unlock()
	for name, p := range map[string]sim.Vec{"a": a, "b": b} {
		if dev := math.Abs(p.Len() - srv.terrain.SampleRadius(terrain.Normalize(p))); dev > 0.05 {
			t.Errorf("%s is %.2f m off the ground", name, dev)
		}
	}
	if d := a.Sub(b).Len(); d < 2*sim.BodyRadius {
		t.Errorf("a and b overlap: %.2f m apart", d)
	}
}
