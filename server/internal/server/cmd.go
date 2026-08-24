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

// eyeHeightMeters is eye_height (GDD "M1 on-foot movement" rule table),
// duplicated locally the same way internal/sim/combat.go and
// internal/terrain/generate.go do — a server-authoritative eye position
// must never come from the client, and re-deriving it here from a
// registry would be one more place a bad def value could bite.
const eyeHeightMeters = 1.7

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
	eye := w.Pos.Add(w.Up.Scale(eyeHeightMeters))
	targetUp := terrain.Normalize(terrain.Vec(target))
	targetEye := target.Add(sim.Vec(targetUp).Scale(eyeHeightMeters))
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
		return reply(protocol.StatusOK, encodeJSON(map[string]any{"stock": npc.Stock}))

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
		if err := sim.Buy(w.Player, npc, body.Item, body.Qty, w.Reg); err != nil {
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
		// Per-weapon magazine/reserve state has not landed on store.Player
		// yet (a later wave item); until it does, reload is routed but
		// reports an empty magazine rather than fabricating ammo.
		return reply(protocol.StatusOK, encodeJSON(map[string]any{"magazine": 0, "reserve": 0}))

	default:
		return reply(protocol.StatusUnknownOpcode, nil)
	}
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
