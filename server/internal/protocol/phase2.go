// Phase 2 message codecs (docs/PROTOCOL.md "Message types"): cmd, cmd_result,
// defs, fire, colliders.
package protocol

import (
	"encoding/binary"
	"fmt"
	"math"
)

// Cmd is the C→S request message: u16 seq | u16 opcode | u32 data_len |
// bytes data (UTF-8 JSON).
type Cmd struct {
	Seq    uint16
	Opcode uint16
	Data   []byte
}

// CmdResult is the S→C reply: u16 seq | u16 opcode | u8 status | u32 data_len
// | bytes data (UTF-8 JSON).
type CmdResult struct {
	Seq    uint16
	Opcode uint16
	Status uint8
	Data   []byte
}

// Defs is the S→C definitions payload: u32 data_len | bytes data (UTF-8 JSON).
type Defs struct {
	Data []byte
}

// Fire is the C→S shot request: u16 seq | f32 dir[3].
type Fire struct {
	Seq uint16
	Dir [3]float32
}

// Collider is one row inside colliders (PROTOCOL.md, 42 bytes):
// u8 kind | u8 _pad | f32 center[3] | f32 half[3] | f32 quat[4].
type Collider struct {
	Kind   uint8
	Center [3]float32
	Half   [3]float32
	Quat   [4]float32
}

// Colliders is the S→C world collision geometry: u16 count | collider × count.
type Colliders struct {
	List []Collider
}

// Prop is one row inside props (PROTOCOL.md, variable length):
// f32 pos[3] | f32 quat[4] | f32 scale | u16 asset_len | bytes asset.
//
// Variable rather than fixed, unlike Collider, because the payload IS a name:
// the asset id straight out of art/manifest.json. The alternative -- a string
// table plus fixed rows of indices -- buys nothing here. Colliders are bulk
// data with up to 1560 rows in a message and every byte counts; props are
// hand-authored dressing and the two zones that have any carry fourteen
// between them.
type Prop struct {
	Asset string
	Pos   [3]float32
	Quat  [4]float32
	Scale float32
}

// Props is the S→C zone dressing: u16 count | prop × count.
//
// Visual, plus collision since 2026-10-02: the solid ones (sim/props.go
// PropBoxes) are boxes both sides derive from this list, never shipped as
// colliders. Nothing that can be shot depends on it.
type Props struct {
	List []Prop
}

// dirLenTolerance is how far a fire.Dir's length may stray from 1 and still
// be accepted (PROTOCOL.md, ParseFire validation).
const dirLenTolerance = 1e-3

// ---- encoders (full frames, type prefix included) ----

// EncodeCmd renders a cmd frame.
func EncodeCmd(c Cmd) []byte {
	b := putU16(nil, MsgCmd)
	b = putU16(b, c.Seq)
	b = putU16(b, c.Opcode)
	b = putU32(b, uint32(len(c.Data)))
	return append(b, c.Data...)
}

// EncodeCmdResult renders a cmd_result frame.
func EncodeCmdResult(c CmdResult) []byte {
	b := putU16(nil, MsgCmdResult)
	b = putU16(b, c.Seq)
	b = putU16(b, c.Opcode)
	b = append(b, c.Status)
	b = putU32(b, uint32(len(c.Data)))
	return append(b, c.Data...)
}

// EncodeDefs renders a defs frame.
func EncodeDefs(d Defs) []byte {
	b := putU16(nil, MsgDefs)
	b = putU32(b, uint32(len(d.Data)))
	return append(b, d.Data...)
}

// EncodeFire renders a fire frame.
func EncodeFire(f Fire) []byte {
	b := putU16(nil, MsgFire)
	b = putU16(b, f.Seq)
	for i := 0; i < 3; i++ {
		b = putF32(b, f.Dir[i])
	}
	return b
}

// EncodeColliders renders a colliders frame.
func EncodeColliders(c Colliders) []byte {
	b := putU16(nil, MsgColliders)
	b = putU16(b, uint16(len(c.List)))
	for _, col := range c.List {
		b = append(b, col.Kind, 0) // _pad
		for i := 0; i < 3; i++ {
			b = putF32(b, col.Center[i])
		}
		for i := 0; i < 3; i++ {
			b = putF32(b, col.Half[i])
		}
		for i := 0; i < 4; i++ {
			b = putF32(b, col.Quat[i])
		}
	}
	return b
}

// EncodeProps renders a props frame.
func EncodeProps(p Props) []byte {
	b := putU16(nil, MsgProps)
	b = putU16(b, uint16(len(p.List)))
	for _, pr := range p.List {
		for i := 0; i < 3; i++ {
			b = putF32(b, pr.Pos[i])
		}
		for i := 0; i < 4; i++ {
			b = putF32(b, pr.Quat[i])
		}
		b = putF32(b, pr.Scale)
		b = putU16(b, uint16(len(pr.Asset)))
		b = append(b, pr.Asset...)
	}
	return b
}

// ---- decoders (payload only) ----

