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
	// Class picks the client's hold clips ("" long gun, "pistol"); the
	// server only carries it to the client in defs.
	Class string `json:"class,omitempty"`
}

// Melee is a hand weapon's swing (slot `melee`, GDD "Melee"). Every body
// whose capsule comes within Range of the wielder's chest and lies inside
// Arc degrees of the facing takes Damage; Arc 360 hits all around. Interval
// is the swing cadence, server-enforced like fire_interval. Hands picks the
// hold (1 or 2) and is the client's only reason to read it.
type Melee struct {
	Damage   int     `json:"damage"`
	Interval float64 `json:"interval"`
	Range    float64 `json:"range"`
	Arc      float64 `json:"arc"`
	Hands    int     `json:"hands"`
}

// Item is one row of the item table (server/data/items.json).
type Item struct {
	ID       string  `json:"id"`
	Name     string  `json:"name"`
	Kind     string  `json:"kind"`
	Slot     string  `json:"slot,omitempty"`
	Asset    string  `json:"asset,omitempty"`
	Rarity   string  `json:"rarity,omitempty"` // Phase 8: common…legendary; absent reads as common
	StackMax int     `json:"stack_max"`
	Weapon   *Weapon `json:"weapon,omitempty"`
	Melee    *Melee  `json:"melee,omitempty"`
	// Phase 11.7: the character panel reads these. desc is the tooltip
	// line; armor cuts incoming damage (sim.Mitigate, GDD "Equipment").
	Desc  string `json:"desc,omitempty"`
	Armor *Armor `json:"armor,omitempty"`
	// Phase 12: Value is what shop_sell pays before sell_rate; 0/absent is
	// unsellable. Supersedes names a lesser tool this one stands in for
	// (a node asking for tool.drill accepts tool.drill.mk2).
	Value      int64  `json:"value,omitempty"`
	Supersedes string `json:"supersedes,omitempty"`
	// Phase 13 (GDD "use", "Weapon mods"): what `use` does with the item,
	// and the deltas a worn mod adds to the primary's weapon table.
	Consumable *Consumable `json:"consumable,omitempty"`
	Ability    *Ability    `json:"ability,omitempty"`
	Mod        *Mod        `json:"mod,omitempty"`
}

// Consumable is a one-shot: one unit leaves the bag, Heal lands, the item
// id cools for Cooldown seconds.
type Consumable struct {
	Heal     int     `json:"heal,omitempty"`
	Cooldown float64 `json:"cooldown"`
	Throw    *Throw  `json:"throw,omitempty"`
}

// Throw makes a consumable a thrown charge (GDD "Throwables"): it leaves
// the hand at Speed m/s along the look, falls under gravity, and bursts on
// the first thing it touches, dealing Damage at the centre falling off
// linearly to half at Radius.
type Throw struct {
	Damage int     `json:"damage"`
	Radius float64 `json:"radius"`
	Speed  float64 `json:"speed"`
}

// Ability is what worn gear does on `use`. ID names the effect the server
// implements ("scan"); Range is the effect's reach where it has one.
type Ability struct {
	ID       string  `json:"id"`
	Range    float64 `json:"range,omitempty"`
	Cooldown float64 `json:"cooldown"`
}

// Mod is a set of additive deltas onto a Weapon table. Every field is
// optional; zero adds nothing.
type Mod struct {
	Damage       int     `json:"damage,omitempty"`
	Magazine     int     `json:"magazine,omitempty"`
	MaxRange     float64 `json:"max_range,omitempty"`
	FalloffStart float64 `json:"falloff_start,omitempty"`
	FalloffEnd   float64 `json:"falloff_end,omitempty"`
}

