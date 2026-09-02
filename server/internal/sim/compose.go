// Phase 4 — seated-body composition (GDD "Seats and occupancy"):
//
//	pos  = vehicle.pos + rotate(vehicle.quat, seat_pos[seat])
//	quat = vehicle.quat, vel = vehicle.vel, grounded = vehicle.grounded
//
// A seated body is attached, not co-simulated: the integrator never steps
// it, the server composes its snapshot transform every tick, and the client
// renders that as given (PROTOCOL: "the client … never re-composes").

package sim

import "space-adventure/server/internal/terrain"

// RoverSeatPos is the rover seat table's seat_pos column (GDD "Rover
// seats"), body origin in the rover's local frame, indexed by wire seat
// number. Index 0 unused.
var RoverSeatPos = [CrewSizeRover + 1]Vec{
	1: {-0.35, 0.95, +0.10}, // driver
	2: {+0.35, 0.95, -0.40}, // passenger
}

// RoverDisembarkLocal is disembark_local (GDD "Rover seats"): right side,
// 2 m out — outside the hull, inside board_dist.
var RoverDisembarkLocal = Vec{2.0, 0.0, 0.0}

// ComposeSeat returns the world pos of a body in the given seat.
func ComposeSeat(vehiclePos Vec, vehicleQuat Quat, seat uint16) Vec {
	if seat == 0 || int(seat) >= len(RoverSeatPos) {
		return vehiclePos
	}
	return vehiclePos.Add(Rotate(vehicleQuat, RoverSeatPos[seat]))
}

// DisembarkState places a body leaving the vehicle, per the GDD disembark
// rule: vehicle_pos + rotate(vehicle_quat, disembark_local) projected
// radially onto the surface, vel = 0, facing = vehicle forward projected
// to the tangent plane (spawn-facing fallback when degenerate).
func DisembarkState(t *terrain.Field, vehiclePos Vec, vehicleQuat Quat) State {
	p := vehiclePos.Add(Rotate(vehicleQuat, RoverDisembarkLocal))
	up := terrain.Normalize(p)
	pos := up.Scale(t.SampleRadius(up))

	facing := tangential(Rotate(vehicleQuat, Vec{0, 0, 1}), up)
	if facing.Len() < EpsDeg {
		facing = tangential(Vec{0, 0, 1}, up)
		if facing.Len() < EpsDeg {
			facing = tangential(Vec{1, 0, 0}, up)
		}
	}
	return State{
		Pos:      pos,
		Vel:      Vec{},
		Grounded: t.Walkable(up),
		Facing:   terrain.Normalize(facing),
	}
}
