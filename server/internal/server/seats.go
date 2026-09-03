// Phase 4 — board/disembark validation, occupancy, control repoint
// (ROADMAP tasks 8 and 10). The rules are GDD "Seats and occupancy",
// applied to the rover as written; the wire semantics are PROTOCOL
// "board / disembark": events, processed in receive order, every request
// answered by a unicast seat_result, and the occupancy change itself is
// whatever the next snapshot says.

package server

import (
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
)

// boardDist is board_dist (GDD "Seats and occupancy"): the requesting body
// must be within 8 m of the vehicle origin, server-measured at the tick the
// request is processed.
const boardDist = 8.0

// board handles one board request. First request processed wins the seat;
// the loser is told no.
func (s *Server) board(c *client, b protocol.Board) {
	s.mu.Lock()
	defer s.mu.Unlock()

	result := protocol.SeatInvalid
	if c.seat == 0 {
		if ent, bank := s.vehicleByID(b.VehicleID); bank != nil && bank.ValidSeat(b.Seat) {
			switch {
			case sim.Vec(ent.Pos).Sub(c.entity.State.Pos).Len() > boardDist:
				result = protocol.SeatOutOfRange
			case bank.Seats[b.Seat] != 0:
				result = protocol.SeatOccupied
			default:
				bank.Seats[b.Seat] = c.entity.ID
				c.seatVehicle, c.seat = b.VehicleID, b.Seat
				result = protocol.SeatGranted
			}
		}
	}
	c.send(msg{data: protocol.EncodeSeatResult(protocol.SeatResult{
		EntityID: b.VehicleID, Seat: b.Seat, Result: result,
	})})
}

// disembark frees the requester's seat and places the body per the GDD
// disembark rule. Always available, at any speed — C32: nobody is trapped.
func (s *Server) disembark(c *client) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if c.seat == 0 {
		c.send(msg{data: protocol.EncodeSeatResult(protocol.SeatResult{
			EntityID: c.entity.ID, Result: protocol.SeatInvalid,
		})})
		return
	}
	seat := c.seat
	if ent, bank := s.vehicleByID(c.seatVehicle); bank != nil {
		bank.Seats[seat] = 0
		st := sim.DisembarkState(ent.Kind, s.terrain, sim.Vec(ent.Pos), sim.Quat(ent.Quat))
		c.entity.State = st
		c.entity.PrevLook = st.Facing
	}
	c.seatVehicle, c.seat = 0, 0
	c.send(msg{data: protocol.EncodeSeatResult(protocol.SeatResult{
		EntityID: c.entity.ID, Seat: seat, Result: protocol.SeatGranted,
	})})
}

// freeSeat releases c's seat if it holds one — the disconnect half of GDD
// "Control handoff". Nothing else happens on purpose: the rover's input
// zeroes next tick (tick() resets it every tick), so the same step runs
// with zero input and the rover coasts to a stop. Caller holds s.mu.
func (s *Server) freeSeat(c *client) {
	if c.seat == 0 {
		return
	}
	if _, bank := s.vehicleByID(c.seatVehicle); bank != nil {
		bank.Seats[c.seat] = 0
	}
	c.seatVehicle, c.seat = 0, 0
}

// vehicleByID finds a world vehicle (rover OR ship — anything whose state
// carries seats) and its seat bank. Non-vehicles and unknown ids return
// nil — a board request naming a shopkeeper is result 3, not a panic.
// Caller holds s.mu.
func (s *Server) vehicleByID(id uint32) (*sim.Ent, *sim.SeatBank) {
	for _, e := range s.worldEnts {
		if e.ID == id {
			if v, ok := e.Data.(sim.Seater); ok {
				return e, v.Bank()
			}
			return nil, nil
		}
	}
	return nil, nil
}