// Node is one resource node def (server/data/nodes.json, Phase 12, GDD
// "Nodes"). A placement's health pool is Yields; Channel and Respawn are
// seconds; Loot is rolled once per yield; XP lands in Skill per yield.
type Node struct {
	ID      string  `json:"id"`
	Name    string  `json:"name"`
	Asset   string  `json:"asset"`
	Skill   string  `json:"skill"`
	Level   int     `json:"level"`
	Tool    string  `json:"tool"`
	Channel float64 `json:"channel"`
	Yields  int     `json:"yields"`
	Respawn float64 `json:"respawn"`
	Loot    string  `json:"loot"`
	XP      int64   `json:"xp"`
}

// Recipe is one workbench recipe (server/data/recipes.json, Phase 12, GDD
// "The workbench and recipes"): every input × qty is consumed, the output ×
// qty granted, atomically; Level gates on Engineering.
type Recipe struct {
	ID     string    `json:"id"`
	Name   string    `json:"name"`
	Level  int       `json:"level"`
	Inputs []ItemQty `json:"inputs"`
	Output ItemQty   `json:"output"`
	XP     int64     `json:"xp"`
}

// ItemQty is an item id and a count.
type ItemQty struct {
	Item string `json:"item"`
	Qty  int    `json:"qty"`
}

// SellRate is the fraction of an item's Value a shop pays (GDD
// "shop_sell"). Shipped in defs.constants so the SELL column can show it.
const SellRate = 0.5

// Armor is a wearable's protective value. Summed over the worn slots into
// the character panel's ARMOR stat and into sim.Mitigate.
type Armor struct {
	Value int `json:"value"`
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

// Mission is one template from server/data/missions.json (Phase 10,
// docs/GDD.md "Missions and parties"). Types: kill, scout, fetch, bounty.
type Mission struct {
	ID            string `json:"id"`
	Type          string `json:"type"`
	Name          string `json:"name"`
	Text          string `json:"text"`
	Archetype     string `json:"archetype,omitempty"`
	Count         int    `json:"count,omitempty"`
	Item          string `json:"item,omitempty"`
	Poi           string `json:"poi,omitempty"`
	Reward        int64  `json:"reward"`
	Board         bool   `json:"board,omitempty"`
	Starter       bool   `json:"starter,omitempty"`
	ClaimMinutes  int    `json:"claim_minutes,omitempty"`
	RepostMinutes int    `json:"repost_minutes,omitempty"`
}

// Skill is one roster row (server/data/skills.json, Phase 11).
type Skill struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Reserved bool   `json:"reserved,omitempty"`
	Efficacy struct {
		Kind     string  `json:"kind"`
		PerLevel float64 `json:"per_level"`
	} `json:"efficacy,omitempty"`
}

// Synergy is one declared cross-skill bonus.
type Synergy struct {
	Source   string  `json:"source"`
	Target   string  `json:"target"`
	What     string  `json:"what"`
	PerLevel float64 `json:"per_level"`
	Where    string  `json:"where,omitempty"` // "" = everywhere, "poi" = inside a discovered POI
}

// SkillAwards is the verb → XP table.
type SkillAwards struct {
	DamageXPPerPoint int64 `json:"damage_xp_per_point"`
	KillXP           int64 `json:"kill_xp"`
	KillXPWarlord    int64 `json:"kill_xp_warlord"`
	SprintXPPer10m   int64 `json:"sprint_xp_per_10m"`
	DriveXPPer10m    int64 `json:"drive_xp_per_10m"`
	FlyXPPer10m      int64 `json:"fly_xp_per_10m"`
	LandingXP        int64 `json:"landing_xp"`
	PickupXPPerItem  int64 `json:"pickup_xp_per_item"`
	CommerceXPPer5cr int64 `json:"commerce_xp_per_5cr"`
	DiscoveryXP      int64 `json:"discovery_xp"`
	ScoutMissionXP   int64 `json:"scout_mission_xp"`
}

// UnlockRequirement gates a purchase behind a skill level.
type UnlockRequirement struct {
	Item  string `json:"item"`
	Skill string `json:"skill"`
	Level int    `json:"level"`
}

