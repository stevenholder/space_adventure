// Seated-body composition (GDD "Seats and occupancy"):
//
//	pos  = vehicle.pos + rotate(vehicle.quat, seat_pos[seat])
//	quat = vehicle.quat, vel = vehicle.vel, grounded = vehicle.grounded
//
// A seated body is attached, not co-simulated: the integrator never steps
// it, the server composes its snapshot transform every tick, and the client
// renders that as given (PROTOCOL: "the client … never re-composes").
// The seat tables are per vehicle kind — the rover's from GDD "Rover
// seats", the ship's from the "Seats and occupancy" table itself.

package sim

import (
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// RoverSeatPos is the rover seat table's seat_pos column (GDD "Rover
// seats"), body origin in the rover's local frame, indexed by wire seat
// number. Index 0 unused.
var RoverSeatPos = [MaxCrew + 1]Vec{
	1: {-0.35, 0.95, +0.10}, // driver
	2: {+0.35, 0.95, -0.40}, // passenger
}

// SeatEyeAbove is the rover's seat_eye − seat_pos (GDD "Rover seats"): a
// seated passenger's shot starts this far over the seat. Along the planet's
// up, not the rover's -- ponytail: off by 0.6·sin(tilt) on a slope, a few cm.
const SeatEyeAbove = 0.60

// ShipSeatPos is the ship seat table's seat_pos column (GDD "Seats and
// occupancy"), ship local frame.
var ShipSeatPos = [MaxCrew + 1]Vec{
	1: {0.00, 1.44, +1.90}, // pilot
	2: {+0.35, 1.44, +0.75},
	3: {-0.35, 1.44, +0.75},
}

// RoverDisembarkLocal is the rover's disembark_local (GDD "Rover seats").
var RoverDisembarkLocal = Vec{2.0, 0.0, 0.0}

// ShipDisembarkLocal is the ship's disembark_local (GDD "Seats and
// occupancy"): bow-side, outside the hull, inside board_dist.
var ShipDisembarkLocal = Vec{4.0, 0.0, 1.5}

func seatTable(kind EntityKind) *[MaxCrew + 1]Vec {
	if uint16(kind) == protocol.EntityTypeShip {
		return &ShipSeatPos
	}
	return &RoverSeatPos
}

func disembarkLocal(kind EntityKind) Vec {
	if uint16(kind) == protocol.EntityTypeShip {
		return ShipDisembarkLocal
	}
	return RoverDisembarkLocal
}

// ComposeSeat returns the world pos of a body in the given seat of the
// given vehicle kind. Invalid seats degrade to the vehicle origin.
func ComposeSeat(kind EntityKind, vehiclePos Vec, vehicleQuat Quat, seat uint16) Vec {
	t := seatTable(kind)
	if seat == 0 || int(seat) >= len(t) {
		return vehiclePos
	}
	return vehiclePos.Add(Rotate(vehicleQuat, t[seat]))
}

// DisembarkState places a body leaving the vehicle, per the GDD disembark
// rule: vehicle_pos + rotate(vehicle_quat, disembark_local) projected
// radially onto the surface, vel = 0, facing = vehicle forward projected
// to the tangent plane (spawn-facing fallback when degenerate).
func DisembarkState(kind EntityKind, t *terrain.Field, vehiclePos Vec, vehicleQuat Quat) State {
	p := vehiclePos.Add(Rotate(vehicleQuat, disembarkLocal(kind)))
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
