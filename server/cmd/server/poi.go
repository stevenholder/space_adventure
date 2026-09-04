package main

// `server poi` — the Phase 9 placement solver (docs/GDD.md "Placement (the
// solver's contract)"; ROADMAP Phase 9 task 7).
//
// Scans candidate directions on the sphere and reports the sites where a
// new POI's flatten disc clears EVERY world feature — spawn plain, the
// existing zones, the landmarks, the craters, and the sites it has already
// picked this run. camp.json's comment records how the manual version of
// this went twice ("anything the terrain audit checks for has to be in the
// avoid set, not just the things you remembered"); this is that lesson as
// code.
//
// Deterministic: a fixed Fibonacci-sphere candidate lattice, ranked by
// clearance, greedy pick. Same seed and args, same sites. The output is
// meant to be pasted into a committed zone JSON and REVIEWED — the solver
// proposes, git history decides.

import (
	"flag"
	"fmt"
	"math"
	"sort"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/terrain"
)

type poiFeature struct {
	name   string
	dir    terrain.Vec
	extent float64 // metres of surface the feature claims around its centre
}

func runPOI(args []string) error {
	fs := flag.NewFlagSet("poi", flag.ExitOnError)
	seed := fs.Uint64("seed", 1337, "world seed")
	count := fs.Int("count", 2, "sites to pick")
	flatten := fs.Float64("flatten", 26, "flatten_radius of the new POI (m)")
	falloff := fs.Float64("falloff", 10, "flatten_falloff of the new POI (m)")
	spacing := fs.Float64("spacing", 120, "min surface distance between picked sites (m)")
	slopeMax := fs.Float64("slope-max", 12, "max mean slope over the disc (degrees)")
	if err := fs.Parse(args); err != nil {
		return err
	}

	reg, err := defs.Load()
	if err != nil {
		return fmt.Errorf("poi: load defs: %w", err)
	}
	field := terrain.Generate(*seed)

	// The reach of the POI being placed: its own flatten disc plus falloff
	// band plus the style guide's 10 m of daylight.
	reach := *flatten + *falloff + 10

	features := []poiFeature{{"spawn", terrain.SpawnDir, 25 + 12}}
	for id, z := range reg.Zones {
		if z.FlattenRadius == 0 {
			continue
		}
		features = append(features, poiFeature{
			"zone:" + id,
			terrain.Vec{z.OriginDir[0], z.OriginDir[1], z.OriginDir[2]},
			z.FlattenRadius + z.FlattenFalloff,
		})
	}
	for _, l := range field.Landmarks {
		features = append(features, poiFeature{"landmark:" + l.Name, l.Dir, 25})
	}
	for i, c := range field.Craters {
		features = append(features, poiFeature{fmt.Sprintf("crater:%d", i), c.Dir, c.Radius})
	}

	surfDist := func(a, b terrain.Vec) float64 {
		dot := a.Dot(b)
		if dot > 1 {
			dot = 1
		} else if dot < -1 {
			dot = -1
		}
		return math.Acos(dot) * terrain.PlanetRadius
	}

	// Candidate lattice: Fibonacci sphere, dense enough that the best site
	// within a few metres is always sampled. Fixed count = fixed order =
	// deterministic output.
	const candidates = 20000
	golden := math.Pi * (3 - math.Sqrt(5))
	type site struct {
		dir       terrain.Vec
		clearance float64 // distance to the nearest feature edge, minus reach
		slope     float64
	}
	var sites []site
	for i := 0; i < candidates; i++ {
		y := 1 - 2*float64(i)/float64(candidates-1)
		r := math.Sqrt(1 - y*y)
		th := golden * float64(i)
		d := terrain.Vec{r * math.Cos(th), y, r * math.Sin(th)}

		clear := math.Inf(1)
		for _, f := range features {
			c := surfDist(d, f.dir) - f.extent - reach
			if c < clear {
				clear = c
			}
		}
		if clear < 0 {
			continue
		}
		// Mean slope over the disc: centre + a ring at half the flatten
		// radius. Coarse, deliberately — the flatten will level it anyway;
		// this only rejects siting a compound on a mountainside.
		slope := field.Slope(d)
		n := 1.0
		ang := *flatten / 2 / terrain.PlanetRadius
		t := terrain.Normalize(terrain.Vec{-d[1] * d[0], 1 - d[1]*d[1], -d[1] * d[2]})
		if t.Len() < 1e-9 {
			t = terrain.Vec{1, 0, 0}
		}
		for k := 0; k < 6; k++ {
			rot := math.Pi * 2 * float64(k) / 6
			off := t.Scale(math.Cos(rot) * ang).Add(terrain.Cross(d, t).Scale(math.Sin(rot) * ang))
			p := terrain.Normalize(d.Add(off))
			slope += field.Slope(p)
			n++
		}
		slope /= n
		if slope > *slopeMax*math.Pi/180 {
			continue
		}
		sites = append(sites, site{d, clear, slope})
	}

	sort.Slice(sites, func(i, j int) bool {
		if sites[i].clearance != sites[j].clearance {
			return sites[i].clearance > sites[j].clearance
		}
		return sites[i].dir[0] < sites[j].dir[0] // total order for determinism
	})

	var picked []site
	for _, s := range sites {
		ok := true
		for _, p := range picked {
			if surfDist(s.dir, p.dir) < *spacing {
				ok = false
				break
			}
		}
		if ok {
			picked = append(picked, s)
			if len(picked) == *count {
				break
			}
		}
	}

	fmt.Printf("poi solver: seed %d, reach %.0f m, %d features in the avoid set\n",
		*seed, reach, len(features))
	for i, s := range picked {
		fmt.Printf("site %d: origin_dir [%.7f, %.7f, %.7f]  clearance %.1f m  mean slope %.1f°\n",
			i+1, s.dir[0], s.dir[1], s.dir[2], s.clearance, s.slope*180/math.Pi)
	}
	if len(picked) < *count {
		return fmt.Errorf("poi: only %d of %d sites fit — the planet is full at this reach",
			len(picked), *count)
	}
	return nil
}
