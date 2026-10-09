// The craft channel (Phase 22, docs/GDD.md "The refinery"): the gather
// channel's rules with units. A unit's inputs leave the bag when it starts
// and come back if it is cancelled; when it ends its output lands with its
// XP, then the next unit starts. The tick steps the timer under s.mu; a
// finished unit lands in drainCrafts, which takes the identity first and
// s.mu inside it — the cmd path's lock order — so a unit lands, pays and
// starts the next one atomically.
package server

import (
	"math"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// craftMinChannel floors one unit's channel after craft_speed (GDD).
const craftMinChannel = 0.5

// craftState is one player's running craft. Zero value = not crafting.
type craftState struct {
	recipe    defs.Recipe
	left      int // units still to finish, the running one included
	unitTicks int
	ticks     int // left on the running unit
	from      [3]float64
	held      bool   // the running unit's inputs are out of the bag
	pending   bool   // the running unit finished; drainCrafts lands it
	gen       uint64 // bumped per channel, so a stale drain is ignored
}

func (k *craftState) active() bool { return k.recipe.ID != "" }

// craftDue is a finished unit waiting for drainCrafts.
type craftDue struct {
	c   *client
	gen uint64
}

// refund is a cancelled unit's inputs owed back to a bag.
type refund struct {
	c      *client
	recipe defs.Recipe
}

// craftDuration is one unit's channel: the recipe's seconds × (1 − the
// skill's craft_speed and its synergies), floored.
func craftDuration(reg *defs.Registry, p *store.Player, r defs.Recipe) float64 {
	d := r.Seconds * (1 - craftSpeed(reg, p, r.Skill))
	if d < craftMinChannel {
		d = craftMinChannel
	}
	return d
}

// craftTicks is a duration in whole ticks, at least one.
func craftTicks(d float64) int {
	return max(1, int(math.Round(d*sim.TickHz)))
}

// startCraft begins qty units of r, the first unit's inputs already taken
// by the caller. False when any channel is running (`busy`). Under s.mu.
func (s *Server) startCraft(c *client, r defs.Recipe, qty, ticks int) bool {
	if s.channelling(c) {
		return false
	}
	s.craftGen++
	c.craft = craftState{recipe: r, left: qty, unitTicks: ticks, ticks: ticks,
		from: c.entity.State.Pos, held: true, gen: s.craftGen}
	return true
}

// sendCraftEnd tells c one unit landed ("done") or the channel ended and
// why; item/qty are what landed on done, what did NOT land otherwise.
// Under s.mu.
func (s *Server) sendCraftEnd(c *client, recipe, reason, item string, qty int) {
	c.send(msg{data: protocol.EncodeEvent(protocol.Event{
		EntityID: c.entity.ID, EventID: protocol.EventCraftEnd,
		Data: encodeJSON(map[string]any{"recipe": recipe, "reason": reason, "item": item, "qty": qty}),
	})})
}

// endCraft clears c's craft with reason and returns the recipe whose unit
// inputs are owed back (nil when none are out). Under s.mu.
func (s *Server) endCraft(c *client, reason string) *defs.Recipe {
	k := c.craft
	c.craft = craftState{}
	s.sendCraftEnd(c, k.recipe.ID, reason, k.recipe.Output.Item, k.recipe.Output.Qty)
	if k.held {
		return &k.recipe
	}
	return nil
}

// stepCrafts runs every craft one tick: a broken channel ends (its refund
// queued), a finished unit goes to drainCrafts. Under s.mu.
func (s *Server) stepCrafts() []craftDue {
	var due []craftDue
	for _, c := range s.clients {
		if c.entity == nil || !c.craft.active() || c.craft.pending {
			continue
		}
		if end := channelBroken(c.craft.from, c.entity.State.Pos, c.vitals.DeadTicks > 0); end != "" {
			s.cancelChannelQueued(c, end)
			continue
		}
		c.craft.ticks--
		if c.craft.ticks <= 0 {
			c.craft.pending = true
			due = append(due, craftDue{c: c, gen: c.craft.gen})
		}
	}
	return due
}

// drainCrafts lands each finished unit: output (+ craft_extra) or, with no
// room, the inputs back and the end; the XP; then the next unit's inputs,
// or missing_materials. Called with s.mu NOT held.
func (s *Server) drainCrafts(due []craftDue) {
	for _, d := range due {
		c := d.c
		c.ident.Mutate(func(p *store.Player) {
			s.mu.Lock()
			defer s.mu.Unlock()
			k := &c.craft
			if !k.active() || k.gen != d.gen || !k.pending {
				return // cancelled since the tick: the cancel refunded it
			}
			r := k.recipe
			bonus := 0
			if s.rng.Float64() < kindBonus(s.reg, p, r.Skill, "craft_extra") {
				bonus = 1
			}
			made, err := sim.LandUnit(p, r, bonus, s.reg)
			if err != nil {
				sim.ReturnUnit(p, r, s.reg)
				k.held = false
				s.endCraft(c, sim.ReasonNoSpace)
				return
			}
			k.held = false
			c.awardLocked(r.Skill, r.XP)
			s.sendCraftEnd(c, r.ID, "done", r.Output.Item, made)
			k.left--
			if k.left <= 0 {
				c.craft = craftState{}
				return
			}
			if sim.TakeUnit(p, r) != nil {
				s.endCraft(c, sim.ReasonMissingMaterials)
				return
			}
			k.held, k.pending, k.ticks = true, false, k.unitTicks
		})
	}
}

// drainRefunds hands cancelled units' inputs back. Called with s.mu NOT
// held.
func (s *Server) drainRefunds(rs []refund) {
	for _, r := range rs {
		r.c.ident.Mutate(func(p *store.Player) { sim.ReturnUnit(p, r.recipe, s.reg) })
	}
}
