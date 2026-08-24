package server

import (
	"math"

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
	ent    *sim.Ent
	arch   defs.NPC
	brain  ai.Brain
	steer  ai.Steerer
	melee  ai.MeleeState
	ranged ai.RangedState
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
		if n.ent.Flags&protocol.FlagDead != 0 {
			continue // the sim's respawn timer owns a dead NPC
		}
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
		// leashing, still otherwise.
		switch {
		case (state == ai.StateAggro || state == ai.StateAttack) && haveTarget:
			n.stepSteer(target, s.terrain, s.colliders)
		case state == ai.StateLeash:
			n.stepSteer(n.brain.Post, s.terrain, s.colliders)
		}

		if !haveTarget {
			continue
		}
		dist := vecDist(n.ent.Pos, target)
		inRange := dist <= arch.AttackRange
		hasLOS := s.losBetween(eyeOf(n.ent.Pos), eyeOf(target))

		if arch.ProjectileSpeed > 0 {
			shot := ai.StepRanged(&n.ranged, inRange, hasLOS,
				n.ent.Pos, selfUp, target, s.candidateVel(cands, n.brain.TargetID),
				ai.Archetype{}, arch.AttackDamage, arch.ProjectileSpeed,
				ticksOf(arch.AttackInterval), ticksOf(arch.AttackWindup))
			if shot.Speed > 0 {
				s.spawnProjectile(n.ent.ID, shot)
			}
			continue
		}

		if dmg := ai.StepMelee(&n.melee, inRange && hasLOS, arch.AttackDamage,
			ticksOf(arch.AttackInterval), ticksOf(arch.AttackWindup)); dmg > 0 {
			s.damagePlayer(n.brain.TargetID, dmg, n.ent.ID)
		}
	}
}

// stepSteer moves the NPC one tick and writes the result back onto the entity.
func (n *npcAI) stepSteer(target [3]float64, t *terrain.Field, cs []protocol.Collider) {
	n.steer.Pos = n.ent.Pos
	ai.Step(&n.steer, target, sim.DT, t, cs)
	n.ent.Pos = n.steer.Pos
	n.ent.Vel = n.steer.Vel
	n.ent.Quat = quatFromForward(n.steer.Facing, n.ent.Pos)
}

func eyeOf(pos [3]float64) [3]float64 {
	up := terrain.Normalize(terrain.Vec(pos))
	return [3]float64{pos[0] + up[0]*eyeHeightMeters, pos[1] + up[1]*eyeHeightMeters, pos[2] + up[2]*eyeHeightMeters}
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
	s.world.Add(e)
	s.worldEnts = append(s.worldEnts, e)
}

// forwardOf extracts the forward (local -Z) axis from an orientation quat.
func forwardOf(q [4]float64) [3]float64 {
	x, y, z, w := q[0], q[1], q[2], q[3]
	return [3]float64{
		-(2 * (x*z + w*y)),
		-(2 * (y*z - w*x)),
		-(1 - 2*(x*x+y*y)),
	}
}

// quatFromForward rebuilds an orientation from a facing direction and the
// entity's own radial up — the same convention the player body uses, so a
// remote NPC renders upright on its own patch of ground rather than on the
// viewer's.
func quatFromForward(facing [3]float64, pos [3]float64) [4]float64 {
	up := terrain.Normalize(terrain.Vec(pos))
	f := terrain.Normalize(terrain.Vec(facing))
	right := terrain.Normalize(terrain.Cross(f, up))
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
	applied, died := sim.Damage(&c.vitals, amount)
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
	}
}

// stepPlayerVitals advances every player's death timer and regeneration, and
// returns them to the spawn point on the tick they respawn.
func (s *Server) stepPlayerVitals() {
	for _, c := range s.clients {
		if respawned := sim.StepVitals(&c.vitals, sim.DT); respawned {
			spawn := sim.SpawnState(s.terrain)
			c.entity.State = spawn
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
