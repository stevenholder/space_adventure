package defs

import (
	"sort"

	"space-adventure/server/internal/terrain"
)

// BuildTerrain generates the world's terrain field for a seed and applies
// every zone's flattening to it.
//
// This exists so the server and the `dump` subcommand cannot end up on
// different planets. The server flattens zone sites before encoding the
// terrain payload; `dump` used a raw terrain.Generate(seed), so the Go sim ran
// on unflattened ground while the client sim ran on the flattened field it had
// received over the wire. The C5 conformance diff would then have been
// comparing two different worlds — and, worse, passing whenever the scripted
// route happened to avoid a zone.
//
// Zone order is sorted rather than map order: Go randomises map iteration, and
// the blend between two overlapping zones is order-dependent, so an unsorted
// loop would produce a subtly different field on every boot.
func BuildTerrain(seed uint64, reg *Registry) *terrain.Field {
	t := terrain.Generate(seed)
	ApplyZoneFlattening(t, reg)
	return t
}

// ApplyZoneFlattening levels the field under every zone, in a deterministic
// order. Split out so a caller that already has a field can reuse it.
func ApplyZoneFlattening(t *terrain.Field, reg *Registry) {
	if reg == nil {
		return
	}
	ids := make([]string, 0, len(reg.Zones))
	for id := range reg.Zones {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		z := reg.Zones[id]
		terrain.Flatten(t, z.OriginDir, z.FlattenRadius, z.FlattenFalloff)
	}
}
