// Placeable stations (Phase 23, docs/GDD.md "Placeable stations"): a bench,
// forge or chest a character puts down in the world and that stays there
// across restarts. The structure is a world Ent (EntityTypeStructure) whose
// Def is the item id and whose Data is *structureState; the store row
// (store.Structure) carries the same state under a stable key.
//
// Locks: the world side lives under s.mu like every other world ent. A
// store write never happens under s.mu: persistStructure takes
// structWriteMu, reads the CURRENT state under s.mu, and writes it after
// releasing s.mu — so whichever persist runs last writes the latest state,
// however the callers interleave.
package server

import (
	"context"
	"fmt"
	"log"
	"math"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

const (
	// structureCap is how many structures one character may have placed.
	structureCap = 5
	// placeReach is how far in front of the body a structure lands.
	placeReach = 2.0
	// placeClear is the free radius a structure needs around its centre,
	// and placeProbe how far out the slope is sampled.
	placeClear = 1.5
	placeProbe = 1.5
	// pickUpSeconds is the pick-up channel.
	pickUpSeconds = 1.5
	// structureAimHeight is where the interact cone points on a structure.
	structureAimHeight = 0.6
)

// reasonNoRoom refuses a place: the ground is too steep or something is
// in the way.
const reasonNoRoom = "no_room"

// placeSlopeMax is the steepest ground a structure sits on (15°).
var placeSlopeMax = 15 * math.Pi / 180

// structureState is a placed structure's server state (Ent.Data).
type structureState struct {
	Key       string // the store row id, stable across restarts
	Owner     string // the owning character's token
	OwnerName string
	Yaw       float64
	PlacedMs  int64
	Stacks    []store.Stack // a chest's contents; nil for a station
}

// pickUpState is one player's running pick-up channel. Zero = none.
type pickUpState struct {
	structure uint32
	ticks     int
	from      [3]float64
	pending   bool // finished; drainPickUps lands it
}

// structureInfo is what a cmd learns about a structure (cmdWorld).
type structureInfo struct {
	Item  defs.Item
	Pos   sim.Vec
	Mine  bool // the requester placed it
	Party bool // its owner is online in the requester's party
	Empty bool // no chest contents
}

// errStructureGone is a structure that vanished between lookup and use.
var errStructureGone = sim.RefusalError{Reason: "not_found"}

func isStructure(e *sim.Ent) bool {
	return e != nil && e.Kind == sim.EntityKind(protocol.EntityTypeStructure)
}

func structureOf(e *sim.Ent) *structureState {
	if !isStructure(e) {
		return nil
	}
	st, _ := e.Data.(*structureState)
	return st
}

// spawnData is a world ent's spawn row data: the def, and for a structure
// NUL and the owner's name (PROTOCOL "spawn").
func spawnData(e *sim.Ent) []byte {
	if st := structureOf(e); st != nil {
		return []byte(e.Def + "\x00" + st.OwnerName)
	}
	return []byte(e.Def)
}

// structureCollider is a placed structure's box, from its item's asset.
func (s *Server) structureCollider(e *sim.Ent) (protocol.Collider, bool) {
	return sim.PropCollider(protocol.Prop{
		Asset: s.reg.Items[e.Def].Asset,
		Pos:   v3f32(e.Pos),
		Quat:  v4f32(e.Quat),
		Scale: 1,
	})
}

// yawBasis is the tangent frame yaw is measured in at up: 0 along world +Z
// flattened onto the ground (world +X where +Z is the up axis), positive
// toward up × ref.
func yawBasis(up sim.Vec) (ref, right sim.Vec) {
	ref = up.Scale(-up[2]).Add(sim.Vec{0, 0, 1})
	if ref.Len() < 1e-6 {
		ref = up.Scale(-up[0]).Add(sim.Vec{1, 0, 0})
	}
	ref = terrain.Normalize(ref)
	return ref, terrain.Cross(up, ref)
}

func yawOf(fwd, up sim.Vec) float64 {
	ref, right := yawBasis(up)
	return math.Atan2(fwd.Dot(right), fwd.Dot(ref))
}

func yawForward(yaw float64, up sim.Vec) sim.Vec {
	ref, right := yawBasis(up)
	return ref.Scale(math.Cos(yaw)).Add(right.Scale(math.Sin(yaw)))
}

// newStructureEnt builds the world ent for a structure at pos facing yaw.
func (s *Server) newStructureEnt(item string, pos sim.Vec, st *structureState) *sim.Ent {
	up := terrain.Normalize(pos)
	return &sim.Ent{
		ID:   s.nextWorldID(),
		Kind: sim.EntityKind(protocol.EntityTypeStructure),
		Pos:  [3]float64(pos),
		Quat: quatFromForward([3]float64(yawForward(st.Yaw, up)), [3]float64(pos)),
		Def:  item,
		Data: st,
	}
}

// loadStructures puts every stored structure back into the world (SetStore,
// before serving). A row naming an item that is no longer a structure is
// skipped with a log line, not loaded as something else.
func (s *Server) loadStructures(ctx context.Context) error {
	rows, err := s.store.ListStructures(ctx)
	if err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, r := range rows {
		if it, ok := s.reg.Items[r.Item]; !ok || it.Kind != "structure" {
			log.Printf("structures: skipping %s: %q is not a structure item", r.ID, r.Item)
			continue
		}
		st := &structureState{Key: r.ID, Owner: r.Owner, OwnerName: r.OwnerName,
			Yaw: r.Yaw, PlacedMs: r.PlacedMs}
		if s.reg.Items[r.Item].Station == "" {
			st.Stacks = r.Stacks
		}
		s.world.Add(s.newStructureEnt(r.Item, sim.Vec(r.Pos), st))
	}
	s.syncWorldEnts()
	if len(rows) > 0 {
		log.Printf("structures: loaded %d", len(rows))
	}
	return nil
}

// structureRowLocked is key's current store row; false once it left the
// world. Under s.mu.
func (s *Server) structureRowLocked(key string) (store.Structure, bool) {
	for _, e := range s.world.Ents {
		if st := structureOf(e); st != nil && st.Key == key {
			return store.Structure{ID: st.Key, Owner: st.Owner, OwnerName: st.OwnerName, Item: e.Def,
				Pos: e.Pos, Yaw: st.Yaw, PlacedMs: st.PlacedMs, Stacks: append([]store.Stack{}, st.Stacks...)}, true
		}
	}
	return store.Structure{}, false
}

// persistStructure writes key's current state, or deletes its row when it
// is no longer in the world. Called with s.mu NOT held.
func (s *Server) persistStructure(key string) {
	if s.store == nil || key == "" {
		return
	}
	s.structWriteMu.Lock()
	defer s.structWriteMu.Unlock()
	s.mu.Lock()
	row, live := s.structureRowLocked(key)
	s.mu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), storeTimeout)
	defer cancel()
	var err error
	if live {
		err = s.store.PutStructure(ctx, &row)
	} else {
		err = s.store.DeleteStructure(ctx, key)
	}
	if err != nil {
		log.Printf("structures: persisting %s: %v", key, err)
	}
}

