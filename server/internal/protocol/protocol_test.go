package protocol

import (
	"errors"
	"math"
	"reflect"
	"testing"
)

// roundTrip encodes a frame, splits it, and decodes the payload back.
func roundTrip(t *testing.T, typ uint16, frame []byte, decode func([]byte) (any, error), want any) {
	t.Helper()
	gotTyp, payload, err := DecodeFrame(frame)
	if err != nil {
		t.Fatalf("DecodeFrame: %v", err)
	}
	if gotTyp != typ {
		t.Fatalf("frame type = 0x%04x, want 0x%04x", gotTyp, typ)
	}
	got, err := decode(payload)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("decoded = %#v, want %#v", got, want)
	}
}

func TestHelloRoundTrip(t *testing.T) {
	h := Hello{ClientVer: VersionM1, Name: "Steve \xf0\x9f\x9a\x80", Token: "tok-abc123"}
	roundTrip(t, MsgHello, EncodeHello(h), func(p []byte) (any, error) { return DecodeHello(p) }, h)

	// Empty name, empty token.
	h2 := Hello{ClientVer: 2, Name: ""}
	roundTrip(t, MsgHello, EncodeHello(h2), func(p []byte) (any, error) { return DecodeHello(p) }, h2)
}

// TestHelloTokenLessPayload verifies backward compatibility: a hello payload
// that ends right after the name (no token fields at all, as an old client
// sends) parses as Token == "" rather than an error.
func TestHelloTokenLessPayload(t *testing.T) {
	p := []byte{1, 0, 5, 0, 0, 0, 'S', 't', 'e', 'v', 'e'}
	got, err := DecodeHello(p)
	if err != nil {
		t.Fatalf("DecodeHello: %v", err)
	}
	want := Hello{ClientVer: 1, Name: "Steve", Token: ""}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("decoded = %#v, want %#v", got, want)
	}
}

func TestHelloTokenOverLongRejected(t *testing.T) {
	tok := make([]byte, MaxTokenLen+1)
	for i := range tok {
		tok[i] = 'a'
	}
	h := Hello{ClientVer: VersionM1, Name: "Steve", Token: string(tok)}
	if _, err := DecodeHello(EncodeHello(h)[2:]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("DecodeHello over-long token: err = %v, want ErrBadPayload", err)
	}
}

func TestHelloTokenNonPrintableRejected(t *testing.T) {
	h := Hello{ClientVer: VersionM1, Name: "Steve", Token: "bad\x00tok"}
	if _, err := DecodeHello(EncodeHello(h)[2:]); !errors.Is(err, ErrBadPayload) {
		t.Fatalf("DecodeHello non-printable token: err = %v, want ErrBadPayload", err)
	}
}

func TestHelloAckRoundTrip(t *testing.T) {
	h := HelloAck{ServerVer: VersionM1, TickHz: 20, WorldSeed: 0xDEADBEEF, EntityID: 7}
	roundTrip(t, MsgHelloAck, EncodeHelloAck(h), func(p []byte) (any, error) { return DecodeHelloAck(p) }, h)
}

func TestInputRoundTrip(t *testing.T) {
	in := Input{
		MoveX:      -0.25,
		MoveY:      1.0,
		LookDir:    [3]float32{0.1, -0.7, 0.7},
		ActionMask: ActionSprint | ActionJump,
		Seq:        0xFFFF,
		Mode:       2,
	}
	roundTrip(t, MsgInput, EncodeInput(in), func(p []byte) (any, error) { return DecodeInput(p) }, in)
}

// A 24-byte input predates the mode byte and must still decode, as mode 0.
// This is the whole reason the byte was appended rather than prepended: it
// lets the server take the change without every client moving in lockstep.
// Without it the retiring TypeScript client and all twelve .mjs harnesses
// would have had to land in the same commit as the server.
func TestInputWithoutModeByteDecodesAsModeZero(t *testing.T) {
	// Seq deliberately has a NON-ZERO high byte. With Seq 7 the last byte of a
	// 24-byte payload is 0, so a decoder that wrongly read the mode from the
	// end of the buffer still produced mode 0 and this test passed while the
	// bug it exists to catch was present.
	const seq = 0x0107
	full := EncodeInput(Input{MoveX: -0.25, MoveY: 1.0, LookDir: [3]float32{0.1, -0.7, 0.7},
		ActionMask: ActionSprint, Seq: seq, Mode: 3})
	payload := full[2:] // strip the u16 frame type
	if len(payload) != 25 {
		t.Fatalf("encoded input is %d bytes, want 25", len(payload))
	}

	legacy, err := DecodeInput(payload[:24])
	if err != nil {
		t.Fatalf("24-byte input: %v", err)
	}
	if legacy.Mode != 0 {
		t.Errorf("mode = %d, want 0", legacy.Mode)
	}
	// Every other field must land at the offset it had before the byte
	// existed. If the byte had been prepended these would all be one out.
	if legacy.MoveX != -0.25 || legacy.MoveY != 1.0 || legacy.Seq != seq ||
		legacy.ActionMask != ActionSprint || legacy.LookDir != [3]float32{0.1, -0.7, 0.7} {
		t.Errorf("24-byte decode shifted a field: %+v", legacy)
	}

	if _, err := DecodeInput(payload[:23]); err == nil {
		t.Error("23-byte input decoded; short payloads must still be rejected")
	}
	if _, err := DecodeInput(append(append([]byte{}, payload...), 0)); err == nil {
		t.Error("26-byte input decoded; trailing bytes must still be rejected")
	}
}

