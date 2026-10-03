package server

import (
	"testing"
	"time"

	"space-adventure/server/internal/ai"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// An NPC round that reaches a player damages them through damagePlayer, armor
// and all. Before, projectiles only swept World ents and players are not
// World ents, so every gunner shot passed straight through its target.
func TestNPCProjectileHitsPlayer(t *testing.T) {
	for _, c := range []struct {
		name  string
		chest string
		want  int // health after one 20-damage round
	}{
		{"bare", "", 80},
		{"bulwark suit", "armor.suit.bulwark", 100 - sim.Mitigate(20, 18)},
	} {
		srv, url := newTestServer(t) // one body per world: a second spawns on the first and would soak the round
		_, id := joinClient(t, url, "target")
		cl := published(srv, id)
		if c.chest != "" {
			cl.ident.Mutate(func(p *store.Player) { p.Equipped["chest"] = c.chest })
		}

		srv.mu.Lock()
		pos := terrain.Vec(cl.entity.State.Pos)
		up := terrain.Normalize(pos)
		side := terrain.Normalize(terrain.Cross(up, terrain.Vec{0, 0, 1}))
		origin := pos.Add(up.Scale(1.2)).Add(side.Scale(6))
		srv.spawnProjectile(424242, ai.Shot{Origin: [3]float64(origin), Dir: [3]float64(side.Scale(-1)), Speed: 45, Damage: 20})
		srv.mu.Unlock()

		deadline := time.Now().Add(2 * time.Second)
		var health int
		for time.Now().Before(deadline) {
			srv.mu.Lock()
			health = cl.vitals.Health
			srv.mu.Unlock()
			if health < 100 {
				break
			}
			time.Sleep(10 * time.Millisecond)
		}
		if health != c.want {
			t.Fatalf("%s: health %d after the round, want %d", c.name, health, c.want)
		}
	}
}
