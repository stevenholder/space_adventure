package server

import (
	"math"
	"math/rand"
	"testing"

	"space-adventure/server/internal/ai"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// wanderWorld builds a server (not running its tick loop) with one combat
// NPC left in its AI list, given wander radius w, and a fixed RNG.
func wanderWorld(t *testing.T, w float64) (*Server, *npcAI) {
	t.Helper()
	s, err := New(terrain.Generate(1337), 1337)
	if err != nil {
		t.Fatal(err)
	}
	if len(s.npcAI) == 0 {
		t.Fatal("no combat NPC in the world")
	}
	n := s.npcAI[0]
	n.wander = w
	s.npcAI = []*npcAI{n}
	s.rng = rand.New(rand.NewSource(1))
	return s, n
}

// surfaceDist is the great-circle distance between a and b at a's radius.
func surfaceDist(a, b [3]float64) float64 {
	ua, ub := terrain.Normalize(terrain.Vec(a)), terrain.Normalize(terrain.Vec(b))
	c := math.Max(-1, math.Min(1, ua.Dot(ub)))
	return math.Acos(c) * terrain.Vec(a).Len()
}

func TestWanderStaysInRadius(t *testing.T) {
	s, n := wanderWorld(t, 10)
	post := n.brain.Post
	prev := n.ent.Pos
	moved, legs := 0.0, 0
	for tick := 0; tick < ticksOf(60); tick++ {
		had := n.hasPoint
		s.stepNPCs(uint32(tick))
		if n.hasPoint && !had {
			legs++
		}
		if d := surfaceDist(post, n.ent.Pos); d > 12 {
			t.Fatalf("tick %d: %.2f m from post", tick, d)
		}
		moved += vecDist(prev, n.ent.Pos)
		prev = n.ent.Pos
	}
	if moved <= 2 {
		t.Fatalf("moved only %.2f m in 60 s (%d legs)", moved, legs)
	}
	if n.brain.State != ai.StatePatrol {
		t.Fatalf("state %v, want PATROL", n.brain.State)
	}
}

func TestWanderZeroIsInert(t *testing.T) {
	s, n := wanderWorld(t, 0)
	start := n.ent.Pos
	patrolled := false
	for tick := 0; tick < ticksOf(60); tick++ {
		s.stepNPCs(uint32(tick))
		patrolled = patrolled || n.brain.State == ai.StatePatrol
	}
	if !patrolled {
		t.Fatal("never reached PATROL")
	}
	for i := range start {
		if math.Abs(n.ent.Pos[i]-start[i]) > 1e-9 {
			t.Fatalf("moved: %v -> %v", start, n.ent.Pos)
		}
	}
}

func TestWanderDropsOnAggro(t *testing.T) {
	s, n := wanderWorld(t, 10)
	for tick := 0; !(n.hasPoint && n.legTicks > 5); tick++ {
		if tick > ticksOf(30) {
			t.Fatal("no leg under way within 30 s")
		}
		s.stepNPCs(uint32(tick))
	}
	if n.steer.Speed != n.arch.MoveSpeed*wanderSpeed {
		t.Fatalf("mid-leg speed %.2f, want %.2f", n.steer.Speed, n.arch.MoveSpeed*wanderSpeed)
	}

	// A living player inside aggro radius (beyond attack range), in LOS.
	self := n.ent.Pos
	up := terrain.Normalize(terrain.Vec(self))
	east := terrain.Normalize(terrain.Cross(terrain.Vec{0, 1, 0}, up))
	north := terrain.Cross(up, east)
	dist := math.Max(n.arch.AttackRange+1, n.arch.AggroRadius*0.6)
	var pos [3]float64
	found := false
	for i := 0; i < 16 && !found; i++ {
		th := float64(i) * math.Pi / 8
		d := terrain.Normalize(terrain.Vec(self).Add(east.Scale(dist * math.Cos(th))).Add(north.Scale(dist * math.Sin(th))))
		pos = [3]float64(d.Scale(s.terrain.SampleRadius(d)))
		found = vecDist(self, pos) <= n.arch.AggroRadius && s.losBetween(self, pos)
	}
	if !found {
		t.Fatal("no clear spot for the player")
	}
	const pid = 1 << 30
	s.clients[pid] = &client{
		srv: s, out: make(chan msg, 256), done: make(chan struct{}),
		entity: &entity{ID: pid, State: sim.State{Pos: sim.Vec(pos)}, Health: 100},
		vitals: sim.Vitals{Health: 100},
	}

	s.stepNPCs(9999)
	if n.brain.State != ai.StateAggro && n.brain.State != ai.StateAttack {
		t.Fatalf("state %v, want AGGRO", n.brain.State)
	}
	if n.legOn || n.hasPoint || n.legTicks != 0 || n.pauseTicks != 0 {
		t.Fatalf("leg survived aggro: on=%v point=%v legTicks=%d pause=%d", n.legOn, n.hasPoint, n.legTicks, n.pauseTicks)
	}
	if n.steer.Speed != n.arch.MoveSpeed {
		t.Fatalf("aggro speed %.2f, want full %.2f", n.steer.Speed, n.arch.MoveSpeed)
	}
}
