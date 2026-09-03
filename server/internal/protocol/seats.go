// Phase 4 — the seat messages (docs/PROTOCOL.md 0x000B–0x000D).
//
// board and disembark are EVENTS, not command state: processed once, in
// receive order, on the tick they arrive (PROTOCOL "board / disembark").
// Every request gets a seat_result, unicast to the requester; the
// authoritative occupancy change is what the next snapshot says.

package protocol

import "encoding/binary"

// Board is a client's request for one specific seat on one vehicle.
type Board struct {
	VehicleID uint32
	Seat      uint16
}

// SeatResult result codes (PROTOCOL "Constants").
const (
	SeatGranted    uint8 = 0
	SeatOccupied   uint8 = 1
	SeatOutOfRange uint8 = 2
	SeatInvalid    uint8 = 3
)

// SeatResult answers exactly one board or disembark request.
type SeatResult struct {
	EntityID uint32 // the vehicle for board, the requester's body for disembark
	Seat     uint16
	Result   uint8
}

// EncodeBoard renders a board frame.
func EncodeBoard(b Board) []byte {
	buf := putU16(nil, MsgBoard)
	buf = putU32(buf, b.VehicleID)
	return putU16(buf, b.Seat)
}

// DecodeBoard parses a board payload.
func DecodeBoard(p []byte) (Board, error) {
	if err := need(p, 6, "board"); err != nil {
		return Board{}, err
	}
	return Board{
		VehicleID: binary.LittleEndian.Uint32(p[0:]),
		Seat:      binary.LittleEndian.Uint16(p[4:]),
	}, nil
}

// EncodeDisembark renders a disembark frame. No payload: the server knows
// which body asked and where it is seated.
func EncodeDisembark() []byte {
	return putU16(nil, MsgDisembark)
}

// DecodeDisembark parses a disembark payload (which must be empty — a
// payload here is a framing bug, same judgment as need's trailing check).
func DecodeDisembark(p []byte) error {
	return need(p, 0, "disembark")
}

// EncodeSeatResult renders a seat_result frame.
func EncodeSeatResult(r SeatResult) []byte {
	buf := putU16(nil, MsgSeatResult)
	buf = putU32(buf, r.EntityID)
	buf = putU16(buf, r.Seat)
	return append(buf, r.Result)
}

// DecodeSeatResult parses a seat_result payload.
func DecodeSeatResult(p []byte) (SeatResult, error) {
	if err := need(p, 7, "seat_result"); err != nil {
		return SeatResult{}, err
	}
	return SeatResult{
		EntityID: binary.LittleEndian.Uint32(p[0:]),
		Seat:     binary.LittleEndian.Uint16(p[4:]),
		Result:   p[6],
	}, nil
}
