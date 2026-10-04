// Melee, the weapon swap and thrown charges (docs/GDD.md "Melee",
// "Throwables"; PROTOCOL.md `wield`, `explosion`). A player carries a gun
// in `primary` and a hand weapon in `melee`; `wield` picks which one is in
// the hand, and `fire` does whatever the hand holds -- a hitscan shot or a
// swing. The `equipped` event has always meant "what this body holds", so
// a swap is just another `equipped`.
package server

import (
	"encoding/json"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

const slotMelee = "melee"

// heldItem is what a body with these slots holds: the melee weapon when it
// asked for it (or carries no gun), else the gun. Empty when neither.
func heldItem(eq map[string]string, wantMelee bool) string {
	if wantMelee && eq[slotMelee] != "" {
		return eq[slotMelee]
	}
	if eq[slotPrimary] != "" {
		return eq[slotPrimary]
	}
	return eq[slotMelee]
}

// held is c's item in hand, off a fresh identity snapshot.
func (c *client) held() string {
	return heldItem(c.ident.Snapshot().Equipped, c.wieldMelee.Load())
}

// wieldCmd is OpWield: {"slot":"primary"|"melee"} picks the hand. The
// result names the item actually held (a missing weapon falls back to the
// other slot), and a change is broadcast as `equipped`.
func (s *Server) wieldCmd(c *client, req protocol.Cmd) protocol.CmdResult {
	res := protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode}
	var body struct {
		Slot string `json:"slot"`
	}
	if !decodeStrict(req.Data, &body) || (body.Slot != slotPrimary && body.Slot != slotMelee) {
		res.Status = protocol.StatusMalformed
		return res
	}
	eq := c.ident.Snapshot().Equipped
	before := heldItem(eq, c.wieldMelee.Load())
	c.wieldMelee.Store(body.Slot == slotMelee)
	after := heldItem(eq, c.wieldMelee.Load())
	if after != before {
		f := equippedFrame(c.entity.ID, after)
		s.mu.Lock()
		s.broadcast(f)
		s.mu.Unlock()
	}
	res.Status = protocol.StatusOK
	res.Data, _ = json.Marshal(map[string]any{"slot": body.Slot, "item": after})
	return res
}

// kill is one death a player caused, paid after s.mu is released (mission
// and bounty credit take identity locks, which never nest under s.mu).
type kill struct {
	killer  *client
	members []*client
	arch    string
	victim  uint32
}

// payKills runs the credit for kills. s.mu must NOT be held.
func (s *Server) payKills(kills []kill) {
	for _, k := range kills {
		s.missionKillCredit(k.members, k.arch)
		s.bountyResolveKill(k.killer, k.victim)
	}
}

// hitEntLocked lands dmg on world entity id for player c: health, the `hit`
// event, skill XP, and on a kill the loot hooks and kill XP. Returns the
// kill to pay, nil when the victim lives. Caller holds s.mu.
func (s *Server) hitEntLocked(c *client, skill string, id uint32, point sim.Vec, dmg int) *kill {
	e := s.world.Ents[id]
	if e == nil || e.Health <= 0 || dmg <= 0 {
		return nil
	}
	e.Health -= dmg
	if e.Health < 0 {
		e.Health = 0
	}
	data := appendU32(make([]byte, 0, 20), c.entity.ID)
	for i := 0; i < 3; i++ {
		data = appendF32(data, float32(point[i]))
	}
	data = appendU16(data, uint16(dmg))
	data = appendU16(data, uint16(e.Health))
	s.broadcast(protocol.EncodeEvent(protocol.Event{EntityID: id, EventID: protocol.EventHit, Data: data}))
	c.awardLocked(skill, int64(dmg)*s.reg.Awards.DamageXPPerPoint)
	if e.Health > 0 {
		return nil
	}
	arch := s.npcArchetypeOf(id)
	if n := s.npcOf(id); n != nil {
		n.lootExtra = c.lootExtra
		if s.inDiscoveredPOI(c, n.ent.Pos) {
			n.lootExtra = c.lootExtraPOI
		}
	}
	kxp := s.reg.Awards.KillXP
	if arch == "npc.warlord" {
		kxp = s.reg.Awards.KillXPWarlord
	}
	c.awardLocked(skill, kxp)
	return &kill{killer: c, members: append([]*client{}, s.partyMembers(c)...), arch: arch, victim: id}
}

