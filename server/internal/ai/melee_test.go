package ai

import "testing"

// npc.grunt numbers from docs/GDD.md at 20 Hz: attack_interval 1.2s = 24
// ticks, attack_windup 0.35s = 7 ticks, attack_damage 12.
const (
	interval = 24
	windup   = 7
	dmg      = 12
)

// Damage lands exactly windupTicks after the swing starts, not sooner.
// Tick 1 starts the swing (no damage); the swing then needs windupTicks
// more ticks before damage lands, so the landing tick is windup+1.
func TestStepMelee_DamageLandsAfterWindup(t *testing.T) {
	m := &MeleeState{}
	for tick := 1; tick <= windup; tick++ {
		if got := StepMelee(m, true, dmg, interval, windup); got != 0 {
			t.Fatalf("tick %d: got damage %d, want 0 (still winding up)", tick, got)
		}
	}
	got := StepMelee(m, true, dmg, interval, windup)
	if got != dmg {
		t.Fatalf("tick %d (windup end): got damage %d, want %d", windup+1, got, dmg)
	}
}

// Leaving range during the windup makes the swing miss (0 damage), the
// dodge case — inRange is re-checked at the moment damage lands.
func TestStepMelee_LeavingRangeDuringWindupMisses(t *testing.T) {
	m := &MeleeState{}
	// Start the swing in range.
	if got := StepMelee(m, true, dmg, interval, windup); got != 0 {
		t.Fatalf("swing start: got damage %d, want 0", got)
	}
	// Leave range for the rest of the windup, including the landing tick.
	for tick := 2; tick <= windup; tick++ {
		if got := StepMelee(m, false, dmg, interval, windup); got != 0 {
			t.Fatalf("tick %d: got damage %d, want 0", tick, got)
		}
	}
	got := StepMelee(m, false, dmg, interval, windup)
	if got != 0 {
		t.Fatalf("windup end out of range: got damage %d, want 0 (miss)", got)
	}
	if m.WindupTicks != 0 {
		t.Fatalf("windup should be over: got WindupTicks=%d", m.WindupTicks)
	}
}

// Under continuous contact, damage events land exactly attackIntervalTicks
// apart.
func TestStepMelee_CadenceMatchesAttackInterval(t *testing.T) {
	m := &MeleeState{}
	const swings = 4
	var landings []int
	for tick := 1; tick <= interval*swings; tick++ {
		if got := StepMelee(m, true, dmg, interval, windup); got != 0 {
			if got != dmg {
				t.Fatalf("tick %d: got damage %d, want 0 or %d", tick, got, dmg)
			}
			landings = append(landings, tick)
		}
	}
	if len(landings) != swings {
		t.Fatalf("got %d landings, want %d: %v", len(landings), swings, landings)
	}
	for i := 1; i < len(landings); i++ {
		gap := landings[i] - landings[i-1]
		if gap != interval {
			t.Fatalf("landing %d->%d gap = %d, want %d", i-1, i, gap, interval)
		}
	}
}

// Out of range, a swing never starts and no damage is ever dealt.
func TestStepMelee_OutOfRangeNeverStarts(t *testing.T) {
	m := &MeleeState{}
	for tick := 1; tick <= interval*3; tick++ {
		if got := StepMelee(m, false, dmg, interval, windup); got != 0 {
			t.Fatalf("tick %d: got damage %d, want 0 (never in range)", tick, got)
		}
	}
	if m.WindupTicks != 0 || m.CooldownTicks != 0 {
		t.Fatalf("state should stay idle: %+v", m)
	}
}
