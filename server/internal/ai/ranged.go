// Package ai implements NPC behavior for Phase 3 combat (docs/GDD.md
// "Phase 3 — NPC combat at an encampment").
package ai

import "space-adventure/server/internal/terrain"

// RangedState is one gunner's shot timer. Same shape as MeleeState by
// design: a ranged attack is the same windup/cadence machine as a melee
// swing, it just resolves to a projectile instead of direct damage.
type RangedState struct {
	CooldownTicks int // ticks until the next shot may START
	WindupTicks   int // ticks left in the current shot's telegraph; 0 = not winding up
}

// Shot is what a resolved ranged attack asks the caller to spawn. Zero
// Speed means "no shot this tick" — StepRanged does not spawn anything
// itself, the caller owns the world, which keeps this unit-testable.
type Shot struct {
	Origin, Dir [3]float64
	Speed       float64
	Damage      int
}

// eyeHeight is the GDD "First-person body" eye height. The shot's Origin
// sits here, not at the gunner's feet — a shot from the floor clips the
// ground immediately and looks like the gunner is shooting its own boots.
// Line of sight is likewise an eye-to-eye ray (GDD "Line of sight"), so the
// aim point uses the same offset.
const eyeHeight = terrain.EyeHeightMeters

// StepRanged advances one gunner by a tick. It returns the projectile to
// spawn this tick, or a zero Shot. Mirrors StepMelee's (melee.go)
// windup/cadence machine closely: a shot starts only when CooldownTicks ==
// 0, inRange and hasLOS. Starting a shot commits CooldownTicks to
// attackIntervalTicks immediately, so successive shots are spaced exactly
// attackIntervalTicks apart, independent of the windup.
//
// The shot RESOLVES windupTicks after it starts, not on the starting tick —
// the windup is the player's frame to react in. inRange and hasLOS are both
// re-evaluated at the moment the shot resolves, not when it started: if the
// target stepped behind cover or out of range during the windup, the shot
// never fires — otherwise cover does nothing against gunners and the windup
// is decoration. The cooldown still applies either way: a miss costs the
// same cadence as a hit.
func StepRanged(r *RangedState, inRange, hasLOS bool,
	self, selfUp, targetPos, targetVel [3]float64,
	a Archetype, damage int, projectileSpeed float64,
	attackIntervalTicks, windupTicks int) Shot {

	if r.CooldownTicks > 0 {
		r.CooldownTicks--
	}

	if r.WindupTicks > 0 {
		r.WindupTicks--
		if r.WindupTicks == 0 && inRange && hasLOS {
			return resolveShot(self, selfUp, targetPos, targetVel, damage, projectileSpeed)
		}
		return Shot{}
	}

	if r.CooldownTicks == 0 && inRange && hasLOS {
		r.CooldownTicks = attackIntervalTicks
		if windupTicks <= 0 {
			// No telegraph: resolve immediately.
			return resolveShot(self, selfUp, targetPos, targetVel, damage, projectileSpeed)
		}
		r.WindupTicks = windupTicks
	}
	return Shot{}
}

// resolveShot builds the projectile a resolved shot fires. It leads the
// target: aims at where the target will be after the projectile's travel
// time, not where it is now, using one iteration (t = distance /
// projectileSpeed, aimPoint = targetPos + targetVel*t) — good enough at
// gunner ranges without solving the quadratic.
func resolveShot(self, selfUp, targetPos, targetVel [3]float64, damage int, projectileSpeed float64) Shot {
	up := Vec(selfUp)
	origin := Vec(self).Add(up.Scale(eyeHeight))

	aimPoint := Vec(targetPos)
	if projectileSpeed > 0 {
		t := distBrain(self, targetPos) / projectileSpeed
		aimPoint = aimPoint.Add(Vec(targetVel).Scale(t))
	}
	aimEye := aimPoint.Add(up.Scale(eyeHeight))

	dir := aimEye.Sub(origin)
	if l := dir.Len(); l > degenEps {
		dir = dir.Scale(1 / l)
	} else {
		dir = Vec{}
	}

	return Shot{
		Origin: [3]float64(origin),
		Dir:    [3]float64(dir),
		Speed:  projectileSpeed,
		Damage: damage,
	}
}