func TestSnapshotRoundTrip(t *testing.T) {
	s := Snapshot{
		Tick:   42,
		AckSeq: 1234,
		Entities: []Entity{
			{ID: 1, Pos: [3]float32{-150.5, 0, 0}, Quat: [4]float32{0, 0, 0, 1}, Vel: [3]float32{0, 0, 4.5}},
			{ID: 2, Pos: [3]float32{0, 150.5, 0.25}, Quat: [4]float32{0.5, 0.5, 0.5, 0.5}, Vel: [3]float32{-1, 2, -3}},
			// Every Phase 2 field non-zero, and PitchQ NEGATIVE. The struct
			// comparison below covers the new fields only if a fixture
			// actually sets them — an all-zero row round-trips through a
			// sign-losing decode (uint8 instead of int8) without complaint,
			// which is exactly how looking-down would come back as
			// looking-up on a remote body.
			{
				ID: 3, Pos: [3]float32{1, 2, 3}, Quat: [4]float32{0, 1, 0, 0}, Vel: [3]float32{4, 5, 6},
				ParentID: 7, Seat: 2, Health: 65535, Flags: FlagGrounded | FlagFiring, PitchQ: -127,
			},
		},
	}
	frame := EncodeSnapshot(s)
	gotTyp, payload, err := DecodeFrame(frame)
	if err != nil {
		t.Fatalf("DecodeFrame: %v", err)
	}
	if gotTyp != MsgSnapshot {
		t.Fatalf("frame type = 0x%04x, want 0x%04x", gotTyp, MsgSnapshot)
	}
	got, err := DecodeSnapshot(payload)
	if err != nil {
		t.Fatalf("DecodeSnapshot: %v", err)
	}
	if got.Tick != s.Tick || got.AckSeq != s.AckSeq || len(got.Entities) != len(s.Entities) {
		t.Fatalf("decoded header = %#v", got)
	}
	for i := range s.Entities {
		if got.Entities[i] != s.Entities[i] {
			t.Fatalf("entity %d = %#v, want %#v", i, got.Entities[i], s.Entities[i])
		}
	}

	// Frame is exactly u16 type + 8-byte header + EntitySize per entity.
	if got, want := len(frame), 2+8+3*EntitySize; got != want {
		t.Fatalf("frame size = %d, want %d", got, want)
	}

	// Empty snapshot.
	e := Snapshot{Tick: 1, Entities: []Entity{}}
	roundTrip(t, MsgSnapshot, EncodeSnapshot(e), func(p []byte) (any, error) { return DecodeSnapshot(p) }, e)
}

func TestSpawnRoundTrip(t *testing.T) {
	s := Spawn{EntityID: 3, EntityType: EntityTypePlayer, Data: []byte("Player 3")}
	roundTrip(t, MsgSpawn, EncodeSpawn(s), func(p []byte) (any, error) { return DecodeSpawn(p) }, s)

	// Empty data.
	e := Spawn{EntityID: 4, EntityType: EntityTypeShip}
	roundTrip(t, MsgSpawn, EncodeSpawn(e), func(p []byte) (any, error) { return DecodeSpawn(p) }, e)
}

func TestDespawnRoundTrip(t *testing.T) {
	d := Despawn{EntityID: 0xABCD}
	roundTrip(t, MsgDespawn, EncodeDespawn(d), func(p []byte) (any, error) { return DecodeDespawn(p) }, d)
}

