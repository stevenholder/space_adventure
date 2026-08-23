// `server codec` — cross-language wire conformance support.
//
// The Go and TypeScript codecs are two implementations of docs/PROTOCOL.md,
// and each one's unit tests only prove it round-trips ITSELF. That is not the
// property that matters: what matters is that they agree with EACH OTHER.
// Phase 2's first cut compiled on both ends, passed both test suites, and
// still disagreed — the TS encoders returned bare payloads where the Go
// parsers expected a framed message, so the seq would have gone on the wire
// as the message type.
//
// This subcommand is the Go half of test/t12-codec-parity.mjs, the same way
// `server dump` is the Go half of the C5 trajectory diff: a file of hex in,
// a file of hex out, so the comparison is a diff rather than bespoke
// harness code.
//
//	server codec emit <out.hex>    write S→C frames (Go encodes; TS must decode)
//	server codec parse <in.hex>    read C→S frames (TS encoded; Go must parse)
package main

import (
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"strings"

	"space-adventure/server/internal/protocol"
)

func runCodec(args []string) error {
	if len(args) != 2 {
		return errors.New("usage: server codec emit|parse <file.hex>")
	}
	switch args[0] {
	case "emit":
		return codecEmit(args[1])
	case "parse":
		return codecParse(args[1])
	default:
		return fmt.Errorf("unknown codec mode %q (use emit|parse)", args[0])
	}
}

// codecEmit writes one `name hex` line per S→C message, with values chosen so
// a byte-order or offset slip cannot coincidentally still decode: asymmetric
// seq/opcode, a negative quat component, and a fractional half-extent.
func codecEmit(path string) error {
	var b strings.Builder
	put := func(name string, frame []byte) {
		fmt.Fprintf(&b, "%s %s\n", name, hex.EncodeToString(frame))
	}
	put("cmd_result", protocol.EncodeCmdResult(protocol.CmdResult{
		Seq: 4097, Opcode: protocol.OpShopBuy, Status: protocol.StatusRefused,
		Data: []byte(`{"reason":"insufficient_credits"}`),
	}))
	put("defs", protocol.EncodeDefs(protocol.Defs{
		Data: []byte(`{"items":[{"id":"weapon.pulse","price":250}]}`),
	}))
	put("colliders", protocol.EncodeColliders(protocol.Colliders{List: []protocol.Collider{
		{Kind: protocol.ColliderBox, Center: [3]float32{12, 1.25, 6}, Half: [3]float32{17, 1.25, 0.3}, Quat: [4]float32{0, 0, 0, 1}},
		{Kind: protocol.ColliderSphere, Center: [3]float32{8, 1, 4}, Half: [3]float32{1, 0, 0}, Quat: [4]float32{0.5, -0.5, 0.5, 0.5}},
	}}))
	return os.WriteFile(path, []byte(b.String()), 0o644)
}

// codecParse reads `name hex` lines the TypeScript codec produced and decodes
// them, printing one JSON-ish line per message for the harness to assert on.
// A frame whose type byte does not match its name is an error: that is exactly
// the unframed-encoder bug this exists to catch.
func codecParse(path string) error {
	raw, err := os.ReadFile(path)
	if err != nil {
		return fmt.Errorf("reading %s: %w", path, err)
	}
	for _, line := range strings.Split(strings.TrimSpace(string(raw)), "\n") {
		f := strings.Fields(line)
		if len(f) != 2 {
			return fmt.Errorf("bad line %q", line)
		}
		frame, err := hex.DecodeString(f[1])
		if err != nil {
			return fmt.Errorf("%s: bad hex: %w", f[0], err)
		}
		typ, payload, err := protocol.DecodeFrame(frame)
		if err != nil {
			return fmt.Errorf("%s: DecodeFrame: %w", f[0], err)
		}
		switch f[0] {
		case "cmd":
			if typ != protocol.MsgCmd {
				return fmt.Errorf("cmd: frame type %#04x, want %#04x (encoder did not frame?)", typ, protocol.MsgCmd)
			}
			c, err := protocol.ParseCmd(payload)
			if err != nil {
				return fmt.Errorf("ParseCmd: %w", err)
			}
			fmt.Printf("cmd seq=%d opcode=%d data=%s\n", c.Seq, c.Opcode, c.Data)
		case "fire":
			if typ != protocol.MsgFire {
				return fmt.Errorf("fire: frame type %#04x, want %#04x (encoder did not frame?)", typ, protocol.MsgFire)
			}
			fr, err := protocol.ParseFire(payload)
			if err != nil {
				return fmt.Errorf("ParseFire: %w", err)
			}
			fmt.Printf("fire seq=%d dir=%g,%g,%g\n", fr.Seq, fr.Dir[0], fr.Dir[1], fr.Dir[2])
		default:
			return fmt.Errorf("unknown message name %q", f[0])
		}
	}
	return nil
}
