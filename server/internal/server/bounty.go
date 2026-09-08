package server

// The bounty state machine (docs/GDD.md "Bounties — the limited missions").
//
// One bounty lives at a time. The scheduler posts it: a warlord spawned at
// a hostile POI and a priority_offer broadcast to every client. The first
// mission_accept CLAIMS it for the accepter's whole party — decided under
// s.mu, which is what makes the race exactly-one (C75). It releases on
// completion, on every claimant abandoning or disconnecting, or on the
// 15-minute expiry; a stolen kill (anyone outside the claim) releases it
// without pay. Every release despawns the warlord and re-posts after the
// cooldown. Nobody squats a bounty; content never wedges (C76).
//
// Lock order as everywhere in this phase: claim decisions and entity
// surgery under s.mu; identity mutations (mission state, pay) strictly
// after release.

import (
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// bountyPOIs are the sites the scheduler rotates through — the hostile
// POIs. The relay stays safe on purpose (GDD: the contrast is the point).
var bountyPOIs = []string{"outpost", "camp"}

type bountyState struct {
	mission   defs.Mission
	entityID  uint32
	poi       string
	claimed   bool
	claimants []*client
	expiresAt time.Time
}

// bountyTick runs from the scout-cadence goroutine (once a second): posts
// when due, expires when due.
func (s *Server) bountyTick() {
	var toRelease []*client
	var mission defs.Mission

	s.mu.Lock()
	now := time.Now()
	if s.bounty == nil {
		if !s.bountyRepostAt.IsZero() && now.Before(s.bountyRepostAt) {
			s.mu.Unlock()
			return
		}
		s.postBountyLocked()
		s.mu.Unlock()
		return
	}
	if s.bounty.claimed && now.After(s.bounty.expiresAt) {
		mission = s.bounty.mission
		toRelease = append(toRelease, s.bounty.claimants...)
		s.releaseBountyLocked()
	}
	s.mu.Unlock()

	// Expired: the claimants' journal entries deactivate, unpaid.
	s.deactivateMission(toRelease, mission.ID)
}

// postBountyLocked spawns the warlord at the next POI and broadcasts the
// offer. Caller holds s.mu.
func (s *Server) postBountyLocked() {
	var m defs.Mission
	for _, cand := range s.reg.Missions {
		if cand.Type == "bounty" {
			m = cand
			break
		}
	}
	if m.ID == "" {
		return // no bounty template shipped
	}
	poi := bountyPOIs[s.bountyRotation%len(bountyPOIs)]
	s.bountyRotation++
	z, ok := s.reg.Zones[poi]
	if !ok {
		return
	}

	arch := s.reg.NPCs[m.Archetype]
	dir := terrain.Normalize(terrain.Vec(z.OriginDir))
	pos := [3]float64(dir.Scale(s.terrain.SampleRadius(dir)))
	quat := [4]float64{0, 0, 0, 1}
	id := s.nextWorldID()
	ent := &sim.Ent{
		ID:     id,
		Kind:   sim.EntityKind(protocol.EntityTypeNPC),
		Pos:    pos,
		Quat:   quat,
		Health: arch.MaxHealth,
		Def:    m.Archetype,
		Data:   sim.CombatStateFor(m.Archetype, arch, pos, quat),
	}
	s.world.Add(ent)
	if n := newNPCAI(ent, arch); n != nil {
		s.npcAI = append(s.npcAI, n)
	}
	s.syncWorldEnts()

	s.bounty = &bountyState{mission: m, entityID: id, poi: poi}
	s.broadcast(protocol.EncodeEvent(protocol.Event{
		EntityID: id,
		EventID:  protocol.EventPriorityOffer,
		Data: encodeJSON(map[string]any{
			"id": m.ID, "poi": poi, "expires_s": m.ClaimMinutes * 60,
		}),
	}))
}

// releaseBountyLocked despawns the warlord and arms the re-post. Caller
// holds s.mu.
func (s *Server) releaseBountyLocked() {
	b := s.bounty
	if b == nil {
		return
	}
	s.bounty = nil
	repost := time.Duration(b.mission.RepostMinutes) * time.Minute
	if repost <= 0 {
		repost = 2 * time.Minute
	}
	s.bountyRepostAt = time.Now().Add(repost)

	if e := s.world.Ents[b.entityID]; e != nil {
		s.world.Remove(b.entityID)
	}
	kept := s.npcAI[:0]
	for _, n := range s.npcAI {
		if n.ent.ID != b.entityID {
			kept = append(kept, n)
		}
	}
	s.npcAI = kept
	s.syncWorldEnts()
}

// bountyAccept is the claim: first party wins, decided under s.mu.
func (s *Server) bountyAccept(c *client, req protocol.Cmd, m defs.Mission) protocol.CmdResult {
	reply := func(status uint8, data []byte) protocol.CmdResult {
		return protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode, Status: status, Data: data}
	}
	s.mu.Lock()
	b := s.bounty
	if b == nil || b.mission.ID != m.ID {
		s.mu.Unlock()
		return reply(protocol.StatusRefused, encodeJSON(map[string]string{"reason": "no_bounty"}))
	}
	if b.claimed {
		s.mu.Unlock()
		return reply(protocol.StatusRefused, encodeJSON(map[string]string{"reason": "claimed"}))
	}
	b.claimed = true
	b.claimants = append([]*client{}, s.partyMembers(c)...)
	minutes := m.ClaimMinutes
	if minutes <= 0 {
		minutes = 15
	}
	b.expiresAt = time.Now().Add(time.Duration(minutes) * time.Minute)
	claimants := append([]*client{}, b.claimants...)
	s.mu.Unlock()

	// The journal entry activates for every claimant — after the lock.
	for _, member := range claimants {
		member.ident.Mutate(func(p *store.Player) {
			st := missionState(p, m.ID)
			st.Active = true
			st.Count = 0
		})
	}
	return reply(protocol.StatusOK, nil)
}