// removeOwnedStructures takes every structure the tokens own out of the
// world and the store (Kick: the character is gone). Called with s.mu NOT
// held.
func (s *Server) removeOwnedStructures(want map[string]bool) {
	s.mu.Lock()
	for _, e := range s.world.Ents {
		if st := structureOf(e); st != nil && want[st.Owner] {
			s.world.Remove(e.ID)
		}
	}
	s.mu.Unlock()
	if s.store == nil {
		return
	}
	s.structWriteMu.Lock()
	defer s.structWriteMu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), storeTimeout)
	defer cancel()
	for t := range want {
		if err := s.store.DeleteStructuresByOwner(ctx, t); err != nil {
			log.Printf("structures: deleting for a kicked owner: %v", err)
		}
	}
}

// structureFor resolves id for requester c — cmdWorld.FindStructure.
func (s *Server) structureFor(c *client, id uint32) (structureInfo, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e := s.world.Ents[id]
	st := structureOf(e)
	if st == nil {
		return structureInfo{}, false
	}
	it, ok := s.reg.Items[e.Def]
	if !ok {
		return structureInfo{}, false
	}
	info := structureInfo{Item: it, Pos: sim.Vec(e.Pos), Empty: len(st.Stacks) == 0}
	tok := ""
	if c.ident != nil {
		tok = c.ident.token
	}
	info.Mine = tok != "" && tok == st.Owner
	for _, m := range s.partyMembers(c) {
		if m != c && m.ident != nil && m.ident.token != "" && m.ident.token == st.Owner {
			info.Party = true
		}
	}
	return info, true
}

