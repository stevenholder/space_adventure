package server

// defaultInterestRadius is the default culling radius, in metres. The GDD's
// horizon at r=150 is ~23 m; 120 m is generously beyond anything a client
// can actually see while still bounding the per-tick row count once entity
// counts grow past a handful of players (Phase 3's encampment, C25).
const defaultInterestRadius = 120

// Interest decides which entities a client is sent. It is a pure filter: it
// takes no lock and touches no shared state, so callers own concurrency.
type Interest struct{ Radius float64 }

// NewInterest returns an Interest with the default radius.
func NewInterest() Interest { return Interest{Radius: defaultInterestRadius} }

// InterestEnt is the minimal per-entity input Interest needs: an id to
// return, a position to test, and whether the entity is the client's own.
type InterestEnt struct {
	ID  uint32
	Pos [3]float64
	Own bool
}

// Visible reports whether an entity at entPos is of interest to a client at
// clientPos. Always true for the client's own entity: culling your own body
// leaves the client with nothing to reconcile prediction against, which
// freezes the local player — the worst possible failure mode for what is
// meant to be a pure bandwidth optimisation.
//
// No hysteresis band: an entity that sits near the boundary will flap in and
// out of consecutive snapshots as either side moves. That is deliberate, not
// an oversight — the client already tolerates absence (it interpolates
// through gaps and despawns on a missing entity), so a flapping distant
// entity degrades to "briefly not drawn," not a correctness problem. If
// flapping ever proves visible in practice, the fix is a hysteresis band
// (e.g. enter at Radius, exit at Radius*1.1), not a bigger radius — a bigger
// radius just moves the flap boundary and gives up the bandwidth bound.
func (i Interest) Visible(clientPos, entPos [3]float64, own bool) bool {
	if own {
		return true
	}
	dx := clientPos[0] - entPos[0]
	dy := clientPos[1] - entPos[1]
	dz := clientPos[2] - entPos[2]
	distSq := dx*dx + dy*dy + dz*dz
	return distSq <= i.Radius*i.Radius
}

// Filter returns the ids of the entities in ents that are visible to a
// client at clientPos, in the same order as ents. Snapshot rows are order-
// sensitive on the wire (PROTOCOL.md), and map iteration is not
// deterministic in Go, so Filter must walk the input slice in order rather
// than, say, building and draining a map.
func (i Interest) Filter(clientPos [3]float64, ents []InterestEnt) []uint32 {
	out := make([]uint32, 0, len(ents))
	for _, e := range ents {
		if i.Visible(clientPos, e.Pos, e.Own) {
			out = append(out, e.ID)
		}
	}
	return out
}
