// `server route` — solve a walkable path between two directions on the planet.
//
// Straight lines do not work here. The terrain deliberately contains slopes
// steeper than max_slope, and steep ground is slid rather than climbed (GDD
// "Rule table"), so a naive bearing walks into a scarp and stops. That is what
// left the camp unreachable and why C10's lap route came out of a
// 13,920-candidate scan instead of a heading.
//
// This is a greedy walk with detours — the same thing a player does when they
// meet a wall: try the direct bearing, and if the ground ahead is too steep,
// fan out to either side until something is walkable and still makes progress.
// It is not optimal and does not need to be; it needs to produce a route a
// walker can actually follow.
//
//	server route -from 0,1,0 -to 0.4,-0.5,-0.77 [-seed 1337]
package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"math"
	"os"
	"strconv"
	"strings"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/terrain"
)

// routeStep is how far along the surface each probe advances, in metres. Small
// enough to squeeze between features, large enough that a full route is a few
// hundred steps rather than tens of thousands.
const routeStep = 4.0

// waypointEvery collapses the raw probe path into waypoints a walker can aim
// at, so followers steer at a handful of points rather than replaying a
// thousand micro-headings.
const waypointEvery = 6

func runRoute(args []string) error {
	fs := flag.NewFlagSet("route", flag.ContinueOnError)
	from := fs.String("from", "0,1,0", "start direction x,y,z")
	to := fs.String("to", "", "goal direction x,y,z")
	seed := fs.Uint64("seed", 1337, "world seed")
	out := fs.String("out", "", "write waypoints JSON here (default stdout)")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if *to == "" {
		return errors.New("route: -to is required")
	}
	start, err := parseDir(*from)
	if err != nil {
		return err
	}
	goal, err := parseDir(*to)
	if err != nil {
		return err
	}

	reg, err := defs.Load()
	if err != nil {
		return fmt.Errorf("route: loading defs: %w", err)
	}
	field := defs.BuildTerrain(*seed, reg)

	path, ok := solveRoute(field, start, goal)
	res := map[string]any{
		"seed":      *seed,
		"from":      start,
		"to":        goal,
		"reachable": ok,
		"steps":     len(path),
		"waypoints": waypointsOf(field, path),
		"length_m":  pathLength(path),
	}
	enc, _ := json.MarshalIndent(res, "", " ")
	if *out == "" {
		fmt.Println(string(enc))
		return nil
	}
	return os.WriteFile(*out, append(enc, '\n'), 0o644)
}

// solveRoute finds a walkable path over the terrain lattice.
//
// This was originally a greedy walk that tried the direct bearing and fanned
// out when blocked. It failed to reach the camp after 3768 m — not because the
// camp was walled off (a flood fill proves 96.6% of the planet is connected to
// spawn, camp included) but because greedy detours spiral: each step is
// locally sensible and the route never commits to going around.
//
// A breadth-first search over the same walkable graph the flood fill uses
// finds a path whenever one exists, and finds a shortest one in lattice steps.
// The graph is 25,350 nodes, so this is cheap.
func solveRoute(f *terrain.Field, start, goal terrain.Vec) ([]terrain.Vec, bool) {
	startKey, goalKey := nodeKeyOf(start), nodeKeyOf(goal)
	if startKey == goalKey {
		return []terrain.Vec{terrain.Normalize(start), terrain.Normalize(goal)}, true
	}

	prev := map[int]int{startKey: -1}
	queue := []int{startKey}
	cell := (math.Pi / 2) / float64(terrain.FaceGrid-1)

	for len(queue) > 0 {
		key := queue[0]
		queue = queue[1:]
		if key == goalKey {
			return rebuildPath(prev, goalKey), true
		}
		d := terrain.Normalize(nodeDir(key))
		for _, n := range neighboursOf(d, cell) {
			nk := nodeKeyOf(n)
			if _, ok := prev[nk]; ok {
				continue
			}
			if !f.Walkable(n) || !walkableBetween(f, d, n) {
				continue
			}
			prev[nk] = key
			queue = append(queue, nk)
		}
	}
	return nil, false
}

// neighboursOf steps one lattice cell in eight tangent directions. Stepping
// through directions rather than (row, col) arithmetic means a step across a
// cube-face border lands on the correct neighbour with no seam special case.
func neighboursOf(d terrain.Vec, cell float64) []terrain.Vec {
	up := d
	ref := terrain.Vec{0, 0, 1}
	if math.Abs(ref.Dot(up)) > 0.999 {
		ref = terrain.Vec{1, 0, 0}
	}
	north := terrain.Normalize(ref.Sub(up.Scale(ref.Dot(up))))
	east := terrain.Cross(up, north)

	out := make([]terrain.Vec, 0, 8)
	for i := 0; i < 8; i++ {
		ang := float64(i) * math.Pi / 4
		dir := terrain.Vec{
			north[0]*math.Cos(ang) + east[0]*math.Sin(ang),
			north[1]*math.Cos(ang) + east[1]*math.Sin(ang),
			north[2]*math.Cos(ang) + east[2]*math.Sin(ang),
		}
		axis := terrain.Normalize(terrain.Cross(up, dir))
		out = append(out, terrain.Normalize(rotateAbout(up, axis, cell)))
	}
	return out
}

