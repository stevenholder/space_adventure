// `server collide` — the Go half of the cross-language collider parity test.
//
// ResolveColliders exists twice, in Go and in TypeScript, and replay converges
// only while the two agree. Each side's unit tests exercise its own
// implementation, which proves neither agrees with the other. That gap is not
// hypothetical in this project: the strafe axis was wrong in BOTH sims for all
// of M1 (they agreed, so the conformance diff passed), and the Phase 2 codecs
// were each self-consistent while disagreeing on framing.
//
// The C5 trajectory diff cannot cover this — its route has no colliders at
// all. So this reads a shared scenario file and emits results for
// test/t13-collide-parity.mjs to diff against the TypeScript port.
//
//	server collide <scenarios.json> <out.json>
package main

import (
	"encoding/json"
	"errors"
	"os"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
)

type collideScenario struct {
	Name      string       `json:"name"`
	Pos       [3]float64   `json:"pos"`
	Vel       [3]float64   `json:"vel"`
	Up        [3]float64   `json:"up"`
	Grounded  bool         `json:"grounded"`
	Radius    float64      `json:"radius"`
	Colliders []jsonCollid `json:"colliders"`
}

type jsonCollid struct {
	Kind   uint8      `json:"kind"`
	Center [3]float64 `json:"center"`
	Half   [3]float64 `json:"half"`
	Quat   [4]float64 `json:"quat"`
}

type collideResult struct {
	Name     string     `json:"name"`
	Pos      [3]float64 `json:"pos"`
	Vel      [3]float64 `json:"vel"`
	Grounded bool       `json:"grounded"`
}

func runCollide(args []string) error {
	if len(args) != 2 {
		return errors.New("usage: server collide <scenarios.json> <out.json>")
	}
	raw, err := os.ReadFile(args[0])
	if err != nil {
		return err
	}
	var scenarios []collideScenario
	if err := json.Unmarshal(raw, &scenarios); err != nil {
		return err
	}

	out := make([]collideResult, 0, len(scenarios))
	for _, s := range scenarios {
		cs := make([]protocol.Collider, len(s.Colliders))
		for i, c := range s.Colliders {
			cs[i] = protocol.Collider{Kind: c.Kind}
			for j := 0; j < 3; j++ {
				cs[i].Center[j] = float32(c.Center[j])
				cs[i].Half[j] = float32(c.Half[j])
			}
			for j := 0; j < 4; j++ {
				cs[i].Quat[j] = float32(c.Quat[j])
			}
		}
		r := s.Radius
		pos, vel, grounded := sim.ResolveColliders(s.Pos, s.Vel, s.Up, s.Grounded, cs,
			func([3]float64) float64 { return r })
		out = append(out, collideResult{Name: s.Name, Pos: pos, Vel: vel, Grounded: grounded})
	}

	enc, err := json.MarshalIndent(out, "", " ")
	if err != nil {
		return err
	}
	return os.WriteFile(args[1], append(enc, '\n'), 0o644)
}
