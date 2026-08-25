package ai

import "testing"

// grunt is the melee archetype from the GDD "NPC archetypes" table, used
// throughout as a representative Archetype.
var grunt = Archetype{
	AggroRadius:    22,
	LeashRadius:    45,
	AttackRange:    2.0,
	AttackInterval: 1.2,
	AttackWindup:   0.35,
}

func alwaysLOS(from, to [3]float64) bool { return true }

func TestIdleToAggroOnAggroRadius(t *testing.T) {
	b := &Brain{State: StateIdle, Post: [3]float64{0, 0, 0}}
	self := [3]float64{0, 0, 0}
	cands := []Candidate{{ID: 1, Pos: [3]float64{10, 0, 0}, Alive: true}}

	got := StepBrain(b, grunt, self, cands, alwaysLOS, 0.1)

	if got != StateAggro {
		t.Fatalf("state = %v, want StateAggro", got)
	}
	if b.TargetID != 1 {
		t.Fatalf("TargetID = %v, want 1", b.TargetID)
	}
}

func TestAggroToAttackWithinAttackRange(t *testing.T) {
	self := [3]float64{0, 0, 0}
	b := &Brain{State: StateAggro, Post: self, TargetID: 1}
	cands := []Candidate{{ID: 1, Pos: [3]float64{1.5, 0, 0}, Alive: true}}

	got := StepBrain(b, grunt, self, cands, alwaysLOS, 0.1)

	if got != StateAttack {
		t.Fatalf("state = %v, want StateAttack", got)
	}
}

// TestAttackHysteresisHoldsAtThreshold verifies the ATTACK -> AGGRO edge
// uses attack_range * 1.15, not attack_range: a target backed off to just
// inside that scaled threshold must NOT flip the NPC out of ATTACK.
func TestAttackHysteresisHoldsAtThreshold(t *testing.T) {
	self := [3]float64{0, 0, 0}
	b := &Brain{State: StateAttack, Post: self, TargetID: 1}
	justInside := grunt.AttackRange*attackRangeHysteresis - 0.01
	cands := []Candidate{{ID: 1, Pos: [3]float64{justInside, 0, 0}, Alive: true}}

	got := StepBrain(b, grunt, self, cands, alwaysLOS, 0.1)

	if got != StateAttack {
		t.Fatalf("state = %v, want StateAttack (must stay, not twitch)", got)
	}
}

func TestAttackToAggroPastHysteresisThreshold(t *testing.T) {
	self := [3]float64{0, 0, 0}
	b := &Brain{State: StateAttack, Post: self, TargetID: 1}
	justOutside := grunt.AttackRange*attackRangeHysteresis + 0.01
	cands := []Candidate{{ID: 1, Pos: [3]float64{justOutside, 0, 0}, Alive: true}}

	got := StepBrain(b, grunt, self, cands, alwaysLOS, 0.1)

	if got != StateAggro {
		t.Fatalf("state = %v, want StateAggro", got)
	}
}

// TestLeashIsFromPostNotPlayer verifies leash distance is measured from the
// NPC's post, not from the target: here the target sits right next to the
// NPC (which would never leash a player-keyed check), but the NPC itself is
// far from its post, past leash_radius, so it must leash anyway.
func TestLeashIsFromPostNotPlayer(t *testing.T) {
	post := [3]float64{0, 0, 0}
	self := [3]float64{50, 0, 0} // 50m from post > 45m leash_radius
	b := &Brain{State: StateAggro, Post: post, TargetID: 1}
	cands := []Candidate{{ID: 1, Pos: self, Alive: true}} // target adjacent to self

	got := StepBrain(b, grunt, self, cands, alwaysLOS, 0.1)

	if got != StateLeash {
		t.Fatalf("state = %v, want StateLeash", got)
	}
}

func TestLeashReturnsToIdleAtPostArrive(t *testing.T) {
	post := [3]float64{0, 0, 0}
	self := [3]float64{1.0, 0, 0} // within post_arrive (1.5m)
	b := &Brain{State: StateLeash, Post: post}

	got := StepBrain(b, grunt, self, nil, alwaysLOS, 0.1)

	if got != StateIdle {
		t.Fatalf("state = %v, want StateIdle", got)
	}
}

