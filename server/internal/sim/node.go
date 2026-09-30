// Resource node behaviour (Phase 12, docs/GDD.md "Nodes"): a node's Health
// is its remaining yields, a gather takes one, and at zero it counts down a
// respawn like a target dummy — same ID, same Pos, never removed, so clients
// see depletion through the snapshot alone.
package sim

import "space-adventure/server/internal/protocol"

// NodeState is the node-kind's per-entity state, stored in Ent.Data.
type NodeState struct {
	// Yields is the full pool the node refills to; RespawnTotal the ticks a
	// depleted node waits. Both are copied from the def at placement so the
	// step needs no registry.
	Yields       int
	RespawnTotal int
	// RespawnTicks counts down once depleted; 0 while the node has yields.
	RespawnTicks int
}

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeNode), StepNode)
}

// NewNodeState builds the state for a placement from its def's yields and
// respawn seconds.
func NewNodeState(yields int, respawnSecs float64) *NodeState {
	return &NodeState{Yields: yields, RespawnTotal: int(respawnSecs * TickHz)}
}

// StepNode advances one node by one tick. With yields left it does nothing.
// The tick it is found depleted it starts the countdown; when the countdown
// ends it is full again.
func StepNode(e *Ent, dt float64, ctx StepCtx) {
	st, ok := e.Data.(*NodeState)
	if !ok {
		return
	}
	if e.Health > 0 {
		st.RespawnTicks = 0
		return
	}
	if st.RespawnTicks == 0 {
		st.RespawnTicks = st.RespawnTotal
		if st.RespawnTicks <= 0 {
			e.Health = st.Yields
		}
		return
	}
	st.RespawnTicks--
	if st.RespawnTicks == 0 {
		e.Health = st.Yields
	}
}

// TakeYield removes one yield from a node. False if it has none.
func TakeYield(e *Ent) bool {
	if e.Health <= 0 {
		return false
	}
	e.Health--
	return true
}
