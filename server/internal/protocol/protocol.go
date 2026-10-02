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
	MsgHello      uint16 = 0x0001
	MsgHelloAck   uint16 = 0x0002
	MsgInput      uint16 = 0x0003
	MsgSnapshot   uint16 = 0x0004
	MsgSpawn      uint16 = 0x0005
	MsgDespawn    uint16 = 0x0006
	MsgEvent      uint16 = 0x0007
	MsgPing       uint16 = 0x0008
	MsgPong       uint16 = 0x0009
	MsgTerrain    uint16 = 0x000A
	MsgBoard      uint16 = 0x000B // Phase 4
	MsgDisembark  uint16 = 0x000C // Phase 4
	MsgSeatResult uint16 = 0x000D // Phase 4
	MsgCmd        uint16 = 0x000E
	MsgCmdResult  uint16 = 0x000F
	MsgDefs       uint16 = 0x0010
	MsgFire       uint16 = 0x0011
	MsgColliders  uint16 = 0x0012
	MsgProps      uint16 = 0x0013
)

// entity_type values (PROTOCOL.md constants).
const (
	EntityTypePlayer     uint16 = 0x0001
	EntityTypeShip       uint16 = 0x0002 // Phase 5
	EntityTypeNPC        uint16 = 0x0003
	EntityTypeTarget     uint16 = 0x0004
	EntityTypeVehicle    uint16 = 0x0005 // Phase 4
	EntityTypeLoot       uint16 = 0x0006 // Phase 3
	EntityTypeProjectile uint16 = 0x0007 // Phase 3
	EntityTypeNode       uint16 = 0x0008 // Phase 12: ore/wreck node; health = yields left
)

// action_mask bits (PROTOCOL.md constants).
const (
	ActionSprint uint16 = 0x0001
	ActionJump   uint16 = 0x0002
	ActionBoost  uint16 = 0x0004 // Phase 5
)

// Entity.Flags bits (PROTOCOL.md constants). 0x20-0x80 reserved, sent as 0.
const (
	FlagGrounded  uint8 = 0x01
	FlagSprinting uint8 = 0x02
	FlagDead      uint8 = 0x04
	FlagFiring    uint8 = 0x08
	FlagSpace     uint8 = 0x10 // Phase 5: the ship is in the space regime
)

// cmd opcodes (PROTOCOL.md constants).
const (
	OpShopList  uint16 = 0x0001
	OpShopBuy   uint16 = 0x0002
	OpEquip     uint16 = 0x0003
	OpInventory uint16 = 0x0004
	OpReload    uint16 = 0x0005
	// Phase 10 (PROTOCOL.md): parties and missions, JSON bodies.
	OpPartyInvite    uint16 = 0x0006
	OpPartyRespond   uint16 = 0x0007
	OpPartyLeave     uint16 = 0x0008
	OpMissionList    uint16 = 0x0009
	OpMissionAccept  uint16 = 0x000A
	OpMissionAbandon uint16 = 0x000B
	OpMissionTurnin  uint16 = 0x000C
	OpMissionShare   uint16 = 0x000D
	OpSkills         uint16 = 0x000E // Phase 11: the full sheet
	// Phase 12 (PROTOCOL.md): the artisan loop.
	OpShopSell     uint16 = 0x000F
	OpGather       uint16 = 0x0010
	OpGatherCancel uint16 = 0x0011
	OpCraft        uint16 = 0x0012
	OpUse          uint16 = 0x0013 // Phase 13: a consumable or a worn ability
	OpShopBuyback  uint16 = 0x0014 // Phase 13: re-buy something sold this session, at what the shop paid
)

// cmd_result status codes (PROTOCOL.md constants).
const (
	StatusOK            uint8 = 0
	StatusUnknownOpcode uint8 = 1
	StatusMalformed     uint8 = 2
	StatusRefused       uint8 = 3
	StatusRateLimited   uint8 = 4
	StatusNotFound      uint8 = 5
)

