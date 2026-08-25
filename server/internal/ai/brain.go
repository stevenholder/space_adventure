// Package ai implements the NPC decision layer: the state machine, target
// selection, and line-of-sight gated transitions described in docs/GDD.md,
// "Phase 3 — NPC combat at an encampment" -> "AI state machine (binding —
// implement this table, not the concept)".
//
// This file decides state only. It does not move the NPC (see steer.go, a
// sibling file in this package) and does not apply damage (melee and
// projectiles are separate systems).
package ai

import "math"

// State is one node of the NPC state machine (GDD "AI state machine").
type State uint8

const (
	StateIdle State = iota
	StatePatrol
	StateAggro
	StateAttack
	StateLeash
	StateDead
)

// Constants from the GDD "AI state machine" param table.
const (
	// idleDwell is how long an NPC stays IDLE before it may move to PATROL
	// (IDLE -> PATROL row).
	idleDwell = 3.0 // s

	// retargetInterval bounds how often target selection re-runs. Re-running
	// every tick makes an NPC oscillate between two equidistant players and
	// never reach either (GDD "Target selection").
	retargetInterval = 0.5 // s

	// postArrive is the distance from its post at which a LEASH-ing NPC
	// is considered "back" and returns to IDLE (LEASH -> IDLE row).
	postArrive = 1.5 // m

	// npcRespawn is how long a dead NPC stays dead before returning to IDLE,
	// at full health, at its post (DEAD -> IDLE row).
	npcRespawn = 20.0 // s

	// attackRangeHysteresis scales attack_range for the ATTACK -> AGGRO
	// transition. Using attack_range itself would let an enemy sitting at
	// exactly that distance flip state every tick (GDD "The ATTACK->AGGRO
	// threshold is attack_range · 1.15, not attack_range").
	attackRangeHysteresis = 1.15

	// loseTargetSecs is how long an engaged NPC tolerates having no valid
	// target before going home (GDD "AI state machine").
	//
	// Leash keyed only to distance-from-post leaves an enemy that chased a
	// short way and then lost you standing in the open forever: inside its
	// leash radius so it never disengages, and with no target so it never
	// moves. Measured at 17.9 m from post, motionless for 60 s. The grace
	// period stops it snapping home the instant a target ducks behind cover.
	loseTargetSecs = 2.0
)

// Candidate is one player the brain may target.
type Candidate struct {
	ID    uint32
	Pos   [3]float64
	Alive bool
}

// Archetype holds the per-NPC-type tuning values consulted by Step (GDD "NPC
// archetypes" table, the subset this package needs).
type Archetype struct {
	AggroRadius, LeashRadius, AttackRange float64
	AttackInterval, AttackWindup          float64
}

// Brain is one NPC's decision state.
type Brain struct {
	State        State
	Post         [3]float64 // where it stands guard; leash is measured from HERE
	TargetID     uint32
	StateTicks   int
	RetargetTick int
	AttackTick   int
	// LostTicks counts how long the brain has had no valid target while
	// engaged. See the loseTarget rule below.
	LostTicks int
}

func distBrain(a, b [3]float64) float64 {
	dx := a[0] - b[0]
	dy := a[1] - b[1]
	dz := a[2] - b[2]
	return math.Sqrt(dx*dx + dy*dy + dz*dz)
}

// selectTarget returns the closest living candidate within radius of self
// that also has line of sight, or 0 if none qualifies (GDD "Target
// selection").
func selectTarget(self [3]float64, cands []Candidate, radius float64, losFn func(from, to [3]float64) bool) uint32 {
	var bestID uint32
	bestDist := radius
	found := false
	for _, c := range cands {
		if !c.Alive {
			continue
		}
		d := distBrain(self, c.Pos)
		if d > radius {
			continue
		}
		if !losFn(self, c.Pos) {
			continue
		}
		if !found || d < bestDist {
			found = true
			bestDist = d
			bestID = c.ID
		}
	}
	if !found {
		return 0
	}
	return bestID
}

func findCandidate(cands []Candidate, id uint32) (Candidate, bool) {
	for _, c := range cands {
		if c.ID == id {
			return c, true
		}
	}
	return Candidate{}, false
}

