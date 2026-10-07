// `server drive` — the Go half of the C30 drive-conformance diff
// (test/t23-drive-conformance.mjs), the same file-in/file-out shape as
// `server dump` is for C5/C40.
//
// Script lines (JSONL): {"input": {"throttle": x, "steer": y}} — one per
// tick; an optional {"start": {"dir", "facing"}} line re-parks the rover. The start state is the deterministic mirrored one both sims build
// from primitives they already share: the spawn point, facing the spawn
// bearing, grounded (NOT sim.SpawnRover — the walkable-retry scan is
// server-only and the client has no reason to mirror it).
//
// Output lines: {"tick", "pos", "vel", "quat", "grounded"} — quat included
// because a drive model that agrees on position but disagrees on heading
// diverges one tick later, and C30's bar covers pos/quat/vel.

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

type driveScriptLine struct {
	// Start, when present, re-parks the rover: grounded at rest on the
	// surface along Dir, facing Facing projected onto the tangent plane.
	// t23's steep-slope scenario uses it to start on a > drive_slope_max
	// scarp; SimDump --drive reads the same line the same way.
	Start *struct {
		Dir    [3]float64 `json:"dir"`
		Facing [3]float64 `json:"facing"`
	} `json:"start"`
	Input *struct {
		Throttle float64 `json:"throttle"`
		Steer    float64 `json:"steer"`
		EffMult  float64 `json:"eff_mult"`
	} `json:"input"`
}

type driveDumpLine struct {
	Tick     int        `json:"tick"`
	Pos      [3]float64 `json:"pos"`
	Vel      [3]float64 `json:"vel"`
	Quat     [4]float64 `json:"quat"`
	Grounded bool       `json:"grounded"`
}

func runDrive(args []string) error {
	fs := flag.NewFlagSet("drive", flag.ContinueOnError)
	inputs := fs.String("inputs", "", "JSONL drive script (required)")
	seed := fs.Uint("seed", defaultSeed, "world seed")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if *inputs == "" {
		return fmt.Errorf("drive: -inputs is required")
	}
	f, err := os.Open(*inputs)
	if err != nil {
		return fmt.Errorf("drive: %w", err)
	}
	defer f.Close()

	reg, err := defs.Load()
	if err != nil {
		return fmt.Errorf("drive: loading defs: %w", err)
	}
	field := defs.BuildTerrain(uint64(*seed), reg)
	// Same rule as `dump`: run on the EXACT quantised field a client
	// receives, or the diff measures the representation gap.
	q, err := terrain.Decode(field.Encode())
	if err != nil {
		return fmt.Errorf("drive: re-decoding terrain: %w", err)
	}
	q.Seed = field.Seed
	field = q

	// The mirrored start: spawn point, facing the spawn bearing, grounded.
	start := sim.SpawnState(field)
	up := terrain.Normalize(start.Pos)
	e := &sim.Ent{
		Kind: sim.EntityKind(protocol.EntityTypeVehicle),
		Pos:  [3]float64(start.Pos),
		Quat: [4]float64(sim.QuatFromBasis(terrain.Cross(up, start.Facing), up, start.Facing)),
		Data: &sim.VehicleState{Grounded: true},
	}
	v := e.Data.(*sim.VehicleState)

	enc := json.NewEncoder(os.Stdout)
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	tick := 0
	for sc.Scan() {
		raw := bytes.TrimSpace(sc.Bytes())
		if len(raw) == 0 {
			continue
		}
		var l driveScriptLine
		if err := json.Unmarshal(raw, &l); err != nil {
			return fmt.Errorf("drive: tick %d: %w", tick, err)
		}
		if l.Start != nil {
			su := terrain.Normalize(terrain.Vec(l.Start.Dir))
			sf := terrain.Vec(l.Start.Facing)
			sf = terrain.Normalize(sf.Sub(su.Scale(sf.Dot(su))))
			e.Pos = [3]float64(su.Scale(field.SampleRadius(su)))
			e.Vel = [3]float64{}
			e.Quat = [4]float64(sim.QuatFromBasis(terrain.Cross(su, sf), su, sf))
			v.Grounded = true
		}
		if l.Input == nil {
			continue
		}
		v.Throttle, v.Steer = l.Input.Throttle, l.Input.Steer
		v.EffMult = l.Input.EffMult
		sim.StepRover(e, sim.DT, sim.StepCtx{Terrain: field})
		if err := enc.Encode(driveDumpLine{
			Tick: tick, Pos: e.Pos, Vel: e.Vel, Quat: e.Quat, Grounded: v.Grounded,
		}); err != nil {
			return fmt.Errorf("drive: %w", err)
		}
		tick++
	}
	if err := sc.Err(); err != nil {
		return fmt.Errorf("drive: %w", err)
	}
	fmt.Fprintf(os.Stderr, "drive: %d ticks (seed %d)\n", tick, *seed)
	return nil
}
