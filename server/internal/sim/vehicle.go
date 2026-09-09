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

// MaxCrew is the largest crew any vehicle carries (the ship's 3), sizing
// the seat array once for every Seater.
const MaxCrew = 3

// SeatBank is the seat occupancy every crewed vehicle shares. Seats[i] is
// the entity id in seat i, 0 = empty; index 0 is unused so the index IS
// the wire seat number. Crew is how many of the slots this vehicle has.
type SeatBank struct {
	Seats [MaxCrew + 1]uint32
	Crew  int
}

// SeatOf returns the seat the entity occupies, 0 if not aboard.
func (b *SeatBank) SeatOf(id uint32) uint16 {
	for i := 1; i <= b.Crew; i++ {
		if b.Seats[i] == id {
			return uint16(i)
		}
	}
	return 0
}

// ValidSeat reports whether the seat number exists on this vehicle.
func (b *SeatBank) ValidSeat(seat uint16) bool {
	return seat >= 1 && int(seat) <= b.Crew
}

// Seater is any Ent.Data that carries seats — the gateway's board/
// disembark/composition code works against this, not a concrete kind.
type Seater interface{ Bank() *SeatBank }

// VehicleState is the rover's kind-specific state in Ent.Data.
type VehicleState struct {
	SeatBank

	// Grounded is carried, not on the wire's f32 triplet (GDD: same rule as
	// the ship's ω/grounded), but mirrored into the flags bit.
	Grounded bool

	// Throttle and Steer are THIS tick's sanitised driver input, written by
	// the gateway before World.Step and zero whenever seat 1 is empty —
	// which makes driver disconnect coast-and-stop fall out for free: the
	// same step runs with zero input (GDD "Control handoff").
	Throttle, Steer float64

	// EffMult is the driver's Driving efficacy (Phase 11), written beside
	// the input each tick; 0 (no driver, or untrained) reads as 1.0. Both
	// sims apply it identically, keeping drive prediction conformant.
	EffMult float64
}

// NewVehicleState builds the rover's state, parked.
func NewVehicleState() *VehicleState {
	return &VehicleState{SeatBank: SeatBank{Crew: CrewSizeRover}, Grounded: true}
}

func (v *VehicleState) Bank() *SeatBank { return &v.SeatBank }

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeVehicle), StepRover)
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