func TestDeadRespawnsAfterNpcRespawn(t *testing.T) {
	post := [3]float64{0, 0, 0}
	b := &Brain{State: StateDead, Post: post}
	const dt = 1.0 // fixed tick length, as the ticks-times-dt fields assume

	var got State
	for i := 0; i < int(npcRespawn)-1; i++ {
		got = StepBrain(b, grunt, post, nil, alwaysLOS, dt)
		if got != StateDead {
			t.Fatalf("tick %d: state = %v before npc_respawn elapsed, want StateDead", i, got)
		}
	}

	got = StepBrain(b, grunt, post, nil, alwaysLOS, dt) // this tick reaches npc_respawn
	if got != StateIdle {
		t.Fatalf("state = %v after npc_respawn elapsed, want StateIdle", got)
	}
}

// TestRetargetNotEveryTick verifies target selection only re-runs every
// retarget_interval: with two equidistant candidates whose relative order
// flips each tick, the chosen target must not change on every tick.
func TestRetargetNotEveryTick(t *testing.T) {
	self := [3]float64{0, 0, 0}
	post := self
	c1 := Candidate{ID: 1, Pos: [3]float64{10, 0, 0}, Alive: true}
	c2 := Candidate{ID: 2, Pos: [3]float64{0, 10, 0}, Alive: true} // same distance as c1

	b := &Brain{State: StateAggro, Post: post, TargetID: 1}
	dt := 0.1 // retarget_interval is 0.5s, so 3 ticks stay well under it

	for i := 0; i < 3; i++ {
		var cands []Candidate
		if i%2 == 0 {
			cands = []Candidate{c1, c2}
		} else {
			cands = []Candidate{c2, c1}
		}
		StepBrain(b, grunt, self, cands, alwaysLOS, dt)
		if b.TargetID != 1 {
			t.Fatalf("tick %d: TargetID = %v, want 1 (must not retarget every tick)", i, b.TargetID)
		}
	}
}

// TestLosingTheTargetSendsItHome: an engaged NPC that loses its target must go
// home, even while still inside its leash radius.
//
// Leash keyed only to distance-from-post is not enough. An NPC that chased a
// short way and then lost the player is inside its leash radius, so it never
// disengages, and has no target, so it never moves — measured standing 17.9 m
// from its post for 60 s before this rule existed.
func TestLosingTheTargetSendsItHome(t *testing.T) {
	b := &Brain{Post: [3]float64{0, 0, 0}}
	a := Archetype{AggroRadius: 20, LeashRadius: 45, AttackRange: 2}
	los := func(_, _ [3]float64) bool { return true }
	self := [3]float64{10, 0, 0} // 10 m out: well inside leash

	// Engage.
	if got := StepBrain(b, a, self, []Candidate{{ID: 7, Pos: [3]float64{12, 0, 0}, Alive: true}}, los, 0.05); got != StateAggro {
		t.Fatalf("state = %v, want AGGRO once a player is in range", got)
	}

	// Target vanishes. It must not sit there indefinitely.
	var last State
	for i := 0; i < int(loseTargetSecs/0.05)+2; i++ {
		last = StepBrain(b, a, self, nil, los, 0.05)
	}
	if last != StateLeash {
		t.Errorf("state = %v after losing the target for %.0f s, want LEASH", last, loseTargetSecs)
	}

	// And the grace period is real: one tick without a target is not enough.
	b2 := &Brain{Post: [3]float64{0, 0, 0}}
	StepBrain(b2, a, self, []Candidate{{ID: 7, Pos: [3]float64{12, 0, 0}, Alive: true}}, los, 0.05)
	if got := StepBrain(b2, a, self, nil, los, 0.05); got == StateLeash {
		t.Error("leashed after a single tick without a target; the grace period is not applied")
	}
}