func rebuildPath(prev map[int]int, goalKey int) []terrain.Vec {
	var keys []int
	for k := goalKey; k != -1; k = prev[k] {
		keys = append(keys, k)
	}
	out := make([]terrain.Vec, 0, len(keys))
	for i := len(keys) - 1; i >= 0; i-- {
		out = append(out, terrain.Normalize(nodeDir(keys[i])))
	}
	return out
}

// advance returns the point one routeStep along the great circle toward goal,
// rotated by offsetDeg about the local up.
func advance(cur, goal terrain.Vec, offsetDeg float64) terrain.Vec {
	up := terrain.Normalize(cur)
	toward := tangentTo(cur, goal)
	if toward.Len() < 1e-9 {
		return cur
	}
	dir := rotateAbout(terrain.Normalize(toward), up, offsetDeg*math.Pi/180)
	ang := routeStep / terrain.PlanetRadius
	// Rotate cur about (up x dir) by ang — i.e. step along the great circle.
	axis := terrain.Normalize(terrain.Cross(up, dir))
	return terrain.Normalize(rotateAbout(up, axis, ang))
}

func tangentTo(cur, goal terrain.Vec) terrain.Vec {
	up := terrain.Normalize(cur)
	d := goal.Sub(up.Scale(goal.Dot(up)))
	return d
}

func rotateAbout(v, axis terrain.Vec, ang float64) terrain.Vec {
	c, s := math.Cos(ang), math.Sin(ang)
	cross := terrain.Cross(axis, v)
	dot := axis.Dot(v)
	return terrain.Vec{
		v[0]*c + cross[0]*s + axis[0]*dot*(1-c),
		v[1]*c + cross[1]*s + axis[1]*dot*(1-c),
		v[2]*c + cross[2]*s + axis[2]*dot*(1-c),
	}
}

// walkableBetween samples the segment and rejects it if any sample sits on
// ground steeper than the player can climb. Sampling matters: checking only
// the endpoint steps straight over a thin ridge.
func walkableBetween(f *terrain.Field, a, b terrain.Vec) bool {
	const samples = 4
	for i := 1; i <= samples; i++ {
		t := float64(i) / samples
		d := terrain.Normalize(terrain.Vec{
			a[0] + (b[0]-a[0])*t,
			a[1] + (b[1]-a[1])*t,
			a[2] + (b[2]-a[2])*t,
		})
		if !f.Walkable(d) {
			return false
		}
	}
	return true
}

func angleBetween(a, b terrain.Vec) float64 {
	d := terrain.Normalize(a).Dot(terrain.Normalize(b))
	return math.Acos(math.Max(-1, math.Min(1, d)))
}

func pathLength(p []terrain.Vec) float64 {
	total := 0.0
	for i := 1; i < len(p); i++ {
		total += angleBetween(p[i-1], p[i]) * terrain.PlanetRadius
	}
	return total
}

func waypointsOf(f *terrain.Field, p []terrain.Vec) [][3]float64 {
	out := make([][3]float64, 0, len(p)/waypointEvery+2)
	for i := 0; i < len(p); i += waypointEvery {
		d := terrain.Normalize(p[i])
		out = append(out, [3]float64(d.Scale(f.SampleRadius(d))))
	}
	if len(p) > 0 {
		d := terrain.Normalize(p[len(p)-1])
		out = append(out, [3]float64(d.Scale(f.SampleRadius(d))))
	}
	return out
}

func parseDir(s string) (terrain.Vec, error) {
	parts := strings.Split(s, ",")
	if len(parts) != 3 {
		return terrain.Vec{}, fmt.Errorf("route: %q is not x,y,z", s)
	}
	var v terrain.Vec
	for i, p := range parts {
		f, err := strconv.ParseFloat(strings.TrimSpace(p), 64)
		if err != nil {
			return terrain.Vec{}, fmt.Errorf("route: %q: %w", s, err)
		}
		v[i] = f
	}
	return terrain.Normalize(v), nil
}

// --- reachability -----------------------------------------------------------