// placeStructure puts item down in front of c: the cap, the ground and the
// clearance, then take (the bag's unit), then the world. Returns the new
// entity id or the refusal. Called with s.mu NOT held; persists after.
func (s *Server) placeStructure(c *client, item string, take func() error) (uint32, string) {
	s.mu.Lock()
	id, key, reason := s.placeLocked(c, item, take)
	s.mu.Unlock()
	if reason == "" {
		s.persistStructure(key)
	}
	return id, reason
}

func (s *Server) placeLocked(c *client, item string, take func() error) (id uint32, key, reason string) {
	tok := c.ident.token
	n := 0
	for _, e := range s.world.Ents {
		if st := structureOf(e); st != nil && st.Owner == tok {
			n++
		}
	}
	if n >= structureCap {
		return 0, "", "too_many"
	}
	if c.seat != 0 || c.vitals.DeadTicks > 0 {
		return 0, "", reasonNoRoom
	}
	body := c.entity.State.Pos
	up := terrain.Normalize(body)
	f := c.entity.State.Facing
	f = f.Sub(up.Scale(f.Dot(up)))
	if f.Len() < 1e-6 {
		return 0, "", reasonNoRoom
	}
	f = terrain.Normalize(f)
	dir := terrain.Normalize(body.Add(f.Scale(placeReach)))
	target := dir.Scale(s.terrain.SampleRadius(dir))
	if !s.roomAtLocked(target, f) {
		return 0, "", reasonNoRoom
	}
	if err := take(); err != nil {
		return 0, "", refusalReason(err)
	}
	st := &structureState{
		Key:       fmt.Sprintf("s_%d_%08x", time.Now().UnixMilli(), s.rng.Uint32()),
		Owner:     tok,
		OwnerName: c.entity.Name,
		// Faces the placer: its forward points back at the body.
		Yaw:      yawOf(f.Scale(-1), dir),
		PlacedMs: time.Now().UnixMilli(),
	}
	if s.reg.Items[item].Station == "" {
		st.Stacks = []store.Stack{}
	}
	e := s.newStructureEnt(item, target, st)
	s.world.Add(e)
	return e.ID, st.Key, ""
}

// roomAtLocked is the place rule: flat ground (≤ 15° at the point and 1.5 m
// out along ±facing and ±right) and nothing within 1.5 m — a structure,
// NPC, node, vehicle or other body by centre (horizontal), a wall, rock or
// solid prop by its surface. Under s.mu.
func (s *Server) roomAtLocked(target, f sim.Vec) bool {
	up := terrain.Normalize(target)
	right := terrain.Cross(up, f)
	for _, p := range []sim.Vec{target,
		target.Add(f.Scale(placeProbe)), target.Sub(f.Scale(placeProbe)),
		target.Add(right.Scale(placeProbe)), target.Sub(right.Scale(placeProbe))} {
		if s.terrain.Slope(terrain.Normalize(p)) > placeSlopeMax {
			return false
		}
	}
	near := func(p [3]float64) bool {
		d := sim.Vec(p).Sub(target)
		d = d.Sub(up.Scale(d.Dot(up)))
		return d.Len() < placeClear
	}
	for _, e := range s.world.Ents {
		switch uint16(e.Kind) {
		case protocol.EntityTypeStructure, protocol.EntityTypeNPC, protocol.EntityTypeNode,
			protocol.EntityTypeTarget, protocol.EntityTypeVehicle, protocol.EntityTypeShip:
			if near(e.Pos) {
				return false
			}
		}
	}
	for _, o := range s.clients {
		if o.entity != nil && o.seat == 0 && near(o.entity.State.Pos) {
			return false
		}
	}
	for _, col := range s.solid {
		if sim.ColliderWithin(target, placeClear, col) {
			return false
		}
	}
	return true
}

// refusalReason is a sim refusal's reason, else a bare "refused".
func refusalReason(err error) string {
	if re, ok := err.(sim.RefusalError); ok {
		return re.Reason
	}
	return "refused"
}

