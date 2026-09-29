package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"space-adventure/server/internal/defs"

	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// dump runs a JSONL input script through the authoritative sim and emits a
// JSONL trajectory — one output line per input line. This is the shared
// harness for criterion 5 (client-vs-server file diff): the client's Node
// entry point parses the identical script format and must emit byte-identical
// lines.
//
// Script lines (JSONL, one object per line, in order):
//
//	{"seed": 1337}      optional; must be the first line; overrides --seed
//	{"state": {"pos": [x,y,z], "vel": [x,y,z], "grounded": true,
//	           "facing": [x,y,z]}}
//	                optional; initial state before the first input line.
//	                Default: the spawn state (GDD "Spawn").
//	{"input": {"move_x": 1, "move_y": 0, "look": [1,0,0],
//	           "action_mask": 1}}
//	                one per simulated tick (20 Hz); all fields optional,
//	                zero values are the defaults.
//
// Output lines:
//
//	{"tick": 0, "pos": [x,y,z], "vel": [x,y,z], "grounded": true}
//
// tick is 0-based (the first input line is tick 0). Floats are the shortest
// round-trip decimal form, so identical double values print identically on
// both ends.
type scriptLine struct {
	Seed  *uint64      `json:"seed"`
	State *scriptState `json:"state"`
	Input *scriptInput `json:"input"`
}

type scriptState struct {
	Pos      [3]float64 `json:"pos"`
	Vel      [3]float64 `json:"vel"`
	Grounded bool       `json:"grounded"`
	Facing   [3]float64 `json:"facing"`
}

type scriptInput struct {
	MoveX      float64    `json:"move_x"`
	MoveY      float64    `json:"move_y"`
	Look       [3]float64 `json:"look"`
	ActionMask uint16     `json:"action_mask"`
	SprintMult float64    `json:"sprint_mult"`
}

type dumpLine struct {
	Tick     int        `json:"tick"`
	Pos      [3]float64 `json:"pos"`
	Vel      [3]float64 `json:"vel"`
	Grounded bool       `json:"grounded"`
}

func runDump(args []string) error {
	fs := flag.NewFlagSet("dump", flag.ContinueOnError)
	inputs := fs.String("inputs", "", "JSONL input script path")
	seed := fs.Uint("seed", defaultSeed, "world seed")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if *inputs == "" {
		return fmt.Errorf("dump: -inputs is required")
	}
	f, err := os.Open(*inputs)
	if err != nil {
		return fmt.Errorf("dump: %w", err)
	}
	defer f.Close()

	worldSeed := uint64(*seed)
	// The same field the server runs on, zone flattening included. A raw
	// terrain.Generate here would put the Go sim on unflattened ground while
	// the client sim runs on the flattened field it received over the wire,
	// and the C5 conformance diff would compare two different worlds.
	reg, err := defs.Load()
	if err != nil {
		return fmt.Errorf("dump: loading defs: %w", err)
	}
	field := defs.BuildTerrain(worldSeed, reg)
	// Round-trip through the wire encoding so this sim runs on the EXACT
	// quantized field a client receives. Without it the Go sim uses f64
	// radii while the TypeScript sim uses the u16 wire values, and the C5
	// diff measures that representation gap on top of any real divergence —
	// which is precisely what it must not do. Zone flattening made the gap
	// visible: its smoothstep blend band quantizes unevenly, and the seam leg
	// drifted 0.02 m, nearly twice the 0.01125 m bar, with both sims correct.
	if q, err := terrain.Decode(field.Encode()); err == nil {
		q.Seed = field.Seed
		field = q
	} else {
		return fmt.Errorf("dump: re-decoding terrain: %w", err)
	}
	state := sim.SpawnState(field)
	var prevLook sim.Vec
	havePrevLook, inputSeen, stateSeen := false, false, false

	enc := json.NewEncoder(os.Stdout)
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	tick := 0
	lineNo := 0
	for sc.Scan() {
		lineNo++
		raw := bytes.TrimSpace(sc.Bytes())
		if len(raw) == 0 {
			continue
		}
		var l scriptLine
		if err := json.Unmarshal(raw, &l); err != nil {
			return fmt.Errorf("dump: line %d: %w", lineNo, err)
		}
		which := 0
		if l.Seed != nil {
			which++
		}
		if l.State != nil {
			which++
		}
		if l.Input != nil {
			which++
		}
		if which != 1 {
			return fmt.Errorf("dump: line %d: exactly one of seed/state/input per line", lineNo)
		}
		switch {
		case l.Seed != nil:
			if stateSeen || inputSeen {
				return fmt.Errorf("dump: line %d: seed must precede state and input lines", lineNo)
			}
			worldSeed = *l.Seed
			field = defs.BuildTerrain(worldSeed, reg)
			state = sim.SpawnState(field)
		case l.State != nil:
			if inputSeen {
				return fmt.Errorf("dump: line %d: state must precede input lines", lineNo)
			}
			s := l.State
			state = sim.State{
				Pos:      sim.Vec(s.Pos),
				Vel:      sim.Vec(s.Vel),
				Grounded: s.Grounded,
				Facing:   sim.Vec(s.Facing),
			}
			stateSeen = true
		case l.Input != nil:
			inputSeen = true
			in := sim.Input{
				MoveX:      l.Input.MoveX,
				MoveY:      l.Input.MoveY,
				Look:       sim.Vec(l.Input.Look),
				ActionMask: l.Input.ActionMask,
				SprintMult: l.Input.SprintMult,
			}
			// Mirror the server's join: the first step's prevLook is the
			// spawn (or script) facing.
			if !havePrevLook {
				prevLook = state.Facing
				havePrevLook = true
			}
			prevLook = sim.Step(&state, in, prevLook, field, sim.DT)
			if err := enc.Encode(dumpLine{
				Tick:     tick,
				Pos:      state.Pos,
				Vel:      state.Vel,
				Grounded: state.Grounded,
			}); err != nil {
				return fmt.Errorf("dump: %w", err)
			}
			tick++
		}
	}
	if err := sc.Err(); err != nil {
		return fmt.Errorf("dump: %w", err)
	}
	fmt.Fprintf(os.Stderr, "dump: %d ticks (seed %d)\n", tick, worldSeed)
	return nil
}
