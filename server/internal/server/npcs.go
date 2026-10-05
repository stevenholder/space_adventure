package server

import (
	"math"
	"math/rand"

	"space-adventure/server/internal/ai"
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// npcAI is one combat NPC's per-tick state: what it has decided, where it is
// going, and where it is in its swing.
//
// It lives here rather than in Ent.Data because Ent.Data already carries
// sim.NPCState (post, respawn timer) which the sim package owns. Splitting by
// owner keeps the sim testable without the ai package and vice versa.
type npcAI struct {
	ent  *sim.Ent
	arch defs.NPC
	// dropped guards the loot roll. Death is observed by polling health here,
	// so without it every tick between health hitting 0 and the sim setting
	// FlagDead would roll the table again — an NPC that dies once paying out
	// several times over.
	dropped bool
	// lootExtra is the killer's extra-roll chance, stashed on the kill
	// tick because the roll happens later, from the tick loop.
	lootExtra float64
	brain     ai.Brain
	steer     ai.Steerer
	melee     ai.MeleeState
	ranged    ai.RangedState
	// closeIn: a shooter carrying a melee weapon has it in hand (the target
	// is within reach); swapTicks counts down the swap before it can strike.
	closeIn   bool
	swapTicks int
	// wander is how far (m) this NPC strays from its post while in PATROL
	// (GDD "Wildlife — herds and wandering"). 0, the zone-NPC default,
	// keeps PATROL inert.
	wander float64
	// The current wander leg (GDD "Wandering"). legOn: a leg cycle has
	// begun in this PATROL stint (so the first leg waits a pause too);
	// hasPoint: legPoint is being walked to, for legTicks so far;
	// pauseTicks: standing still before the next pick.
	legOn, hasPoint      bool
	legPoint             [3]float64
	legTicks, pauseTicks int
}

// Wander leg parameters (GDD "Wildlife — herds and wandering", binding).
const (
	wanderTries    = 8   // wander_pick: re-rolls while the ground is not Walkable
	wanderSpeed    = 0.5 // wander_speed: × move_speed
	wanderArrive   = 1.0 // wander_arrive: m
	wanderLeg      = 8.0 // wander_leg: s
	wanderPauseMin = 1.0 // wander_pause: uniform 1.0–4.0 s
	wanderPauseMax = 4.0
)

// npcSwapSeconds is how long an NPC takes to change weapons.
const npcSwapSeconds = 0.5

// npcMelee is archetype arch's melee table, nil without one.
func (s *Server) npcMelee(arch defs.NPC) *defs.Melee {
	if it, ok := s.reg.Items[arch.Melee]; ok && it.Melee != nil {
		m := *it.Melee
		return &m
	}
	return nil
}

// npcHeld is what NPC entity id (archetype a) holds: its gun, or its melee
// weapon when it has no gun or has closed in. Caller holds s.mu.
func (s *Server) npcHeld(id uint32, a defs.NPC) string {
	if n := s.npcOf(id); n != nil && n.closeIn && a.Melee != "" {
		return a.Melee
	}
	if a.Primary != "" {
		return a.Primary
	}
	return a.Melee
}

// ticksOf converts a duration in seconds to whole ticks. The tick rate is
// fixed, so 1.2 s is exactly 24 ticks; accumulating dt instead needs an
// epsilon and drifts, and this sim feeds replay.
func ticksOf(seconds float64) int {
	t := int(math.Round(seconds * sim.TickHz))
	if t < 1 {
		return 1
	}
	return t
}

// newNPCAI builds the runner state for one spawned NPC. Returns nil for a
// non-combat NPC (a shopkeeper has no MoveSpeed), so the caller can simply
// skip it rather than special-casing kinds.
func newNPCAI(e *sim.Ent, arch defs.NPC) *npcAI {
	if arch.MoveSpeed <= 0 {
		return nil
	}
	post := e.Pos
	if st, ok := e.Data.(*sim.NPCState); ok {
		post = st.Post
	}
	return &npcAI{
		ent:   e,
		arch:  arch,
		brain: ai.Brain{Post: post},
		steer: ai.Steerer{
			Pos: e.Pos, Facing: forwardOf(e.Quat), Speed: arch.MoveSpeed,
			TurnRate: arch.TurnRate * math.Pi / 180,
		},
	}
}

// stepNPCs advances every combat NPC one tick: decide, move, attack.
//
// Order matters and mirrors the GDD: the brain picks a state and target from
// where things are NOW, then steering moves toward that target, then the
// attack machine resolves against the post-move distance. Attacking before
// moving would let an NPC hit from where it used to be.
func (s *Server) stepNPCs(tick uint32) {
	if len(s.npcAI) == 0 {
		return
	}
	cands := s.npcCandidates()

	for _, n := range s.npcAI {
		if n.ent.Health <= 0 {
			n.clearLeg() // dying drops the leg
			s.dropNPCLoot(n)
			continue
		}
		if n.ent.Flags&protocol.FlagDead != 0 {
			n.clearLeg()
			n.dropped = false // respawning: arm the next death's roll
			continue          // the sim's respawn timer owns a dead NPC
		}
		n.dropped = false
		arch := n.arch
		self := n.ent.Pos
		selfUp := [3]float64(terrain.Normalize(terrain.Vec(self)))

		state := ai.StepBrain(&n.brain, ai.Archetype{
			AggroRadius:    arch.AggroRadius,
			LeashRadius:    arch.LeashRadius,
			AttackRange:    arch.AttackRange,
			AttackInterval: arch.AttackInterval,
			AttackWindup:   arch.AttackWindup,
		}, self, cands, s.losBetween, sim.DT)

		target, haveTarget := s.candidatePos(cands, n.brain.TargetID)

		// Move: toward the target while chasing, back to the post while
		// leashing, a wander leg in PATROL when the member wanders, still
		// otherwise. Full speed unless the wander leg slows it.
		n.steer.Speed = arch.MoveSpeed
		if state != ai.StatePatrol || n.wander <= 0 {
			n.clearLeg() // leaving PATROL for any reason drops the leg
		}
		switch {
		// Against everything solid -- structures, rocks, props, players,
		// rovers, ships, other NPCs -- not just the shipped walls.
		case (state == ai.StateAggro || state == ai.StateAttack) && haveTarget:
			n.stepSteer(target, s.terrain, s.collidersFor(n.ent.ID))
		case state == ai.StateLeash:
			n.stepSteer(n.brain.Post, s.terrain, s.collidersFor(n.ent.ID))
		case state == ai.StatePatrol && n.wander > 0:
			n.stepWander(s)
		}

		if !haveTarget {
			continue
		}
		dist := vecDist(n.ent.Pos, target)
		inRange := dist <= arch.AttackRange
		hasLOS := s.losBetween(eyeOf(n.ent.Pos, arch.EyeHeight()), eyeOf(target, eyeHeightMeters))

		mw := s.npcMelee(arch)
		if mw != nil && arch.ProjectileSpeed > 0 {
			// A shooter with a blade: the blade once the target is inside
			// its reach (plus a step), the gun otherwise. Each swap costs
			// npcSwapSeconds and restarts both attack machines.
			want := dist <= mw.Range+1.5
			if want != n.closeIn {
				n.closeIn = want
				n.swapTicks = ticksOf(npcSwapSeconds)
				n.melee, n.ranged = ai.MeleeState{}, ai.RangedState{}
				s.broadcast(equippedFrame(n.ent.ID, s.npcHeld(n.ent.ID, arch)))
			}
			if n.swapTicks > 0 {
				n.swapTicks--
				continue
			}
			if n.closeIn {
				if arch.MeleeDamage > 0 {
					mw.Damage = arch.MeleeDamage
				}
				before := n.melee.WindupTicks
				hit := ai.StepMelee(&n.melee, dist <= mw.Range+0.5 && hasLOS, mw.Damage,
					ticksOf(mw.Interval), ticksOf(arch.AttackWindup))
				if attackStarted(before, n.melee.WindupTicks, hit > 0) {
					s.broadcastAttack(n.ent.ID, n.brain.TargetID)
				}
				if hit > 0 {
					s.npcSwing(n, forwardOf(n.ent.Quat), *mw)
				}
				continue
			}
		}

		if arch.ProjectileSpeed > 0 {
			before := n.ranged.WindupTicks
			shot := ai.StepRanged(&n.ranged, inRange, hasLOS,
				n.ent.Pos, selfUp, target, s.candidateVel(cands, n.brain.TargetID),
				ai.Archetype{EyeHeight: arch.EyeHeight()}, arch.AttackDamage, arch.ProjectileSpeed,
				ticksOf(arch.AttackInterval), ticksOf(arch.AttackWindup))
			if attackStarted(before, n.ranged.WindupTicks, shot.Speed > 0) {
				s.broadcastAttack(n.ent.ID, n.brain.TargetID)
			}
			if shot.Speed > 0 {
				s.spawnProjectile(n.ent.ID, shot)
			}
			continue
		}

		before := n.melee.WindupTicks
		dmg := ai.StepMelee(&n.melee, inRange && hasLOS, arch.AttackDamage,
			ticksOf(arch.AttackInterval), ticksOf(arch.AttackWindup))
		if attackStarted(before, n.melee.WindupTicks, dmg > 0) {
			s.broadcastAttack(n.ent.ID, n.brain.TargetID)
		}
		if dmg > 0 && mw != nil {
			// A brawler with a weapon in hand hits everyone in its arc, with
			// its own numbers and at least its own reach.
			mw.Damage = dmg
			if mw.Range < arch.AttackRange {
				mw.Range = arch.AttackRange
			}
			s.npcSwing(n, forwardOf(n.ent.Quat), *mw)
		} else if dmg > 0 {
			s.damagePlayer(n.brain.TargetID, dmg, n.ent.ID)
		}
	}
}

// attackStarted: this tick began an attack -- a wind-up that was not running
// now is, or (no wind-up at all) one that landed straight away. A landing at
// the END of a wind-up is not a start: that attack was announced already.
func attackStarted(windupBefore, windupAfter int, landed bool) bool {
	return windupBefore == 0 && (windupAfter > 0 || landed)
}

// broadcastAttack tells every client an NPC has begun an attack, so its body
// can swing (the client plays the model's `attack` clip). Cosmetic: the
// damage still lands on its own schedule, through damagePlayer or a projectile.
func (s *Server) broadcastAttack(attacker, target uint32) {
	s.broadcast(protocol.EncodeEvent(protocol.Event{
		EntityID: attacker, EventID: protocol.EventAttack, Data: appendU32(nil, target),
	}))
}

// clearLeg forgets the wander leg and any pause: the next PATROL stint
// starts its cycle afresh.
func (n *npcAI) clearLeg() {
	n.legOn, n.hasPoint = false, false
	n.legTicks, n.pauseTicks = 0, 0
}

// startPause stands the member still for wander_pause before its next pick.
func (n *npcAI) startPause(rng *rand.Rand) {
	n.pauseTicks = ticksOf(wanderPauseMin + (wanderPauseMax-wanderPauseMin)*rng.Float64())
	n.ent.Vel = [3]float64{}
}

// stepWander advances one PATROL tick of a wandering member: pause, pick a
// point, walk to it at half speed, pause again (GDD "Wandering").
// Caller holds s.mu (s.rng).
func (n *npcAI) stepWander(s *Server) {
	if !n.legOn {
		n.legOn = true
		n.startPause(s.rng) // the first leg starts after a pause too
	}
	if n.pauseTicks > 0 {
		n.pauseTicks--
		return
	}
	if !n.hasPoint {
		p, ok := s.pickWanderPoint(n.brain.Post, n.wander)
		if !ok {
			n.startPause(s.rng) // 8 misses: skip the leg
			return
		}
		n.legPoint, n.hasPoint, n.legTicks = p, true, 0
	}
	n.steer.Speed = n.arch.MoveSpeed * wanderSpeed
	n.stepSteer(n.legPoint, s.terrain, s.collidersFor(n.ent.ID))
	n.legTicks++
	if vecDist(n.ent.Pos, n.legPoint) <= wanderArrive || n.legTicks >= ticksOf(wanderLeg) {
		n.hasPoint = false
		n.startPause(s.rng)
	}
}

// pickWanderPoint rolls a point uniform in the disc of radius r around post,
// in post's tangent plane, glued to the surface; re-rolled up to wanderTries
// times while the ground there is not Walkable. Caller holds s.mu.
func (s *Server) pickWanderPoint(post [3]float64, r float64) ([3]float64, bool) {
	up := terrain.Normalize(terrain.Vec(post))
	k := terrain.Vec{0, 1, 0}
	if math.Abs(up.Dot(k)) > 0.9 {
		k = terrain.Vec{1, 0, 0}
	}
	east := terrain.Normalize(terrain.Cross(k, up))
	north := terrain.Cross(up, east)
	for i := 0; i < wanderTries; i++ {
		d := r * math.Sqrt(s.rng.Float64())
		th := 2 * math.Pi * s.rng.Float64()
		p := terrain.Vec(post).Add(east.Scale(d * math.Cos(th))).Add(north.Scale(d * math.Sin(th)))
		dir := terrain.Normalize(p)
		if !s.terrain.Walkable(dir) {
			continue
		}
		return [3]float64(dir.Scale(s.terrain.SampleRadius(dir))), true
	}
	return [3]float64{}, false
}

// stepSteer moves the NPC one tick and writes the result back onto the entity.
func (n *npcAI) stepSteer(target [3]float64, t *terrain.Field, cs []protocol.Collider) {
	n.steer.Pos = n.ent.Pos
	ai.Step(&n.steer, target, sim.DT, t, cs)
	n.ent.Pos = n.steer.Pos
	n.ent.Vel = n.steer.Vel
	n.ent.Quat = quatFromForward(n.steer.Facing, n.ent.Pos)
}

// eyeOf raises a body's feet to its eye, h metres along its up.
func eyeOf(pos [3]float64, h float64) [3]float64 {
	up := terrain.Normalize(terrain.Vec(pos))
	return [3]float64{pos[0] + up[0]*h, pos[1] + up[1]*h, pos[2] + up[2]*h}
}

func vecDist(a, b [3]float64) float64 {
	return math.Sqrt((a[0]-b[0])*(a[0]-b[0]) + (a[1]-b[1])*(a[1]-b[1]) + (a[2]-b[2])*(a[2]-b[2]))
}

// npcCandidates lists the living players an NPC may target.
//
// Built once per tick and shared by every NPC: with 30 NPCs and 10 players
// this is 30 walks over a 10-element slice instead of 300 map lookups, and it
// fixes the candidate set for the whole tick so two NPCs cannot disagree about
// where a player was.
func (s *Server) npcCandidates() []ai.Candidate {
	out := make([]ai.Candidate, 0, len(s.clients))
	for _, c := range s.clients {
		out = append(out, ai.Candidate{
			ID:    c.entity.ID,
			Pos:   [3]float64(c.entity.State.Pos),
			Alive: c.vitals.Health > 0,
		})
	}
	return out
}

func (s *Server) candidatePos(cands []ai.Candidate, id uint32) ([3]float64, bool) {
	for _, c := range cands {
		if c.ID == id {
			return c.Pos, true
		}
	}
	return [3]float64{}, false
}

func (s *Server) candidateVel(cands []ai.Candidate, id uint32) [3]float64 {
	if c, ok := s.clients[id]; ok {
		return [3]float64(c.entity.State.Vel)
	}
	return [3]float64{}
}

// losBetween reports line of sight between two world points.
//
// Static colliders only, never the terrain: at these ranges the horizon does
// not occlude, and marching the radius field along a ray costs far more than
// the handful of box tests it would replace (GDD, "AI state machine").
func (s *Server) losBetween(from, to [3]float64) bool {
	dir := [3]float64{to[0] - from[0], to[1] - from[1], to[2] - from[2]}
	length := math.Sqrt(dir[0]*dir[0] + dir[1]*dir[1] + dir[2]*dir[2])
	if length < 1e-9 {
		return true
	}
	for i := range dir {
		dir[i] /= length
	}
	return !sim.SegmentHitsColliders(from, dir, length, s.colliders)
}

// spawnProjectile puts a gunner's shot into the world.
func (s *Server) spawnProjectile(owner uint32, shot ai.Shot) {
	s.worldID++
	e := &sim.Ent{
		ID:   s.worldID,
		Kind: sim.EntityKind(protocol.EntityTypeProjectile),
		Pos:  shot.Origin,
		Vel:  [3]float64{shot.Dir[0] * shot.Speed, shot.Dir[1] * shot.Speed, shot.Dir[2] * shot.Speed},
		Data: &sim.ProjectileState{
			Owner:     owner,
			Damage:    shot.Damage,
			Speed:     shot.Speed,
			LifeTicks: sim.LifeTicksForRange(60, shot.Speed),
		},
	}
	// Only add to the world. syncWorldEnts folds it into the snapshot cache
	// and announces the spawn — doing it here as well would announce nothing
	// and hide the entity from the diff.
	s.world.Add(e)
	s.fresh = append(s.fresh, e)
}

// forwardOf extracts the forward (local +Z, the Sim's facing) axis from an
// orientation quat.
func forwardOf(q [4]float64) [3]float64 {
	x, y, z, w := q[0], q[1], q[2], q[3]
	return [3]float64{
		2 * (x*z + w*y),
		2 * (y*z - w*x),
		1 - 2*(x*x+y*y),
	}
}

// quatFromForward rebuilds an orientation from a facing direction and the
// entity's own radial up — the same convention the player body uses, so a
// remote NPC renders upright on its own patch of ground rather than on the
// viewer's.
func quatFromForward(facing [3]float64, pos [3]float64) [4]float64 {
	up := terrain.Normalize(terrain.Vec(pos))
	f := terrain.Normalize(terrain.Vec(facing))
	right := terrain.Normalize(terrain.Cross(up, f)) // as State.OrientationQuat: (f, up) mirrored the basis and turned every NPC to face away
	return [4]float64(sim.QuatFromBasis(right, [3]float64(up), f))
}

// damagePlayer applies NPC damage to a player and broadcasts the outcome.
//
// Damage is applied HERE and nowhere else so invulnerability, the regen delay
// and the death timer all live behind one door. A second damage path is how
// one of them gets forgotten.
func (s *Server) damagePlayer(victimID uint32, amount int, attacker uint32) {
	c, ok := s.clients[victimID]
	if !ok {
		return
	}
	snap := c.ident.Snapshot()
	applied, died := sim.Damage(&c.vitals, sim.Mitigate(amount, sim.ArmorOf(s.reg, &snap)))
	if applied == 0 {
		return // invulnerable, already dead, or nothing to do — no event
	}
	c.entity.Health = c.vitals.Health

	hitData := make([]byte, 0, 20)
	hitData = appendU32(hitData, attacker)
	for i := 0; i < 3; i++ {
		hitData = appendF32(hitData, float32(c.entity.State.Pos[i]))
	}
	hitData = appendU16(hitData, uint16(applied))
	hitData = appendU16(hitData, healthU16(c.vitals.Health))
	s.broadcast(protocol.EncodeEvent(protocol.Event{
		EntityID: victimID, EventID: protocol.EventHit, Data: hitData,
	}))

	if died {
		s.broadcast(protocol.EncodeEvent(protocol.Event{
			EntityID: victimID, EventID: protocol.EventDeath,
			Data: appendU32(nil, attacker),
		}))
		// The spill needs the identity, which never nests under s.mu:
		// queue it for tick() to drain after the lock drops.
		s.pendingSpills = append(s.pendingSpills, spill{c: c, pos: c.entity.State.Pos})
		s.cancelGather(c, "died")
	} else {
		s.cancelGather(c, "hit")
	}
}

// stepPlayerVitals advances every player's death timer and regeneration, and
// returns them to the spawn point on the tick they respawn.
func (s *Server) stepPlayerVitals() {
	for _, c := range s.clients {
		if respawned := sim.StepVitals(&c.vitals, sim.DT); respawned {
			c.entity.State = s.clearSpawn(c.entity.ID)
			c.entity.Health = c.vitals.Health
		}
		c.entity.Health = c.vitals.Health
	}
}

func (s *Server) broadcast(frame []byte) {
	for _, c := range s.clients {
		c.send(msg{data: frame})
	}
}

// dropNPCLoot rolls a dead NPC's table once, at the spot it fell.
//
// Loot is rolled HERE rather than inside the sim's NPC step because the roll
// needs the registry, the id counter and the server's RNG — all server-owned.
// Keeping the RNG on the server side also keeps drops unpredictable to a
// client that knows the world seed.
func (s *Server) dropNPCLoot(n *npcAI) {
	if n.dropped || n.arch.Loot == "" {
		return
	}
	n.dropped = true
	sim.DropLoot(s.world, s.reg, n.arch.Loot, n.ent.Pos, s.nextWorldID, s.rng,
		sim.StepCtx{World: s.world, Events: &s.pendingEvents}, n.lootExtra)
	// Newly created drops have to reach worldEnts too, or they are simulated
	// but never appear in a snapshot — the same shape as the Phase 2 bug where
	// NPCs existed server-side and no client could see them.
	s.syncWorldEnts()
}

// nextWorldID hands out ids for runtime-spawned entities, continuing the
// zone-placed range so player ids are never touched.
func (s *Server) nextWorldID() uint32 {
	s.worldID++
	return s.worldID
}

// syncWorldEnts rebuilds the stable-order snapshot cache and announces the
// difference.
//
// Snapshot rows carry no entity_type — the client learns what an entity IS from
// its `spawn` frame (docs/PROTOCOL.md). Those were only sent at join for
// entities that already existed, so a projectile or loot drop created at
// runtime arrived as a row of an unknown kind and the client had no renderer to
// pick: gunners landed hits while nothing was ever drawn. Announce additions
// and removals as they happen.
//
// No "same count, nothing changed" shortcut: an add and a remove in one tick
// (a loot drop as a round dies) keep the count and hid both.
func (s *Server) syncWorldEnts() {
	order := s.world.Order()
	had := make(map[uint32]bool, len(s.worldEnts))
	for _, e := range s.worldEnts {
		had[e.ID] = true
	}
	ents := make([]*sim.Ent, 0, len(order))
	live := make(map[uint32]bool, len(order))
	for _, id := range order {
		e := s.world.Ents[id]
		if e == nil {
			continue
		}
		live[id] = true
		ents = append(ents, e)
		if !had[id] {
			s.broadcast(protocol.EncodeSpawn(protocol.Spawn{
				EntityID:   e.ID,
				EntityType: uint16(e.Kind),
				Data:       []byte(e.Def),
			}))
		}
	}
	for _, e := range s.worldEnts {
		if !live[e.ID] {
			s.broadcast(protocol.EncodeDespawn(protocol.Despawn{EntityID: e.ID}))
			s.history.Forget(e.ID)
		}
	}
	// A round fired point-blank hits in the tick it was fired and is gone
	// before this runs. It still existed: spawn and despawn it, so a client
	// learns the shot happened (the hit event alone names no projectile).
	for _, e := range s.fresh {
		if !live[e.ID] {
			s.broadcast(protocol.EncodeSpawn(protocol.Spawn{EntityID: e.ID, EntityType: uint16(e.Kind), Data: []byte(e.Def)}))
			s.broadcast(protocol.EncodeDespawn(protocol.Despawn{EntityID: e.ID}))
		}
	}
	s.fresh = s.fresh[:0]
	s.worldEnts = ents
}
