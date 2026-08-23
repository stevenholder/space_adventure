package protocol

import (
	"encoding/binary"
	"errors"
	"math"
	"testing"
)

func TestCmdRoundTrip(t *testing.T) {
	c := Cmd{Seq: 42, Opcode: OpShopBuy, Data: []byte(`{"item":"pistol"}`)}
	roundTrip(t, MsgCmd, EncodeCmd(c), func(p []byte) (any, error) { return ParseCmd(p) }, c)

	// Empty data.
	c2 := Cmd{Seq: 1, Opcode: OpInventory, Data: nil}
	got, err := ParseCmd(EncodeCmd(c2)[2:])
	if err != nil {
		t.Fatalf("ParseCmd: %v", err)
	}
	if got.Seq != c2.Seq || got.Opcode != c2.Opcode || len(got.Data) != 0 {
		t.Fatalf("decoded = %#v, want %#v", got, c2)
	}
}

func TestCmdOverLongDataRejected(t *testing.T) {
	data := make([]byte, MaxCmdBody+1)
	c := Cmd{Seq: 1, Opcode: OpEquip, Data: data}
	if _, err := ParseCmd(EncodeCmd(c)[2:]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseCmd over-long data: err = %v, want ErrBadPayload", err)
	}
}

func TestCmdTruncatedRejected(t *testing.T) {
	frame := EncodeCmd(Cmd{Seq: 1, Opcode: OpReload, Data: []byte("hello")})
	payload := frame[2:]
	if _, err := ParseCmd(payload[:len(payload)-1]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseCmd truncated: err = %v, want ErrBadPayload", err)
	}
	if _, err := ParseCmd(payload[:6]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseCmd truncated header: err = %v, want ErrBadPayload", err)
	}
}

func TestCmdResultRoundTrip(t *testing.T) {
	c := CmdResult{Seq: 7, Opcode: OpShopList, Status: StatusOK, Data: []byte(`{"ok":true}`)}
	roundTrip(t, MsgCmdResult, EncodeCmdResult(c), func(p []byte) (any, error) { return ParseCmdResult(p) }, c)
}

func TestCmdResultOverLongDataRejected(t *testing.T) {
	data := make([]byte, MaxCmdBody+1)
	c := CmdResult{Seq: 1, Opcode: OpEquip, Status: StatusRefused, Data: data}
	if _, err := ParseCmdResult(EncodeCmdResult(c)[2:]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseCmdResult over-long data: err = %v, want ErrBadPayload", err)
	}
}

func TestCmdResultTruncatedRejected(t *testing.T) {
	frame := EncodeCmdResult(CmdResult{Seq: 1, Opcode: OpReload, Status: StatusOK, Data: []byte("hi")})
	payload := frame[2:]
	if _, err := ParseCmdResult(payload[:len(payload)-1]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseCmdResult truncated: err = %v, want ErrBadPayload", err)
	}
}

func TestDefsRoundTrip(t *testing.T) {
	d := Defs{Data: []byte(`{"items":{}}`)}
	roundTrip(t, MsgDefs, EncodeDefs(d), func(p []byte) (any, error) { return ParseDefs(p) }, d)
}

func TestDefsTruncatedRejected(t *testing.T) {
	frame := EncodeDefs(Defs{Data: []byte("payload")})
	payload := frame[2:]
	if _, err := ParseDefs(payload[:len(payload)-1]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseDefs truncated: err = %v, want ErrBadPayload", err)
	}
}

func TestFireRoundTrip(t *testing.T) {
	f := Fire{Seq: 99, Dir: [3]float32{0, 0, 1}}
	roundTrip(t, MsgFire, EncodeFire(f), func(p []byte) (any, error) { return ParseFire(p) }, f)

	f2 := Fire{Seq: 1, Dir: [3]float32{0.5773503, 0.5773503, 0.5773503}}
	roundTrip(t, MsgFire, EncodeFire(f2), func(p []byte) (any, error) { return ParseFire(p) }, f2)
}

func TestFireNonFiniteRejected(t *testing.T) {
	for _, bad := range [][3]float32{
		{float32(math.NaN()), 0, 1},
		{float32(math.Inf(1)), 0, 1},
		{float32(math.Inf(-1)), 0, 1},
	} {
		f := Fire{Seq: 1, Dir: bad}
		if _, err := ParseFire(EncodeFire(f)[2:]); !errors.Is(err, ErrBadPayload) {
			t.Fatalf("ParseFire non-finite dir %v: err = %v, want ErrBadPayload", bad, err)
		}
	}
}

func TestFireBadLengthRejected(t *testing.T) {
	for _, bad := range [][3]float32{
		{0, 0, 0},   // zero length
		{2, 0, 0},   // too long
		{0.1, 0, 0}, // too short
	} {
		f := Fire{Seq: 1, Dir: bad}
		if _, err := ParseFire(EncodeFire(f)[2:]); !errors.Is(err, ErrBadPayload) {
			t.Fatalf("ParseFire bad length dir %v: err = %v, want ErrBadPayload", bad, err)
		}
	}
}

