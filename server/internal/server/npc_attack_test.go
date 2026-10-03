package server

import (
	"testing"

	"space-adventure/server/internal/ai"
)

// Every attack an NPC makes is announced exactly once, a wind-up before it
// lands (so the client's swing leads the hit), and an instant attack (no
// wind-up) is announced on the tick it lands.
func TestAttackAnnouncedOncePerAttack(t *testing.T) {
	for _, windup := range []int{0, 7} {
		var m ai.MeleeState
		starts, hits := []int{}, []int{}
		for tick := 0; tick < 200; tick++ {
			before := m.WindupTicks
			dmg := ai.StepMelee(&m, true, 10, 24, windup)
			if attackStarted(before, m.WindupTicks, dmg > 0) {
				starts = append(starts, tick)
			}
			if dmg > 0 {
				hits = append(hits, tick)
			}
		}
		if len(hits) == 0 || len(starts) != len(hits) {
			t.Fatalf("windup %d: %d starts for %d hits", windup, len(starts), len(hits))
		}
		for i := range hits {
			if hits[i]-starts[i] != windup {
				t.Fatalf("windup %d: attack %d announced at %d, landed at %d", windup, i, starts[i], hits[i])
			}
		}
	}
}

func TestRangedAttackAnnouncedOnce(t *testing.T) {
	var r ai.RangedState
	starts, shots := 0, 0
	for tick := 0; tick < 200; tick++ {
		before := r.WindupTicks
		shot := ai.StepRanged(&r, true, true, [3]float64{0, 100, 0}, [3]float64{0, 1, 0},
			[3]float64{0, 100, 10}, [3]float64{}, ai.Archetype{}, 8, 45, 32, 5)
		if attackStarted(before, r.WindupTicks, shot.Speed > 0) {
			starts++
		}
		if shot.Speed > 0 {
			shots++
		}
	}
	if shots == 0 || starts != shots {
		t.Fatalf("%d starts for %d shots", starts, shots)
	}
}