// StepBrain advances the brain one tick and returns the state it is now in.
// losFn reports line of sight between two world points. Step decides state
// only: it does not move the NPC and does not apply damage.
//
// Named StepBrain, not Step: steer.go owns movement in this same package and
// had its own Step. Two file-disjoint briefs can still collide in the
// NAMESPACE, which is the axis a per-file split does not cover.
func StepBrain(b *Brain, a Archetype, self [3]float64, cands []Candidate,
	losFn func(from, to [3]float64) bool, dt float64) State {

	prevState := b.State
	b.StateTicks++
	b.RetargetTick++
	b.AttackTick++

	switch b.State {
	case StateIdle:
		// IDLE -> AGGRO: a living player within aggro_radius and in LOS.
		if tid := selectTarget(self, cands, a.AggroRadius, losFn); tid != 0 {
			b.TargetID = tid
			b.State = StateAggro
			break
		}
		// IDLE -> PATROL: idle_dwell elapsed. No patrol-route concept exists
		// in this brief's contract, so a route is assumed available whenever
		// the dwell has elapsed; PATROL itself is otherwise inert here.
		if secondsIn(b.StateTicks, dt) >= idleDwell {
			b.State = StatePatrol
		}

	case StatePatrol:
		// IDLE/PATROL -> AGGRO
		if tid := selectTarget(self, cands, a.AggroRadius, losFn); tid != 0 {
			b.TargetID = tid
			b.State = StateAggro
		}

	case StateAggro:
		if retargetDue(b, dt) {
			b.TargetID = selectTarget(self, cands, a.AggroRadius, losFn)
		}
		if tgt, ok := findCandidate(cands, b.TargetID); b.TargetID != 0 && ok && tgt.Alive {
			d := distBrain(self, tgt.Pos)
			// AGGRO -> ATTACK: target within attack_range and in LOS.
			if d <= a.AttackRange && losFn(self, tgt.Pos) {
				b.State = StateAttack
			}
			b.LostTicks = 0
		} else {
			b.TargetID = 0
			b.LostTicks++
		}
		// AGGRO/ATTACK -> LEASH: too far from POST, or the target is lost.
		if distBrain(self, b.Post) > a.LeashRadius || lostTooLong(b, dt) {
			b.State = StateLeash
			b.LostTicks = 0
		}

	case StateAttack:
		if retargetDue(b, dt) {
			b.TargetID = selectTarget(self, cands, a.AggroRadius, losFn)
		}
		if tgt, ok := findCandidate(cands, b.TargetID); ok && tgt.Alive {
			d := distBrain(self, tgt.Pos)
			losOK := losFn(self, tgt.Pos)
			// ATTACK -> AGGRO: outside attack_range * hysteresis or LOS lost.
			if d > a.AttackRange*attackRangeHysteresis || !losOK {
				b.State = StateAggro
			}
			b.LostTicks = 0
		} else {
			b.TargetID = 0
			b.State = StateAggro
			b.LostTicks++
		}
		// AGGRO/ATTACK -> LEASH
		if distBrain(self, b.Post) > a.LeashRadius || lostTooLong(b, dt) {
			b.State = StateLeash
			b.LostTicks = 0
		}

	case StateLeash:
		// LEASH -> IDLE: back within post_arrive of its post.
		if distBrain(self, b.Post) <= postArrive {
			b.State = StateIdle
			b.TargetID = 0
		}

	case StateDead:
		// DEAD -> IDLE: npc_respawn elapsed, at full health, at its post.
		if secondsIn(b.StateTicks, dt) >= npcRespawn {
			b.State = StateIdle
			b.TargetID = 0
		}
	}

	if b.State != prevState {
		b.StateTicks = 0
	}
	return b.State
}

// Kill transitions the brain to DEAD from any state (GDD "any -> DEAD when
// health reaches 0"). Health tracking itself lives outside this package;
// callers invoke Kill when they observe health hit 0.
func Kill(b *Brain) {
	if b.State != StateDead {
		b.State = StateDead
		b.StateTicks = 0
		b.TargetID = 0
	}
}

func retargetDue(b *Brain, dt float64) bool {
	if secondsIn(b.RetargetTick, dt) >= retargetInterval {
		b.RetargetTick = 0
		return true
	}
	return false
}

func secondsIn(ticks int, dt float64) float64 {
	return float64(ticks) * dt
}

// lostTooLong reports whether the brain has been without a valid target for
// longer than the grace period.
func lostTooLong(b *Brain, dt float64) bool {
	if dt <= 0 {
		return false
	}
	return float64(b.LostTicks)*dt >= loseTargetSecs
}