// NPC is one NPC archetype (server/data/npcs.json).
type NPC struct {
	ID    string `json:"id"`
	Name  string `json:"name"`
	Asset string `json:"asset"`
	Kind  string `json:"kind"`
	Verb  string `json:"verb"`
	// Armor drawn on the body (item ids by worn slot), replayed to a joiner
	// as `worn` events right after the NPC's spawn. Cosmetic only.
	Worn map[string]string `json:"worn"`
	// Weapon in hand (item id), replayed as an `equipped` event after the
	// spawn. Cosmetic: NPC combat does not read it.
	Primary string `json:"primary"`
	// Melee is a hand weapon (item id with a `melee` table). An archetype
	// with one fights with ITS numbers inside its reach; one that also
	// shoots (projectile_speed) swaps to it when a target closes in.
	Melee string `json:"melee,omitempty"`
	// MeleeDamage is what this archetype's swing deals when it also shoots
	// (its attack_damage is the round's); 0 = the weapon's own damage.
	MeleeDamage int `json:"melee_damage,omitempty"`
	Stock       []struct {
		Item  string `json:"item"`
		Price int64  `json:"price"`
	} `json:"stock"`
	// The combat archetype's tunables (docs/GDD.md, "NPC archetypes" table).
	// All zero for non-combat (shop) NPCs, which carry none of these keys in
	// npcs.json — a zero MoveSpeed is how the AI runner tells the two apart.
	MaxHealth       int     `json:"max_health"`
	MoveSpeed       float64 `json:"move_speed"`
	AggroRadius     float64 `json:"aggro_radius"`
	LeashRadius     float64 `json:"leash_radius"`
	AttackRange     float64 `json:"attack_range"`
	AttackDamage    int     `json:"attack_damage"`
	AttackInterval  float64 `json:"attack_interval"`
	AttackWindup    float64 `json:"attack_windup"`
	ProjectileSpeed float64 `json:"projectile_speed"`
	TurnRate        float64 `json:"turn_rate"`
	// Loot is the table id rolled when this archetype dies (loot.json).
	Loot string `json:"loot"`
	// Body size in metres: hitbox capsule radius and height, and the eye the
	// AI sees and shoots from. Zero means the standing-person defaults; read
	// them through Radius/Height/EyeHeight, never directly.
	RadiusM    float64 `json:"radius,omitempty"`
	HeightM    float64 `json:"height,omitempty"`
	EyeHeightM float64 `json:"eye_height,omitempty"`
}

// The standing-person body every archetype without its own size gets: the
// items.json "npc" hitbox (0.35 x 1.8), sim.BodyRadius and
// terrain.EyeHeightMeters.
const (
	DefaultNPCRadius    = 0.35
	DefaultNPCHeight    = 1.8
	DefaultNPCEyeHeight = 1.7
)

// Radius is the archetype's hitbox and body-sphere radius.
func (n NPC) Radius() float64 { return orDefault(n.RadiusM, DefaultNPCRadius) }

// Height is the archetype's hitbox capsule height.
func (n NPC) Height() float64 { return orDefault(n.HeightM, DefaultNPCHeight) }

// EyeHeight is where the archetype sees and shoots from, and where a player
// aims to interact with it.
func (n NPC) EyeHeight() float64 { return orDefault(n.EyeHeightM, DefaultNPCEyeHeight) }

func orDefault(v, def float64) float64 {
	if v > 0 {
		return v
	}
	return def
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
	Type string     `json:"type"` // "npc" | "target" | "node" (Phase 12)
	Def  string     `json:"def"`
	Pos  [3]float64 `json:"pos"`
	Yaw  float64    `json:"yaw"`
}

// ZoneProp is one piece of visual dressing authored in a zone's local tangent
// frame: a model id from art/manifest.json, where it stands and which way it
// faces.
//
// Scale is optional and defaults to 1 — the models are imported at their real
// size, so a barrel that needs scaling is usually a barrel imported wrong.
type ZoneProp struct {
	Asset string     `json:"asset"`
	Pos   [3]float64 `json:"pos"`
	Yaw   float64    `json:"yaw"`
	Scale float64    `json:"scale,omitempty"`
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
	Props          []ZoneProp     `json:"props"`
	// Layout (Phase 9): kit placements on the 4 m module grid, expanded at
	// load into DERIVED colliders and props (layout.go) — one source for
	// what you see and what you hit.
	Layout *ZoneLayout `json:"layout,omitempty"`
}

