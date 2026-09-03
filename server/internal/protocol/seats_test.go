package protocol

import (
	"errors"
	"testing"
)

func TestBoardRoundTrip(t *testing.T) {
	in := Board{VehicleID: 0x01020304, Seat: 2}
	frame := EncodeBoard(in)
	typ, payload, err := DecodeFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if typ != MsgBoard {
		t.Fatalf("type = %#x, want %#x", typ, MsgBoard)
	}
	out, err := DecodeBoard(payload)
	if err != nil {
		t.Fatal(err)
	}
	if out != in {
		t.Fatalf("round trip: got %+v, want %+v", out, in)
	}
}

func TestDisembarkRoundTrip(t *testing.T) {
	frame := EncodeDisembark()
	typ, payload, err := DecodeFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if typ != MsgDisembark {
		t.Fatalf("type = %#x, want %#x", typ, MsgDisembark)
	}
	if err := DecodeDisembark(payload); err != nil {
		t.Fatal(err)
	}
}

func TestSeatResultRoundTrip(t *testing.T) {
	in := SeatResult{EntityID: 7, Seat: 1, Result: SeatOccupied}
	frame := EncodeSeatResult(in)
	typ, payload, err := DecodeFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if typ != MsgSeatResult {
		t.Fatalf("type = %#x, want %#x", typ, MsgSeatResult)
	}
	out, err := DecodeSeatResult(payload)
	if err != nil {
		t.Fatal(err)
	}
	if out != in {
		t.Fatalf("round trip: got %+v, want %+v", out, in)
	}
}

// A wrong-length payload is usually a field-alignment bug, so short AND
// trailing bytes must both refuse (same rule as DecodeInput).
func TestSeatDecodeErrors(t *testing.T) {
	cases := []struct {
		name string
		f    func([]byte) error
		n    int
	}{
		{"board", func(p []byte) error { _, err := DecodeBoard(p); return err }, 6},
		{"disembark", DecodeDisembark, 0},
		{"seat_result", func(p []byte) error { _, err := DecodeSeatResult(p); return err }, 7},
	}
	for _, c := range cases {
		if c.n > 0 {
			if err := c.f(make([]byte, c.n-1)); !errors.Is(err, ErrBadPayload) {
				t.Errorf("%s short: err = %v, want ErrBadPayload", c.name, err)
			}
		}
		if err := c.f(make([]byte, c.n+1)); !errors.Is(err, ErrBadPayload) {
			t.Errorf("%s trailing: err = %v, want ErrBadPayload", c.name, err)
		}
	}
}
