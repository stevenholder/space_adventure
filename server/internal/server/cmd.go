// cmd dispatch and the reliable channel's rate limit (docs/PROTOCOL.md,
// "cmd / cmd_result — the reliable channel"; docs/tasks/phase2-wave2.md
// "W2-14"). handleCmd is the pure core: given one decoded request and the
// requester's server-truth state, it returns exactly one cmd_result.
//
// It takes that state as a cmdWorld rather than reaching through *client
// because the identity (store.Player on a connection, W2-15) and NPC
// world-entity plumbing it needs have not landed yet. A thin method on
// *client that builds a cmdWorld and calls handleCmd is the natural home
// for the wiring once they do; this file does not add it, to stay inside
// its two-file budget.
package server

import (
	"bytes"
	"encoding/json"
	"errors"
	"math"
	"space-adventure/server/internal/terrain"
	"time"
	"unicode/utf8"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// Rate limit (PROTOCOL.md: "10 cmd per connection per second, burst 20").
const (
	cmdRateHz    = 10.0
	cmdRateBurst = 20.0
)

// Interaction constants (GDD "Interaction"): re-validated here against the
// server's own positions — a client's own check is a UI affordance only.
const interactDist = 3.0

// eyeHeightMeters is terrain.EyeHeightMeters — one definition, aliased so
// call sites read locally. A server-authoritative eye position must never
// come from the client, and re-deriving it from a registry would be one
// more place a bad def value could bite.
const eyeHeightMeters = terrain.EyeHeightMeters

// interactCosMin is cos(interact_cone), interact_cone = 20 deg half-angle.
var interactCosMin = math.Cos(20.0 * math.Pi / 180)

// cmdRate is a token bucket refilled from wall-clock time (not a list of
// timestamps), one per connection. Not safe for concurrent use: cmd
// handling happens on the connection's single reader goroutine.
type cmdRate struct {
	tokens float64
	last   time.Time
}

func newCmdRate(now time.Time) *cmdRate {
	return &cmdRate{tokens: cmdRateBurst, last: now}
}

// allow refills for elapsed time then takes one token if available. A
// caller that gets false must not execute the cmd.
func (r *cmdRate) allow(now time.Time) bool {
	if d := now.Sub(r.last); d > 0 {
		r.tokens = math.Min(cmdRateBurst, r.tokens+d.Seconds()*cmdRateHz)
		r.last = now
	}
	if r.tokens < 1 {
		return false
	}
	r.tokens--
	return true
}

// cmdWorld is the requester's server-truth state a cmd is validated
// against: their player row, the content registry, their own eye position
// and look direction (for the interaction re-check), and a lookup from a
// shop NPC's entity_id to its archetype and world position.
type cmdWorld struct {
	Player  *store.Player
	Reg     *defs.Registry
	Pos     sim.Vec
	Up      sim.Vec
	Look    sim.Vec
	FindNPC func(entityID uint32) (npc defs.NPC, pos sim.Vec, ok bool)
	// FindNode resolves a resource node (Phase 12): its def, position and
	// remaining yields. Nil in registries that place none.
	FindNode func(entityID uint32) (node defs.Node, pos sim.Vec, health int, ok bool)
	// Gather starts the channel (false = busy); CancelGather ends one
	// (false = none running); Rand is the server RNG for craft_extra. All
	// take s.mu themselves. Nil in fixtures that never gather or craft.
	Busy         func() bool
	Gather       func(node uint32, ticks int) bool
	CancelGather func() bool
	Rand         func() float64
	// Phase 13 (use.go): the connection's vitals and cooldowns live under
	// s.mu, and the scan reads the world. All take s.mu themselves.
	// Phase 13 buyback (shop.go): the connection's sale log, under s.mu.
	Buyback       func() []buybackEntry
	PushBuyback   func(buybackEntry)
	PopBuyback    func(item string) (buybackEntry, bool)
	Vitals        func() (health int, dead bool)
	Heal          func(n int) (after int)
	CoolingFor    func(item string) (readyIn float64)
	StartCooldown func(item string, secs float64)
	Scan          func(rng float64) []map[string]any

	// Ent is the requester's own server-side entity, for the rounds
	// currently in the magazine.
	//
	// The magazine lives on the connection rather than on the stored player
	// row: it is per-life state, not something to persist. It is only ever
	// touched by this connection's reader goroutine — `fire` and this
	// handler are both dispatched from it — so it needs no lock of its own.
	Ent *entity
}

// inRange re-validates interact_dist and interact_cone (GDD "Interaction")
// against the requester's own server-truth eye/look, never anything the
// client sent.
// inRange reports whether the requester is close enough to `target` and looking
// near enough at it, per GDD "Interaction".
//
// `target` is an entity ORIGIN, which for a standing character is at its feet.
// The GDD specifies the check against the target's EYE, and the difference is
// not cosmetic: from a 1.7 m eye at 2 m away, the vector to another character's
// feet points ~40 degrees downward — outside the 20 degree cone — so a player
// had to stare at the floor to talk to a shopkeeper standing right in front of
// them. Measured as a live out_of_range refusal at 2.04 m against a 3.0 m
// interact_dist. Raise the target to its own eye height along its own up.
func inRange(w cmdWorld, target sim.Vec) bool {
	return inRangeAt(w, target, eyeHeightMeters)
}

// Aim heights for things that are not a standing person (Phase 12): a
// node is a metre of rock, a bench a slab at 0.9 m. Aiming at 1.7 m above
// either means looking over it, and the middle of the rock falls outside
// the 20° cone from conversation range. The client's Interact mirrors
// these.
const (
	nodeAimHeight  = 0.6
	benchAimHeight = 0.9
)

// aimHeight is where the cone check points on an NPC: a bench is aimed
// at its top, anyone else at their eye.
func aimHeight(npc defs.NPC) float64 {
	if npc.Kind == "bench" {
		return benchAimHeight
	}
	return eyeHeightMeters
}

// inRangeAt is inRange with the target raised by `height` instead of a
// person's eye.
func inRangeAt(w cmdWorld, target sim.Vec, height float64) bool {
	eye := w.Pos.Add(w.Up.Scale(eyeHeightMeters))
	targetUp := terrain.Normalize(terrain.Vec(target))
	targetEye := target.Add(sim.Vec(targetUp).Scale(height))
	to := targetEye.Sub(eye)
	d := to.Len()
	if d > interactDist {
		return false
	}
	if d < 1e-9 {
		return true // coincident: any look direction faces it
	}
	return w.Look.Dot(to.Scale(1/d)) >= interactCosMin
}

// handleCmd routes one decoded cmd to its rule and returns the single
// cmd_result it produces (PROTOCOL.md). Order: rate limit first (an
// over-budget cmd is never even parsed, let alone executed), then body
// validity, then the opcode's own rules.
func handleCmd(rate *cmdRate, now time.Time, req protocol.Cmd, w cmdWorld) protocol.CmdResult {
	reply := func(status uint8, data []byte) protocol.CmdResult {
		return protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode, Status: status, Data: data}
	}
	refuse := func(reason string) protocol.CmdResult {
		return reply(protocol.StatusRefused, encodeJSON(map[string]string{"reason": reason}))
	}
	// refuseErr maps a sim refusal to its reason code, anything else to a
	// bare "refused".
	refuseErr := func(err error) protocol.CmdResult {
		var re sim.RefusalError
		if errors.As(err, &re) {
			return refuse(re.Reason)
		}
		return refuse("refused")
	}

	if !rate.allow(now) {
		return reply(protocol.StatusRateLimited, nil)
	}
	if len(req.Data) > protocol.MaxCmdBody || !utf8.Valid(req.Data) {
		return reply(protocol.StatusMalformed, nil)
	}

	switch req.Opcode {
	case protocol.OpShopList:
		var body struct {
			NPC uint32 `json:"npc"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		npc, pos, ok := w.FindNPC(body.NPC)
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if !inRange(w, pos) {
			return refuse("out_of_range")
		}
		out := map[string]any{"stock": npc.Stock}
		if w.Buyback != nil {
			bb := w.Buyback()
			if bb == nil {
				bb = []buybackEntry{}
			}
			out["buyback"] = bb
		}
		return reply(protocol.StatusOK, encodeJSON(out))

	case protocol.OpShopBuy:
		var body struct {
			NPC  uint32 `json:"npc"`
			Item string `json:"item"`
			Qty  int    `json:"qty"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		npc, pos, ok := w.FindNPC(body.NPC)
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if !inRange(w, pos) {
			return refuse("out_of_range")
		}
		// Data-driven unlock gates (GDD "Skills"): a purchase below the
		// required level is refused `locked`. Empty at launch.
		for _, u := range w.Reg.Unlocks {
			if u.Item == body.Item && skillLevel(w.Player, u.Skill) < u.Level {
				return refuse("locked")
			}
		}
		// Commerce: −per_level on buy prices per level (cap −19.6% at 99).
		buyMult := 1 - efficacyBonus(w.Reg, w.Player, "commerce")
		if err := sim.BuyAt(w.Player, npc, body.Item, body.Qty, w.Reg, buyMult); err != nil {
			var re sim.RefusalError
			if errors.As(err, &re) {
				return refuse(re.Reason)
			}
			return refuse("refused")
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"credits":   w.Player.Credits,
			"inventory": w.Player.Inventory,
		}))

	case protocol.OpEquip:
		var body struct {
			Slot string `json:"slot"`
			Item string `json:"item"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		if err := sim.Equip(w.Player, body.Slot, body.Item, w.Reg); err != nil {
			var re sim.RefusalError
			if errors.As(err, &re) {
				return refuse(re.Reason)
			}
			return refuse("refused")
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{"equipped": w.Player.Equipped}))

	case protocol.OpInventory:
		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"credits":   w.Player.Credits,
			"inventory": w.Player.Inventory,
			"equipped":  w.Player.Equipped,
		}))

	case protocol.OpReload:
		// Moves carried ammunition into the magazine. Until this landed,
		// `reload` was routed but did nothing, so a player was permanently
		// dry after thirty shots — the only way to get rounds back was to
		// re-equip, because equipping is what resets the magazine.
		if w.Ent == nil {
			return refuse("refused")
		}
		wp, ok := sim.WeaponWith(w.Reg, w.Player)
		if !ok {
			return refuse(sim.ReasonNotOwned)
		}

		capacity := wp.Magazine
		if w.Ent.Magazine >= capacity {
			// A mag mod that came off leaves more rounds loaded than the
			// bare rifle holds: clamp on this reload and hand the surplus
			// back to the bag (best effort — a full bag loses them), never
			// empty the magazine (GDD "Weapon mods").
			if surplus := w.Ent.Magazine - capacity; surplus > 0 {
				w.Ent.Magazine = capacity
				_ = sim.AddItem(w.Player, wp.AmmoItem, surplus, w.Reg)
			}
			return reply(protocol.StatusOK, encodeJSON(map[string]any{
				"magazine": w.Ent.Magazine,
				"reserve":  sim.CountItem(w.Player, wp.AmmoItem),
			}))
		}

		reserve := sim.CountItem(w.Player, wp.AmmoItem)
		take := capacity - w.Ent.Magazine
		if take > reserve {
			take = reserve
		}
		if take <= 0 {
			return refuse("no_ammo")
		}
		if err := sim.TakeItem(w.Player, wp.AmmoItem, take); err != nil {
			return refuse("refused")
		}
		w.Ent.Magazine += take

		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"magazine": w.Ent.Magazine,
			"reserve":  reserve - take,
		}))

	case protocol.OpShopSell:
		var body struct {
			NPC  uint32 `json:"npc"`
			Item string `json:"item"`
			Qty  int    `json:"qty"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		npc, pos, ok := w.FindNPC(body.NPC)
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if !inRange(w, pos) {
			return refuse("out_of_range")
		}
		bonus := synergyBonus(w.Reg, w.Player, "sell_bonus", "")
		paid, err := sim.SellAt(w.Player, npc, body.Item, body.Qty, w.Reg, bonus)
		if err != nil {
			return refuseErr(err)
		}
		if w.PushBuyback != nil {
			w.PushBuyback(buybackEntry{Item: body.Item, Qty: body.Qty, Price: paid})
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"credits": w.Player.Credits, "inventory": w.Player.Inventory,
		}))

	case protocol.OpShopBuyback:
		// The newest sale of that item comes back whole, at what the shop
		// paid; a refusal puts the entry back where it was.
		var body struct {
			NPC  uint32 `json:"npc"`
			Item string `json:"item"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		npc, pos, ok := w.FindNPC(body.NPC)
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if !inRange(w, pos) {
			return refuse("out_of_range")
		}
		if npc.Kind != "shop" || w.PopBuyback == nil {
			return refuse(sim.ReasonNoStock)
		}
		e, ok := w.PopBuyback(body.Item)
		if !ok {
			return refuse("no_buyback")
		}
		if e.Price > w.Player.Credits {
			w.PushBuyback(e)
			return refuse(sim.ReasonInsufficientCredits)
		}
		if err := sim.AddItem(w.Player, e.Item, e.Qty, w.Reg); err != nil {
			w.PushBuyback(e)
			return refuseErr(err)
		}
		w.Player.Credits -= e.Price
		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"credits": w.Player.Credits, "inventory": w.Player.Inventory,
		}))

	case protocol.OpCraft:
		var body struct {
			NPC    uint32 `json:"npc"`
			Recipe string `json:"recipe"`
			Qty    int    `json:"qty"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		npc, pos, ok := w.FindNPC(body.NPC)
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if !inRangeAt(w, pos, aimHeight(npc)) {
			return refuse("out_of_range")
		}
		if npc.Kind != "bench" {
			return refuse(sim.ReasonUnknownRecipe)
		}
		r, ok := w.Reg.Recipes[body.Recipe]
		if !ok {
			return refuse(sim.ReasonUnknownRecipe)
		}
		// craft_extra: one roll per call for one bonus unit (GDD).
		bonus := 0
		if w.Rand != nil && w.Rand() < efficacyBonus(w.Reg, w.Player, "engineering") {
			bonus = 1
		}
		made, err := sim.Craft(w.Player, r, body.Qty, skillLevel(w.Player, "engineering"), w.Reg, bonus)
		if err != nil {
			return refuseErr(err)
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{
			"inventory": w.Player.Inventory,
			"crafted":   map[string]any{"item": r.Output.Item, "qty": made},
		}))

	case protocol.OpGather:
		// Wave 1 skeleton: every refusal the GDD orders before the channel
		// starts, then not_implemented where the channel (task 7) begins.
		var body struct {
			Node uint32 `json:"node"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		if w.FindNode == nil {
			return reply(protocol.StatusNotFound, nil)
		}
		nd, pos, health, ok := w.FindNode(body.Node)
		if !ok {
			return reply(protocol.StatusNotFound, nil)
		}
		if !inRangeAt(w, pos, nodeAimHeight) {
			return refuse("out_of_range")
		}
		if w.Busy != nil && w.Busy() {
			return refuse("busy")
		}
		if !toolSatisfies(w.Reg, w.Player.Equipped["tool"], nd.Tool) {
			return refuse("no_tool")
		}
		if skillLevel(w.Player, nd.Skill) < nd.Level {
			return refuse(sim.ReasonLocked)
		}
		if health <= 0 {
			return refuse("depleted")
		}
		if !sim.CanFit(w.Player, w.Reg, nd.Loot) {
			return refuse(sim.ReasonNoSpace)
		}
		if w.Gather == nil {
			return refuse("busy")
		}
		duration := gatherDuration(w.Reg, w.Player, nd)
		if !w.Gather(body.Node, int(math.Round(duration*sim.TickHz))) {
			return refuse("busy")
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{"node": body.Node, "duration": duration}))

	case protocol.OpUse:
		var body struct {
			Item string `json:"item"`
		}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		return handleUse(w, body.Item, reply, refuse)

	case protocol.OpGatherCancel:
		var body struct{}
		if !decodeStrict(req.Data, &body) {
			return reply(protocol.StatusMalformed, nil)
		}
		if w.CancelGather == nil || !w.CancelGather() {
			return refuse("not_gathering")
		}
		return reply(protocol.StatusOK, encodeJSON(map[string]any{}))

	default:
		return reply(protocol.StatusUnknownOpcode, nil)
	}
}

// toolSatisfies reports whether the worn tool is `want` or supersedes it
// (GDD "Nodes": a drill mk2 stands in for a drill).
func toolSatisfies(reg *defs.Registry, worn, want string) bool {
	for i := 0; worn != "" && i < 8; i++ {
		if worn == want {
			return true
		}
		worn = reg.Items[worn].Supersedes
	}
	return false
}

// decodeStrict unmarshals data into v, rejecting unknown fields and
// trailing garbage — a syntactically-valid-but-wrong-shape body is
// malformed just as much as broken JSON.
func decodeStrict(data []byte, v any) bool {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	if err := dec.Decode(v); err != nil {
		return false
	}
	return !dec.More()
}

// encodeJSON marshals v; a marshal failure here would be a bug in a
// hand-built map literal above, not a runtime condition, so it panics
// rather than threading an error return through every reply.
func encodeJSON(v any) []byte {
	b, err := json.Marshal(v)
	if err != nil {
		panic("server: cmd result failed to marshal: " + err.Error())
	}
	return b
}
