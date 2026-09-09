package server

// The skill engine (docs/GDD.md "Skills", Phase 11). Training is the server
// observing what it already knows: damage it resolved, metres it moved you,
// loot it granted, credits it took, ground it watched you discover. Awards
// accumulate under s.mu and FLUSH once a second per player — identity
// mutation and events strictly after s.mu releases, the phase-10 lock order.
//
// Efficacy derives from level as (level−1) × per_level, so a fresh player's
// multipliers are exactly 1.0 and nothing anywhere changes until trained.

import (
	"encoding/json"
	"math"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/skills"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// awardLocked queues XP for one skill. Caller holds s.mu.
func (c *client) awardLocked(skill string, xp int64) {
	if xp <= 0 {
		return
	}
	if c.xpPending == nil {
		c.xpPending = map[string]int64{}
	}
	c.xpPending[skill] += xp
}

// skillsTick runs from the once-a-second sweep goroutine: it converts the
// tick loop's metre accumulators into XP, sweeps walk-over pickups and POI
// discovery, then flushes every pending award.
func (s *Server) skillsTick() {
	aw := s.reg.Awards

	type pickupHit struct {
		c     *client
		entID uint32
		item  string
		qty   int
	}
	type discoveryHit struct {
		c   *client
		poi string
	}
	var pickups []pickupHit
	var discoveries []discoveryHit

	s.mu.Lock()
	for _, c := range s.clients {
		if c.entity == nil {
			continue
		}
		// Metres → XP, keeping the sub-10 m remainder in the accumulator.
		if aw.SprintXPPer10m > 0 {
			steps := int64(c.sprintMeters / 10)
			if steps > 0 {
				c.sprintMeters -= float64(steps) * 10
				c.awardLocked("athletics", steps*aw.SprintXPPer10m)
			}
		}
		if aw.DriveXPPer10m > 0 {
			steps := int64(c.driveMeters / 10)
			if steps > 0 {
				c.driveMeters -= float64(steps) * 10
				c.awardLocked("driving", steps*aw.DriveXPPer10m)
			}
		}
		if aw.FlyXPPer10m > 0 {
			steps := int64(c.flyMeters / 10)
			if steps > 0 {
				c.flyMeters -= float64(steps) * 10
				c.awardLocked("piloting", steps*aw.FlyXPPer10m)
			}
		}

		// Walk-over pickups (GDD "Loot": no prompt, no keypress). The sim's
		// TryPickup was built for this and never wired — the claim half runs
		// here under s.mu, the grant half after release, because AddItem
		// needs the identity and identities never nest under s.mu.
		// ponytail: 1 Hz sweep; move into the tick if pickup ever feels laggy.
		if c.seat == 0 {
			pos := c.entity.State.Pos
			for _, e := range s.worldEnts {
				if e.Kind != sim.EntityKind(protocol.EntityTypeLoot) {
					continue
				}
				st, ok := e.Data.(*sim.LootState)
				if !ok || st.Claimed {
					continue
				}
				dx, dy, dz := e.Pos[0]-pos[0], e.Pos[1]-pos[1], e.Pos[2]-pos[2]
				if dx*dx+dy*dy+dz*dz > sim.LootPickupRadius*sim.LootPickupRadius {
					continue
				}
				st.Claimed = true // single-grant, decided under the lock
				pickups = append(pickups, pickupHit{c, e.ID, st.Item, st.Qty})
			}
		}

		// POI discovery: entering any layout-zone's mast radius for the
		// first time EVER pays Recon once. The cache mirrors the persisted
		// set for the same reason scoutTargets does.
		unit := terrain.Normalize(c.entity.State.Pos)
		for id, z := range s.reg.Zones {
			if z.Layout == nil {
				continue
			}
			if c.discovered != nil && c.discovered[id] {
				continue
			}
			dot := unit.Dot(terrain.Normalize(terrain.Vec(z.OriginDir)))
			if dot > 1 {
				dot = 1
			}
			if math.Acos(dot)*terrain.PlanetRadius <= scoutDiscoveryMeters {
				if c.discovered == nil {
					c.discovered = map[string]bool{}
				}
				c.discovered[id] = true
				discoveries = append(discoveries, discoveryHit{c, id})
				c.awardLocked("recon", aw.DiscoveryXP)
			}
		}
	}

	// Collect and clear every pending batch while still under the lock.
	type flush struct {
		c      *client
		awards map[string]int64
	}
	var flushes []flush
	for _, c := range s.clients {
		if len(c.xpPending) > 0 {
			flushes = append(flushes, flush{c, c.xpPending})
			c.xpPending = nil
		}
	}
	s.mu.Unlock()

	// Grant the claimed pickups: identity first, then world removal.
	for _, ph := range pickups {
		var granted bool
		ph.c.ident.Mutate(func(p *store.Player) {
			granted = sim.AddItem(p, ph.item, ph.qty, s.reg) == nil
		})
		s.mu.Lock()
		if granted {
			if s.world.Ents[ph.entID] != nil {
				s.world.Remove(ph.entID)
			}
			s.syncWorldEnts()
			ph.c.awardLocked("scavenging", int64(ph.qty)*aw.PickupXPPerItem)
		} else if e := s.world.Ents[ph.entID]; e != nil {
			if st, ok := e.Data.(*sim.LootState); ok {
				st.Claimed = false // no space: back on the ground for anyone
			}
		}
		s.mu.Unlock()
	}

	// Persist discoveries on the sheet.
	for _, d := range discoveries {
		d.c.ident.Mutate(func(p *store.Player) {
			for _, have := range p.Skills.Discovered {
				if have == d.poi {
					return
				}
			}
			p.Skills.Discovered = append(p.Skills.Discovered, d.poi)
		})
	}

	// Flush XP: mutate, detect level-ups, tell the player.
	for _, f := range flushes {
		type result struct {
			skill   string
			xp      int64
			level   int
			leveled bool
		}
		var results []result
		f.c.ident.Mutate(func(p *store.Player) {
			if p.Skills.XP == nil {
				p.Skills.XP = map[string]int64{}
			}
			for skill, add := range f.awards {
				before := skills.LevelForXP(p.Skills.XP[skill])
				p.Skills.XP[skill] += add
				after := skills.LevelForXP(p.Skills.XP[skill])
				results = append(results, result{skill, p.Skills.XP[skill], after, after > before})
			}
		})
		leveled := false
		for _, r := range results {
			if r.leveled {
				leveled = true
			}
			data, _ := json.Marshal(map[string]any{
				"skill": r.skill, "xp": r.xp, "level": r.level,
				"next_at": skills.PointsForLevel(r.level + 1), "leveled": r.leveled,
			})
			f.c.send(msg{data: protocol.EncodeEvent(protocol.Event{
				EventID: protocol.EventSkillXP, Data: data,
			})})
		}
		if leveled {
			s.refreshMovementMults(f.c)
		}
	}
}

// skillsCmd answers OpSkills with the whole sheet.
func (s *Server) skillsCmd(c *client, req protocol.Cmd) protocol.CmdResult {
	if !c.rate.allow(time.Now()) {
		return protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode, Status: protocol.StatusRateLimited}
	}
	var xp map[string]int64
	var discovered []string
	c.ident.Mutate(func(p *store.Player) {
		xp = make(map[string]int64, len(p.Skills.XP))
		for k, v := range p.Skills.XP {
			xp[k] = v
		}
		discovered = append([]string{}, p.Skills.Discovered...)
	})
	levels := map[string]int{}
	for _, sk := range s.reg.Skills {
		levels[sk.ID] = skills.LevelForXP(xp[sk.ID])
	}
	return protocol.CmdResult{
		Seq: req.Seq, Opcode: req.Opcode, Status: protocol.StatusOK,
		Data: encodeJSON(map[string]any{
			"xp": xp, "levels": levels, "discovered": discovered,
		}),
	}
}

