package sim

import "testing"

// step calls StepVitals n times with the fixed sim tick, returning whether
// any of those calls reported a respawn.
func step(v *Vitals, n int) (respawned bool) {
	for i := 0; i < n; i++ {
		if StepVitals(v, DT) {
			respawned = true
		}
	}
	return respawned
}

func TestDamageKills(t *testing.T) {
	v := &Vitals{Health: PlayerMaxHealth}
	applied, died := Damage(v, 100)
	if applied != 100 {
		t.Fatalf("applied = %d, want 100", applied)
	}
	if !died {
		t.Fatalf("died = false, want true")
	}
	if v.Health != 0 {
		t.Fatalf("Health = %d, want 0", v.Health)
	}
	if v.DeadTicks != RespawnDelayTicks {
		t.Fatalf("DeadTicks = %d, want %d", v.DeadTicks, RespawnDelayTicks)
	}
}

func TestRespawnAtExactlyRespawnDelayTicks(t *testing.T) {
	v := &Vitals{Health: PlayerMaxHealth}
	Damage(v, PlayerMaxHealth)

	if step(v, RespawnDelayTicks-1) {
		t.Fatalf("respawned before %d ticks elapsed", RespawnDelayTicks)
	}
	if v.DeadTicks != 1 {
		t.Fatalf("DeadTicks = %d, want 1 with one tick left", v.DeadTicks)
	}

	if !step(v, 1) {
		t.Fatalf("did not respawn on tick %d", RespawnDelayTicks)
	}
	if v.Health != PlayerMaxHealth {
		t.Fatalf("Health = %d, want %d on respawn", v.Health, PlayerMaxHealth)
	}
	if v.DeadTicks != 0 {
		t.Fatalf("DeadTicks = %d, want 0 on respawn", v.DeadTicks)
	}
	if v.InvulnTicks != RespawnInvulnTicks {
		t.Fatalf("InvulnTicks = %d, want %d on respawn", v.InvulnTicks, RespawnInvulnTicks)
	}
}

func TestDamageDuringInvulnerabilityIsZero(t *testing.T) {
	v := &Vitals{Health: PlayerMaxHealth}
	Damage(v, PlayerMaxHealth)
	step(v, RespawnDelayTicks) // respawn, grants InvulnTicks

	if v.InvulnTicks == 0 {
		t.Fatalf("expected invulnerability right after respawn")
	}
	applied, died := Damage(v, 50)
	if applied != 0 || died {
		t.Fatalf("Damage during invuln = (%d, %v), want (0, false)", applied, died)
	}
	if v.Health != PlayerMaxHealth {
		t.Fatalf("Health = %d, want unchanged %d", v.Health, PlayerMaxHealth)
	}

	// A blocked hit must not restart the regen delay: SinceDamageTick is
	// exactly what respawn left it at (0), untouched by the blocked hit.
	if v.SinceDamageTick != 0 {
		t.Fatalf("SinceDamageTick = %d, want 0 (unaffected by the blocked hit)", v.SinceDamageTick)
	}
}

func TestRegenStartsAfterDelayAndCaps(t *testing.T) {
	v := &Vitals{Health: PlayerMaxHealth}
	Damage(v, 40) // Health = 60

	// Through the full delay, no regen yet.
	step(v, HealthRegenDelayTicks)
	if v.Health != 60 {
		t.Fatalf("Health = %d during regen delay, want 60 (no regen yet)", v.Health)
	}

	// One full second (20 ticks) past the delay: exactly regen_rate hp.
	step(v, TickHz)
	if v.Health != 60+HealthRegenRate {
		t.Fatalf("Health = %d after 1s of regen, want %d", v.Health, 60+HealthRegenRate)
	}

	// Run well past full: must cap at PlayerMaxHealth, never overshoot.
	step(v, 10*TickHz)
	if v.Health != PlayerMaxHealth {
		t.Fatalf("Health = %d, want capped at %d", v.Health, PlayerMaxHealth)
	}
}

func TestDamageDuringRegenStopsAndRestartsDelay(t *testing.T) {
	v := &Vitals{Health: PlayerMaxHealth}
	Damage(v, 40) // Health = 60
	step(v, HealthRegenDelayTicks+TickHz)
	if v.Health != 60+HealthRegenRate {
		t.Fatalf("Health = %d, want %d before interrupting hit", v.Health, 60+HealthRegenRate)
	}
	healthBeforeHit := v.Health

	Damage(v, 10)
	if v.Health != healthBeforeHit-10 {
		t.Fatalf("Health = %d after hit, want %d", v.Health, healthBeforeHit-10)
	}

	// Regen must not resume until health_regen_delay elapses again.
	step(v, HealthRegenDelayTicks)
	if v.Health != healthBeforeHit-10 {
		t.Fatalf("Health = %d, regen resumed before the delay restarted", v.Health)
	}
	step(v, TickHz)
	if v.Health != healthBeforeHit-10+HealthRegenRate {
		t.Fatalf("Health = %d, want regen resumed exactly after the restarted delay", v.Health)
	}
}

func TestDamagingDeadPlayerIsInert(t *testing.T) {
	v := &Vitals{Health: PlayerMaxHealth}
	Damage(v, PlayerMaxHealth)
	deadTicksBefore := v.DeadTicks

	applied, died := Damage(v, 30)
	if applied != 0 || died {
		t.Fatalf("Damage on dead player = (%d, %v), want (0, false)", applied, died)
	}
	if v.Health != 0 {
		t.Fatalf("Health = %d, want 0", v.Health)
	}
	if v.DeadTicks != deadTicksBefore {
		t.Fatalf("DeadTicks = %d, want unchanged %d (not re-killed)", v.DeadTicks, deadTicksBefore)
	}
}