// event_id values (PROTOCOL.md constants).
const (
	EventExplosion   uint16 = 0x0001 // reserved for later
	EventShotFired   uint16 = 0x0002
	EventHit         uint16 = 0x0003
	EventDeath       uint16 = 0x0004
	EventLootDropped uint16 = 0x0005
	EventEquipped    uint16 = 0x0006 // Phase 3.5 (C16): data = item id, UTF-8
	// Phase 10 (PROTOCOL.md): JSON data, unicast unless noted.
	EventMissionProgress uint16 = 0x0007
	EventMissionComplete uint16 = 0x0008
	EventPartyUpdate     uint16 = 0x0009
	EventPriorityOffer   uint16 = 0x000A // broadcast
	EventPartyInvited    uint16 = 0x000B
	EventMissionShared   uint16 = 0x000C // data = the full mission template
	EventSkillXP         uint16 = 0x000D // Phase 11: {skill,xp,level,next_at,leveled}
	EventGatherEnd       uint16 = 0x000E // Phase 12: {node,reason,item,qty}, unicast
	EventWorn            uint16 = 0x000F // armor: data = "slot=item" UTF-8 (item empty = cleared), broadcast + replayed at join
)

// collider kinds (PROTOCOL.md `colliders`).
const (
	ColliderBox    uint8 = 0
	ColliderSphere uint8 = 1
)

// VersionM1 is the M1 protocol version (client_ver / server_ver).
const VersionM1 uint16 = 1

// VersionPhase2 is the Phase 2 protocol version (client_ver / server_ver).
const VersionPhase2 uint16 = 2

// MaxMessageSize is the maximum WebSocket message size in bytes (64 KiB).
const MaxMessageSize = 64 << 10

// MaxCmdBody is the cmd/cmd_result JSON body cap in bytes (4 KiB).
const MaxCmdBody = 4 << 10

// ColliderSize is the wire size of one collider row in bytes.
const ColliderSize = 1 + 1 + 3*4 + 3*4 + 4*4 // 42

// ColliderMax is how many colliders fit one 64 KiB message.
const ColliderMax = 1560

// PropMax is how many props one `props` message may carry. Props are hand-
// authored zone dressing, not bulk data -- the two zones that have any carry
// fourteen between them -- so this is a sanity bound on a hand-edited file
// rather than a limit anyone is expected to reach.
const PropMax = 4096

// Errors.
var (
	ErrTooLarge   = errors.New("message exceeds 64 KiB limit")
	ErrBadPayload = errors.New("bad payload")
	ErrUnknownMsg = errors.New("unknown message type")
)

// Hello is the C→S join message: u16 client_ver | u32 name_len | bytes name |
// u32 token_len | bytes token.
type Hello struct {
	ClientVer uint16
	Name      string
	Token     string
}

// MaxTokenLen is the maximum accepted hello token length in bytes.
const MaxTokenLen = 64

// HelloAck is the S→C reply: u16 server_ver | u16 tick_hz | u32 world_seed |
// u32 entity_id.
type HelloAck struct {
	ServerVer uint16
	TickHz    uint16
	WorldSeed uint32
	EntityID  uint32
}

// Input is the C→S command state (queued, one applied per tick):
// f32 v[5] | u16 action_mask | u16 seq | u8 mode.
//
// The five floats are one vector whose meaning Mode selects, so the message
// never changes shape between modes. Mode 0 is on foot, and MoveX/MoveY/
// LookDir are its names for v[0..4].
//
// Mode is the LAST field and it is optional: appending rather than prepending
// left every other field at the offset it has had since Phase 1, and a
// 24-byte payload decodes as mode 0. That is what lets the mode byte land
// without a version flip and without updating every client in lockstep — the
// same shape as Hello, whose payload may end after the name.
type Input struct {
	MoveX      float32
	MoveY      float32
	LookDir    [3]float32
	ActionMask uint16
	Seq        uint16
	Mode       uint8
}

