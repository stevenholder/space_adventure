package server

import (
	"encoding/json"
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// testWorld builds a cmdWorld standing at the origin, looking at +X, with
// one shop NPC at distance dist along +X (so dist == interactDist puts it
// exactly at the edge of range, and anything further is out of range).
func testWorld(p *store.Player, npc defs.NPC, dist float64) cmdWorld {
	reg := &defs.Registry{
		Items:    map[string]defs.Item{"widget": {ID: "widget", StackMax: 99}},
		InvSlots: 20,
	}
	return cmdWorld{
		Player: p,
		Reg:    reg,
		// On the surface, not at the planet's centre: inRange raises the
		// target to its own eye height along ITS own radial up, so a fixture
		// at the origin has no meaningful up and the geometry is nonsense.
		// The NPC stands at its FEET, like every real placement — the old
		// fixture pre-raised it to eye height, which quietly compensated for
		// the feet-vs-eye bug this test is meant to cover.
		Pos:  sim.Vec{0, planetSurfaceY, 0},
		Up:   sim.Vec{0, 1, 0},
		Look: sim.Vec{1, 0, 0},
		FindNPC: func(id uint32) (defs.NPC, sim.Vec, bool) {
			if id != 1 {
				return defs.NPC{}, sim.Vec{}, false
			}
			return npc, sim.Vec{dist, planetSurfaceY, 0}, true
		},
	}
}

// planetSurfaceY is a stand-in surface radius for these fixtures; the exact
// value does not matter, only that the player is ON the sphere rather than at
// its centre.
const planetSurfaceY = 150.0

func shopNPC() defs.NPC {
	n := defs.NPC{ID: "trader", Kind: "shop"}
	n.Stock = []struct {
		Item  string `json:"item"`
		Price int64  `json:"price"`
	}{{Item: "widget", Price: 100}}
	return n
}

func buyReq(seq uint16) protocol.Cmd {
	return protocol.Cmd{Seq: seq, Opcode: protocol.OpShopBuy,
		Data: []byte(`{"npc":1,"item":"widget","qty":1}`)}
}

// TestHandleCmdRateLimit: bursting past 10/s, burst 20 gets StatusRateLimited
// and the refused cmd does not execute (credits unchanged).
func TestHandleCmdRateLimit(t *testing.T) {
	p := &store.Player{Credits: 100000}
	w := testWorld(p, shopNPC(), 1.0)
	now := time.Unix(0, 0)
	rate := newCmdRate(now)

	const burst = int(cmdRateBurst)
	var last protocol.CmdResult
	for i := 0; i < burst+1; i++ {
		last = handleCmd(rate, now, buyReq(uint16(i)), w)
	}
	if last.Status != protocol.StatusRateLimited {
		t.Fatalf("cmd %d: status = %d, want StatusRateLimited", burst, last.Status)
	}
	wantCredits := int64(100000) - 100*int64(burst)
	if p.Credits != wantCredits {
		t.Fatalf("credits = %d, want %d (the rate-limited buy must not execute)", p.Credits, wantCredits)
	}
}

// TestHandleCmdOutOfRange: shop_buy against an NPC beyond interact_dist is
// refused with reason out_of_range and leaves credits untouched.
func TestHandleCmdOutOfRange(t *testing.T) {
	p := &store.Player{Credits: 500}
	w := testWorld(p, shopNPC(), interactDist+0.5) // just past 3.0 m
	rate := newCmdRate(time.Now())

	res := handleCmd(rate, time.Now(), buyReq(1), w)
	if res.Status != protocol.StatusRefused {
		t.Fatalf("status = %d, want StatusRefused", res.Status)
	}
	var body struct {
		Reason string `json:"reason"`
	}
	if err := json.Unmarshal(res.Data, &body); err != nil {
		t.Fatalf("unmarshal result data: %v", err)
	}
	if body.Reason != "out_of_range" {
		t.Fatalf("reason = %q, want out_of_range", body.Reason)
	}
	if p.Credits != 500 {
		t.Fatalf("credits = %d, want unchanged 500", p.Credits)
	}
}

// TestHandleCmdMalformed: invalid JSON is StatusMalformed, never a
// disconnect (there is nothing to disconnect here — handleCmd just
// returns a result either way, which is the point: a buggy client is
// indistinguishable from a hostile one only in that neither gets closed).
func TestHandleCmdMalformed(t *testing.T) {
	p := &store.Player{Credits: 500}
	w := testWorld(p, shopNPC(), 1.0)
	rate := newCmdRate(time.Now())

	req := protocol.Cmd{Seq: 9, Opcode: protocol.OpShopBuy, Data: []byte(`{not json`)}
	res := handleCmd(rate, time.Now(), req, w)
	if res.Status != protocol.StatusMalformed {
		t.Fatalf("status = %d, want StatusMalformed", res.Status)
	}
	if res.Seq != 9 || res.Opcode != protocol.OpShopBuy {
		t.Fatalf("result seq/opcode = %d/%d, want 9/%d", res.Seq, res.Opcode, protocol.OpShopBuy)
	}
}

// TestHandleCmdUnknownOpcode: an opcode outside the routed set is
// StatusUnknownOpcode.
func TestHandleCmdUnknownOpcode(t *testing.T) {
	p := &store.Player{Credits: 500}
	w := testWorld(p, shopNPC(), 1.0)
	rate := newCmdRate(time.Now())

	req := protocol.Cmd{Seq: 3, Opcode: 0x00FF, Data: []byte(`{}`)}
	res := handleCmd(rate, time.Now(), req, w)
	if res.Status != protocol.StatusUnknownOpcode {
		t.Fatalf("status = %d, want StatusUnknownOpcode", res.Status)
	}
}

// TestHandleCmdBuyOK: a valid in-range, in-cone, affordable buy is
// StatusOK, debits exactly once, and echoes seq/opcode.
func TestHandleCmdBuyOK(t *testing.T) {
	p := &store.Player{Credits: 500}
	w := testWorld(p, shopNPC(), 1.0)
	rate := newCmdRate(time.Now())

	res := handleCmd(rate, time.Now(), buyReq(7), w)
	if res.Status != protocol.StatusOK {
		t.Fatalf("status = %d, want StatusOK (data=%s)", res.Status, res.Data)
	}
	if res.Seq != 7 || res.Opcode != protocol.OpShopBuy {
		t.Fatalf("result seq/opcode = %d/%d, want 7/%d", res.Seq, res.Opcode, protocol.OpShopBuy)
	}
	if p.Credits != 400 {
		t.Fatalf("credits = %d, want 400 (debited exactly once)", p.Credits)
	}
	if len(p.Inventory) != 1 || p.Inventory[0].Item != "widget" || p.Inventory[0].Qty != 1 {
		t.Fatalf("inventory = %+v, want one widget", p.Inventory)
	}

	// A second identical buy, still within budget, debits again: proves the
	// first result was not a fluke of double-application.
	res2 := handleCmd(rate, time.Now(), buyReq(8), w)
	if res2.Status != protocol.StatusOK || p.Credits != 300 {
		t.Fatalf("second buy: status=%d credits=%d, want StatusOK/300", res2.Status, p.Credits)
	}
}