// skillLevel reads one skill's level off a player row.
func skillLevel(p *store.Player, skill string) int {
	return skills.LevelForXP(p.Skills.XP[skill])
}

// efficacyMult is 1 + (level−1)·per_level for the named skill — the fresh-
// player identity: level 1 is exactly 1.0.
func (s *Server) efficacyMult(p *store.Player, skillID string) float64 {
	for _, sk := range s.reg.Skills {
		if sk.ID == skillID {
			return 1 + float64(skillLevel(p, skillID)-1)*sk.Efficacy.PerLevel
		}
	}
	return 1
}

// refreshMovementMults recomputes the sprint/drive/flight multipliers from
// the player's current levels and stores them under s.mu, where the tick's
// step reads them without an identity lock. The step then applies effMult's
// identity for any that are still 1.0.
func (s *Server) refreshMovementMults(c *client) {
	var sp, dr, fl float64
	c.ident.Mutate(func(p *store.Player) {
		sp = s.efficacyMult(p, "athletics")
		dr = s.efficacyMult(p, "driving")
		fl = s.efficacyMult(p, "piloting")
	})
	s.mu.Lock()
	c.sprintMult, c.driveMult, c.flightMult = sp, dr, fl
	s.mu.Unlock()
}

// loadSkillCaches mirrors the persisted discovered set under s.mu at join.
func (s *Server) loadSkillCaches(c *client) {
	var discovered []string
	c.ident.Mutate(func(p *store.Player) {
		discovered = append([]string{}, p.Skills.Discovered...)
	})
	set := make(map[string]bool, len(discovered))
	for _, d := range discovered {
		set[d] = true
	}
	s.mu.Lock()
	c.discovered = set
	s.mu.Unlock()
	s.refreshMovementMults(c) // a reconnecting player arrives already trained
}

var _ = defs.Skill{} // referenced again when task 6 lands the efficacy hooks
