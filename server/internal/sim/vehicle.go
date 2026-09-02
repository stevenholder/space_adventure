// Phase 4 — the rover entity: state, deterministic spawn, seat occupancy.
//
// A vehicle is a place, not a mount (GDD "Vehicles and crew"): it exists in
// the world whether or not anyone is aboard, and it is stepped by the same
// per-kind registry as every other world entity. The drive model itself is
// drive.go; this file is the entity's state and its birth.

package sim

import (
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// CrewSizeRover is crew_size_rover (GDD "Ground drive model"): seat 1 the
// driver, seat 2 the passenger. The wire seat field is u16 — more seats
// later is a rule-table change, not a wire change.
const CrewSizeRover = 2

// VehicleState is the rover's kind-specific state in Ent.Data.
type VehicleState struct {
	// Grounded is carried, not on the wire (GDD: same rule as the ship's
	// ω/grounded) — the client's mirrored step derives its own.
	Grounded bool

	// Throttle and Steer are THIS tick's sanitised driver input, written by
	// the gateway before World.Step and zero whenever seat 1 is empty —
	// which makes driver disconnect coast-and-stop fall out for free: the
	// same step runs with zero input (GDD "Control handoff").
	Throttle, Steer float64

	// Seats[i] is the entity id occupying seat i, 0 = empty. Index 0 is
	// unused so the index IS the wire seat number.
	Seats [CrewSizeRover + 1]uint32
}

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeVehicle), StepRover)
}

// SeatOf returns the seat the entity occupies, 0 if not aboard.
func (v *VehicleState) SeatOf(id uint32) uint16 {
	for i := 1; i <= CrewSizeRover; i++ {
		if v.Seats[i] == id {
			return uint16(i)
		}
	}
	return 0
}

// SpawnRover builds the parked rover deterministically (GDD "Ground drive
// model", "Deterministic spawn"): a candidate rover_spawn_dist from spawn
// along the spawn bearing, walkable-retry k at +10 m steps, quat basis
// +X = up × forward. Same field, same rover — bug reports stay
// reproducible, exactly like the fixed world seed.
func SpawnRover(t *terrain.Field) (pos Vec, quat Quat) {
	up0 := terrain.Normalize(terrain.SpawnDir)
	p0 := up0.Scale(t.SampleRadius(up0))
	bearing := SpawnState(t).Facing

	cand := up0
	for k := 0; k < 7; k++ {
		dist := RoverSpawnDist + 10*float64(k)
		cand = terrain.Normalize(p0.Add(bearing.Scale(dist)))
		if t.Walkable(cand) {
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