// runReach floods walkable ground outward from a start direction and reports
// which of the world's features are in the same connected region.
//
// The greedy router answers "did THIS attempt get through"; a flood fill
// answers "is there any walkable path at all", which is the question that
// decides whether a site needs moving rather than a cleverer route.
func runReach(args []string) error {
	fs := flag.NewFlagSet("reach", flag.ContinueOnError)
	from := fs.String("from", "0,1,0", "start direction x,y,z")
	seed := fs.Uint64("seed", 1337, "world seed")
	probe := fs.String("probe", "", "semicolon-separated name=x,y,z directions to test")
	if err := fs.Parse(args); err != nil {
		return err
	}
	start, err := parseDir(*from)
	if err != nil {
		return err
	}
	reg, err := defs.Load()
	if err != nil {
		return err
	}
	field := defs.BuildTerrain(*seed, reg)

	seen := floodWalkable(field, start)

	total := terrain.NumFaces * terrain.FaceGrid * terrain.FaceGrid
	res := map[string]any{
		"seed":            *seed,
		"reachable_nodes": len(seen),
		"lattice_nodes":   total,
		"reachable_frac":  float64(len(seen)) / float64(total),
	}
	if *probe != "" {
		hits := map[string]bool{}
		for _, spec := range strings.Split(*probe, ";") {
			kv := strings.SplitN(spec, "=", 2)
			if len(kv) != 2 {
				continue
			}
			d, err := parseDir(kv[1])
			if err != nil {
				return err
			}
			hits[kv[0]] = seen[nodeKeyOf(d)]
		}
		res["probes"] = hits
	}
	enc, _ := json.MarshalIndent(res, "", " ")
	fmt.Println(string(enc))
	return nil
}

// nodeKeyOf snaps a direction to its lattice cell. Nodes are addressed through
// FaceOf/DirOf rather than by walking (row, col) across face borders, so cube
// seams need no special case — the mapping handles them.
func nodeKeyOf(d terrain.Vec) int {
	face, u, v := terrain.FaceOf(terrain.Normalize(d))
	col := int(math.Round((u + 1) / 2 * float64(terrain.FaceGrid-1)))
	row := int(math.Round((v + 1) / 2 * float64(terrain.FaceGrid-1)))
	col = clampInt(col, 0, terrain.FaceGrid-1)
	row = clampInt(row, 0, terrain.FaceGrid-1)
	return face*terrain.FaceGrid*terrain.FaceGrid + row*terrain.FaceGrid + col
}

func nodeDir(key int) terrain.Vec {
	face := key / (terrain.FaceGrid * terrain.FaceGrid)
	rem := key % (terrain.FaceGrid * terrain.FaceGrid)
	row, col := rem/terrain.FaceGrid, rem%terrain.FaceGrid
	u := 2*float64(col)/float64(terrain.FaceGrid-1) - 1
	v := 2*float64(row)/float64(terrain.FaceGrid-1) - 1
	return terrain.DirOf(face, u, v)
}

func floodWalkable(f *terrain.Field, start terrain.Vec) map[int]bool {
	seen := map[int]bool{}
	startKey := nodeKeyOf(start)
	queue := []int{startKey}
	seen[startKey] = true

	// Step one lattice cell in eight tangent directions and snap back. Going
	// through directions rather than (row, col) arithmetic means a step across
	// a cube-face border lands on the right neighbour automatically.
	cell := (math.Pi / 2) / float64(terrain.FaceGrid-1)

	for len(queue) > 0 {
		key := queue[0]
		queue = queue[1:]
		d := terrain.Normalize(nodeDir(key))
		up := d
		ref := terrain.Vec{0, 0, 1}
		if math.Abs(ref.Dot(up)) > 0.999 {
			ref = terrain.Vec{1, 0, 0}
		}
		north := terrain.Normalize(ref.Sub(up.Scale(ref.Dot(up))))
		east := terrain.Cross(up, north)

		for i := 0; i < 8; i++ {
			ang := float64(i) * math.Pi / 4
			dir := terrain.Vec{
				north[0]*math.Cos(ang) + east[0]*math.Sin(ang),
				north[1]*math.Cos(ang) + east[1]*math.Sin(ang),
				north[2]*math.Cos(ang) + east[2]*math.Sin(ang),
			}
			axis := terrain.Normalize(terrain.Cross(up, dir))
			n := terrain.Normalize(rotateAbout(up, axis, cell))
			nk := nodeKeyOf(n)
			if seen[nk] || !f.Walkable(n) || !walkableBetween(f, d, n) {
				continue
			}
			seen[nk] = true
			queue = append(queue, nk)
		}
	}
	return seen
}

