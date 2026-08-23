// Package defs parses the embedded server/data content once at startup into
// an indexed registry, and builds the `defs` message payload the client
// receives (docs/PROTOCOL.md, "defs — the data the client needs").
package defs

import (
	"encoding/json"
	"fmt"
	"io/fs"

	"space-adventure/server/data"
)

// MaxPayload is the wire cap on the `defs` message body — one message,
// 64 KiB (docs/PROTOCOL.md, `defs`).
const MaxPayload = 64 * 1024

// Weapon is a weapon's rule table (docs/GDD.md, "Weapons").
type Weapon struct {
	Damage        int     `json:"damage"`
	FireInterval  float64 `json:"fire_interval"`
	Magazine      int     `json:"magazine"`
	ReloadTime    float64 `json:"reload_time"`
	AmmoItem      string  `json:"ammo_item"`
	MaxRange      float64 `json:"max_range"`
	FalloffStart  float64 `json:"falloff_start"`
	FalloffEnd    float64 `json:"falloff_end"`
	FalloffMin    float64 `json:"falloff_min"`
	SpreadBase    float64 `json:"spread_base"`
	SpreadMax     float64 `json:"spread_max"`
	SpreadPerShot float64 `json:"spread_per_shot"`
	SpreadDecay   float64 `json:"spread_decay"`
}

// Item is one row of the item table (server/data/items.json).
type Item struct {
	ID       string  `json:"id"`
	Name     string  `json:"name"`
	Kind     string  `json:"kind"`
	Slot     string  `json:"slot,omitempty"`
	Asset    string  `json:"asset,omitempty"`
	StackMax int     `json:"stack_max"`
	Weapon   *Weapon `json:"weapon,omitempty"`
}

// EntityDef is one entity type's render/health/hitbox def
// (server/data/items.json, "entity_defs").
type EntityDef struct {
	Type      string `json:"type"`
	Asset     string `json:"asset"`
	MaxHealth int    `json:"max_health"`
	Hitbox    struct {
		Radius float64 `json:"radius"`
		Height float64 `json:"height"`
	} `json:"hitbox"`
	Damageable bool    `json:"damageable"`
	Respawn    float64 `json:"respawn,omitempty"`
}

// NPC is one NPC archetype (server/data/npcs.json).
type NPC struct {
	ID    string `json:"id"`
	Name  string `json:"name"`
	Asset string `json:"asset"`
	Kind  string `json:"kind"`
	Verb  string `json:"verb"`
	Stock []struct {
		Item  string `json:"item"`
		Price int64  `json:"price"`
	} `json:"stock"`
}

// ZoneCollider is one static collider authored in a zone's local tangent
// frame (docs/GDD.md, "Static colliders" -> "Authoring frame").
type ZoneCollider struct {
	Kind string     `json:"kind"` // "box" | "sphere"
	Pos  [3]float64 `json:"pos"`
	Half [3]float64 `json:"half"`
	Yaw  float64    `json:"yaw"`
}

// ZoneEntity is one entity placement authored in a zone's local tangent
// frame.
type ZoneEntity struct {
	Type string     `json:"type"` // "npc" | "target"
	Def  string     `json:"def"`
	Pos  [3]float64 `json:"pos"`
	Yaw  float64    `json:"yaw"`
}

// Zone is a zone file parsed as authored — raw local-frame coordinates.
// Composing it to world space is a separate later brief (W2-4); this
// package does not transform it.
type Zone struct {
	ID             string         `json:"id"`
	OriginDir      [3]float64     `json:"origin_dir"`
	FlattenRadius  float64        `json:"flatten_radius"`
	FlattenFalloff float64        `json:"flatten_falloff"`
	Colliders      []ZoneCollider `json:"colliders"`
	Entities       []ZoneEntity   `json:"entities"`
}

// Registry is the indexed, parsed content of server/data, plus the built
// `defs` message payload.
type Registry struct {
	StartCredits int64
	StartItems   []struct {
		Item string `json:"item"`
		Qty  int    `json:"qty"`
	}
	InvSlots int
	Items    map[string]Item
	Entities map[string]EntityDef
	NPCs     map[string]NPC
	Zones    map[string]Zone
	Payload  []byte
}

// itemsFile mirrors server/data/items.json.
type itemsFile struct {
	StartCredits int64 `json:"start_credits"`
	StartItems   []struct {
		Item string `json:"item"`
		Qty  int    `json:"qty"`
	} `json:"start_items"`
	InvSlots   int         `json:"inv_slots"`
	Items      []Item      `json:"items"`
	EntityDefs []EntityDef `json:"entity_defs"`
}

