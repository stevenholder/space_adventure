// Phase 5 — ship ownership, gateway side (GDD "Ownership").
//
// Ownership IS having the ship.v1 item: purchase, refusals, inventory and
// reconnect persistence are the Phase 2 shop machinery unchanged. This
// file only makes the world agree with the inventory — a ship on the pad
// for every owner, spawned on purchase and on join, one per owner.

package server

import (
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// ownerKey is the stable identity a ship is keyed by: the token when the
// session has one, else a per-session key — an ephemeral player's
// "persistence" is the session, so their ship's is too.
func (c *client) ownerKey() string {
	if c.ident != nil && c.ident.token != "" {
		return c.ident.token
	}
	return "session:" + c.entity.Name
}

func hasShipItem(p store.Player) bool {
	for _, st := range p.Inventory {
		if st.Item == sim.ShipItemID && st.Qty > 0 {
			return true
		}
	}
	return false
}

// ensureShip spawns the owner's ship on the pad if the world does not hold
// one already. Called after a successful purchase and on join — both idempotent
// paths to the same invariant: owner in inventory ⇒ ship in world.
func (s *Server) ensureShip(owner string) {
	s.mu.Lock()
	defer s.mu.Unlock()

	var occupied []sim.Vec
	for _, e := range s.worldEnts {
		if st, ok := e.Data.(*sim.ShipState); ok {
			if st.Owner == owner {
				return // already in the world
			}
			occupied = append(occupied, sim.Vec(e.Pos))
		}
	}

	pos, quat := sim.SpawnShip(s.terrain, occupied)
	ship := &sim.Ent{
		ID:     s.nextWorldID(),
		Kind:   sim.EntityKind(protocol.EntityTypeShip),
		Pos:    [3]float64(pos),
		Quat:   [4]float64(quat),
		Health: s.reg.Entities["ship"].MaxHealth,
		Flags:  protocol.FlagGrounded,
		Def:    "ship",
		Data:   sim.NewShipState(owner),
	}
	s.world.Add(ship)
	// syncWorldEnts announces it to every connected client on the next
	// tick; worldEnts itself is rebuilt there too, from world.Order().
}

// syncOwnedShip is the per-client hook: make the world match the
// inventory. Safe to call often; ensureShip early-returns.
func (s *Server) syncOwnedShip(c *client) {
	if c.ident == nil || c.entity == nil {
		return
	}
	if hasShipItem(c.ident.Snapshot()) {
		s.ensureShip(c.ownerKey())
	}
}
