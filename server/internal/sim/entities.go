// Generic type-tagged entity store. Phase 1 modelled the world as a map of
// player bodies; this adds a container that can also hold NPCs, targets,
// loot and projectiles, each stepped by a per-Kind function. It moves
// nothing from the existing player path — Player bodies keep stepping
// exactly as sim.go already does.
//
// Iteration MUST follow `order`, never range over the Ents map directly: Go
// randomises map iteration order every run, so a sim that visits entities
// in a different order each boot is not reproducible. That breaks the
// Go/TS conformance diff, the circumnavigation replay, and any bug report
// that starts "it only happens sometimes".
package sim

// EntityKind mirrors protocol.EntityType* values (protocol.EntityTypePlayer,
// EntityTypeNPC, EntityTypeTarget, ...).
type EntityKind uint16

// Ent is one entity in the world: a player body, NPC, target, loot pickup
// or projectile, distinguished by Kind. Data carries kind-specific state
// (e.g. the player's sim.State, a target's respawn timer) and is opaque to
// the store itself.
type Ent struct {
	ID     uint32
	Kind   EntityKind
	Pos    [3]float64
	Vel    [3]float64
	Quat   [4]float64
	Health int
	Flags  uint8
	PitchQ int8
	Def    string // defs id: npc / item / entity-def key
	Data   any    // kind-specific state
}

// StepCtx is passed to every per-Kind step function. It is intentionally
// minimal for now — the per-kind behaviours (NPC AI, target respawn, ...)
// land in later briefs and will grow this struct as they need inputs.
type StepCtx struct{}

// StepFunc steps a single entity forward by dt.
type StepFunc func(e *Ent, dt float64, ctx StepCtx)

// stepRegistry maps EntityKind to its step function. Unregistered kinds
// fall back to a no-op (see stepDefault) — this brief adds the container
// only, no NPC/target behaviour.
var stepRegistry = map[EntityKind]StepFunc{}

// RegisterStep installs the step function for a Kind. Later briefs call
// this from init() (or explicit setup) to wire in NPC/target/projectile
// behaviour without touching World itself.
func RegisterStep(kind EntityKind, fn StepFunc) {
	stepRegistry[kind] = fn
}

// stepDefault is the no-op used for any Kind with no registered StepFunc.
func stepDefault(e *Ent, dt float64, ctx StepCtx) {}

// World holds all entities in the simulation, keyed by ID, with a parallel
// `order` slice that fixes the iteration order.
type World struct {
	Ents  map[uint32]*Ent
	order []uint32
}

// NewWorld returns an empty World ready for Add/Remove/Step.
func NewWorld() *World {
	return &World{Ents: make(map[uint32]*Ent)}
}

// Add inserts e into the world. If e.ID already exists, its entry is
// replaced in place and `order` is left unchanged (no duplicate entry).
// Otherwise e.ID is appended to `order`, so Add of a fresh id is always an
// insertion at the end — the deterministic, reproducible order this store
// exists to guarantee.
func (w *World) Add(e *Ent) {
	if _, exists := w.Ents[e.ID]; !exists {
		w.order = append(w.order, e.ID)
	}
	w.Ents[e.ID] = e
}

// Remove deletes id from the world, keeping `order` consistent: id is
// dropped from `order` and no stale id is left behind. Removing an id not
// present is a no-op.
func (w *World) Remove(id uint32) {
	if _, exists := w.Ents[id]; !exists {
		return
	}
	delete(w.Ents, id)
	for i, oid := range w.order {
		if oid == id {
			w.order = append(w.order[:i], w.order[i+1:]...)
			break
		}
	}
}

// Step advances every entity by dt, dispatching to the step function
// registered for its Kind (or the no-op default). Entities are visited in
// `order`, never via a map range, so the same World steps identically on
// every run.
func (w *World) Step(dt float64, ctx StepCtx) {
	for _, id := range w.order {
		e, ok := w.Ents[id]
		if !ok {
			continue
		}
		fn, ok := stepRegistry[e.Kind]
		if !ok {
			fn = stepDefault
		}
		fn(e, dt, ctx)
	}
}
