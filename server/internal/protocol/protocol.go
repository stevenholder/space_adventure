// Package protocol implements the Space Adventure wire contract from
// docs/PROTOCOL.md: WebSocket binary messages, little-endian, one message
// per WebSocket message.
//
// Frame layout:
//
//	frame = u16 type | payload
//
// The WebSocket transport preserves message boundaries, so the frame carries
// no length prefix; the payload runs to the end of the message. Max message
// size is 64 KiB (exceeding it closes the connection with code 1009).
package protocol

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math"
)

// Message type ids (PROTOCOL.md "Message types").
const (
	MsgHello    uint16 = 0x0001
	MsgHelloAck uint16 = 0x0002
	MsgInput    uint16 = 0x0003
	MsgSnapshot uint16 = 0x0004
	MsgSpawn    uint16 = 0x0005
	MsgDespawn  uint16 = 0x0006
	MsgEvent    uint16 = 0x0007
	MsgPing     uint16 = 0x0008
	MsgPong     uint16 = 0x0009
	MsgTerrain  uint16 = 0x000A
)

// entity_type values (PROTOCOL.md constants).
const (
	EntityTypePlayer uint16 = 0x0001
	EntityTypeShip   uint16 = 0x0002 // reserved, M2
)

// action_mask bits (PROTOCOL.md constants).
const (
	ActionSprint uint16 = 0x0001
	ActionJump   uint16 = 0x0002
	ActionBoost  uint16 = 0x0004 // reserved, M2
)

// event_id values (PROTOCOL.md constants).
const (
	EventExplosion uint16 = 0x0001 // reserved for later
)

// VersionM1 is the M1 protocol version (client_ver / server_ver).
const VersionM1 uint16 = 1

// MaxMessageSize is the maximum WebSocket message size in bytes (64 KiB).
const MaxMessageSize = 64 << 10

// Errors.
var (
	ErrTooLarge   = errors.New("message exceeds 64 KiB limit")
	ErrBadPayload = errors.New("bad payload")
	ErrUnknownMsg = errors.New("unknown message type")
)

// Hello is the C→S join message: u16 client_ver | u32 name_len | bytes name.
type Hello struct {
	ClientVer uint16
	Name      string
}

// HelloAck is the S→C reply: u16 server_ver | u16 tick_hz | u32 world_seed |
// u32 entity_id.
type HelloAck struct {
	ServerVer uint16
	TickHz    uint16
	WorldSeed uint32
	EntityID  uint32
}

// Input is the C→S on-foot command state (latest wins):
// f32 move_x | f32 move_y | f32 look_dir[3] | u16 action_mask | u16 seq.
type Input struct {
	MoveX      float32
	MoveY      float32
	LookDir    [3]float32
	ActionMask uint16
	Seq        uint16
}

// Entity is one snapshot row:
// u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3] (44 bytes).
type Entity struct {
	ID   uint32
	Pos  [3]float32
	Quat [4]float32
	Vel  [3]float32
}

// EntitySize is the wire size of one snapshot entity row in bytes.
const EntitySize = 4 + 3*4 + 4*4 + 3*4

// Snapshot is the S→C full state per tick:
// u32 tick | u16 ack_seq | u16 count | entity × count.
// AckSeq is the only per-client field; the server encodes the body once per
// tick and patches those 2 bytes (payload offset 4) per connection.
type Snapshot struct {
	Tick     uint32
	AckSeq   uint16
	Entities []Entity
}

// Spawn is the S→C entity-appearance message:
// u32 entity_id | u16 entity_type | u32 data_len | bytes data
// (M1: data is the sanitized UTF-8 display name).
type Spawn struct {
	EntityID   uint32
	EntityType uint16
	Data       []byte
}

// Despawn is the S→C entity-leave message: u32 entity_id.
type Despawn struct {
	EntityID uint32
}

// Event is the S→C world event message:
// u32 entity_id | u16 event_id | u32 data_len | bytes data.
type Event struct {
	EntityID uint32
	EventID  uint16
	Data     []byte
}

// Ping is the C→S heartbeat: u32 ts_ms (client-local monotonic ms, echoed).
type Ping struct {
	TSMs uint32
}

