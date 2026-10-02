// `server rocks [-seed N] <out.json>` -- the Go half of the rock-scatter
// parity test (test/t38-rock-parity.mjs): this world's rocks, placed on the
// exact wire-quantized field a client receives, exactly as Server.New does.
package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

func runRocks(args []string) error {
	fs := flag.NewFlagSet("rocks", flag.ContinueOnError)
	seed := fs.Uint("seed", defaultSeed, "world seed")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if fs.NArg() != 1 {
		return errors.New("usage: server rocks [-seed N] <out.json>")
	}
	reg, err := defs.Load()
	if err != nil {
		return fmt.Errorf("rocks: loading defs: %w", err)
	}
	field := defs.BuildTerrain(uint64(*seed), reg)
	q, err := terrain.Decode(field.Encode())
	if err != nil {
		return fmt.Errorf("rocks: re-decoding terrain: %w", err)
	}
	type row struct {
		Pos, Dir, Scale [3]float64
		Spin            float64
		Variant         int
	}
	var out []row
	for _, r := range sim.RockScatter(q, uint32(*seed)) {
		out = append(out, row{[3]float64(r.Pos), [3]float64(r.Dir), [3]float64(r.Scale), r.Spin, r.Variant})
	}
	enc, err := json.Marshal(out)
	if err != nil {
		return err
	}
	return os.WriteFile(fs.Arg(0), enc, 0o644)
}