// LootEntry is one row of a loot table in server/data/loot.json: an item, how
// many, and an independent roll chance.
type LootEntry struct {
	Item   string  `json:"item"`
	Qty    int     `json:"qty"`
	Chance float64 `json:"chance"`
}

type lootFile struct {
	Tables map[string][]LootEntry `json:"tables"`
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
	// EquipSlots is the ordered slot set the character panel draws and
	// Equip validates against (items.json equip_slots, Phase 11.7).
	EquipSlots []string
	Missions   map[string]Mission
	Skills     []Skill
	Synergies  []Synergy
	Awards     SkillAwards
	Unlocks    []UnlockRequirement
	Items      map[string]Item
	Entities   map[string]EntityDef
	NPCs       map[string]NPC
	Zones      map[string]Zone
	Loot       map[string][]LootEntry
	// Phase 14: wildlife herds (wildlife.json), file order kept.
	Herds []Herd
	// Phase 12: nodes and recipes, indexed by id; the slices keep file order
	// for the payload so every client lists them the same way.
	Nodes      map[string]Node
	Recipes    map[string]Recipe
	nodeList   []Node
	recipeList []Recipe
	Payload    []byte
}

// itemsFile mirrors server/data/items.json.
type itemsFile struct {
	StartCredits int64 `json:"start_credits"`
	StartItems   []struct {
		Item string `json:"item"`
		Qty  int    `json:"qty"`
	} `json:"start_items"`
	InvSlots   int         `json:"inv_slots"`
	EquipSlots []string    `json:"equip_slots"`
	Items      []Item      `json:"items"`
	EntityDefs []EntityDef `json:"entity_defs"`
}

// npcsFile mirrors server/data/npcs.json.
type npcsFile struct {
	NPCs []NPC `json:"npcs"`
}

// Load parses the embedded server/data JSON into an indexed Registry and
// builds the `defs` message payload.
func Load() (*Registry, error) { return load(data.FS) }

