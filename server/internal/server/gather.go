// The gather channel and the death spill (Phase 12, docs/GDD.md "The
// channel", "Death spills the raw").
//
// Channel state lives on the client under s.mu and is stepped in tick().
// Anything that touches the identity — the yield grant, the spill's strip
// — is queued under the lock and drained after tick() releases it, the
// same shape as skillsTick's pickups: identities never nest under s.mu.
package server

import (
	"math"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

const (
	// gatherMoveTol is how far a channelling body may drift from where the
	// channel started before it ends `moved` (GDD `gather_move_tol`).
	gatherMoveTol = 0.5
	// gatherMinChannel floors the channel after efficacy (GDD
	// `gather_min_channel`).
	gatherMinChannel = 1.0
)

// gatherState is one player's running channel. Zero value = not gathering.
type gatherState struct {
	node  uint32
	ticks int
	from  [3]float64
}

// yield is a completed channel waiting for its grant outside s.mu.
type yield struct {
	c     *client
	node  uint32
	def   defs.Node
	items []defs.ItemQty
}

// spill is a death waiting for its strip-and-drop outside s.mu.
type spill struct {
	c   *client
	pos [3]float64
}

// gatherDuration is the channel in seconds after efficacy: the node's
// channel × (1 − skill efficacy − synergies aimed at that skill), floored.
func gatherDuration(reg *defs.Registry, p *store.Player, nd defs.Node) float64 {
	bonus := efficacyBonus(reg, p, nd.Skill) + synergyBonusFor(reg, p, "gather_speed", "", nd.Skill)
	d := nd.Channel * (1 - bonus)
	if d < gatherMinChannel {
		d = gatherMinChannel
	}
	return d
}

// startGather begins c's channel on node for ticks. False when one is
// already running (`busy`). Under s.mu.
func (s *Server) startGather(c *client, node uint32, ticks int) bool {
	if c.gather.node != 0 {
		return false
	}
	c.gather = gatherState{node: node, ticks: ticks, from: c.entity.State.Pos}
	return true
}

// endGather clears c's channel and tells them why. items is the yield on
// "done", nil otherwise. Under s.mu.
func (s *Server) endGather(c *client, reason string, item string, qty int) {
	node := c.gather.node
	c.gather = gatherState{}
	body := map[string]any{"node": node, "reason": reason}
	if reason == "done" {
		body["item"], body["qty"] = item, qty
	}
	c.send(msg{data: protocol.EncodeEvent(protocol.Event{
		EntityID: c.entity.ID, EventID: protocol.EventGatherEnd, Data: encodeJSON(body),
	})})
}

// cancelGather ends a running channel with reason; false if none was
// running. Under s.mu.
func (s *Server) cancelGather(c *client, reason string) bool {
	if c.gather.node == 0 {
		return false
	}
	s.endGather(c, reason, "", 0)
	return true
}

// stepGather advances one channel by a tick and reports how it ended:
// "" while still running, "done" when the yield is due, or the cancel
// reason. Pure, so the rule is testable without a world.
func stepGather(g *gatherState, pos [3]float64, dead bool, nodeHealth int) string {
	if g.node == 0 {
		return ""
	}
	if dead {
		return "died"
	}
	dx, dy, dz := pos[0]-g.from[0], pos[1]-g.from[1], pos[2]-g.from[2]
	if math.Sqrt(dx*dx+dy*dy+dz*dz) > gatherMoveTol {
		return "moved"
	}
	if nodeHealth <= 0 {
		return "depleted"
	}
	g.ticks--
	if g.ticks <= 0 {
		return "done"
	}
	return ""
}

// stepGathers runs every channel one tick and returns the yields to grant.
// A yield takes the node's health here, under the lock, so two players on
// one node cannot both take its last one. Under s.mu.
func (s *Server) stepGathers() []yield {
	var due []yield
	for _, c := range s.clients {
		if c.entity == nil || c.gather.node == 0 {
			continue
		}
		e := s.world.Ents[c.gather.node]
		health := 0
		if e != nil {
			health = e.Health
		}
		switch end := stepGather(&c.gather, c.entity.State.Pos, c.vitals.DeadTicks > 0, health); end {
		case "":
		case "done":
			nd := s.reg.Nodes[e.Def]
			if !sim.TakeYield(e) {
				s.endGather(c, "depleted", "", 0)
				continue
			}
			due = append(due, yield{c: c, node: c.gather.node, def: nd, items: sim.RollLoot(s.reg, nd.Loot, s.rng)})
		default:
			s.endGather(c, end, "", 0)
		}
	}
	return due
}

// drainYields grants the due yields: the bag through the identity, then the
// XP and the gather_end under s.mu. Called with s.mu NOT held.
func (s *Server) drainYields(due []yield) {
	for _, y := range due {
		var got defs.ItemQty
		y.c.ident.Mutate(func(p *store.Player) {
			for _, it := range y.items {
				if sim.AddItem(p, it.Item, it.Qty, s.reg) == nil {
					got = it // one entry per node table today; the last lands in the event
				}
			}
		})
		s.mu.Lock()
		if y.c.gather.node == y.node {
			y.c.awardLocked(y.def.Skill, y.def.XP)
			s.endGather(y.c, "done", got.Item, got.Qty)
		}
		s.mu.Unlock()
	}
}

// drainSpills strips each dead player's materials and drops them where the
// body fell. Called with s.mu NOT held.
func (s *Server) drainSpills(spills []spill) {
	for _, sp := range spills {
		var stacks []defs.ItemQty
		sp.c.ident.Mutate(func(p *store.Player) {
			stacks = sim.SpillMaterials(p, s.reg)
		})
		if len(stacks) == 0 {
			continue
		}
		s.mu.Lock()
		ctx := sim.StepCtx{World: s.world, Events: &s.pendingEvents}
		for _, st := range stacks {
			sim.DropStack(s.world, st.Item, st.Qty, sp.pos, s.nextWorldID, ctx)
		}
		s.syncWorldEnts()
		s.mu.Unlock()
	}
}
