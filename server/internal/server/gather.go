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
	// handGatherMult is the bare-hands channel (Phase 22, GDD "From
	// nothing"): three times the tool's, one unit per yield, half the XP.
	handGatherMult = 3.0
	// handRefusedTool is the one node tool the hands cannot stand in for:
	// copper keeps the mk2 drill as its gate.
	handRefusedTool = "tool.drill.mk2"
)

// gatherState is one player's running channel. Zero value = not gathering.
type gatherState struct {
	node  uint32
	ticks int
	from  [3]float64
	hand  bool // no tool worn: one unit per yield, half XP (Phase 22)
}

// yield is a completed channel waiting for its grant outside s.mu.
type yield struct {
	c     *client
	node  uint32
	def   defs.Node
	items []defs.ItemQty
	hand  bool
}

// spill is a death waiting for its strip-and-drop outside s.mu.
type spill struct {
	c   *client
	pos [3]float64
}

// gatherDuration is the channel in seconds after efficacy: the node's
// channel × mult (the hands' 3, a crude tool's gather_mult) × (1 − skill
// efficacy − synergies aimed at that skill), floored.
func gatherDuration(reg *defs.Registry, p *store.Player, nd defs.Node, mult float64) float64 {
	bonus := efficacyBonus(reg, p, nd.Skill) + synergyBonusFor(reg, p, "gather_speed", "", nd.Skill)
	d := nd.Channel * mult * (1 - bonus)
	if d < gatherMinChannel {
		d = gatherMinChannel
	}
	return d
}

// startGather begins c's channel on node for ticks. False when any channel
// is already running (`busy`). Under s.mu.
func (s *Server) startGather(c *client, node uint32, ticks int, hand bool) bool {
	if s.channelling(c) {
		return false
	}
	c.gather = gatherState{node: node, ticks: ticks, from: c.entity.State.Pos, hand: hand}
	return true
}

// channelling reports whether c holds any channel, gather or craft. Under
// s.mu.
func (s *Server) channelling(c *client) bool {
	return c.gather.node != 0 || c.craft.active()
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

// cancelChannel ends whichever channel c holds with reason — gather_cancel
// cancels ANY channel from Phase 22 on. refund is a craft unit's inputs the
// caller owes back to the bag (it may hold the identity; this may not).
// false if nothing was running. Under s.mu.
func (s *Server) cancelChannel(c *client, reason string) (ok bool, refund *defs.Recipe) {
	if c.gather.node != 0 {
		s.endGather(c, reason, "", 0)
		return true, nil
	}
	if c.craft.active() {
		return true, s.endCraft(c, reason)
	}
	return false, nil
}

// cancelChannelQueued is cancelChannel from inside the tick, where the
// identity cannot be taken: the refund waits in pendingRefunds for tick()
// to drain after the lock drops. Under s.mu.
func (s *Server) cancelChannelQueued(c *client, reason string) {
	if _, r := s.cancelChannel(c, reason); r != nil {
		s.pendingRefunds = append(s.pendingRefunds, refund{c: c, recipe: *r})
	}
}

// stepGather advances one channel by a tick and reports how it ended:
// "" while still running, "done" when the yield is due, or the cancel
// reason. Pure, so the rule is testable without a world.
func stepGather(g *gatherState, pos [3]float64, dead bool, nodeHealth int) string {
	if g.node == 0 {
		return ""
	}
	if end := channelBroken(g.from, pos, dead); end != "" {
		return end
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

// channelBroken is the cancel rule every channel shares: dying, or moving
// past the tolerance from where it started.
func channelBroken(from, pos [3]float64, dead bool) string {
	if dead {
		return "died"
	}
	dx, dy, dz := pos[0]-from[0], pos[1]-from[1], pos[2]-from[2]
	if math.Sqrt(dx*dx+dy*dy+dz*dz) > gatherMoveTol {
		return "moved"
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
			items := sim.RollLoot(s.reg, nd.Loot, s.rng)
			if c.gather.hand {
				for i := range items {
					items[i].Qty = 1 // the hands pull one unit, not the table's count
				}
			}
			due = append(due, yield{c: c, node: c.gather.node, def: nd, items: items, hand: c.gather.hand})
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
			xp := y.def.XP
			if y.hand {
				xp /= 2
			}
			y.c.awardLocked(y.def.Skill, xp)
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
