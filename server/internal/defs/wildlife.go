package defs

import (
	"encoding/json"
	"fmt"
	"io/fs"
	"math"

	"space-adventure/server/internal/terrain"
)

// Herd is one wildlife group: count members of one archetype scattered
// around a centre on open ground (docs/GDD.md "Wildlife — herds and
// wandering (Phase 14)", server/data/wildlife.json).
type Herd struct {
	ID        string     `json:"id"`
	Def       string     `json:"def"`
	Count     int        `json:"count"`
	OriginDir [3]float64 `json:"origin_dir"`
	Spread    float64    `json:"spread"`
	Wander    float64    `json:"wander"`
}

// The GDD audit limits for wildlife.json.
const (
	herdMaxCount     = 12
	herdMaxMembers   = 64   // snapshots carry every entity to every client
	herdMinClearance = 30.0 // m surface distance from spawn and every zone
	herdGoldenAngle  = 2.399963
)

// loadHerds reads wildlife.json (optional: missing = no herds), normalising
// each origin_dir; the audit runs later, once NPCs and zones are indexed.
func loadHerds(fsys fs.FS) ([]Herd, error) {
	raw, err := fs.ReadFile(fsys, "wildlife.json")
	if err != nil {
		return nil, nil
	}
	var wf struct {
		Herds []Herd `json:"herds"`
	}
	if err := json.Unmarshal(raw, &wf); err != nil {
		return nil, fmt.Errorf("defs: parse wildlife.json: %w", err)
	}
	for i := range wf.Herds {
		h := &wf.Herds[i]
		if l := vLen(h.OriginDir); isFinite(l) && l > 0 {
			h.OriginDir = vScale(h.OriginDir, 1/l)
		}
	}
	return wf.Herds, nil
}

// auditHerds is the GDD wildlife audit table, verbatim. Every error names the
// herd.
func auditHerds(reg *Registry) error {
	seen := map[string]bool{}
	total := 0
	surface := func(a, b [3]float64) float64 {
		return math.Acos(math.Max(-1, math.Min(1, vDot(a, vNormalize(b))))) * terrain.PlanetRadius
	}
	for _, h := range reg.Herds {
		if len(h.ID) < 6 || h.ID[:5] != "herd." {
			return fmt.Errorf("defs: herd %q: id must be herd.*", h.ID)
		}
		if seen[h.ID] {
			return fmt.Errorf("defs: herd %s: defined twice", h.ID)
		}
		seen[h.ID] = true
		n, ok := reg.NPCs[h.Def]
		if !ok {
			return fmt.Errorf("defs: herd %s: unknown npc %q", h.ID, h.Def)
		}
		if n.MaxHealth <= 0 || n.MoveSpeed <= 0 {
			return fmt.Errorf("defs: herd %s: %s is not a combatant (max_health and move_speed must be > 0)", h.ID, h.Def)
		}
		if h.Count < 1 || h.Count > herdMaxCount {
			return fmt.Errorf("defs: herd %s: count %d outside 1-%d", h.ID, h.Count, herdMaxCount)
		}
		l := vLen(h.OriginDir)
		if !isFinite(h.OriginDir[0]) || !isFinite(h.OriginDir[1]) || !isFinite(h.OriginDir[2]) || !isFinite(l) || l == 0 {
			return fmt.Errorf("defs: herd %s: origin_dir must be finite and non-zero", h.ID)
		}
		if !isFinite(h.Spread) || h.Spread < 0 {
			return fmt.Errorf("defs: herd %s: spread %v must be >= 0", h.ID, h.Spread)
		}
		if min := 1.5 * n.Radius() * math.Sqrt(float64(h.Count)); h.Count > 1 && h.Spread < min {
			return fmt.Errorf("defs: herd %s: spread %v below %.2f (1.5 x radius x sqrt(count)) — members would stack", h.ID, h.Spread, min)
		}
		if !isFinite(h.Wander) || h.Wander < 0 || h.Wander > n.LeashRadius-2 {
			return fmt.Errorf("defs: herd %s: wander %v outside 0..leash_radius-2 (%v)", h.ID, h.Wander, n.LeashRadius-2)
		}
		c := vScale(h.OriginDir, 1/l)
		if d := surface(c, [3]float64{0, 1, 0}); d < herdMinClearance {
			return fmt.Errorf("defs: herd %s: centre %.1f m from spawn, need >= %v", h.ID, d, herdMinClearance)
		}
		for zid, z := range reg.Zones {
			if d := surface(c, z.OriginDir); d < herdMinClearance {
				return fmt.Errorf("defs: herd %s: centre %.1f m from zone %s, need >= %v", h.ID, d, zid, herdMinClearance)
			}
		}
		total += h.Count
	}
	if total > herdMaxMembers {
		return fmt.Errorf("defs: wildlife.json: %d herd members, budget is %d", total, herdMaxMembers)
	}
	return nil
}

// ComposeHerd places a herd's members (docs/GDD.md "Wildlife", Placement):
// member i at r = spread·sqrt((i+0.5)/count), θ = i·golden angle, in the
// east/north tangent frame ComposeZone builds from origin_dir, glued to the
// surface by radiusFn, facing outward along its offset (a lone member, or
// one with no offset, faces east). Deterministic: no RNG.
func ComposeHerd(h Herd, radiusFn func([3]float64) float64) []Placement {
	up := vNormalize(h.OriginDir)
	ref := [3]float64{0, 0, 1}
	if math.Abs(vDot(ref, up)) > 0.999 {
		ref = [3]float64{1, 0, 0}
	}
	north := vNormalize(vAdd(ref, vScale(up, -vDot(ref, up))))
	east := vCross(up, north)
	origin := vScale(up, radiusFn(up))

	out := make([]Placement, 0, h.Count)
	for i := 0; i < h.Count; i++ {
		r := h.Spread * math.Sqrt((float64(i)+0.5)/float64(h.Count))
		th := float64(i) * herdGoldenAngle
		offset := vAdd(vScale(east, r*math.Cos(th)), vScale(north, r*math.Sin(th)))
		dir := vNormalize(vAdd(origin, offset))
		pos := vScale(dir, radiusFn(dir))

		// Forward is local +Z (the Sim's facing), so the basis is the same
		// right-handed (right, up, forward) one ComposeZone's frame uses.
		fwd := east
		if h.Count > 1 && r > 0 {
			fwd = offset
		}
		fwd = vNormalize(vAdd(fwd, vScale(dir, -vDot(fwd, dir))))
		right := vCross(dir, fwd)
		out = append(out, Placement{
			Type: "npc",
			Def:  h.Def,
			Pos:  pos,
			Quat: [4]float64(quatFromBasis(right, dir, fwd)),
		})
	}
	return out
}