// npcsFile mirrors server/data/npcs.json.
type npcsFile struct {
	NPCs []NPC `json:"npcs"`
}

// Load parses the embedded server/data JSON into an indexed Registry and
// builds the `defs` message payload.
func Load() (*Registry, error) {
	raw, err := data.FS.ReadFile("items.json")
	if err != nil {
		return nil, fmt.Errorf("defs: read items.json: %w", err)
	}
	var itemsF itemsFile
	if err := json.Unmarshal(raw, &itemsF); err != nil {
		return nil, fmt.Errorf("defs: parse items.json: %w", err)
	}

	raw, err = data.FS.ReadFile("npcs.json")
	if err != nil {
		return nil, fmt.Errorf("defs: read npcs.json: %w", err)
	}
	var npcsF npcsFile
	if err := json.Unmarshal(raw, &npcsF); err != nil {
		return nil, fmt.Errorf("defs: parse npcs.json: %w", err)
	}

	zoneFiles, err := fs.Glob(data.FS, "zones/*.json")
	if err != nil {
		return nil, fmt.Errorf("defs: glob zones: %w", err)
	}

	reg := &Registry{
		StartCredits: itemsF.StartCredits,
		StartItems:   itemsF.StartItems,
		InvSlots:     itemsF.InvSlots,
		Items:        make(map[string]Item, len(itemsF.Items)),
		Entities:     make(map[string]EntityDef, len(itemsF.EntityDefs)),
		NPCs:         make(map[string]NPC, len(npcsF.NPCs)),
		Zones:        make(map[string]Zone, len(zoneFiles)),
	}
	for _, it := range itemsF.Items {
		reg.Items[it.ID] = it
	}
	for _, ed := range itemsF.EntityDefs {
		reg.Entities[ed.Type] = ed
	}
	for _, n := range npcsF.NPCs {
		reg.NPCs[n.ID] = n
	}
	for _, zf := range zoneFiles {
		raw, err := data.FS.ReadFile(zf)
		if err != nil {
			return nil, fmt.Errorf("defs: read %s: %w", zf, err)
		}
		var z Zone
		if err := json.Unmarshal(raw, &z); err != nil {
			return nil, fmt.Errorf("defs: parse %s: %w", zf, err)
		}
		reg.Zones[z.ID] = z
	}

	payload, err := buildPayload(reg)
	if err != nil {
		return nil, err
	}
	if len(payload) >= MaxPayload {
		return nil, fmt.Errorf("defs: payload %d bytes exceeds %d byte cap", len(payload), MaxPayload)
	}
	reg.Payload = payload

	return reg, nil
}

// payloadNPC is the client-visible slice of an NPC: display name and verb,
// not its stock (that arrives per-NPC via `shop_list`) or any other
// server-side bookkeeping.
type payloadNPC struct {
	Name  string `json:"name"`
	Asset string `json:"asset"`
	Verb  string `json:"verb"`
}

// payloadConstants are the GDD "Interaction" constants a client needs to
// draw its own interact prompt (docs/GDD.md, "Interaction").
type payloadConstants struct {
	InteractDist float64 `json:"interact_dist"`
	InteractCone float64 `json:"interact_cone"`
}

// payload is the JSON shape of the `defs` message body: what a client needs
// to render and predict, and nothing it is not allowed to know.
type payload struct {
	Items     map[string]Item       `json:"items"`
	Entities  map[string]EntityDef  `json:"entities"`
	NPCs      map[string]payloadNPC `json:"npcs"`
	Constants payloadConstants      `json:"constants"`
}

func buildPayload(reg *Registry) ([]byte, error) {
	p := payload{
		Items:    reg.Items,
		Entities: reg.Entities,
		NPCs:     make(map[string]payloadNPC, len(reg.NPCs)),
		Constants: payloadConstants{
			InteractDist: 3.0,
			InteractCone: 20.0,
		},
	}
	for id, n := range reg.NPCs {
		p.NPCs[id] = payloadNPC{Name: n.Name, Asset: n.Asset, Verb: n.Verb}
	}
	b, err := json.Marshal(p)
	if err != nil {
		return nil, fmt.Errorf("defs: marshal payload: %w", err)
	}
	return b, nil
}
