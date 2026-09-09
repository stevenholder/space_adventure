// `server flight` — the Go half of the C34 flight-conformance diff
// (test/t25-flight-conformance.mjs), the same shape as `server drive`.
//
// Script lines (JSONL): {"input": {"thrust", "roll", "yaw_rate",
// "pitch_rate", "boost"}} — one per tick, from the mirrored start (spawn
// point, facing the spawn bearing, grounded). Output includes quat, ω,
// grounded and the space flag: any carried state the two sims could
// disagree on goes in the diff.

package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"os"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

type flightScriptLine struct {
	Input *struct {
		Thrust    float64 `json:"thrust"`
		Roll      float64 `json:"roll"`
		YawRate   float64 `json:"yaw_rate"`
		PitchRate float64 `json:"pitch_rate"`
		Boost     bool    `json:"boost"`
		EffMult   float64 `json:"eff_mult"`
	} `json:"input"`
}

type flightDumpLine struct {
	Tick     int        `json:"tick"`
	Pos      [3]float64 `json:"pos"`
	Vel      [3]float64 `json:"vel"`
	Quat     [4]float64 `json:"quat"`
	Omega    [3]float64 `json:"omega"`
	Grounded bool       `json:"grounded"`
	Space    bool       `json:"space"`
}

func runFlight(args []string) error {
	fs := flag.NewFlagSet("flight", flag.ContinueOnError)
	inputs := fs.String("inputs", "", "JSONL flight script (required)")
	seed := fs.Uint("seed", defaultSeed, "world seed")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if *inputs == "" {
		return fmt.Errorf("flight: -inputs is required")
	}
	f, err := os.Open(*inputs)
	if err != nil {
		return fmt.Errorf("flight: %w", err)
	}
	defer f.Close()

	reg, err := defs.Load()
	if err != nil {
		return fmt.Errorf("flight: loading defs: %w", err)
	}
	field := defs.BuildTerrain(uint64(*seed), reg)
	q, err := terrain.Decode(field.Encode())
	if err != nil {
		return fmt.Errorf("flight: re-decoding terrain: %w", err)
	}
	q.Seed = field.Seed
	field = q

	start := sim.SpawnState(field)
	up := terrain.Normalize(start.Pos)
	e := &sim.Ent{
		Kind: sim.EntityKind(protocol.EntityTypeShip),
		Pos:  [3]float64(start.Pos),
		Quat: [4]float64(sim.QuatFromBasis(terrain.Cross(up, start.Facing), up, start.Facing)),
		Data: sim.NewShipState(""),
	}
	st := e.Data.(*sim.ShipState)

	enc := json.NewEncoder(os.Stdout)
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	tick := 0
	for sc.Scan() {
		raw := bytes.TrimSpace(sc.Bytes())
		if len(raw) == 0 {
			continue
		}
		var l flightScriptLine
		if err := json.Unmarshal(raw, &l); err != nil {
			return fmt.Errorf("flight: tick %d: %w", tick, err)
		}
		if l.Input == nil {
			continue
		}
		st.Thrust, st.Roll = l.Input.Thrust, l.Input.Roll
		st.YawRate, st.PitchRate = l.Input.YawRate, l.Input.PitchRate
		st.Boost = l.Input.Boost
		st.EffMult = l.Input.EffMult
		sim.StepShip(e, sim.DT, sim.StepCtx{Terrain: field})
		if err := enc.Encode(flightDumpLine{
			Tick: tick, Pos: e.Pos, Vel: e.Vel, Quat: e.Quat,
			Omega: [3]float64(st.Omega), Grounded: st.Grounded, Space: st.Space,
		}); err != nil {
			return fmt.Errorf("flight: %w", err)
		}
		tick++
	}
	if err := sc.Err(); err != nil {
		return fmt.Errorf("flight: %w", err)
	}
	fmt.Fprintf(os.Stderr, "flight: %d ticks (seed %d)\n", tick, *seed)
	return nil
}
