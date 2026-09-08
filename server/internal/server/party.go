package server

// Parties (docs/GDD.md "Missions and parties", Phase 10).
//
// Max four, leaderless, session-scoped: any member invites, anyone leaves,
// the party dissolves at one remaining member, and NOTHING persists — a
// party is a set of live connections and dies with them. All party state
// lives under s.mu beside the client map it indexes into; the cmds route
// here from doCmd BEFORE handleCmd, because they read and write other
// clients, which cmdWorld's single-player view deliberately cannot.
//
// Credit semantics ride on this (wave 2): a qualifying action by any member
// progresses the mission for every member who holds it, so the only thing a
// party IS server-side is this membership list.

import (
	"encoding/json"
	"time"

	"space-adventure/server/internal/protocol"
)

// partyMax is the roster cap (GDD: "Max 4").
const partyMax = 4

type party struct {
	members []*client
}

// partyCmd routes one party opcode. Caller holds NOTHING; s.mu is taken
// here. Rate limiting mirrors handleCmd's order: an over-budget cmd does
// not act.
func (s *Server) partyCmd(c *client, req protocol.Cmd) protocol.CmdResult {
	reply := func(status uint8, data []byte) protocol.CmdResult {
		return protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode, Status: status, Data: data}
	}
	refuse := func(reason string) protocol.CmdResult {
		return reply(protocol.StatusRefused, encodeJSON(map[string]string{"reason": reason}))
	}
	if !c.rate.allow(time.Now()) {
		return reply(protocol.StatusRateLimited, nil)
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	switch req.Opcode {
	case protocol.OpPartyInvite:
		var body struct {
			Target uint32 `json:"target"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		target := s.clients[body.Target]
		if target == nil || target.entity == nil {
			return reply(protocol.StatusNotFound, nil)
		}
		if target == c {
			return refuse("self")
		}
		if c.party != nil {
			if len(c.party.members) >= partyMax {
				return refuse("party_full")
			}
			if target.party == c.party {
				return refuse("already_member")
			}
		}
		// Newest invite wins (GDD): a stale pending invite is simply
		// overwritten, and the toast tells the invitee who asked last.
		target.pendingInvite = c.entity.ID
		target.send(msg{data: protocol.EncodeEvent(protocol.Event{
			EntityID: c.entity.ID,
			EventID:  protocol.EventPartyInvited,
			Data:     encodeJSON(map[string]any{"from": c.entity.ID, "name": c.entity.Name}),
		})})
		return reply(protocol.StatusOK, nil)

	case protocol.OpPartyRespond:
		var body struct {
			Accept bool `json:"accept"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		inviterID := c.pendingInvite
		c.pendingInvite = 0
		if inviterID == 0 {
			return refuse("no_invite")
		}
		if !body.Accept {
			return reply(protocol.StatusOK, nil)
		}
		inviter := s.clients[inviterID]
		if inviter == nil {
			return refuse("inviter_gone")
		}
		p := inviter.party
		if p == nil {
			p = &party{}
			p.members = append(p.members, inviter)
			inviter.party = p
		}
		if len(p.members) >= partyMax {
			return refuse("party_full")
		}
		// Accepting while in another party means leaving it — one party at
		// a time, and the accept IS the statement of intent.
		s.removeFromParty(c)
		p.members = append(p.members, c)
		c.party = p
		s.notifyParty(p)
		return reply(protocol.StatusOK, nil)

	case protocol.OpPartyLeave:
		if c.party == nil {
			return refuse("no_party")
		}
		s.removeFromParty(c)
		return reply(protocol.StatusOK, nil)
	}
	return reply(protocol.StatusUnknownOpcode, nil)
}

// removeFromParty detaches c and keeps the party lawful: the leaver gets an
// empty roster, the remainder gets the new one, and a remainder of one is a
// dissolved party (a solo "party" is just a player). Caller holds s.mu.
func (s *Server) removeFromParty(c *client) {
	p := c.party
	if p == nil {
		return
	}
	c.party = nil
	kept := p.members[:0]
	for _, m := range p.members {
		if m != c {
			kept = append(kept, m)
		}
	}
	p.members = kept
	c.send(msg{data: partyUpdateFrame(nil)})
	if len(p.members) == 1 {
		last := p.members[0]
		last.party = nil
		p.members = nil
		last.send(msg{data: partyUpdateFrame(nil)})
		return
	}
	s.notifyParty(p)
}

// notifyParty sends the current roster to every member. Caller holds s.mu.
func (s *Server) notifyParty(p *party) {
	f := partyUpdateFrame(p.members)
	for _, m := range p.members {
		m.send(msg{data: f})
	}
}

func partyUpdateFrame(members []*client) []byte {
	type row struct {
		ID   uint32 `json:"id"`
		Name string `json:"name"`
	}
	rows := make([]row, 0, len(members))
	for _, m := range members {
		rows = append(rows, row{m.entity.ID, m.entity.Name})
	}
	data, _ := json.Marshal(map[string]any{"members": rows})
	return protocol.EncodeEvent(protocol.Event{EventID: protocol.EventPartyUpdate, Data: data})
}

// partyMembers returns the live members sharing c's party, including c —
// or just c alone. The mission credit loop (wave 2) iterates this. Caller
// holds s.mu.
func (s *Server) partyMembers(c *client) []*client {
	if c.party == nil {
		return []*client{c}
	}
	return c.party.members
}
