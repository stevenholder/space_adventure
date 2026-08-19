// Command terrain-probe prints ground truth for a world seed: the generated
// field's Report(), the six landmark placements (with the stored field radius
// at each centre), the crater placement, and SampleRadius at a fixed set of
// probe directions (for cross-validation against the independent Python
// decoder in test/t9-terrain.py).
//
// It runs against a byte-identical copy of server/internal/terrain (see
// test/out/probe-src-*.sha), so it exercises the product code as-is.
package main

import (
	"encoding/json"
	"fmt"
	"math"
	"os"
	"strconv"

	"sa-qa-probe/terrain"
)

type lmOut struct {
	Name       string     `json:"name"`
	Dir        [3]float64 `json:"dir"`
	RAtDir     float64    `json:"r_at_dir"`
	RawAtDir   float64    `json:"raw_at_dir"`
	Footprint  float64    `json:"footprint"`
}
type craterOut struct {
	Dir       [3]float64 `json:"dir"`
	Diam      float64    `json:"diam_m"`
	Depth     float64    `json:"depth_m"`
	RimH      float64    `json:"rim_h_m"`
	RAtCenter float64    `json:"r_at_center"`
	RawCenter float64    `json:"raw_center"`
	RawNoCr   float64    `json:"raw_no_craters"`
	Profile0  float64    `json:"profile_at_center"`
	NodeDir   [3]float64 `json:"node_dir"`
	NodeStored float64   `json:"node_stored"`
	NodeFresh  float64   `json:"node_fresh"`
	RadialWith []float64 `json:"radial_with_craters"`
	RadialNoCr []float64 `json:"radial_no_craters"`
	RadialSt   []float64 `json:"radial_stored"`
}
type out struct {
	Seed        float64             `json:"seed"`
	Report      map[string]any      `json:"report"`
	Landmarks   []lmOut             `json:"landmarks"`
	Craters     []craterOut         `json:"craters"`
	ProbeRadius map[string]float64  `json:"probe_radius"`
}

// fixed probe directions (unit vectors), mirrored in test/t9-terrain.py
var probeDirs = [][3]float64{
	{0, 1, 0}, // spawn
	{0.3333333333, 0.9428090416, 0}, // home beacon direction (50 m out toward +X)
	{1, 0, 0}, {-1, 0, 0}, {0, 0, 1}, {0, 0, -1}, {0, -1, 0}, // face centres
	{0.57735026919, 0.57735026919, 0.57735026919},  // body diagonal
	{-0.57735026919, 0.57735026919, -0.57735026919},
	{0.3, 0.9, 0.31622776602}, // arbitrary
	{0.5, 0.3, -0.81240384047},
}

func norm3(d [3]float64) terrain.Vec {
	l := math.Sqrt(d[0]*d[0] + d[1]*d[1] + d[2]*d[2])
	return terrain.Vec{d[0] / l, d[1] / l, d[2] / l}
}

func main() {
	seed := uint64(1337)
	if len(os.Args) > 1 {
		s, err := strconv.ParseUint(os.Args[1], 10, 64)
		if err != nil {
			fmt.Fprintln(os.Stderr, "bad seed:", err)
			os.Exit(2)
		}
		seed = s
	}
	if len(os.Args) > 2 && os.Args[2] == "decompose" {
		if len(os.Args) != 6 {
			fmt.Fprintln(os.Stderr, "usage: terrain-probe <seed> decompose <x> <y> <z>")
			os.Exit(2)
		}
		var xyz [3]float64
		for i, s := range os.Args[3:6] {
			v, err := strconv.ParseFloat(s, 64)
			if err != nil {
				fmt.Fprintln(os.Stderr, "bad component:", err)
				os.Exit(2)
			}
			xyz[i] = v
		}
		dd := norm3(xyz)
		b, rg, dt, cs := terrain.QADecompose(seed, dd)
		fmt.Printf("base=%.17g ridge=%.17g detail=%.17g craters=%.17g total=%.17g\n",
			b, rg, dt, cs, 150.0+b+rg+dt+cs)
		os.Exit(0)
	}

	f := terrain.Generate(seed)
	rep := f.Report()

	repOut := map[string]any{
		"walkable_fraction":    rep.WalkableFraction,
		"min_r":                rep.MinR,
		"max_r":                rep.MaxR,
		"spawn_flat_err_m":     rep.SpawnFlatErr,
		"clamped":              rep.Clamped,
		"landmark_min_sep_deg": rep.LandmarkMinSep * 180 / math.Pi,
		"seam_max_err":         rep.SeamMaxErr,
	}

	lms, craters := terrain.QAPlacement(seed)
	lmOuts := make([]lmOut, 0, len(lms))
	for _, l := range lms {
		dd := norm3([3]float64{l.Dir[0], l.Dir[1], l.Dir[2]})
		lmOuts = append(lmOuts, lmOut{
			Name:       l.Name,
			Dir:        [3]float64{l.Dir[0], l.Dir[1], l.Dir[2]},
			RAtDir:     f.SampleRadius(dd),
			RawAtDir:   terrain.QARadiusAt(seed, dd),
			Footprint:  l.Footprint,
		})
	}
	crOuts := make([]craterOut, 0, len(craters))
	for _, c := range craters {
		dd := norm3([3]float64{c.Dir[0], c.Dir[1], c.Dir[2]})
		nd, stored, freshNode, freshD := terrain.QANodeDebug(seed, dd)
		axis := [3]float64{0, 1, 0}
		if math.Abs(dd[1]) > 0.9 {
			axis = [3]float64{1, 0, 0}
		}
		wc, nc := terrain.QARadialProfile(seed, dd, terrain.Vec{axis[0], axis[1], axis[2]}, 2, 16)
		st := terrain.QARadialStored(seed, dd, terrain.Vec{axis[0], axis[1], axis[2]}, 2, 16)
		crOuts = append(crOuts, craterOut{
			Dir:       [3]float64{c.Dir[0], c.Dir[1], c.Dir[2]},
			Diam:      2 * c.RimRad * terrain.PlanetRadius,
			Depth:     c.Depth,
			RimH:      c.RimH,
			RAtCenter: f.SampleRadius(dd),
			RawCenter: freshD,
			RawNoCr:   terrain.QARadiusNoCraters(seed, dd),
			Profile0:  terrain.QACraterProfile(0, c.Depth, c.RimH),
			NodeDir:   [3]float64{nd[0], nd[1], nd[2]},
			NodeStored: stored,
			NodeFresh:  freshNode,
			RadialWith: wc,
			RadialNoCr: nc,
			RadialSt:   st,
		})
	}

	probeR := map[string]float64{}
	for i, d := range probeDirs {
		dd := norm3(d)
		probeR[strconv.Itoa(i)] = f.SampleRadius(dd)
	}

	o := out{
		Seed:        float64(seed),
		Report:      repOut,
		Landmarks:   lmOuts,
		Craters:     crOuts,
		ProbeRadius: probeR,
	}
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", " ")
	enc.Encode(o)
}
