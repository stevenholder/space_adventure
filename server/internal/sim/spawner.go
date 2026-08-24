// Zone-driven NPC spawners (docs/GDD.md, "Phase 3 — NPC combat at an
// encampment" -> "AI state machine": the DEAD -> IDLE row, and the
// "NPC archetypes" table).
//
// Like a target dummy (target.go), a spawned NPC is never removed and
// never re-added when it dies — its entity id is stable across death and
// respawn, exactly like the Phase 2 target dummies. Churning spawn/despawn
// every 20 s tears down every client's interpolation buffer and nametag for
// that id.
package sim

import (
	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
)

const (
	// NPCRespawnSecs is npc_respawn from the GDD "AI state machine" param
	// table: how long a dead NPC stays dead before it is restored, at full
	// health, at its post.
	NPCRespawnSecs = 20.0

	// NPCRespawnTicks is that duration in whole ticks. The countdown is
	// integer, not an accumulated float: the tick is fixed at 20 Hz, so
	// 20.0 s is exactly 400 ticks, and summing dt instead needs an epsilon
	// to land exactly — the sim feeds replay and a conformance diff, where
	// "close enough" is the wrong shape.
	NPCRespawnTicks = int(NPCRespawnSecs * TickHz)
)

// NPCState is the NPC-kind's per-entity state, stored in Ent.Data.
//
// Post/PostQuat are where the NPC was placed by its zone — the brain leashes
// to this, and a respawn restores the NPC HERE, not to wherever it died. An
// enemy that respawns where it fell walks the camp's defenders steadily
// toward wherever players killed them.
type NPCState struct {
	Archetype string     // npcs.json id, e.g. "npc.grunt"
	Post      [3]float64 // where it spawned; the brain leashes to this
	PostQuat  [4]float64

	// RespawnTicks counts down in whole ticks once the NPC dies. Zero (or
	// negative) while alive: not counting down.
	RespawnTicks int

	// MaxHealth is the archetype's full health pool, cached at spawn time
	// (from reg.NPCs) so a respawn can restore it without StepNPCRespawn
	// needing its own registry lookup — StepCtx carries no *defs.Registry.
	MaxHealth int
}

// SpawnZoneNPCs creates one entity per NPC placement in a composed zone
// (defs.ComposeZone). Only placements whose Type is "npc" become NPCs — a
// zone's placements also carry targets, which this ignores. Returns the ids
// it created, in placement order.
func SpawnZoneNPCs(w *World, reg *defs.Registry, placements []defs.Placement, nextID func() uint32) []uint32 {
	ids := make([]uint32, 0, len(placements))
	for _, p := range placements {
		if p.Type != "npc" {
			continue
		}

		npc := reg.NPCs[p.Def]
		id := nextID()
		w.Add(&Ent{
			ID:     id,
			Kind:   EntityKind(protocol.EntityTypeNPC),
			Pos:    p.Pos,
			Quat:   p.Quat,
			Health: npc.MaxHealth,
			Def:    p.Def,
			Data: &NPCState{
				Archetype: p.Def,
				Post:      p.Pos,
				PostQuat:  p.Quat,
				MaxHealth: npc.MaxHealth,
			},
		})
		ids = append(ids, id)
	}
	return ids
}

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeNPC), StepNPCRespawn)
}

// StepNPCRespawn advances a dead NPC's respawn timer (GDD "AI state
// machine", the DEAD -> IDLE row). While alive it does nothing. The moment
// Health reaches 0 it sets FlagDead and starts the countdown. Once the
// countdown reaches zero it restores full health, clears FlagDead, and
// returns the NPC to its Post/PostQuat — not to where it died — with the
// same entity id, no World.Add/Remove involved.
//
// An entity with no NPCState (e.g. a shop NPC, which shares EntityTypeNPC
// but carries no combat state) is left alone: nothing to respawn.
func StepNPCRespawn(e *Ent, dt float64, ctx StepCtx) {
	state, ok := e.Data.(*NPCState)
	if !ok {
		return
	}

	if e.Health <= 0 && e.Flags&protocol.FlagDead == 0 {
		e.Health = 0
		e.Flags |= protocol.FlagDead
		state.RespawnTicks = NPCRespawnTicks
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

	e.Health = state.MaxHealth
	e.Flags &^= protocol.FlagDead
	e.Pos = state.Post
	e.Quat = state.PostQuat
	state.RespawnTicks = 0
}