// Entity is one snapshot row (PROTOCOL.md, 54 bytes):
// u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
// | u32 parent_id | u16 seat | u16 health | u8 flags | i8 pitch_q
//
// ParentID and Seat are always 0 in Phase 2; Phase 4 fills them in.
type Entity struct {
	ID       uint32
	Pos      [3]float32
	Quat     [4]float32
	Vel      [3]float32
	ParentID uint32
	Seat     uint16
	Health   uint16
	Flags    uint8
	PitchQ   int8
}

// EntitySize is the wire size of one snapshot entity row in bytes.
const EntitySize = 4 + 3*4 + 4*4 + 3*4 + 4 + 2 + 2 + 1 + 1 // 54

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
	b = append(b, h.Name...)
	b = putU32(b, uint32(len(h.Token)))
	return append(b, h.Token...)
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
	b = putU16(b, in.Seq)
	return append(b, in.Mode)
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
	buf = binary.LittleEndian.AppendUint32(buf, e.ParentID)
	buf = binary.LittleEndian.AppendUint16(buf, e.Seat)
	buf = binary.LittleEndian.AppendUint16(buf, e.Health)
	buf = append(buf, e.Flags)
	buf = append(buf, byte(e.PitchQ))
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

// DecodeHello parses a hello payload. A payload that ends after the name
// (no token fields at all) is a token-less hello from an older client, not a
// protocol violation: it parses with Token == "".
func DecodeHello(p []byte) (Hello, error) {
	var h Hello
	if len(p) < 6 {
		return h, fmt.Errorf("%w: hello needs at least 6 bytes", ErrBadPayload)
	}
	h.ClientVer = binary.LittleEndian.Uint16(p[0:2])
	n := binary.LittleEndian.Uint32(p[2:6])
	if uint32(len(p)-6) < n {
		return h, fmt.Errorf("%w: hello name_len %d != payload %d", ErrBadPayload, n, len(p)-6)
	}
	rest := p[6:]
	h.Name = string(rest[:n])
	rest = rest[n:]
	if len(rest) == 0 {
		return h, nil
	}
	if len(rest) < 4 {
		return h, fmt.Errorf("%w: hello token_len needs 4 bytes, have %d", ErrBadPayload, len(rest))
	}
	tn := binary.LittleEndian.Uint32(rest[0:4])
	rest = rest[4:]
	if uint32(len(rest)) != tn {
		return h, fmt.Errorf("%w: hello token_len %d != payload %d", ErrBadPayload, tn, len(rest))
	}
	if tn > MaxTokenLen {
		return h, fmt.Errorf("%w: hello token_len %d exceeds max %d", ErrBadPayload, tn, MaxTokenLen)
	}
	for _, c := range rest {
		if c < 0x21 || c > 0x7E {
			return h, fmt.Errorf("%w: hello token has non-printable byte 0x%02x", ErrBadPayload, c)
		}
	}
	h.Token = string(rest)
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
	// Exactly 24 (pre-mode-byte) or exactly 25. Not a minimum: `need` rejects
	// trailing bytes on purpose, because a payload that is the wrong length is
	// usually a field-alignment bug rather than a longer message, and the
	// silent version of that is every field read one offset out.
	if len(p) != 24 && len(p) != 25 {
		return in, fmt.Errorf("%w: input needs 24 or 25 bytes, have %d", ErrBadPayload, len(p))
	}
	in.MoveX = f32(p[0:4])
	in.MoveY = f32(p[4:8])
	for i := 0; i < 3; i++ {
		in.LookDir[i] = f32(p[8+4*i : 12+4*i])
	}
	in.ActionMask = binary.LittleEndian.Uint16(p[20:22])
	in.Seq = binary.LittleEndian.Uint16(p[22:24])
	// A 24-byte payload predates the mode byte and means mode 0.
	if len(p) >= 25 {
		in.Mode = p[24]
	}
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
		e.ParentID = binary.LittleEndian.Uint32(p[off+44 : off+48])
		e.Seat = binary.LittleEndian.Uint16(p[off+48 : off+50])
		e.Health = binary.LittleEndian.Uint16(p[off+50 : off+52])
		e.Flags = p[off+52]
		e.PitchQ = int8(p[off+53])
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
