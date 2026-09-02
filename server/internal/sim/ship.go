// Phase 5 — the ship entity: state, deterministic pad spawn, ownership key.
//
// A ship is a place, not a mount, exactly like the rover — a world entity
// stepped by the per-kind registry, whether or not anyone is aboard. The
// flight model itself is flight.go.

package sim

import (
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// CrewSizeShip is crew_size_ship (GDD "Seats and occupancy"): seat 1 the
// pilot, seats 2–3 passengers.
const CrewSizeShip = 3

// PadDist is pad_dist (GDD "Phase 5"): the landing pad sits this far from
// spawn along the spawn bearing rotated −90° about up — perpendicular to
// the rover's parking bearing, so the two never share ground.
const PadDist = 25.0

// padMinSep is the pad scan's occupancy radius: a slot within this of an
// existing ship is skipped, so a second buyer's ship takes the next
// walkable slot (GDD "Ownership").
const padMinSep = 6.0

// ShipState is the ship's kind-specific state in Ent.Data.
type ShipState struct {
	SeatBank

	// Omega is the carried ship-frame angular velocity (about local
	// +X pitch, +Y yaw, +Z roll) — carried, not on the wire, exactly as
	// the GDD flight step specifies; the predicting client carries its own.
	Omega Vec

	// Grounded and Space are carried regime state, mirrored into the
	// flags byte (0x01, 0x10) so the client renders what it predicts.
	Grounded bool
	Space    bool

	// This tick's sanitised pilot input, written by the gateway before
	// World.Step and zero whenever seat 1 is empty — the unpiloted ship
	// coasts and stops on the same step, no special case.
	Thrust, Roll, YawRate, PitchRate float64
	Boost                            bool

	// Owner is the stable identity key of the player who bought this ship
	// (GDD "Ownership": one ship per owner, spawned on purchase or join).
	Owner string
}

// NewShipState builds a parked ship's state for the given owner.
func NewShipState(owner string) *ShipState {
	return &ShipState{SeatBank: SeatBank{Crew: CrewSizeShip}, Grounded: true, Owner: owner}
}

func (s *ShipState) Bank() *SeatBank { return &s.SeatBank }

// SpawnShip places a ship on the pad (GDD "Phase 5" rule table):
// pad_dist from spawn along the spawn bearing rotated −90° about up,
// walkable-retry k at +10 m steps — the rover's scan on a different
// bearing — skipping any slot within padMinSep of a position in occupied.
func SpawnShip(t *terrain.Field, occupied []Vec) (pos Vec, quat Quat) {
	up0 := terrain.Normalize(terrain.SpawnDir)
	p0 := up0.Scale(t.SampleRadius(up0))
	bearing := rotateAboutAxis(SpawnState(t).Facing, up0, -math.Pi/2)

	cand := up0
	for k := 0; k < 7; k++ {
		dist := PadDist + 10*float64(k)
		cand = terrain.Normalize(p0.Add(bearing.Scale(dist)))
		if !t.Walkable(cand) {
			continue
		}
		slot := cand.Scale(t.SampleRadius(cand))
		free := true
		for _, o := range occupied {
			if slot.Sub(o).Len() < padMinSep {
				free = false
				break
			}
		}
		if free {
			break
		}
	}

	pos = cand.Scale(t.SampleRadius(cand))
	forward := tangential(bearing, cand)
	if forward.Len() < EpsDeg {
		forward = tangential(Vec{0, 0, 1}, cand)
	}
	forward = terrain.Normalize(forward)
	right := terrain.Cross(cand, forward) // +X = up × forward
	return pos, QuatFromBasis(right, cand, forward)
}

// ShipItemID is the item whose possession IS ship ownership (GDD
// "Ownership") — purchase, refusals and persistence are the Phase 2 shop
// machinery unchanged.
const ShipItemID = "ship.v1"

var _ = protocol.EntityTypeShip // the kind flight.go registers against