// chestMove runs f on structure id's chest under s.mu — f also moves the
// requester's own bag, which the cmd path already holds — and persists the
// change. Returns the chest after. Called with s.mu NOT held.
func (s *Server) chestMove(id uint32, f func(chest *store.Player) error) ([]store.Stack, error) {
	s.mu.Lock()
	st := structureOf(s.world.Ents[id])
	if st == nil {
		s.mu.Unlock()
		return nil, errStructureGone
	}
	chest := store.Player{Inventory: st.Stacks}
	if err := f(&chest); err != nil {
		s.mu.Unlock()
		return nil, err
	}
	st.Stacks = chest.Inventory
	out := append([]store.Stack{}, st.Stacks...)
	key := st.Key
	s.mu.Unlock()
	s.persistStructure(key)
	return out, nil
}

// startPickUp begins c's pick-up of id, re-checking under the lock what the
// cmd checked: still there, still c's, still empty, no channel running.
// "" on success. Called with s.mu NOT held.
func (s *Server) startPickUp(c *client, id uint32) string {
	s.mu.Lock()
	defer s.mu.Unlock()
	st := structureOf(s.world.Ents[id])
	switch {
	case st == nil:
		return "not_found"
	case c.ident == nil || st.Owner != c.ident.token:
		return "not_yours"
	case len(st.Stacks) > 0:
		return "not_empty"
	case s.channelling(c):
		return "busy"
	}
	c.pickup = pickUpState{structure: id, ticks: int(math.Round(pickUpSeconds * sim.TickHz)),
		from: c.entity.State.Pos}
	return ""
}

// endPickUp clears c's pick-up and sends pick_up_end: ok with the bag, or
// the reason. Under s.mu.
func (s *Server) endPickUp(c *client, reason string, inventory []store.Stack) {
	id := c.pickup.structure
	c.pickup = pickUpState{}
	body := map[string]any{"structure": id, "ok": reason == ""}
	if reason == "" {
		body["inventory"] = inventory
	} else {
		body["reason"] = reason
	}
	c.send(msg{data: protocol.EncodeEvent(protocol.Event{
		EntityID: c.entity.ID, EventID: protocol.EventPickUpEnd, Data: encodeJSON(body),
	})})
}

// stepPickUps runs every pick-up one tick: broken (moved, died) ends it, a
// structure gone ends it not_found, a finished one goes to drainPickUps.
// Under s.mu.
func (s *Server) stepPickUps() []*client {
	var due []*client
	for _, c := range s.clients {
		k := &c.pickup
		if c.entity == nil || k.structure == 0 || k.pending {
			continue
		}
		if end := channelBroken(k.from, c.entity.State.Pos, c.vitals.DeadTicks > 0); end != "" {
			s.endPickUp(c, end, nil)
			continue
		}
		if structureOf(s.world.Ents[k.structure]) == nil {
			s.endPickUp(c, "not_found", nil)
			continue
		}
		k.ticks--
		if k.ticks <= 0 {
			k.pending = true
			due = append(due, c)
		}
	}
	return due
}

// drainPickUps lands each finished pick-up: the item into the bag (or
// no_space, the structure stays), the structure out of the world, the row
// deleted. Takes the identity, then s.mu — the cmd path's order. Called
// with s.mu NOT held, on the tick goroutine: the store delete runs in the
// background.
func (s *Server) drainPickUps(due []*client) {
	for _, c := range due {
		key := ""
		c.ident.Mutate(func(p *store.Player) {
			s.mu.Lock()
			defer s.mu.Unlock()
			k := c.pickup
			if k.structure == 0 || !k.pending {
				return // cancelled since the tick
			}
			e := s.world.Ents[k.structure]
			st := structureOf(e)
			switch {
			case st == nil:
				s.endPickUp(c, "not_found", nil)
				return
			case st.Owner != c.ident.token:
				s.endPickUp(c, "not_yours", nil)
				return
			case len(st.Stacks) > 0:
				s.endPickUp(c, "not_empty", nil) // a party member filled it mid-channel
				return
			}
			if err := sim.AddItem(p, e.Def, 1, s.reg); err != nil {
				s.endPickUp(c, refusalReason(err), nil)
				return
			}
			s.world.Remove(e.ID)
			key = st.Key
			s.endPickUp(c, "", append([]store.Stack{}, p.Inventory...))
		})
		if key != "" {
			go s.persistStructure(key)
		}
	}
}
