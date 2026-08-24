package ai

import (
	"math"
	"testing"
)

// npc.gunner numbers from docs/GDD.md at 20 Hz: attack_interval 1.6s = 32
// ticks, attack_windup 0.25s = 5 ticks, attack_damage 8, projectile_speed 45.
const (
	gInterval  = 32
	gWindup    = 5
	gDmg       = 8
	gProjSpeed = 45.0
)

var (
	gSelf   = [3]float64{0, 0, 0}
	gSelfUp = [3]float64{0, 1, 0}
	gTarget = [3]float64{10, 0, 0}
	gNoVel  = [3]float64{0, 0, 0}
)

func fire(r *RangedState, inRange, hasLOS bool, targetVel [3]float64, windup int) Shot {
	return StepRanged(r, inRange, hasLOS, gSelf, gSelfUp, gTarget, targetVel, Archetype{}, gDmg, gProjSpeed, gInterval, windup)
}

// A shot resolves exactly windupTicks after it starts, not sooner.
func TestStepRanged_ShotResolvesAfterWindup(t *testing.T) {
	r := &RangedState{}
	for tick := 1; tick <= gWindup; tick++ {
		if got := fire(r, true, true, gNoVel, gWindup); got.Speed != 0 {
			t.Fatalf("tick %d: got shot %+v, want none (still winding up)", tick, got)
		}
	}
	got := fire(r, true, true, gNoVel, gWindup)
	if got.Speed == 0 || got.Damage != gDmg {
		t.Fatalf("tick %d (windup end): got %+v, want a resolved shot", gWindup+1, got)
	}
}

// Losing line of sight during the windup makes the shot never fire — the
// cover case: LOS is re-checked at the moment the shot resolves, not when
// it started.
func TestStepRanged_LosingLOSDuringWindupMisses(t *testing.T) {
	r := &RangedState{}
	if got := fire(r, true, true, gNoVel, gWindup); got.Speed != 0 {
		t.Fatalf("shot start: got %+v, want none", got)
	}
	for tick := 2; tick <= gWindup; tick++ {
		if got := fire(r, true, false, gNoVel, gWindup); got.Speed != 0 {
			t.Fatalf("tick %d: got %+v, want none", tick, got)
		}
	}
	if got := fire(r, true, false, gNoVel, gWindup); got.Speed != 0 {
		t.Fatalf("windup end behind cover: got %+v, want none", got)
	}
	if r.WindupTicks != 0 {
		t.Fatalf("windup should be over: got WindupTicks=%d", r.WindupTicks)
	}
}

// Leaving range during the windup makes the shot never fire.
func TestStepRanged_LeavingRangeDuringWindupMisses(t *testing.T) {
	r := &RangedState{}
	if got := fire(r, true, true, gNoVel, gWindup); got.Speed != 0 {
		t.Fatalf("shot start: got %+v, want none", got)
	}
	for tick := 2; tick <= gWindup; tick++ {
		if got := fire(r, false, true, gNoVel, gWindup); got.Speed != 0 {
			t.Fatalf("tick %d: got %+v, want none", tick, got)
		}
	}
	if got := fire(r, false, true, gNoVel, gWindup); got.Speed != 0 {
		t.Fatalf("windup end out of range: got %+v, want none", got)
	}
}

// Under continuous contact, shots land exactly attackIntervalTicks apart.
func TestStepRanged_CadenceMatchesAttackInterval(t *testing.T) {
	r := &RangedState{}
	const shots = 4
	var landings []int
	for tick := 1; tick <= gInterval*shots; tick++ {
		if got := fire(r, true, true, gNoVel, gWindup); got.Speed != 0 {
			landings = append(landings, tick)
		}
	}
	if len(landings) != shots {
		t.Fatalf("got %d landings, want %d: %v", len(landings), shots, landings)
	}
	for i := 1; i < len(landings); i++ {
		if gap := landings[i] - landings[i-1]; gap != gInterval {
			t.Fatalf("landing %d->%d gap = %d, want %d", i-1, i, gap, gInterval)
		}
	}
}

// Origin sits at the gunner's eye (self + up*1.7), not at its feet.
func TestStepRanged_OriginAtEyeHeight(t *testing.T) {
	r := &RangedState{}
	got := fire(r, true, true, gNoVel, 0) // windupTicks 0: resolves immediately
	if got.Speed == 0 {
		t.Fatalf("expected an immediate shot, got none")
	}
	want := [3]float64{gSelf[0], gSelf[1] + eyeHeight, gSelf[2]}
	if got.Origin != want {
		t.Fatalf("Origin = %v, want %v (eye height, not feet)", got.Origin, want)
	}
}

// Dir leads a moving target: a target moving perpendicular to the line of
// sight is aimed ahead of its current position, along its motion. A
// stationary target is aimed straight at it.
func TestStepRanged_LeadsMovingTarget(t *testing.T) {
	stationary := fire(&RangedState{}, true, true, gNoVel, 0)
	if stationary.Speed == 0 {
		t.Fatalf("expected an immediate shot, got none")
	}
	if math.Abs(stationary.Dir[2]) > 1e-9 {
		t.Fatalf("stationary target: Dir = %v, want zero Z (straight at it)", stationary.Dir)
	}

	targetVel := [3]float64{0, 0, 5} // perpendicular to the self->target line
	moving := fire(&RangedState{}, true, true, targetVel, 0)
	if moving.Speed == 0 {
		t.Fatalf("expected an immediate shot, got none")
	}
	if moving.Dir[2] <= 0 {
		t.Fatalf("moving target: Dir = %v, want positive Z (leads the motion)", moving.Dir)
	}

	// Exact lead math: t = dist/speed, aimPoint = targetPos + targetVel*t,
	// both origin and aim point raised to eye height.
	dist := math.Hypot(gTarget[0]-gSelf[0], gTarget[2]-gSelf[2])
	lead := dist / gProjSpeed
	want := norm([3]float64{
		gTarget[0] - gSelf[0],
		0,
		gTarget[2] + targetVel[2]*lead - gSelf[2],
	})
	for i := 0; i < 3; i++ {
		if math.Abs(moving.Dir[i]-want[i]) > 1e-9 {
			t.Fatalf("Dir = %v, want %v (lead math mismatch)", moving.Dir, want)
		}
	}
}

func norm(v [3]float64) [3]float64 {
	l := math.Sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2])
	return [3]float64{v[0] / l, v[1] / l, v[2] / l}
}