// swing resolves a `fire` while a melee weapon is in hand: cadence, then
// every body in the arc. Players are not hit (no PvP). The `attack` event
// (target 0) makes every client play the swing.
func (s *Server) swing(c *client, f protocol.Fire, m defs.Melee) {
	s.mu.Lock()
	if c.seat != 0 {
		s.mu.Unlock()
		return // no swinging from a seat
	}
	tick := s.tickNo
	interval := uint32(ticksOf(m.Interval))
	// LastSwingTick holds tick+1 so 0 can mean "never" even on tick 0.
	if c.entity.LastSwingTick != 0 && tick+2 < c.entity.LastSwingTick+interval {
		s.mu.Unlock()
		return // too soon (1 tick tolerance, like fire)
	}
	c.entity.LastSwingTick = tick + 1
	s.broadcastAttack(c.entity.ID, 0)

	pos := sim.Vec(c.entity.State.Pos)
	up := terrain.Normalize(pos)
	facing := sim.Vec{float64(f.Dir[0]), float64(f.Dir[1]), float64(f.Dir[2])}
	// ponytail: targets at their CURRENT positions, not rewound like a
	// shot; at 2-3 m reach and a wide arc the lag reads fine. Rewind via
	// s.history if close swings feel like misses.
	var kills []kill
	for _, t := range sim.SwingTargets(s.world, c.entity.ID, pos, up, facing, m, s.entityDef) {
		if k := s.hitEntLocked(c, "athletics", t.ID, t.Point, m.Damage); k != nil {
			kills = append(kills, *k)
		}
	}
	s.mu.Unlock()
	s.payKills(kills)
}

// npcSwing lands an NPC's melee weapon on every living, on-foot player in
// its arc (damagePlayer: armor, invulnerability, death). Caller holds s.mu.
func (s *Server) npcSwing(n *npcAI, facing sim.Vec, m defs.Melee) {
	pos := sim.Vec(n.ent.Pos)
	up := terrain.Normalize(pos)
	for _, b := range s.liveBodies() {
		if _, _, ok := sim.InArc(pos, up, facing, m, b.Feet, b.Height, b.Radius); ok {
			s.damagePlayer(b.ID, m.Damage, n.ent.ID)
		}
	}
}

// liveBodies are the on-foot, living players as projectile/burst targets.
// Caller holds s.mu.
func (s *Server) liveBodies() []sim.Body {
	hb := s.reg.Entities["player"].Hitbox
	out := make([]sim.Body, 0, len(s.clients))
	for _, c := range s.clients {
		if c.entity != nil && c.seat == 0 && c.entity.Health > 0 {
			out = append(out, sim.Body{ID: c.entity.ID, Feet: sim.Vec(c.entity.State.Pos), Height: hb.Height, Radius: hb.Radius})
		}
	}
	return out
}

// throwHand is how far ahead of and above the feet a charge leaves the
// hand: at the shoulder, a little in front, clear of the thrower's capsule.
const throwUp, throwAhead = 1.5, 0.5

// throwLocked puts a thrown charge into the world from c's hand along
// look. Caller holds s.mu.
func (s *Server) throwLocked(c *client, item string, t defs.Throw, look sim.Vec) {
	pos := sim.Vec(c.entity.State.Pos)
	up := terrain.Normalize(pos)
	dir := terrain.Normalize(look)
	from := pos.Add(up.Scale(throwUp)).Add(dir.Scale(throwAhead))
	s.worldID++
	e := &sim.Ent{
		ID:   s.worldID,
		Kind: sim.EntityKind(protocol.EntityTypeProjectile),
		Def:  item,
		Pos:  [3]float64(from),
		Vel:  [3]float64(dir.Scale(t.Speed)),
		Data: &sim.ProjectileState{
			Owner: c.entity.ID, Speed: t.Speed, Falls: true, Burst: item,
			LifeTicks: ticksOf(8), // a charge that never lands bursts in the air
		},
	}
	s.world.Add(e)
	s.fresh = append(s.fresh, e)
}

// burstLocked sets off a thrown charge (sim.StepCtx.Burst): the
// `explosion` event, then the area damage. A player's charge hurts world
// entities only (no PvP); an NPC's would hurt players. Caller holds s.mu
// (it runs inside world.Step); kills queue for after the lock.
func (s *Server) burstLocked(at sim.Vec, owner uint32, item string) {
	def, ok := s.reg.Items[item]
	if !ok || def.Consumable == nil || def.Consumable.Throw == nil {
		return
	}
	t := *def.Consumable.Throw
	data := make([]byte, 0, 16+len(item))
	for i := 0; i < 3; i++ {
		data = appendF32(data, float32(at[i]))
	}
	data = appendF32(data, float32(t.Radius))
	data = append(data, item...)
	s.broadcast(protocol.EncodeEvent(protocol.Event{EntityID: owner, EventID: protocol.EventExplosion, Data: data}))

	thrower := s.clients[owner]
	for _, hit := range sim.BurstTargets(s.world, at, t, s.entityDef) {
		dmg := sim.BurstDamage(t, hit.Dist)
		if thrower != nil {
			if k := s.hitEntLocked(thrower, "marksmanship", hit.ID, hit.Point, dmg); k != nil {
				s.pendingKills = append(s.pendingKills, *k)
			}
		}
	}
	if thrower == nil {
		for _, b := range s.liveBodies() {
			if _, d, ok := sim.InBurst(at, t.Radius, b.Feet, b.Height, b.Radius); ok {
				s.damagePlayer(b.ID, sim.BurstDamage(t, d), owner)
			}
		}
	}
}
