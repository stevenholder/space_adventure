package server

import (
	"math"
	"strings"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// Herds are placed after the rover, one NPC per member, inside their disc,
// and only their AI carries a wander radius (GDD "Wildlife — herds and
// wandering", ROADMAP Phase 14 task 3).
func TestWildlifePlaced(t *testing.T) {
	field := terrain.Generate(1337)
	s, err := New(field, 1337)
	if err != nil {
		t.Fatal(err)
	}
	if len(s.reg.Herds) == 0 {
		t.Fatal("no herds loaded")
	}

	var roverID uint32
	for _, e := range s.worldEnts {
		if e.Kind == sim.EntityKind(protocol.EntityTypeVehicle) {
			roverID = e.ID
		}
	}
	if roverID == 0 {
		t.Fatal("no rover")
	}

	want := 0
	for _, h := range s.reg.Herds {
		want += h.Count
	}
	wander := map[uint32]float64{}
	for _, n := range s.npcAI {
		wander[n.ent.ID] = n.wander
	}

	// Members come in herd order, count by count, right after the rover.
	var mobs []uint32
	for _, e := range s.worldEnts {
		if e.Kind == sim.EntityKind(protocol.EntityTypeNPC) && strings.HasPrefix(e.Def, "mob.") {
			mobs = append(mobs, e.ID)
		}
	}
	if len(mobs) != want {
		t.Fatalf("%d mob NPCs in the world, herds declare %d", len(mobs), want)
	}
	byID := map[uint32]int{}
	for i, e := range s.worldEnts {
		byID[e.ID] = i
	}
	next := roverID
	for _, h := range s.reg.Herds {
		c := terrain.Normalize(terrain.Vec(h.OriginDir))
		surf := field.SampleRadius(c)
		for i := 0; i < h.Count; i++ {
			next++
			idx, ok := byID[next]
			if !ok {
				t.Fatalf("%s[%d]: no entity with id %d", h.ID, i, next)
			}
			e := s.worldEnts[idx]
			if e.Def != h.Def || e.Kind != sim.EntityKind(protocol.EntityTypeNPC) {
				t.Fatalf("%s[%d]: id %d is %s kind %d", h.ID, i, next, e.Def, e.Kind)
			}
			d := terrain.Normalize(terrain.Vec(e.Pos))
			dot := c[0]*d[0] + c[1]*d[1] + c[2]*d[2]
			if arc := math.Acos(math.Min(1, dot)) * surf; arc > h.Spread+0.5 {
				t.Errorf("%s[%d]: %.2f m from centre, spread %v", h.ID, i, arc, h.Spread)
			}
			if got, ok := wander[e.ID]; !ok || got != h.Wander {
				t.Errorf("%s[%d]: npcAI wander = %v (present %v), want %v", h.ID, i, got, ok, h.Wander)
			}
			delete(wander, e.ID)
		}
	}
	for id, w := range wander {
		if id <= roverID && w != 0 {
			t.Errorf("zone NPC %d: wander %v, want 0", id, w)
		}
	}
	if len(wander) == 0 {
		t.Error("no zone NPC AI left to check")
	}
	for id := range wander {
		if id > roverID {
			t.Errorf("npcAI %d past the rover is not a herd member", id)
		}
	}
}
