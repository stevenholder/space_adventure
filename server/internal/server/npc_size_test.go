package server

import (
	"math/rand"
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
)

// An NPC's hitbox is its archetype's: a shot passing 0.5 m beside a
// standing person misses, the same shot beside a 0.6 m-wide creature hits.
func TestNPCHitboxFromArchetype(t *testing.T) {
	reg, err := defs.Load()
	if err != nil {
		t.Fatal(err)
	}
	reg.NPCs["mob.wide"] = defs.NPC{ID: "mob.wide", RadiusM: 0.6}
	s := &Server{reg: reg}

	for _, tc := range []struct {
		def  string
		want bool
	}{{"npc.quartermaster", false}, {"mob.wide", true}} {
		w := sim.NewWorld()
		h := sim.NewHistory(0)
		w.Add(&sim.Ent{ID: 1, Kind: sim.EntityKind(protocol.EntityTypePlayer), Health: 100})
		h.Record(100, 1, [3]float64{0, 0, 0}, [3]float64{0, 1, 0})
		w.Add(&sim.Ent{ID: 2, Kind: sim.EntityKind(protocol.EntityTypeNPC), Health: 100, Def: tc.def})
		h.Record(100, 2, [3]float64{0.5, 0, 20}, [3]float64{0, 1, 0})

		shot := sim.Shot{Shooter: 1, Dir: [3]float64{0, 0, 1}, Tick: 100}
		_, _, hit := sim.ResolveShot(w, h, shot, *reg.Items["weapon.pulse"].Weapon, s.entityDef, rand.New(rand.NewSource(1)))
		if hit != tc.want {
			t.Errorf("%s 0.5 m off the line: hit = %v, want %v", tc.def, hit, tc.want)
		}
	}
}