// bountyAbandon removes one claimant; the last one out releases the claim.
func (s *Server) bountyAbandon(c *client, missionID string) {
	s.mu.Lock()
	b := s.bounty
	if b == nil || b.mission.ID != missionID || !b.claimed {
		s.mu.Unlock()
		return
	}
	kept := b.claimants[:0]
	for _, m := range b.claimants {
		if m != c {
			kept = append(kept, m)
		}
	}
	b.claimants = kept
	if len(b.claimants) == 0 {
		s.releaseBountyLocked()
	}
	s.mu.Unlock()
}

// bountyClientGone mirrors bountyAbandon for a disconnect. Called from
// leave() with no locks held.
func (s *Server) bountyClientGone(c *client) {
	var mission string
	s.mu.Lock()
	if b := s.bounty; b != nil && b.claimed {
		mission = b.mission.ID
	}
	s.mu.Unlock()
	if mission != "" {
		s.bountyAbandon(c, mission)
	}
}

// bountyKilled resolves the warlord's death: a claimant's kill pays every
// claimant; anyone else's kill is a stolen bounty — released, unpaid.
// Called from fire() AFTER s.mu is released, with the resolution captured
// by fireLocked.
func (s *Server) bountyResolveKill(killer *client, victimID uint32) {
	var payees, unpaid []*client
	var m defs.Mission

	s.mu.Lock()
	b := s.bounty
	if b == nil || b.entityID != victimID {
		s.mu.Unlock()
		return
	}
	m = b.mission
	if b.claimed {
		isClaimant := false
		for _, cl := range b.claimants {
			if cl == killer {
				isClaimant = true
				break
			}
		}
		if isClaimant {
			payees = append(payees, b.claimants...)
		} else {
			unpaid = append(unpaid, b.claimants...)
		}
	}
	s.releaseBountyLocked()
	s.mu.Unlock()

	for _, member := range payees {
		member.ident.Mutate(func(p *store.Player) {
			st := missionState(p, m.ID)
			st.Active = false
			st.Count = 0
			st.Done++
			p.Credits += m.Reward
		})
		member.send(msg{data: missionCompleteFrame(m)})
	}
	s.deactivateMission(unpaid, m.ID)
}

// deactivateMission clears an active mission without pay (expiry, stolen
// kill). No locks held.
func (s *Server) deactivateMission(members []*client, missionID string) {
	if missionID == "" {
		return
	}
	for _, member := range members {
		member.ident.Mutate(func(p *store.Player) {
			if st, ok := p.Missions[missionID]; ok {
				st.Active = false
				st.Count = 0
			}
		})
	}
}
