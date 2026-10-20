// Phase 5 — ship ownership, gateway side (GDD "Ownership"); Phase 24 —
// the same rule for every vehicle item (GDD "Buildings and the dock").
//
// Ownership IS having the vehicle item (ship.v1, vehicle.rover): crafting
// it at a dock, refusals, inventory and reconnect persistence are the bag
// machinery unchanged. This file only makes the world agree with the
// inventory — one vehicle per owner per item, spawned when the item lands
// (a craft, a buy) and on join: in front of the owner's dock when they have
// one, else on the legacy pad (ships) or beside the world rover (rovers).

package server

import (
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// ownerKey is the stable identity a vehicle is keyed by: the token when the
// session has one, else a per-session key — an ephemeral player's
// "persistence" is the session, so their vehicle's is too.
func (c *client) ownerKey() string {
	if c.ident != nil && c.ident.token != "" {
		return c.ident.token
	}
	return "session:" + c.entity.Name
}

// hasVehicleItem reports whether p carries item.
func hasVehicleItem(p store.Player, item string) bool {
	for _, st := range p.Inventory {
		if st.Item == item && st.Qty > 0 {
			return true
		}
	}
	return false
}

// vehicleEntity is the world entity kind a vehicle item spawns: the entity
// def ("ship" or "vehicle") whose asset is the item's. "" = none.
func (s *Server) vehicleEntity(item string) string {
	it, ok := s.reg.Items[item]
	if !ok || it.Kind != "vehicle" {
		return ""
	}
	for _, def := range []string{"ship", "vehicle"} {
		if ed, ok := s.reg.Entities[def]; ok && ed.Asset != "" && ed.Asset == it.Asset {
			return def
		}
	}
	return ""
}

// ownedVehicle reports e's owner when it is an owned ship or rover.
func ownedVehicle(e *sim.Ent) (owner string, ok bool) {
	switch v := e.Data.(type) {
	case *sim.ShipState:
		return v.Owner, true
	case *sim.VehicleState:
		return v.Owner, true
	}
	return "", false
}

// ensureShip is ensureVehicle for the ship item.
func (s *Server) ensureShip(owner string) { s.ensureVehicle(owner, sim.ShipItemID) }

// ensureVehicle spawns owner's vehicle for item if the world does not hold
// one already. Called after a craft, a purchase and on join — idempotent
// paths to the same invariant: item in inventory ⇒ vehicle in world. Reads
// s.world (not the per-tick worldEnts cache), so two calls inside one tick
// still spawn one.
func (s *Server) ensureVehicle(owner, item string) {
	kind := s.vehicleEntity(item)
	if kind == "" || owner == "" {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	var ships, all []sim.Vec
	var dock *sim.Ent
	for _, id := range s.world.Order() {
		e := s.world.Ents[id]
		if e == nil {
			continue
		}
		if o, ok := ownedVehicle(e); ok {
			_, isShip := e.Data.(*sim.ShipState)
			if o == owner && isShip == (kind == "ship") {
				return // already in the world
			}
			all = append(all, sim.Vec(e.Pos))
			if isShip {
				ships = append(ships, sim.Vec(e.Pos))
			}
			continue
		}
		if st := structureOf(e); st != nil && dock == nil && st.Owner == owner && s.reg.Items[e.Def].Station == "dock" {
			dock = e
		}
	}

	var pos sim.Vec
	var quat sim.Quat
	switch {
	case dock != nil:
		dp := sim.Vec(dock.Pos)
		pos, quat = sim.SpawnAtDock(s.terrain, dp, yawForward(structureOf(dock).Yaw, terrain.Normalize(dp)), all)
	case kind == "ship":
		pos, quat = sim.SpawnShip(s.terrain, ships)
	default:
		pos, quat = sim.SpawnOwnedRover(s.terrain, all)
	}
	e := &sim.Ent{
		ID:     s.nextWorldID(),
		Kind:   sim.EntityKind(protocol.EntityTypeVehicle),
		Pos:    [3]float64(pos),
		Quat:   [4]float64(quat),
		Health: s.reg.Entities[kind].MaxHealth,
		Def:    kind,
	}
	if kind == "ship" {
		e.Kind = sim.EntityKind(protocol.EntityTypeShip)
		e.Flags = protocol.FlagGrounded
		e.Data = sim.NewShipState(owner)
	} else {
		v := sim.NewVehicleState()
		v.Owner = owner
		e.Data = v
	}
	s.world.Add(e)
	// syncWorldEnts announces it to every connected client on the next
	// tick; worldEnts itself is rebuilt there too, from world.Order().
}

// syncOwnedVehicles is the per-client hook: make the world match the
// inventory, one vehicle per vehicle item carried. Safe to call often;
// ensureVehicle early-returns. Called with s.mu NOT held.
func (s *Server) syncOwnedVehicles(c *client) {
	if c.ident == nil || c.entity == nil {
		return
	}
	p := c.ident.Snapshot()
	owner := c.ownerKey()
	seen := map[string]bool{}
	for _, st := range p.Inventory {
		if st.Qty > 0 && !seen[st.Item] && s.reg.Items[st.Item].Kind == "vehicle" {
			seen[st.Item] = true
			s.ensureVehicle(owner, st.Item)
		}
	}
}
