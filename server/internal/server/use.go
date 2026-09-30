// The use verb (Phase 13, docs/GDD.md "use"): a carried consumable or a
// worn ability, validated in the GDD's order, with per-connection
// cooldowns. Effects are small and named by the item's def; the callbacks
// on cmdWorld are how a handler running under the identity lock reaches
// state that lives under s.mu.
package server

import (
	"math"
	"time"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
)

// handleUse is the OpUse body of handleCmd.
func handleUse(w cmdWorld, item string, reply func(uint8, []byte) protocol.CmdResult, refuse func(string) protocol.CmdResult) protocol.CmdResult {
	def, ok := w.Reg.Items[item]
	if !ok {
		return refuse(sim.ReasonUnknownItem)
	}
	if def.Consumable == nil && def.Ability == nil {
		return refuse("unusable")
	}
	if def.Consumable != nil {
		if sim.CountItem(w.Player, item) < 1 {
			return refuse(sim.ReasonNotOwned)
		}
	} else {
		worn := false
		for _, held := range w.Player.Equipped {
			if held == item {
				worn = true
			}
		}
		if !worn {
			return refuse(sim.ReasonNotOwned)
		}
	}
	health, dead := 0, false
	if w.Vitals != nil {
		health, dead = w.Vitals()
	}
	if dead {
		return refuse("dead")
	}
	if w.CoolingFor != nil {
		if left := w.CoolingFor(item); left > 0 {
			return reply(protocol.StatusRefused, encodeJSON(map[string]any{
				"reason": "cooldown", "ready_in": math.Round(left*10) / 10,
			}))
		}
	}

	effect := map[string]any{}
	var cooldown float64
	switch {
	case def.Consumable != nil:
		c := def.Consumable
		cooldown = c.Cooldown
		if c.Heal > 0 {
			if health >= sim.PlayerMaxHealth {
				return refuse("no_effect")
			}
			after := health + c.Heal
			if w.Heal != nil {
				after = w.Heal(c.Heal)
			}
			effect["health"] = after
		}
		if err := sim.TakeItem(w.Player, item, 1); err != nil {
			return refuse(sim.ReasonNotOwned)
		}
	case def.Ability != nil:
		a := def.Ability
		cooldown = a.Cooldown
		switch a.ID {
		case "scan":
			var pings []map[string]any
			if w.Scan != nil {
				pings = w.Scan(a.Range)
			}
			if pings == nil {
				pings = []map[string]any{}
			}
			effect["pings"] = pings
		default:
			return refuse("unusable")
		}
	}
	// Anything used ends a running channel: the hands are busy elsewhere.
	if w.CancelGather != nil {
		w.CancelGather()
	}
	if w.StartCooldown != nil && cooldown > 0 {
		w.StartCooldown(item, cooldown)
	}
	return reply(protocol.StatusOK, encodeJSON(map[string]any{
		"item": item, "effect": effect, "cooldown": cooldown,
	}))
}

// coolingFor reports how long item still cools on c, 0 when ready. Under s.mu.
func (c *client) coolingFor(item string, now time.Time) float64 {
	if until, ok := c.cooldowns[item]; ok {
		if left := until.Sub(now).Seconds(); left > 0 {
			return left
		}
	}
	return 0
}

// scanLocked lists every node and loot drop within rng of c. Under s.mu.
func (s *Server) scanLocked(c *client, rng float64) []map[string]any {
	out := []map[string]any{}
	pos := c.entity.State.Pos
	for _, e := range s.worldEnts {
		if e.Kind != sim.EntityKind(protocol.EntityTypeNode) && e.Kind != sim.EntityKind(protocol.EntityTypeLoot) {
			continue
		}
		dx, dy, dz := e.Pos[0]-pos[0], e.Pos[1]-pos[1], e.Pos[2]-pos[2]
		if dx*dx+dy*dy+dz*dz > rng*rng {
			continue
		}
		out = append(out, map[string]any{
			"id": e.ID, "def": e.Def, "pos": []float64{e.Pos[0], e.Pos[1], e.Pos[2]}, "health": e.Health,
		})
	}
	return out
}