func clampInt(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// --- lap scan ---------------------------------------------------------------

// runLapScan reports, for every azimuth out of spawn, the steepest ground a
// full great-circle lap would cross and which cube faces it visits.
//
// C10's lap is a single great circle. Zone flattening changes the field, and a
// flatten band's outer rim can be steeper than the ground it replaced, so a
// lap that was walkable before a zone existed need not stay walkable. Rather
// than re-running the original 13,920-candidate design scan, this answers the
// narrower question actually being asked: which azimuths still work.
func runLapScan(args []string) error {
	fs := flag.NewFlagSet("lapscan", flag.ContinueOnError)
	seed := fs.Uint64("seed", 1337, "world seed")
	limit := fs.Float64("max-slope", 50, "walkability limit in degrees")
	if err := fs.Parse(args); err != nil {
		return err
	}
	reg, err := defs.Load()
	if err != nil {
		return err
	}
	f := defs.BuildTerrain(*seed, reg)

	spawn := terrain.Normalize(terrain.SpawnDir)
	type cand struct {
		Az       float64 `json:"az"`
		MaxSlope float64 `json:"max_slope_deg"`
		Faces    int     `json:"faces"`
	}
	var best []cand
	for azDeg := 0.0; azDeg < 360; azDeg += 0.5 {
		up := spawn
		ref := terrain.Vec{0, 0, 1}
		if math.Abs(ref.Dot(up)) > 0.999 {
			ref = terrain.Vec{1, 0, 0}
		}
		north := terrain.Normalize(ref.Sub(up.Scale(ref.Dot(up))))
		east := terrain.Cross(up, north)
		a := azDeg * math.Pi / 180
		dir := terrain.Vec{
			north[0]*math.Cos(a) + east[0]*math.Sin(a),
			north[1]*math.Cos(a) + east[1]*math.Sin(a),
			north[2]*math.Cos(a) + east[2]*math.Sin(a),
		}
		axis := terrain.Normalize(terrain.Cross(up, dir))

		maxSlope, faces := 0.0, map[int]bool{}
		const samples = 900 // ~1 m spacing over a 942 m lap
		for i := 0; i < samples; i++ {
			ang := 2 * math.Pi * float64(i) / samples
			p := terrain.Normalize(rotateAbout(up, axis, ang))
			if s := f.Slope(p) * 180 / math.Pi; s > maxSlope {
				maxSlope = s
			}
			fc, _, _ := terrain.FaceOf(p)
			faces[fc] = true
		}
		if maxSlope <= *limit {
			best = append(best, cand{Az: azDeg, MaxSlope: maxSlope, Faces: len(faces)})
		}
	}
	enc, _ := json.MarshalIndent(map[string]any{
		"seed": *seed, "limit_deg": *limit,
		"walkable_azimuths": len(best), "candidates": best,
	}, "", " ")
	fmt.Println(string(enc))
	return nil
}

// runRimScan reports the steepest ground within a radius of each zone origin.
//
// A flatten band's rim slope is roughly (terrain delta) / falloff: level the
// ground inside a radius and blend back over too short a distance and the
// result is a cliff ringing the zone. That is invisible to the zone's own
// tests and shows up far away, as a lap route that no longer has a walkable
// candidate anywhere on the planet.
func runRimScan(args []string) error {
	fs := flag.NewFlagSet("rimscan", flag.ContinueOnError)
	seed := fs.Uint64("seed", 1337, "world seed")
	if err := fs.Parse(args); err != nil {
		return err
	}
	reg, err := defs.Load()
	if err != nil {
		return err
	}
	f := defs.BuildTerrain(*seed, reg)

	out := map[string]any{}
	for id, z := range reg.Zones {
		origin := terrain.Normalize(terrain.Vec(z.OriginDir))
		reach := z.FlattenRadius + z.FlattenFalloff
		if reach <= 0 {
			continue
		}
		maxSlope, atR := 0.0, 0.0
		for r := 0.0; r <= reach+30; r += 1.0 {
			ang := r / terrain.PlanetRadius
			for a := 0.0; a < 360; a += 5 {
				up := origin
				ref := terrain.Vec{0, 0, 1}
				if math.Abs(ref.Dot(up)) > 0.999 {
					ref = terrain.Vec{1, 0, 0}
				}
				north := terrain.Normalize(ref.Sub(up.Scale(ref.Dot(up))))
				east := terrain.Cross(up, north)
				ar := a * math.Pi / 180
				dir := terrain.Vec{
					north[0]*math.Cos(ar) + east[0]*math.Sin(ar),
					north[1]*math.Cos(ar) + east[1]*math.Sin(ar),
					north[2]*math.Cos(ar) + east[2]*math.Sin(ar),
				}
				axis := terrain.Normalize(terrain.Cross(up, dir))
				p := terrain.Normalize(rotateAbout(up, axis, ang))
				if s := f.Slope(p) * 180 / math.Pi; s > maxSlope {
					maxSlope, atR = s, r
				}
			}
		}
		out[id] = map[string]any{
			"flatten_radius": z.FlattenRadius, "flatten_falloff": z.FlattenFalloff,
			"max_slope_deg": maxSlope, "at_radius_m": atR,
		}
	}
	enc, _ := json.MarshalIndent(out, "", " ")
	fmt.Println(string(enc))
	return nil
}
