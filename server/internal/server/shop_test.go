package server

import (
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// TestShopBuyback pins C137/C138's wire: a sale lands on the buyback list
// with what the shop paid, shop_list carries it, buying it back returns
// the whole stack for that price and removes the entry, a refusal leaves
// the entry in place, the list caps at twelve newest-last, and the newest
// sale of an item is the one that comes back.
func TestShopBuyback(t *testing.T) {
	c := &client{}
	p := &store.Player{Credits: 100, Inventory: []store.Stack{{Item: "mat.ore.iron", Qty: 10}}, Equipped: map[string]string{}}
	w := artisanWorld(p, shopNPC(), 5)
	w.Buyback = func() []buybackEntry { return append([]buybackEntry(nil), c.buyback...) }
	w.PushBuyback = c.pushBuyback
	w.PopBuyback = c.popBuyback

	if st, m := run(w, protocol.OpShopSell, `{"npc":1,"item":"mat.ore.iron","qty":4}`); st != protocol.StatusOK || m["credits"].(float64) != 112 {
		t.Fatalf("sell: %d %v", st, m)
	}
	if len(c.buyback) != 1 || c.buyback[0] != (buybackEntry{Item: "mat.ore.iron", Qty: 4, Price: 12}) {
		t.Fatalf("buyback after sale: %+v", c.buyback)
	}
	if st, m := run(w, protocol.OpShopList, `{"npc":1}`); st != protocol.StatusOK || len(m["buyback"].([]any)) != 1 {
		t.Fatalf("shop_list: %d %v", st, m)
	}
	if st, m := run(w, protocol.OpShopBuyback, `{"npc":1,"item":"mat.scrap"}`); st != protocol.StatusRefused || m["reason"] != "no_buyback" {
		t.Fatalf("buyback of a thing never sold: %d %v", st, m)
	}
	p.Credits = 5
	if st, m := run(w, protocol.OpShopBuyback, `{"npc":1,"item":"mat.ore.iron"}`); st != protocol.StatusRefused || m["reason"] != "insufficient_credits" || len(c.buyback) != 1 {
		t.Fatalf("broke: %d %v list=%v", st, m, c.buyback)
	}
	p.Credits = 112
	if st, m := run(w, protocol.OpShopBuyback, `{"npc":1,"item":"mat.ore.iron"}`); st != protocol.StatusOK || m["credits"].(float64) != 100 || sim.CountItem(p, "mat.ore.iron") != 10 || len(c.buyback) != 0 {
		t.Fatalf("buyback: %d %v ore=%d list=%v", st, m, sim.CountItem(p, "mat.ore.iron"), c.buyback)
	}
	if st, m := run(w, protocol.OpShopBuyback, `{"npc":2,"item":"mat.ore.iron"}`); st != protocol.StatusRefused || m["reason"] != "no_stock" {
		t.Fatalf("buyback at the bench: %d %v", st, m)
	}
	for i := 0; i < 14; i++ {
		c.pushBuyback(buybackEntry{Item: "x", Qty: i + 1, Price: 1})
	}
	if len(c.buyback) != buybackMax || c.buyback[0].Qty != 3 || c.buyback[buybackMax-1].Qty != 14 {
		t.Fatalf("cap: %+v", c.buyback)
	}
	c.buyback = nil
	c.pushBuyback(buybackEntry{Item: "a", Qty: 1, Price: 1})
	c.pushBuyback(buybackEntry{Item: "a", Qty: 2, Price: 2})
	if e, ok := c.popBuyback("a"); !ok || e.Qty != 2 || len(c.buyback) != 1 {
		t.Fatalf("newest first: %+v %v", e, c.buyback)
	}
}