// ParseCmd parses a cmd payload.
func ParseCmd(p []byte) (Cmd, error) {
	var c Cmd
	if len(p) < 8 {
		return c, fmt.Errorf("%w: cmd needs at least 8 bytes, have %d", ErrBadPayload, len(p))
	}
	c.Seq = binary.LittleEndian.Uint16(p[0:2])
	c.Opcode = binary.LittleEndian.Uint16(p[2:4])
	n := binary.LittleEndian.Uint32(p[4:8])
	if n > MaxCmdBody {
		return c, fmt.Errorf("%w: cmd data_len %d exceeds max %d", ErrBadPayload, n, MaxCmdBody)
	}
	if err := need(p, 8+int(n), "cmd data"); err != nil {
		return c, err
	}
	c.Data = append([]byte(nil), p[8:]...)
	return c, nil
}

// ParseCmdResult parses a cmd_result payload.
func ParseCmdResult(p []byte) (CmdResult, error) {
	var c CmdResult
	if len(p) < 9 {
		return c, fmt.Errorf("%w: cmd_result needs at least 9 bytes, have %d", ErrBadPayload, len(p))
	}
	c.Seq = binary.LittleEndian.Uint16(p[0:2])
	c.Opcode = binary.LittleEndian.Uint16(p[2:4])
	c.Status = p[4]
	n := binary.LittleEndian.Uint32(p[5:9])
	if n > MaxCmdBody {
		return c, fmt.Errorf("%w: cmd_result data_len %d exceeds max %d", ErrBadPayload, n, MaxCmdBody)
	}
	if err := need(p, 9+int(n), "cmd_result data"); err != nil {
		return c, err
	}
	c.Data = append([]byte(nil), p[9:]...)
	return c, nil
}

// ParseDefs parses a defs payload.
func ParseDefs(p []byte) (Defs, error) {
	var d Defs
	if len(p) < 4 {
		return d, fmt.Errorf("%w: defs needs at least 4 bytes, have %d", ErrBadPayload, len(p))
	}
	n := binary.LittleEndian.Uint32(p[0:4])
	if err := need(p, 4+int(n), "defs data"); err != nil {
		return d, err
	}
	d.Data = append([]byte(nil), p[4:]...)
	return d, nil
}

// ParseFire parses a fire payload.
func ParseFire(p []byte) (Fire, error) {
	var f Fire
	if err := need(p, 14, "fire"); err != nil {
		return f, err
	}
	f.Seq = binary.LittleEndian.Uint16(p[0:2])
	var lenSq float64
	for i := 0; i < 3; i++ {
		v := f32(p[2+4*i : 6+4*i])
		if math.IsNaN(float64(v)) || math.IsInf(float64(v), 0) {
			return Fire{}, fmt.Errorf("%w: fire dir[%d] is not finite", ErrBadPayload, i)
		}
		f.Dir[i] = v
		lenSq += float64(v) * float64(v)
	}
	if math.Abs(math.Sqrt(lenSq)-1) > dirLenTolerance {
		return Fire{}, fmt.Errorf("%w: fire dir length %v not within %v of 1", ErrBadPayload, math.Sqrt(lenSq), dirLenTolerance)
	}
	return f, nil
}

// ParseColliders parses a colliders payload.
func ParseColliders(p []byte) (Colliders, error) {
	var c Colliders
	if len(p) < 2 {
		return c, fmt.Errorf("%w: colliders needs at least 2 bytes, have %d", ErrBadPayload, len(p))
	}
	count := binary.LittleEndian.Uint16(p[0:2])
	if int(count) > ColliderMax {
		return c, fmt.Errorf("%w: colliders count %d exceeds max %d", ErrBadPayload, count, ColliderMax)
	}
	if err := need(p, 2+int(count)*ColliderSize, "colliders list"); err != nil {
		return c, err
	}
	// finite() is applied per collider below. A non-finite centre or extent
	// reaches the sim's push-out, propagates into the player's position, and
	// is then WRITTEN TO THE PLAYER ROW (docs/ARCHITECTURE.md, "Persistence")
	// — so one bad value does not just glitch a frame, it bricks that player
	// across every future reconnect. Reject it at the boundary, like ParseFire
	// already rejects a non-finite aim vector.
	c.List = make([]Collider, count)
	off := 2
	for i := range c.List {
		col := &c.List[i]
		col.Kind = p[off]
		// p[off+1] is _pad, ignored.
		for j := 0; j < 3; j++ {
			col.Center[j] = f32(p[off+2+4*j : off+6+4*j])
		}
		for j := 0; j < 3; j++ {
			col.Half[j] = f32(p[off+14+4*j : off+18+4*j])
		}
		for j := 0; j < 4; j++ {
			col.Quat[j] = f32(p[off+26+4*j : off+30+4*j])
		}
		for j := 0; j < 3; j++ {
			if !isFiniteF32(col.Center[j]) || !isFiniteF32(col.Half[j]) {
				return Colliders{}, fmt.Errorf("%w: collider %d has a non-finite centre/extent", ErrBadPayload, i)
			}
		}
		for j := 0; j < 4; j++ {
			if !isFiniteF32(col.Quat[j]) {
				return Colliders{}, fmt.Errorf("%w: collider %d has a non-finite quaternion", ErrBadPayload, i)
			}
		}
		off += ColliderSize
	}
	return c, nil
}

// isFiniteF32 reports whether v is neither NaN nor an infinity.
func isFiniteF32(v float32) bool {
	f := float64(v)
	return !math.IsNaN(f) && !math.IsInf(f, 0)
}
