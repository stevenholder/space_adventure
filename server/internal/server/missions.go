package server

// Missions (docs/GDD.md "Missions and parties", Phase 10).
//
// The server owns every transition: offers come from the registry, accepts
// and turn-ins are cmds validated at a board NPC, progress is driven by the
// server's own kill and position observations, and completion pays through
// the same identity Mutate the shop uses. The client's journal renders what
// the events say and asserts nothing.
//
// Party credit: a qualifying action by ANY member progresses the mission
// for EVERY member who holds it (GDD). Kill missions credit through the
// party; scout visits do too; fetch is exempt by nature — the items in YOUR
// inventory are the progress, and turn-in consumes them.
//
// Lock order is the file's load-bearing constraint: identity locks are
// taken OUTSIDE s.mu everywhere (doCmd set the precedent). Kill credit is
// observed inside fire() under s.mu, so the attribution is captured there
// and the mutations run after release. The scout sweep does the same from
// its own goroutine.

import (
	"encoding/json"
	"math"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// scoutDiscoveryMeters mirrors the client's compass-discovery range for a
// 12.6 m mast (GDD "Silhouette and the 23 m horizon"): a scout mission
// completes exactly where the POI's marker would appear.
const scoutDiscoveryMeters = 84.0

func (s *Server) missionCmd(c *client, req protocol.Cmd) protocol.CmdResult {
	reply := func(status uint8, data []byte) protocol.CmdResult {
		return protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode, Status: status, Data: data}
	}
	refuse := func(reason string) protocol.CmdResult {
		return reply(protocol.StatusRefused, encodeJSON(map[string]string{"reason": reason}))
	}
	if !c.rate.allow(time.Now()) {
		return reply(protocol.StatusRateLimited, nil)
	}

	switch req.Opcode {
	case protocol.OpMissionList:
		var body struct {
			NPC uint32 `json:"npc"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		starterOnly, ok := s.boardKind(c, body.NPC)
		if !ok {
			return refuse("not_a_board")
		}
		offers := []defs.Mission{}
		for _, m := range s.reg.Missions {
			if m.Type == "bounty" || !m.Board {
				continue
			}
			if starterOnly && !m.Starter {
				continue
			}
			offers = append(offers, m)
		}
		// Deterministic order for the panel (map iteration is not).
		for i := 0; i < len(offers); i++ {
			for j := i + 1; j < len(offers); j++ {
				if offers[j].ID < offers[i].ID {
					offers[i], offers[j] = offers[j], offers[i]
				}
			}
		}
		var state map[string]*store.MissionState
		c.ident.Mutate(func(p *store.Player) { state = snapshotMissions(p) })
		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"offers": offers,
			"state":  state,
		}))

	case protocol.OpMissionAccept:
		var body struct {
			ID string `json:"id"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		m, ok := s.reg.Missions[body.ID]
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if m.Type == "bounty" {
			return s.bountyAccept(c, req, m)
		}
		if _, ok := s.anyBoardInRange(c); !ok {
			return refuse("out_of_range")
		}
		var already bool
		c.ident.Mutate(func(p *store.Player) {
			st := missionState(p, m.ID)
			if st.Active {
				already = true
				return
			}
			st.Active = true
			st.Count = 0
		})
		if already {
			return refuse("already_active")
		}
		s.refreshScoutCache(c)
		return reply(protocol.StatusOK, nil)

	case protocol.OpMissionAbandon:
		var body struct {
			ID string `json:"id"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		var wasActive bool
		c.ident.Mutate(func(p *store.Player) {
			if st, ok := p.Missions[body.ID]; ok && st.Active {
				st.Active = false
				st.Count = 0
				wasActive = true
			}
		})
		if !wasActive {
			return refuse("not_active")
		}
		s.refreshScoutCache(c)
		s.bountyAbandon(c, body.ID)
		return reply(protocol.StatusOK, nil)

	case protocol.OpMissionShare:
		var body struct {
			ID string `json:"id"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		m, ok := s.reg.Missions[body.ID]
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if m.Type == "bounty" {
			// Bounty membership follows the party through the claim machine;
			// sharing a claim by cmd would bypass the race.
			return refuse("not_shareable")
		}
		var holds bool
		c.ident.Mutate(func(p *store.Player) {
			st, ok := p.Missions[m.ID]
			holds = ok && st.Active
		})
		if !holds {
			return refuse("not_active")
		}
		s.mu.Lock()
		members := append([]*client{}, s.partyMembers(c)...)
		s.mu.Unlock()
		if len(members) <= 1 {
			return refuse("no_party")
		}
		shared := 0
		for _, member := range members {
			if member == c {
				continue
			}
			var took bool
			member.ident.Mutate(func(p *store.Player) {
				st := missionState(p, m.ID)
				if st.Active {
					return
				}
				st.Active = true
				st.Count = 0
				took = true
			})
			if took {
				shared++
				member.send(msg{data: missionSharedFrame(c.entity.Name, m)})
				s.refreshScoutCache(member)
			}
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{"shared": shared}))

	case protocol.OpMissionTurnin:
		var body struct {
			NPC uint32 `json:"npc"`
			ID  string `json:"id"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		m, ok := s.reg.Missions[body.ID]
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if m.Type != "fetch" {
			return refuse("not_a_fetch")
		}
		if _, ok := s.boardKind(c, body.NPC); !ok {
			return refuse("not_a_board")
		}
		var status string
		var credits int64
		c.ident.Mutate(func(p *store.Player) {
			st := missionState(p, m.ID)
			if !st.Active {
				status = "not_active"
				return
			}
			if countItem(p, m.Item) < m.Count {
				status = "not_enough"
				return
			}
			removeItem(p, m.Item, m.Count)
			st.Active = false
			st.Count = 0
			st.Done++
			p.Credits += m.Reward
			credits = p.Credits
		})
		if status != "" {
			return refuse(status)
		}
		c.send(msg{data: missionCompleteFrame(m)})
		return reply(protocol.StatusOK, encodeJSON(map[string]any{"credits": credits}))
	}
	return reply(protocol.StatusUnknownOpcode, nil)
}

// boardKind reports whether npcID is a mission board in range: the
// dispatcher offers everything, the quartermaster only starters.
func (s *Server) boardKind(c *client, npcID uint32) (starterOnly bool, ok bool) {
	npc, pos, found := s.findNPC(npcID)
	if !found {
		return false, false
	}
	w := cmdWorld{
		Pos:  c.entity.State.Pos,
		Up:   terrain.Normalize(c.entity.State.Pos),
		Look: c.lookDir(),
	}
	if !inRange(w, npc, pos) {
		return false, false
	}
	switch npc.ID {
	case "npc.dispatcher":
		return false, true
	case "npc.quartermaster":
		return true, true
	}
	return false, false
}

// anyBoardInRange finds a board NPC the player is currently at, for accepts
// (which carry no npc id — the board you are standing at is the board).
func (s *Server) anyBoardInRange(c *client) (uint32, bool) {
	s.mu.Lock()
	ids := make([]uint32, 0, 4)
	for _, e := range s.worldEnts {
		if e.Def == "npc.dispatcher" || e.Def == "npc.quartermaster" {
			ids = append(ids, e.ID)
		}
	}
	s.mu.Unlock()
	for _, id := range ids {
		if _, ok := s.boardKind(c, id); ok {
			return id, true
		}
	}
	return 0, false
}

func missionState(p *store.Player, id string) *store.MissionState {
	if p.Missions == nil {
		p.Missions = map[string]*store.MissionState{}
	}
	st := p.Missions[id]
	if st == nil {
		st = &store.MissionState{}
		p.Missions[id] = st
	}
	return st
}

func snapshotMissions(p *store.Player) map[string]*store.MissionState {
	out := make(map[string]*store.MissionState, len(p.Missions))
	for k, v := range p.Missions {
		cp := *v
		out[k] = &cp
	}
	return out
}

func countItem(p *store.Player, item string) int {
	n := 0
	for _, s := range p.Inventory {
		if s.Item == item {
			n += s.Qty
		}
	}
	return n
}

func removeItem(p *store.Player, item string, n int) {
	kept := p.Inventory[:0]
	for _, s := range p.Inventory {
		if s.Item == item && n > 0 {
			take := s.Qty
			if take > n {
				take = n
			}
			s.Qty -= take
			n -= take
			if s.Qty == 0 {
				continue
			}
		}
		kept = append(kept, s)
	}
	p.Inventory = kept
}

// ---- progress: kills --------------------------------------------------------

// missionKillCredit runs AFTER s.mu is released (fire captures the members
// under the lock). Any member's kill progresses every member holding a
// matching kill mission — and the bounty, when the victim is the claimed
// warlord, which bountyKilled handles.
func (s *Server) missionKillCredit(members []*client, victimArch string) {
	for _, member := range members {
		var completed []defs.Mission
		var progressed []progressRow
		member.ident.Mutate(func(p *store.Player) {
			for id, st := range p.Missions {
				if !st.Active {
					continue
				}
				m, ok := s.reg.Missions[id]
				if !ok || m.Type != "kill" {
					continue
				}
				if m.Archetype != "" && m.Archetype != victimArch {
					continue
				}
				st.Count++
				if st.Count >= m.Count {
					st.Active = false
					st.Count = 0
					st.Done++
					p.Credits += m.Reward
					completed = append(completed, m)
				} else {
					progressed = append(progressed, progressRow{m.ID, st.Count, m.Count})
				}
			}
		})
		for _, row := range progressed {
			member.send(msg{data: missionProgressFrame(row)})
		}
		for _, m := range completed {
			member.send(msg{data: missionCompleteFrame(m)})
		}
	}
}

type progressRow struct {
	ID    string
	Count int
	Goal  int
}

func missionProgressFrame(r progressRow) []byte {
	data, _ := json.Marshal(map[string]any{"id": r.ID, "count": r.Count, "goal": r.Goal})
	return protocol.EncodeEvent(protocol.Event{EventID: protocol.EventMissionProgress, Data: data})
}

// missionSharedFrame carries the FULL template: the recipient may never
// have visited a board, so their journal needs the name and goal, not just
// an id.
func missionSharedFrame(from string, m defs.Mission) []byte {
	data, _ := json.Marshal(map[string]any{"from": from, "mission": m})
	return protocol.EncodeEvent(protocol.Event{EventID: protocol.EventMissionShared, Data: data})
}

func missionCompleteFrame(m defs.Mission) []byte {
	data, _ := json.Marshal(map[string]any{"id": m.ID, "credits": m.Reward})
	return protocol.EncodeEvent(protocol.Event{EventID: protocol.EventMissionComplete, Data: data})
}

// ---- progress: scouting -----------------------------------------------------

// refreshScoutCache recomputes the client's active scout targets (mission id
// → zone origin dir). The cache exists for the lock order: the sweep reads
// positions under s.mu and MUST NOT take identity locks there, so what a
// player is scouting is mirrored under s.mu at every mission mutation.
func (s *Server) refreshScoutCache(c *client) {
	targets := map[string][3]float64{}
	c.ident.Mutate(func(p *store.Player) {
		for id, st := range p.Missions {
			if !st.Active {
				continue
			}
			m, ok := s.reg.Missions[id]
			if !ok || m.Type != "scout" {
				continue
			}
			if z, ok := s.reg.Zones[m.Poi]; ok {
				targets[id] = z.OriginDir
			}
		}
	})
	s.mu.Lock()
	c.scoutTargets = targets
	s.mu.Unlock()
}

// scoutSweep runs once a second from its own goroutine: under s.mu it
// collects (client, mission) pairs whose position is inside the discovery
// radius, then completes them with no lock held — through the party, like a
// kill: any member arriving scouts for every member holding the mission.
func (s *Server) scoutSweep() {
	type hit struct {
		members []*client
		mission string
	}
	var hits []hit
	s.mu.Lock()
	for _, c := range s.clients {
		if len(c.scoutTargets) == 0 || c.entity == nil {
			continue
		}
		pos := terrain.Normalize(c.entity.State.Pos)
		for id, dir := range c.scoutTargets {
			dot := pos.Dot(terrain.Vec(dir))
			if dot > 1 {
				dot = 1
			}
			if math.Acos(dot)*terrain.PlanetRadius <= scoutDiscoveryMeters {
				hits = append(hits, hit{append([]*client{}, s.partyMembers(c)...), id})
			}
		}
	}
	s.mu.Unlock()

	for _, h := range hits {
		m, ok := s.reg.Missions[h.mission]
		if !ok {
			continue
		}
		for _, member := range h.members {
			var done bool
			member.ident.Mutate(func(p *store.Player) {
				st, ok := p.Missions[m.ID]
				if !ok || !st.Active {
					return
				}
				st.Active = false
				st.Count = 0
				st.Done++
				p.Credits += m.Reward
				done = true
			})
			if done {
				member.send(msg{data: missionCompleteFrame(m)})
				s.refreshScoutCache(member)
				if xp := s.reg.Awards.ScoutMissionXP; xp > 0 {
					s.mu.Lock()
					member.awardLocked("recon", xp)
					s.mu.Unlock()
				}
			}
		}
	}
}
