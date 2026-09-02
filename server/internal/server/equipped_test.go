package server

import (
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// nextEquipped reads frames until an `equipped` event for entityID arrives
// and returns the item id it carries.
func (c *wsClient) nextEquipped(t *testing.T, entityID uint32) string {
	t.Helper()
	for i := 0; i < 500; i++ {
		ev, err := protocol.DecodeEvent(c.nextOf(t, protocol.MsgEvent))
		if err != nil {
			t.Fatalf("event: %v", err)
		}
		if ev.EventID == protocol.EventEquipped && ev.EntityID == entityID {
			return string(ev.Data)
		}
	}
	t.Fatalf("no equipped event for entity %d within 500 frames", entityID)
	return ""
}

// joinClient dials, says hello, and returns the connection and its entity id.
func joinClient(t *testing.T, url, name string) (*wsClient, uint32) {
	t.Helper()
	c := dialWS(t, url)
	c.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: name}))
	ack, err := protocol.DecodeHelloAck(c.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatalf("hello_ack: %v", err)
	}
	return c, ack.EntityID
}

// TestEquippedEventBroadcastAndReplay covers C16's weapon clause: a player's
// primary slot is on no entity row, so another client learns it from an
// `equipped` event — broadcast when the slot changes, and replayed once per
// armed player to anyone who joins later.
func TestEquippedEventBroadcastAndReplay(t *testing.T) {
	srv, url := newTestServer(t)

	a, aID := joinClient(t, url, "shooter")
	b, _ := joinClient(t, url, "observer")

	// The default loadout carries ammo but no weapon, so put one in A's
	// inventory the same way a shop purchase would.
	srv.mu.Lock()
	ac := srv.clients[aID]
	srv.mu.Unlock()
	if ac == nil {
		t.Fatalf("client %d not published", aID)
	}
	ac.ident.Mutate(func(p *store.Player) {
		if err := sim.AddItem(p, "weapon.pulse", 1, srv.reg); err != nil {
			t.Fatalf("AddItem: %v", err)
		}
	})

	a.sendFrame(t, protocol.EncodeCmd(protocol.Cmd{
		Seq:    1,
		Opcode: protocol.OpEquip,
		Data:   []byte(`{"slot":"primary","item":"weapon.pulse"}`),
	}))

	// Clause 1: the observer already in the world sees the change.
	if got := b.nextEquipped(t, aID); got != "weapon.pulse" {
		t.Fatalf("observer saw equipped %q, want weapon.pulse", got)
	}

	// Clause 2: a client that joins afterwards is told too, without the
	// change ever happening again.
	c, _ := joinClient(t, url, "latecomer")
	if got := c.nextEquipped(t, aID); got != "weapon.pulse" {
		t.Fatalf("late joiner saw equipped %q, want weapon.pulse", got)
	}
}

// TestEquippedReplayReachesTheOwner covers the half of the join replay that
// was missing: a player is told what THEY are holding.
//
// Everyone else was told, and the owner was not, so a client reconnecting on
// a stored row that already held a weapon came back empty-handed with no way
// to recover — re-equipping the same item is not a change, so it broadcasts
// nothing either. Rendering a weapon is driven off this event, so the gun
// simply never appeared.
func TestEquippedReplayReachesTheOwner(t *testing.T) {
	srv, url := newTestServer(t)

	a, aID := joinClient(t, url, "armed")
	srv.mu.Lock()
	ac := srv.clients[aID]
	srv.mu.Unlock()
	ac.ident.Mutate(func(p *store.Player) {
		if err := sim.AddItem(p, "weapon.pulse", 1, srv.reg); err != nil {
			t.Fatalf("AddItem: %v", err)
		}
		p.Equipped[slotPrimary] = "weapon.pulse"
	})

	// Nothing has changed the slot since the join, so the only way this
	// client can learn is the replay.
	b, bID := joinClient(t, url, "rejoined")
	srv.mu.Lock()
	bc := srv.clients[bID]
	srv.mu.Unlock()
	bc.ident.Mutate(func(p *store.Player) {
		if err := sim.AddItem(p, "weapon.pulse", 1, srv.reg); err != nil {
			t.Fatalf("AddItem: %v", err)
		}
		p.Equipped[slotPrimary] = "weapon.pulse"
	})
	srv.syncEquipped(bc)

	if got := b.nextEquipped(t, bID); got != "weapon.pulse" {
		t.Fatalf("owner saw its own equipped as %q, want weapon.pulse", got)
	}
	_ = a
}
