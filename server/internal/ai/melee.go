// Package ai implements NPC behavior for Phase 3 combat (docs/GDD.md
// "Phase 3 — NPC combat at an encampment").
package ai

// MeleeState is one attacker's swing timer.
type MeleeState struct {
	CooldownTicks int // ticks until the next swing may START
	WindupTicks   int // ticks left in the current swing's telegraph; 0 = not swinging
}

// StepMelee advances one melee attacker by a tick. It returns the damage to
// apply this tick (0 for none) — it does NOT apply damage itself, so the
// caller owns the world and this stays unit-testable.
//
// A swing starts only when CooldownTicks == 0 and inRange. Starting a swing
// commits CooldownTicks to attackIntervalTicks immediately, so successive
// swings are spaced exactly attackIntervalTicks apart under continuous
// contact, independent of how long the windup takes.
//
// Damage lands windupTicks after the swing starts, not on the tick it
// starts — the windup is the player's frame to react in. inRange is
// re-evaluated at the moment damage lands, not when the swing started: if
// the target left melee range during the windup, the swing misses (0
// damage), but the cooldown still applies — a miss costs the same cadence
// as a hit.
func StepMelee(m *MeleeState, inRange bool, damage int, attackIntervalTicks, windupTicks int) int {
	if m.CooldownTicks > 0 {
		m.CooldownTicks--
	}

	if m.WindupTicks > 0 {
		m.WindupTicks--
		if m.WindupTicks == 0 && inRange {
			return damage
		}
		return 0
	}

	if m.CooldownTicks == 0 && inRange {
		m.CooldownTicks = attackIntervalTicks
		if windupTicks <= 0 {
			// No telegraph: resolve immediately.
			return damage
		}
		m.WindupTicks = windupTicks
	}
	return 0
}