func TestFireTruncatedRejected(t *testing.T) {
	frame := EncodeFire(Fire{Seq: 1, Dir: [3]float32{0, 0, 1}})
	payload := frame[2:]
	if _, err := ParseFire(payload[:len(payload)-1]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseFire truncated: err = %v, want ErrBadPayload", err)
	}
}

func TestCollidersRoundTrip(t *testing.T) {
	c := Colliders{List: []Collider{
		{Kind: ColliderBox, Center: [3]float32{1, 2, 3}, Half: [3]float32{4, 5, 6}, Quat: [4]float32{0, 0, 0, 1}},
		{Kind: ColliderSphere, Center: [3]float32{-1, -2, -3}, Half: [3]float32{0.5, 0, 0}, Quat: [4]float32{0, 0, 0, 1}},
	}}
	roundTrip(t, MsgColliders, EncodeColliders(c), func(p []byte) (any, error) { return ParseColliders(p) }, c)

	// Empty list.
	c2 := Colliders{}
	got, err := ParseColliders(EncodeColliders(c2)[2:])
	if err != nil {
		t.Fatalf("ParseColliders: %v", err)
	}
	if len(got.List) != 0 {
		t.Fatalf("decoded list = %#v, want empty", got.List)
	}
}

func TestCollidersOverMaxCountRejected(t *testing.T) {
	// Craft a header claiming more colliders than the payload holds, and
	// more than ColliderMax, without allocating ColliderMax rows.
	p := putU16(nil, uint16(ColliderMax)+1)
	if _, err := ParseColliders(p); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseColliders over-max count: err = %v, want ErrBadPayload", err)
	}
}

func TestCollidersTruncatedRejected(t *testing.T) {
	frame := EncodeColliders(Colliders{List: []Collider{
		{Kind: ColliderBox, Center: [3]float32{1, 2, 3}, Half: [3]float32{4, 5, 6}, Quat: [4]float32{0, 0, 0, 1}},
	}})
	payload := frame[2:]
	if _, err := ParseColliders(payload[:len(payload)-1]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseColliders truncated: err = %v, want ErrBadPayload", err)
	}
	if _, err := ParseColliders(payload[:1]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("ParseColliders truncated header: err = %v, want ErrBadPayload", err)
	}
}

func TestColliderPadIgnoredOnRead(t *testing.T) {
	frame := EncodeColliders(Colliders{List: []Collider{
		{Kind: ColliderBox, Center: [3]float32{1, 2, 3}, Half: [3]float32{4, 5, 6}, Quat: [4]float32{0, 0, 0, 1}},
	}})
	payload := frame[2:]
	if payload[3] != 0 {
		t.Fatalf("_pad byte = %d, want 0", payload[3])
	}
	// Non-zero _pad on the wire must not affect the parsed result.
	payload[3] = 0xFF
	got, err := ParseColliders(payload)
	if err != nil {
		t.Fatalf("ParseColliders: %v", err)
	}
	if got.List[0].Kind != ColliderBox {
		t.Fatalf("Kind = %d, want %d", got.List[0].Kind, ColliderBox)
	}
}

// TestParseCollidersRejectsNonFinite: a non-finite centre reaches the sim's
// push-out, propagates into the player's position, and is then written to the
// player row — so it outlives the session rather than glitching a frame.
func TestParseCollidersRejectsNonFinite(t *testing.T) {
	good := EncodeColliders(Colliders{List: []Collider{{
		Kind: ColliderBox, Center: [3]float32{1, 2, 3},
		Half: [3]float32{1, 1, 1}, Quat: [4]float32{0, 0, 0, 1},
	}}})
	payload := good[2:] // strip the frame type
	if _, err := ParseColliders(payload); err != nil {
		t.Fatalf("precondition: good colliders failed to parse: %v", err)
	}

	for _, tc := range []struct {
		name string
		off  int // byte offset of the f32 to poison, within the payload
	}{
		{"centre", 2 + 2},         // count(2) + kind/pad(2)
		{"half", 2 + 2 + 12},      // ... + center(12)
		{"quat", 2 + 2 + 12 + 12}, // ... + half(12)
	} {
		t.Run(tc.name, func(t *testing.T) {
			bad := append([]byte(nil), payload...)
			binary.LittleEndian.PutUint32(bad[tc.off:tc.off+4], math.Float32bits(float32(math.NaN())))
			if _, err := ParseColliders(bad); !errors.Is(err, ErrBadPayload) {
				t.Errorf("NaN %s accepted (err=%v), want ErrBadPayload", tc.name, err)
			}
		})
	}
}
