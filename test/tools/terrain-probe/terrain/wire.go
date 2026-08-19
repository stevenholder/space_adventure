package terrain

import (
	"encoding/binary"
	"errors"
	"math"
)

// ErrBadTerrain is returned when a terrain payload does not match the
// PROTOCOL "terrain" layout.
var ErrBadTerrain = errors.New("terrain: bad payload")

// HeaderSize is the terrain payload prefix before the radius grid.
const HeaderSize = 10

// Encode serialises f to the PROTOCOL "terrain" payload: u16 grid, f32
// radius_min, f32 radius_max, then grid²×6 u16 radii in little-endian, face
// order +X, −X, +Y, −Y, +Z, −Z, row-major (row = v, col = u).
func (f *Field) Encode() []byte {
	buf := make([]byte, HeaderSize+NumFaces*FaceGrid*FaceGrid*2)
	binary.LittleEndian.PutUint16(buf[0:], FaceGrid)
	binary.LittleEndian.PutUint32(buf[2:], floatbits32(RadiusMin))
	binary.LittleEndian.PutUint32(buf[6:], floatbits32(RadiusMax))
	p := HeaderSize
	for face := range NumFaces {
		grid := f.Radii[face]
		for i := 0; i < len(grid); i++ {
			binary.LittleEndian.PutUint16(buf[p:], uint16(int32((grid[i]-RadiusMin)/(RadiusMax-RadiusMin)*65535+0.5)))
			p += 2
		}
	}
	return buf
}

// Decode parses a PROTOCOL "terrain" payload into a Field. The stored radii
// are the exact decoded values (RadiusMin + code/65535·(RadiusMax−
// RadiusMin)), so a field decoded from Encode(f) collides identically.
func Decode(payload []byte) (*Field, error) {
	if len(payload) < HeaderSize {
		return nil, ErrBadTerrain
	}
	grid := binary.LittleEndian.Uint16(payload[0:])
	if grid != FaceGrid {
		return nil, ErrBadTerrain
	}
	if want := HeaderSize + 6*int(grid)*int(grid)*2; len(payload) != want {
		return nil, ErrBadTerrain
	}
	maxR := math.Float32frombits(binary.LittleEndian.Uint32(payload[6:]))
	minR := math.Float32frombits(binary.LittleEndian.Uint32(payload[2:]))
	// Radius range must be sane: positive floor, max above min.
	if minR <= 0 || maxR <= minR {
		return nil, ErrBadTerrain
	}
	f := &Field{}
	p := HeaderSize
	per := int(grid) * int(grid)
	for face := range NumFaces {
		for i := range per {
			code := binary.LittleEndian.Uint16(payload[p:])
			p += 2
			f.Radii[face][i] = float64(minR) + float64(code)/65535*float64(maxR-minR)
		}
	}
	return f, nil
}

func floatbits32(f float64) uint32 {
	return math.Float32bits(float32(f))
}
