package server

// Bounty lifecycle stubs — wave 3 replaces these with the claim state
// machine (docs/GDD.md "Bounties — the limited missions"). Until then a
// bounty accept refuses cleanly rather than half-working.

import (
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
)

func (s *Server) bountyAccept(c *client, req protocol.Cmd, m defs.Mission) protocol.CmdResult {
	return protocol.CmdResult{
		Seq: req.Seq, Opcode: req.Opcode, Status: protocol.StatusRefused,
		Data: encodeJSON(map[string]string{"reason": "no_bounty"}),
	}
}

func (s *Server) bountyAbandon(c *client, missionID string) {}
