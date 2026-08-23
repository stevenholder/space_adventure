// Target dummy behaviour: hit response and respawn (docs/GDD.md, "Health
// and damage"). A target is never removed and never re-added when it dies —
// its entity_id must stay stable across respawns so clients don't churn
// spawn/despawn for something that gets shot every few seconds.
package sim

import "space-adventure/server/internal/protocol"

const (
	// TargetMaxHealth is the target dummy's full health pool.
	TargetMaxHealth = 100
	// TargetRespawnSecs is how long a dead target stays dead before it is
	// restored to full health in place.
	TargetRespawnSecs = 3.0

	// TargetRespawnTicks is that duration in whole ticks. The countdown is
	// integer, not an accumulated float: the tick is fixed at 20 Hz, so 3.0 s
	// is exactly 60 ticks, and summing dt instead lands a hair short and needs
	// an epsilon to paper over it. The sim feeds replay and the Go/TS
	// conformance diff, so "close enough after 60 additions" is the wrong
	// shape here — an integer count is exact on both ends by construction.
	TargetRespawnTicks = int(TargetRespawnSecs * TickHz)
)

// TargetState is the target-kind's per-entity state, stored in Ent.Data. A
// struct (rather than a bare float64 respawn deadline) so a future field —
// e.g. a kill counter — doesn't force a type change at every call site that
// touches Ent.Data.
type TargetState struct {
	// RespawnTicks counts down in whole ticks once the target
	// dies, in seconds. Zero (or negative) while alive: not counting down.
	RespawnTicks int
}

// TargetIsDamageable reports whether e can currently take damage. A dead
// target is neither damageable nor hit-testable — callers doing hit
// resolution should skip it as a candidate entirely.
func TargetIsDamageable(e *Ent) bool {
	return e.Flags&protocol.FlagDead == 0
}

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeTarget), StepTarget)
}

// StepTarget advances one target dummy by dt. While alive it does nothing.
// The moment Health reaches 0 it sets FlagDead, starts the respawn
// countdown, and emits one EventDeath. Once the countdown reaches zero it
// restores full health and clears FlagDead — same ID, same Pos, no
// World.Add/Remove involved.
func StepTarget(e *Ent, dt float64, ctx StepCtx) {
	state, ok := e.Data.(*TargetState)
	if !ok {
		state = &TargetState{}
		e.Data = state
	}

	if e.Health <= 0 && e.Flags&protocol.FlagDead == 0 {
		e.Health = 0
		e.Flags |= protocol.FlagDead
		state.RespawnTicks = TargetRespawnTicks
		if ctx.Events != nil {
			*ctx.Events = append(*ctx.Events, protocol.Event{
				EntityID: e.ID,
				EventID:  protocol.EventDeath,
			})
		}
		return
	}

	if e.Flags&protocol.FlagDead == 0 {
		return
	}

	state.RespawnTicks--
	if state.RespawnTicks > 0 {
		return
	}

	e.Health = TargetMaxHealth
	e.Flags &^= protocol.FlagDead
	state.RespawnTicks = 0
}