// Pong is the S→C heartbeat reply: u32 ts_ms (echo of ping).
type Pong struct {
	TSMs uint32
}

// Terrain is the S→C world surface field:
// u16 face_grid | f32 radius_min | f32 radius_max |
// u16 radii[6 × face_grid × face_grid].
// Radii are indexed radii[face·face_grid² + row·face_grid + col]; a code
// decodes to radius_min + code/65535·(radius_max − radius_min) metres.
// Face order: +X, −X, +Y, −Y, +Z, −Z.
type Terrain struct {
	FaceGrid  uint16
	RadiusMin float32
	RadiusMax float32
	Radii     []uint16
}

// ---- frame ----

// DecodeFrame splits a raw WebSocket message into (type, payload).
func DecodeFrame(msg []byte) (uint16, []byte, error) {
	if len(msg) > MaxMessageSize {
		return 0, nil, ErrTooLarge
	}
	if len(msg) < 2 {
		return 0, nil, fmt.Errorf("%w: frame is %d bytes", ErrBadPayload, len(msg))
	}
	return binary.LittleEndian.Uint16(msg[:2]), msg[2:], nil
}

// ---- encoders (full frames, type prefix included) ----

func putU16(b []byte, v uint16) []byte {
	return binary.LittleEndian.AppendUint16(b, v)
}

func putU32(b []byte, v uint32) []byte {
	return binary.LittleEndian.AppendUint32(b, v)
}

func putF32(b []byte, v float32) []byte {
	return binary.LittleEndian.AppendUint32(b, math.Float32bits(v))
}

// EncodeHello renders a hello frame.
func EncodeHello(h Hello) []byte {
	b := putU16(nil, MsgHello)
	b = putU16(b, h.ClientVer)
	b = putU32(b, uint32(len(h.Name)))
	return append(b, h.Name...)
}

// EncodeHelloAck renders a hello_ack frame.
func EncodeHelloAck(h HelloAck) []byte {
	b := putU16(nil, MsgHelloAck)
	b = putU16(b, h.ServerVer)
	b = putU16(b, h.TickHz)
	b = putU32(b, h.WorldSeed)
	return putU32(b, h.EntityID)
}

// EncodeInput renders an input frame.
func EncodeInput(in Input) []byte {
	b := putU16(nil, MsgInput)
	b = putF32(b, in.MoveX)
	b = putF32(b, in.MoveY)
	for i := 0; i < 3; i++ {
		b = putF32(b, in.LookDir[i])
	}
	b = putU16(b, in.ActionMask)
	return putU16(b, in.Seq)
}

// AppendSnapshotHeader appends the snapshot header (tick, ackSeq, count) to
// buf. It is split from the entity rows so the tick loop can encode the body
// once and patch ack_seq per connection.
func AppendSnapshotHeader(buf []byte, tick uint32, ackSeq uint16, count uint16) []byte {
	buf = append(buf, byte(MsgSnapshot), byte(MsgSnapshot>>8))
	buf = binary.LittleEndian.AppendUint32(buf, tick)
	buf = binary.LittleEndian.AppendUint16(buf, ackSeq)
	return binary.LittleEndian.AppendUint16(buf, count)
}

// AppendEntity appends one snapshot entity row to buf.
func AppendEntity(buf []byte, e Entity) []byte {
	buf = binary.LittleEndian.AppendUint32(buf, e.ID)
	for i := 0; i < 3; i++ {
		buf = putF32(buf, e.Pos[i])
	}
	for i := 0; i < 4; i++ {
		buf = putF32(buf, e.Quat[i])
	}
	for i := 0; i < 3; i++ {
		buf = putF32(buf, e.Vel[i])
	}
	return buf
}

// EncodeSnapshot renders a full snapshot frame.
func EncodeSnapshot(s Snapshot) []byte {
	b := AppendSnapshotHeader(nil, s.Tick, s.AckSeq, uint16(len(s.Entities)))
	for _, e := range s.Entities {
		b = AppendEntity(b, e)
	}
	return b
}

