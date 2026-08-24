// Player damage, death, respawn, invulnerability and out-of-combat
// regeneration (docs/GDD.md "Phase 3 — NPC combat at an encampment" ->
// "Player death and respawn"). This file owns the Vitals state machine only
// — it never touches Ent.Pos/Vel/Flags. The caller (whatever steps the
// player entity) reads the return values and moves the body / sets
// protocol.FlagDead / zeroes velocity itself.
package sim

const (
	// PlayerMaxHealth is player_max_health from the GDD table.
	PlayerMaxHealth = 100

	// RespawnDelaySecs is respawn_delay: how long a dead player's body stays
	// down before respawn.
	RespawnDelaySecs = 5.0
	// RespawnDelayTicks is that duration in whole ticks. The countdown is
	// integer, not accumulated float seconds: the tick is fixed at 20 Hz, so
	// 5.0 s is exactly 100 ticks, and summing dt instead lands a hair off and
	// needs an epsilon to compare against 5.0. The sim feeds replay and the
	// Go/TS conformance diff, so an integer tick count — exact on both ends
	// by construction — is the only shape that belongs here.
	RespawnDelayTicks = int(RespawnDelaySecs * TickHz)

	// RespawnInvulnSecs is respawn_invuln: immunity granted on respawn.
	RespawnInvulnSecs = 3.0
	// RespawnInvulnTicks is that duration in whole ticks (60 at 20 Hz).
	RespawnInvulnTicks = int(RespawnInvulnSecs * TickHz)

	// HealthRegenDelaySecs is health_regen_delay: how long out of combat
	// before regeneration starts.
	HealthRegenDelaySecs = 8.0
	// HealthRegenDelayTicks is that duration in whole ticks (160 at 20 Hz).
	HealthRegenDelayTicks = int(HealthRegenDelaySecs * TickHz)

	// HealthRegenRate is health_regen_rate in hp/s.
	HealthRegenRate = 8
)

// Vitals is a player's health state, carried alongside their entity.
type Vitals struct {
	Health          int
	DeadTicks       int // ticks until respawn; 0 when alive
	InvulnTicks     int // ticks of post-respawn immunity remaining
	SinceDamageTick int // ticks since damage was last TAKEN
}

// Damage applies damage to a player, honouring invulnerability. Returns the
// damage actually applied and whether this killed them.
//
// Damage on an already-dead player, or during InvulnTicks, applies ZERO and
// leaves SinceDamageTick untouched — an invulnerable respawn that still
// restarts the regen clock accomplishes nothing, since the camp that killed
// you once can just keep hitting you.
func Damage(v *Vitals, amount int) (applied int, died bool) {
	if amount <= 0 {
		return 0, false
	}
	if v.DeadTicks > 0 || v.Health <= 0 {
		return 0, false
	}
	if v.InvulnTicks > 0 {
		return 0, false
	}

	applied = amount
	if applied > v.Health {
		applied = v.Health
	}
	v.Health -= applied
	v.SinceDamageTick = 0

	if v.Health <= 0 {
		v.Health = 0
		v.DeadTicks = RespawnDelayTicks
		died = true
	}
	return applied, died
}

// StepVitals advances one player's vitals by a tick, handling the respawn
// timer and out-of-combat regeneration. Returns true on the tick they
// respawn, so the caller can move them to the spawn point.
//
// Regeneration is derived from SinceDamageTick with integer floor-division,
// not from summing dt·rate onto Health: at 8 hp/s and 20 ticks/s a tick is
// worth 0.4 hp, and Health is an int, so the fractional part has to live
// somewhere durable across ticks. Deriving the cumulative amount from the
// tick count (floor(t·rate/TickHz)) and taking the difference from the
// previous tick gives exact, deterministic per-tick gains that sum to
// exactly rate·seconds over any span — the same on both the Go and TS
// conformance runs, with nothing to drift.
func StepVitals(v *Vitals, dt float64) (respawned bool) {
	if v.DeadTicks > 0 {
		v.DeadTicks--
		if v.DeadTicks == 0 {
			v.Health = PlayerMaxHealth
			v.InvulnTicks = RespawnInvulnTicks
			v.SinceDamageTick = 0
			return true
		}
		return false
	}

	if v.InvulnTicks > 0 {
		v.InvulnTicks--
	}

	v.SinceDamageTick++

	if v.Health > 0 && v.Health < PlayerMaxHealth && v.SinceDamageTick > HealthRegenDelayTicks {
		t := v.SinceDamageTick - HealthRegenDelayTicks
		cur := t * HealthRegenRate / TickHz
		prev := (t - 1) * HealthRegenRate / TickHz
		if gain := cur - prev; gain > 0 {
			v.Health += gain
			if v.Health > PlayerMaxHealth {
				v.Health = PlayerMaxHealth
			}
		}
	}

	return false
}