// load is Load over any file system, so a test can add or break a file.
func load(fsys fs.FS) (*Registry, error) {
	raw, err := fs.ReadFile(fsys, "items.json")
	if err != nil {
		return nil, fmt.Errorf("defs: read items.json: %w", err)
	}
	var itemsF itemsFile
	if err := json.Unmarshal(raw, &itemsF); err != nil {
		return nil, fmt.Errorf("defs: parse items.json: %w", err)
	}

	raw, err = fs.ReadFile(fsys, "npcs.json")
	if err != nil {
		return nil, fmt.Errorf("defs: read npcs.json: %w", err)
	}
	var npcsF npcsFile
	if err := json.Unmarshal(raw, &npcsF); err != nil {
		return nil, fmt.Errorf("defs: parse npcs.json: %w", err)
	}
	// mobs.json is the generated creature library (art/tools/mobs.mjs), the
	// same shape as npcs.json; optional, appended after the hand-written ones.
	if raw, err := fs.ReadFile(fsys, "mobs.json"); err == nil {
		var mobsF npcsFile
		if err := json.Unmarshal(raw, &mobsF); err != nil {
			return nil, fmt.Errorf("defs: parse mobs.json: %w", err)
		}
		npcsF.NPCs = append(npcsF.NPCs, mobsF.NPCs...)
	}

	// Loot tables live in the registry with everything else under
	// server/data. They were briefly parsed in internal/sim instead, which
	// meant two parsers over one embedded dataset — the second one drifts the
	// moment the schema moves.
	raw, err = fs.ReadFile(fsys, "loot.json")
	if err != nil {
		return nil, fmt.Errorf("defs: read loot.json: %w", err)
	}
	var lootF lootFile
	if err := json.Unmarshal(raw, &lootF); err != nil {
		return nil, fmt.Errorf("defs: parse loot.json: %w", err)
	}

	zoneFiles, err := fs.Glob(fsys, "zones/*.json")
	if err != nil {
		return nil, fmt.Errorf("defs: glob zones: %w", err)
	}

	reg := &Registry{
		Loot:         lootF.Tables,
		StartCredits: itemsF.StartCredits,
		StartItems:   itemsF.StartItems,
		InvSlots:     itemsF.InvSlots,
		EquipSlots:   itemsF.EquipSlots,
		Items:        make(map[string]Item, len(itemsF.Items)),
		Entities:     make(map[string]EntityDef, len(itemsF.EntityDefs)),
		NPCs:         make(map[string]NPC, len(npcsF.NPCs)),
		Zones:        make(map[string]Zone, len(zoneFiles)),
		Missions:     map[string]Mission{},
	}
	if raw, err := fs.ReadFile(fsys, "skills.json"); err == nil {
		var sf struct {
			Skills    []Skill             `json:"skills"`
			Synergies []Synergy           `json:"synergies"`
			Awards    SkillAwards         `json:"awards"`
			Unlocks   []UnlockRequirement `json:"unlock_requirements"`
		}
		if err := json.Unmarshal(raw, &sf); err != nil {
			return nil, fmt.Errorf("defs: parse skills.json: %w", err)
		}
		reg.Skills = sf.Skills
		reg.Synergies = sf.Synergies
		reg.Awards = sf.Awards
		reg.Unlocks = sf.Unlocks
	}
	if raw, err := fs.ReadFile(fsys, "missions.json"); err == nil {
		var mf struct {
			Missions []Mission `json:"missions"`
		}
		if err := json.Unmarshal(raw, &mf); err != nil {
			return nil, fmt.Errorf("defs: parse missions.json: %w", err)
		}
		for _, m := range mf.Missions {
			reg.Missions[m.ID] = m
		}
	}
	if raw, err := fs.ReadFile(fsys, "nodes.json"); err == nil {
		var nf struct {
			Nodes []Node `json:"nodes"`
		}
		if err := json.Unmarshal(raw, &nf); err != nil {
			return nil, fmt.Errorf("defs: parse nodes.json: %w", err)
		}
		reg.nodeList = nf.Nodes
		reg.Nodes = make(map[string]Node, len(nf.Nodes))
		for _, n := range nf.Nodes {
			reg.Nodes[n.ID] = n
		}
	}
	if raw, err := fs.ReadFile(fsys, "recipes.json"); err == nil {
		var rf struct {
			Recipes []Recipe `json:"recipes"`
		}
		if err := json.Unmarshal(raw, &rf); err != nil {
			return nil, fmt.Errorf("defs: parse recipes.json: %w", err)
		}
		reg.recipeList = rf.Recipes
		reg.Recipes = make(map[string]Recipe, len(rf.Recipes))
		for _, r := range rf.Recipes {
			reg.Recipes[r.ID] = r
		}
	}
	for _, it := range itemsF.Items {
		reg.Items[it.ID] = it
	}
	for _, ed := range itemsF.EntityDefs {
		reg.Entities[ed.Type] = ed
	}
	for _, n := range npcsF.NPCs {
		if _, dup := reg.NPCs[n.ID]; dup {
			return nil, fmt.Errorf("defs: npc %q defined twice (npcs.json, mobs.json)", n.ID)
		}
		reg.NPCs[n.ID] = n
	}
	for _, zf := range zoneFiles {
		raw, err := fs.ReadFile(fsys, zf)
		if err != nil {
			return nil, fmt.Errorf("defs: read %s: %w", zf, err)
		}
		var z Zone
		if err := json.Unmarshal(raw, &z); err != nil {
			return nil, fmt.Errorf("defs: parse %s: %w", zf, err)
		}
		if err := ExpandLayout(&z); err != nil {
			return nil, fmt.Errorf("defs: %s: %w", zf, err)
		}
		reg.Zones[z.ID] = z
	}

	if err := auditArtisan(reg); err != nil {
		return nil, err
	}
	if reg.Herds, err = loadHerds(fsys); err != nil {
		return nil, err
	}
	if err := auditHerds(reg); err != nil {
		return nil, err
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

// payloadNPC is the client-visible slice of an NPC: display name, verb and
// body size (omitted when default, so the payload stays small), not its
// stock (that arrives per-NPC via `shop_list`) or any other server-side
// bookkeeping.
type payloadNPC struct {
	Name      string  `json:"name"`
	Asset     string  `json:"asset"`
	Verb      string  `json:"verb"`
	Radius    float64 `json:"radius,omitempty"`
	Height    float64 `json:"height,omitempty"`
	EyeHeight float64 `json:"eye_height,omitempty"`
}

// payloadConstants are the GDD "Interaction" constants a client needs to
// draw its own interact prompt (docs/GDD.md, "Interaction").
type payloadConstants struct {
	InteractDist float64 `json:"interact_dist"`
	InteractCone float64 `json:"interact_cone"`
	SellRate     float64 `json:"sell_rate,omitempty"` // Phase 12
}

// payload is the JSON shape of the `defs` message body: what a client needs
// to render and predict, and nothing it is not allowed to know.
type payload struct {
	Items     map[string]Item       `json:"items"`
	Entities  map[string]EntityDef  `json:"entities"`
	NPCs      map[string]payloadNPC `json:"npcs"`
	Constants payloadConstants      `json:"constants"`
	// Phase 11: the skill roster and synergies ride to the client for the
	// K panel and the predictor's efficacy mirror. Additive JSON — old
	// clients ignore it.
	Skills    []Skill   `json:"skills,omitempty"`
	Synergies []Synergy `json:"synergies,omitempty"`
	// Phase 11.7: the slot set and bag size, so the panels draw from data.
	EquipSlots []string `json:"equip_slots,omitempty"`
	InvSlots   int      `json:"inv_slots,omitempty"`
	// Phase 12: nodes (prompts, models, the locked/no-tool hint) and recipes
	// (the bench panel), verbatim from their files.
	Nodes   []Node   `json:"nodes,omitempty"`
	Recipes []Recipe `json:"recipes,omitempty"`
}

func buildPayload(reg *Registry) ([]byte, error) {
	p := payload{
		Items:      reg.Items,
		Entities:   reg.Entities,
		Skills:     reg.Skills,
		Synergies:  reg.Synergies,
		EquipSlots: reg.EquipSlots,
		InvSlots:   reg.InvSlots,
		Nodes:      reg.nodeList,
		Recipes:    reg.recipeList,
		NPCs:       make(map[string]payloadNPC, len(reg.NPCs)),
		Constants: payloadConstants{
			InteractDist: 3.0,
			InteractCone: 20.0,
			SellRate:     SellRate,
		},
	}
	for id, n := range reg.NPCs {
		p.NPCs[id] = payloadNPC{Name: n.Name, Asset: n.Asset, Verb: n.Verb,
			Radius: n.RadiusM, Height: n.HeightM, EyeHeight: n.EyeHeightM}
	}
	b, err := json.Marshal(p)
	if err != nil {
		return nil, fmt.Errorf("defs: marshal payload: %w", err)
	}
	return b, nil
}

// auditArtisan is the load-time cross-check of the Phase 12 data: a node's
// tool, loot table and skill must exist, a recipe's items must exist, and a
// zone may only place node and npc defs that exist. A typo here should kill the
// server at startup with a name, not surface as a `gather` that always
// refuses.
func auditArtisan(reg *Registry) error {
	skill := func(id string) bool {
		for _, sk := range reg.Skills {
			if sk.ID == id {
				return true
			}
		}
		return false
	}
	for _, n := range reg.Nodes {
		if _, ok := reg.Items[n.Tool]; !ok {
			return fmt.Errorf("defs: node %s: tool %q is not an item", n.ID, n.Tool)
		}
		if _, ok := reg.Loot[n.Loot]; !ok {
			return fmt.Errorf("defs: node %s: loot table %q missing", n.ID, n.Loot)
		}
		if !skill(n.Skill) {
			return fmt.Errorf("defs: node %s: skill %q unknown", n.ID, n.Skill)
		}
		if n.Yields < 1 || n.Channel <= 0 {
			return fmt.Errorf("defs: node %s: yields/channel must be positive", n.ID)
		}
	}
	for _, r := range reg.Recipes {
		for _, in := range append([]ItemQty{r.Output}, r.Inputs...) {
			if _, ok := reg.Items[in.Item]; !ok {
				return fmt.Errorf("defs: recipe %s: item %q unknown", r.ID, in.Item)
			}
			if in.Qty < 1 {
				return fmt.Errorf("defs: recipe %s: qty for %q must be positive", r.ID, in.Item)
			}
		}
	}
	// Phase 13: a consumable or ability must cool for a positive time, a
	// mod must change something, and both live on items of the right kind
	// and slot; the mod slot must be declared.
	for _, it := range reg.Items {
		if it.Consumable != nil && (it.Consumable.Cooldown <= 0 || it.Kind != "consumable") {
			return fmt.Errorf("defs: item %s: a consumable needs kind consumable and a positive cooldown", it.ID)
		}
		if it.Melee != nil && (it.Slot != "melee" || it.Melee.Damage <= 0 || it.Melee.Interval <= 0 || it.Melee.Range <= 0 || it.Melee.Arc <= 0) {
			return fmt.Errorf("defs: item %s: a melee weapon sits in slot melee with positive damage, interval, range and arc", it.ID)
		}
		if t := consumableThrow(it); t != nil && (t.Damage <= 0 || t.Radius <= 0 || t.Speed <= 0) {
			return fmt.Errorf("defs: item %s: a throw needs positive damage, radius and speed", it.ID)
		}
		if it.Ability != nil && (it.Ability.Cooldown <= 0 || it.Ability.ID == "" || it.Slot == "") {
			return fmt.Errorf("defs: item %s: an ability needs an id, a positive cooldown and a slot to be worn in", it.ID)
		}
		if it.Mod != nil {
			m := it.Mod
			if it.Slot != "mod" || (m.Damage == 0 && m.Magazine == 0 && m.MaxRange == 0 && m.FalloffStart == 0 && m.FalloffEnd == 0) {
				return fmt.Errorf("defs: item %s: a mod sits in slot mod and changes something", it.ID)
			}
			slotOK := false
			for _, sl := range reg.EquipSlots {
				if sl == "mod" {
					slotOK = true
				}
			}
			if !slotOK {
				return fmt.Errorf("defs: item %s: equip_slots does not declare mod", it.ID)
			}
		}
	}
	for _, n := range reg.NPCs {
		if n.Melee == "" {
			continue
		}
		if it, ok := reg.Items[n.Melee]; !ok || it.Melee == nil {
			return fmt.Errorf("defs: npc %s: melee %q is not a melee weapon", n.ID, n.Melee)
		}
	}
	for zid, z := range reg.Zones {
		for _, e := range z.Entities {
			if e.Type == "node" {
				if _, ok := reg.Nodes[e.Def]; !ok {
					return fmt.Errorf("defs: zone %s places unknown node %q", zid, e.Def)
				}
			}
			if e.Type == "npc" {
				if _, ok := reg.NPCs[e.Def]; !ok {
					return fmt.Errorf("defs: zone %s places unknown npc %q", zid, e.Def)
				}
			}
		}
	}
	return nil
}

func consumableThrow(it Item) *Throw {
	if it.Consumable == nil {
		return nil
	}
	return it.Consumable.Throw
}