// EncodeSpawn renders a spawn frame.
func EncodeSpawn(s Spawn) []byte {
	b := putU16(nil, MsgSpawn)
	b = putU32(b, s.EntityID)
	b = putU16(b, s.EntityType)
	b = putU32(b, uint32(len(s.Data)))
	return append(b, s.Data...)
}

// EncodeDespawn renders a despawn frame.
func EncodeDespawn(d Despawn) []byte {
	b := putU16(nil, MsgDespawn)
	return putU32(b, d.EntityID)
}

// EncodeEvent renders an event frame.
func EncodeEvent(e Event) []byte {
	b := putU16(nil, MsgEvent)
	b = putU32(b, e.EntityID)
	b = putU16(b, e.EventID)
	b = putU32(b, uint32(len(e.Data)))
	return append(b, e.Data...)
}

// EncodePing renders a ping frame.
func EncodePing(p Ping) []byte {
	b := putU16(nil, MsgPing)
	return putU32(b, p.TSMs)
}

// EncodePong renders a pong frame.
func EncodePong(p Pong) []byte {
	b := putU16(nil, MsgPong)
	return putU32(b, p.TSMs)
}

// EncodeTerrain renders a terrain frame. Radii must have length
// 6·face_grid².
func EncodeTerrain(t Terrain) []byte {
	b := putU16(nil, MsgTerrain)
	b = putU16(b, t.FaceGrid)
	b = putF32(b, t.RadiusMin)
	b = putF32(b, t.RadiusMax)
	for _, r := range t.Radii {
		b = putU16(b, r)
	}
	return b
}

// ---- decoders (payload only) ----

func need(b []byte, n int, what string) error {
	if len(b) < n {
		return fmt.Errorf("%w: %s needs %d bytes, have %d", ErrBadPayload, what, n, len(b))
	}
	if len(b) > n {
		return fmt.Errorf("%w: %s has %d trailing bytes", ErrBadPayload, what, len(b)-n)
	}
	return nil
}

func f32(b []byte) float32 {
	return math.Float32frombits(binary.LittleEndian.Uint32(b))
}

// DecodeHello parses a hello payload.
func DecodeHello(p []byte) (Hello, error) {
	var h Hello
	if len(p) < 6 {
		return h, fmt.Errorf("%w: hello needs at least 6 bytes", ErrBadPayload)
	}
	h.ClientVer = binary.LittleEndian.Uint16(p[0:2])
	n := binary.LittleEndian.Uint32(p[2:6])
	if uint32(len(p)-6) != n {
		return h, fmt.Errorf("%w: hello name_len %d != payload %d", ErrBadPayload, n, len(p)-6)
	}
	h.Name = string(p[6:])
	return h, nil
}

// DecodeHelloAck parses a hello_ack payload.
func DecodeHelloAck(p []byte) (HelloAck, error) {
	var h HelloAck
	if err := need(p, 12, "hello_ack"); err != nil {
		return h, err
	}
	h.ServerVer = binary.LittleEndian.Uint16(p[0:2])
	h.TickHz = binary.LittleEndian.Uint16(p[2:4])
	h.WorldSeed = binary.LittleEndian.Uint32(p[4:8])
	h.EntityID = binary.LittleEndian.Uint32(p[8:12])
	return h, nil
}

// DecodeInput parses an input payload.
func DecodeInput(p []byte) (Input, error) {
	var in Input
	if err := need(p, 24, "input"); err != nil {
		return in, err
	}
	in.MoveX = f32(p[0:4])
	in.MoveY = f32(p[4:8])
	for i := 0; i < 3; i++ {
		in.LookDir[i] = f32(p[8+4*i : 12+4*i])
	}
	in.ActionMask = binary.LittleEndian.Uint16(p[20:22])
	in.Seq = binary.LittleEndian.Uint16(p[22:24])
	return in, nil
}