func TestEventRoundTrip(t *testing.T) {
	e := Event{EntityID: 5, EventID: EventExplosion, Data: []byte{1, 2, 3}}
	roundTrip(t, MsgEvent, EncodeEvent(e), func(p []byte) (any, error) { return DecodeEvent(p) }, e)

	empty := Event{EntityID: 6, EventID: EventExplosion}
	roundTrip(t, MsgEvent, EncodeEvent(empty), func(p []byte) (any, error) { return DecodeEvent(p) }, empty)
}

func TestPingPongRoundTrip(t *testing.T) {
	pi := Ping{TSMs: 0x12345678}
	roundTrip(t, MsgPing, EncodePing(pi), func(p []byte) (any, error) { return DecodePing(p) }, pi)
	po := Pong{TSMs: 0x12345678}
	roundTrip(t, MsgPong, EncodePong(po), func(p []byte) (any, error) { return DecodePong(p) }, po)
}

func TestTerrainRoundTrip(t *testing.T) {
	// Small grid keeps the test fast; encoding is generic over face_grid.
	const grid = 3
	radii := make([]uint16, 6*grid*grid)
	for i := range radii {
		radii[i] = uint16(i * 137)
	}
	te := Terrain{FaceGrid: grid, RadiusMin: 124, RadiusMax: 190, Radii: radii}
	frame := EncodeTerrain(te)
	gotTyp, payload, err := DecodeFrame(frame)
	if err != nil {
		t.Fatalf("DecodeFrame: %v", err)
	}
	if gotTyp != MsgTerrain {
		t.Fatalf("frame type = 0x%04x, want 0x%04x", gotTyp, MsgTerrain)
	}
	got, err := DecodeTerrain(payload)
	if err != nil {
		t.Fatalf("DecodeTerrain: %v", err)
	}
	if got.FaceGrid != grid || got.RadiusMin != 124 || got.RadiusMax != 190 || !reflect.DeepEqual(got.Radii, radii) {
		t.Fatalf("decoded terrain mismatch: grid=%d min=%v max=%v", got.FaceGrid, got.RadiusMin, got.RadiusMax)
	}
	// Payload is 10-byte header + u16 per sample.
	if got, want := len(payload), 10+len(radii)*2; got != want {
		t.Fatalf("payload size = %d, want %d", got, want)
	}
}

func TestDecodeErrors(t *testing.T) {
	if _, _, err := DecodeFrame(nil); err == nil {
		t.Fatal("empty frame: want error")
	}
	if _, _, err := DecodeFrame(make([]byte, MaxMessageSize+1)); err != ErrTooLarge {
		t.Fatalf("oversized frame: want ErrTooLarge, got %v", err)
	}
	cases := []struct {
		name   string
		decode func([]byte) (any, error)
		p      []byte
	}{
		{"hello short", func(p []byte) (any, error) { return DecodeHello(p) }, []byte{1, 2, 3}},
		{"hello name_len mismatch", func(p []byte) (any, error) { return DecodeHello(p) }, []byte{1, 0, 5, 0, 0, 0, 'a'}},
		{"hello_ack short", func(p []byte) (any, error) { return DecodeHelloAck(p) }, []byte{1, 2, 3, 4}},
		{"input short", func(p []byte) (any, error) { return DecodeInput(p) }, make([]byte, 19)},
		{"input trailing", func(p []byte) (any, error) { return DecodeInput(p) }, make([]byte, 21)},
		{"snapshot short", func(p []byte) (any, error) { return DecodeSnapshot(p) }, make([]byte, 7)},
		{"spawn short", func(p []byte) (any, error) { return DecodeSpawn(p) }, make([]byte, 9)},
		{"despawn trailing", func(p []byte) (any, error) { return DecodeDespawn(p) }, make([]byte, 5)},
		{"event data_len mismatch", func(p []byte) (any, error) { return DecodeEvent(p) }, append(make([]byte, 10), 9)},
		{"ping short", func(p []byte) (any, error) { return DecodePing(p) }, make([]byte, 3)},
		{"pong trailing", func(p []byte) (any, error) { return DecodePong(p) }, make([]byte, 5)},
		{"terrain bad radii length", func(p []byte) (any, error) { return DecodeTerrain(p) }, []byte{1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3}},
	}
	for _, c := range cases {
		if _, err := c.decode(c.p); err == nil {
			t.Errorf("%s: want error", c.name)
		}
	}
}

func TestFloat32Exactness(t *testing.T) {
	// f32 wire values must round-trip exactly (they are f32 on the wire).
	for _, f := range []float32{0, 1, -1, 0.1, -0.1, 123.456, math.MaxFloat32 / 1e6} {
		b := putF32(nil, f)
		if got := f32(b); got != f {
			t.Fatalf("f32 round trip: got %v want %v", got, f)
		}
	}
}
