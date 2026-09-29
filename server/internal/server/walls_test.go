package server

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// The outpost's longest solid wall run (north, no gate: 16 m + overhang),
// from the real zone data through ExpandLayout and ComposeZone: a ray at
// eye height across it is blocked, and a projectile fired across it dies at
// the wall. This is the path the gunner's shot takes.
func TestOutpostWallBlocksSightAndShots(t *testing.T) {
	field := terrain.Generate(1337)
	s, err := New(field, 1337)
	if err != nil {
		t.Fatal(err)
	}
	var wall *protocol.Collider
	for i := range s.colliders {
		c := &s.colliders[i]
		if c.Kind == 0 && c.Half[0] > 8.0 && c.Half[0] < 9.0 && math.Abs(float64(c.Half[1])-1.55) < 1e-3 {
			wall = c
			break
		}
	}
	if wall == nil {
		t.Fatalf("no 16 m solid wall run among %d colliders", len(s.colliders))
	}
	center := sim.Vec{float64(wall.Center[0]), float64(wall.Center[1]), float64(wall.Center[2])}
	q := sim.Quat{float64(wall.Quat[0]), float64(wall.Quat[1]), float64(wall.Quat[2]), float64(wall.Quat[3])}
	normal := sim.Rotate(q, sim.Vec{0, 0, 1})
	inside := center.Add(normal.Scale(-4))
	outside := center.Add(normal.Scale(4))

	if s.losBetween([3]float64(inside), [3]float64(outside)) {
		t.Fatalf("line of sight crosses the wall: %v -> %v through %v", inside, outside, center)
	}

	// A gunner's round: 45 m/s from just inside toward just outside.
	dir := normal
	s.worldID++
	proj := &sim.Ent{ID: s.worldID, Kind: sim.EntityKind(protocol.EntityTypeProjectile), Pos: [3]float64(inside),
		Vel:  [3]float64{dir[0] * 45, dir[1] * 45, dir[2] * 45},
		Data: &sim.ProjectileState{Owner: 1, Damage: 8, Speed: 45, LifeTicks: 100}}
	s.world.Add(proj)
	for i := 0; i < 20 && s.world.Ents[proj.ID] != nil; i++ {
		s.world.Step(sim.DT, sim.StepCtx{World: s.world, Terrain: s.terrain, Colliders: s.colliders, DefOf: s.entityDef})
		if e := s.world.Ents[proj.ID]; e != nil {
			p := sim.Vec(e.Pos)
			if p.Sub(center).Dot(normal) > 0.7 {
				t.Fatalf("projectile crossed the wall at tick %d: %v", i, p)
			}
		}
	}
	if s.world.Ents[proj.ID] != nil {
		t.Fatalf("projectile still alive after 20 ticks, never stopped at the wall")
	}
}