// DecodeSnapshot parses a snapshot payload.
func DecodeSnapshot(p []byte) (Snapshot, error) {
	var s Snapshot
	if len(p) < 8 {
		return s, fmt.Errorf("%w: snapshot needs at least 8 bytes", ErrBadPayload)
	}
	s.Tick = binary.LittleEndian.Uint32(p[0:4])
	s.AckSeq = binary.LittleEndian.Uint16(p[4:6])
	count := binary.LittleEndian.Uint16(p[6:8])
	if err := need(p, 8+int(count)*EntitySize, "snapshot entities"); err != nil {
		return s, err
	}
	s.Entities = make([]Entity, count)
	off := 8
	for i := range s.Entities {
		e := &s.Entities[i]
		e.ID = binary.LittleEndian.Uint32(p[off : off+4])
		for j := 0; j < 3; j++ {
			e.Pos[j] = f32(p[off+4+4*j : off+8+4*j])
		}
		for j := 0; j < 4; j++ {
			e.Quat[j] = f32(p[off+16+4*j : off+20+4*j])
		}
		for j := 0; j < 3; j++ {
			e.Vel[j] = f32(p[off+32+4*j : off+36+4*j])
		}
		off += EntitySize
	}
	return s, nil
}

// DecodeSpawn parses a spawn payload.
func DecodeSpawn(p []byte) (Spawn, error) {
	var s Spawn
	if len(p) < 10 {
		return s, fmt.Errorf("%w: spawn needs at least 10 bytes", ErrBadPayload)
	}
	s.EntityID = binary.LittleEndian.Uint32(p[0:4])
	s.EntityType = binary.LittleEndian.Uint16(p[4:6])
	n := binary.LittleEndian.Uint32(p[6:10])
	if uint32(len(p)-10) != n {
		return s, fmt.Errorf("%w: spawn data_len %d != payload %d", ErrBadPayload, n, len(p)-10)
	}
	s.Data = append([]byte(nil), p[10:]...)
	return s, nil
}

// DecodeDespawn parses a despawn payload.
func DecodeDespawn(p []byte) (Despawn, error) {
	var d Despawn
	if err := need(p, 4, "despawn"); err != nil {
		return d, err
	}
	d.EntityID = binary.LittleEndian.Uint32(p[0:4])
	return d, nil
}

// DecodeEvent parses an event payload.
func DecodeEvent(p []byte) (Event, error) {
	var e Event
	if len(p) < 10 {
		return e, fmt.Errorf("%w: event needs at least 10 bytes", ErrBadPayload)
	}
	e.EntityID = binary.LittleEndian.Uint32(p[0:4])
	e.EventID = binary.LittleEndian.Uint16(p[4:6])
	n := binary.LittleEndian.Uint32(p[6:10])
	if uint32(len(p)-10) != n {
		return e, fmt.Errorf("%w: event data_len %d != payload %d", ErrBadPayload, n, len(p)-10)
	}
	e.Data = append([]byte(nil), p[10:]...)
	return e, nil
}

// DecodePing parses a ping payload.
func DecodePing(p []byte) (Ping, error) {
	var pi Ping
	if err := need(p, 4, "ping"); err != nil {
		return pi, err
	}
	pi.TSMs = binary.LittleEndian.Uint32(p[0:4])
	return pi, nil
}

// DecodePong parses a pong payload.
func DecodePong(p []byte) (Pong, error) {
	var po Pong
	if err := need(p, 4, "pong"); err != nil {
		return po, err
	}
	po.TSMs = binary.LittleEndian.Uint32(p[0:4])
	return po, nil
}

// DecodeTerrain parses a terrain payload.
func DecodeTerrain(p []byte) (Terrain, error) {
	var t Terrain
	if len(p) < 8 {
		return t, fmt.Errorf("%w: terrain needs at least 8 bytes", ErrBadPayload)
	}
	t.FaceGrid = binary.LittleEndian.Uint16(p[0:2])
	t.RadiusMin = f32(p[2:6])
	t.RadiusMax = f32(p[6:10])
	want := 6 * int(t.FaceGrid) * int(t.FaceGrid)
	if len(p) != 10+2*want {
		return t, fmt.Errorf("%w: terrain radii payload %d != expected %d", ErrBadPayload, len(p)-10, 2*want)
	}
	t.Radii = make([]uint16, want)
	off := 10
	for i := range t.Radii {
		t.Radii[i] = binary.LittleEndian.Uint16(p[off : off+2])
		off += 2
	}
	return t, nil
}
