package server

import (
	"context"
	"slices"
	"strings"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// looksUntil reads ws until the spawn row for id, then returns the run of
// `worn` events for id that immediately follows it, and every look frame
// (slot hair/skin/suit) seen on the way, by entity.
func looksUntil(t *testing.T, ws *wsClient, id uint32) (run []string, seen map[uint32][]string) {
	t.Helper()
	seen = map[uint32][]string{}
	after := false
	for i := 0; i < 2000; i++ {
		typ, payload := ws.next(t)
		switch typ {
		case protocol.MsgSpawn:
			if after {
				return run, seen
			}
			sp, err := protocol.DecodeSpawn(payload)
			if err != nil {
				t.Fatalf("spawn: %v", err)
			}
			after = sp.EntityID == id
		case protocol.MsgEvent:
			ev, err := protocol.DecodeEvent(payload)
			if err != nil {
				t.Fatalf("event: %v", err)
			}
			if ev.EventID != protocol.EventWorn {
				if after {
					return run, seen
				}
				continue
			}
			if after && ev.EntityID != id {
				return run, seen
			}
			d := string(ev.Data)
			if slot, _, _ := strings.Cut(d, "="); slot == slotHair || slot == slotSkin || slot == slotSuit {
				seen[ev.EntityID] = append(seen[ev.EntityID], d)
			}
			if after {
				run = append(run, d)
			}
		default:
			if after {
				return run, seen
			}
		}
	}
	t.Fatalf("no spawn for %d (or nothing after it)", id)
	return
}

// C179: a character's skin and suit go out as `worn` events (slots skin
// and suit) right after each of its spawn rows, behind its hair — on its
// own socket, on the sockets already in, and to a later joiner; defaults
// too, since a character always has both. A guest gets neither.
func TestSpawn_ColourWornFrames(t *testing.T) {
	st := openTestStore(t)
	ctx := context.Background()
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-ash", Name: "Ash", AccountID: "acc1"}); err != nil {
		t.Fatalf("PutPlayer Ash: %v", err)
	}
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-tan", Name: "Tan", AccountID: "acc2", Body: "char.ubc",
		Hair: "hair.buns", Skin: "skin.04", Suit: "suit.rust"}); err != nil {
		t.Fatalf("PutPlayer Tan: %v", err)
	}
	url := newStrictServer(t, st, true)
	ashLooks := []string{"skin=skin.01", "suit=suit.slate"}
	tanLooks := []string{"hair=hair.buns", "skin=skin.04", "suit=suit.rust"}

	ash := hello(t, url, "tok-ash", "ignored")
	ack, err := protocol.DecodeHelloAck(ash.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	ashID := ack.EntityID
	if run, _ := looksUntil(t, ash, ashID); !slices.Equal(run, ashLooks) {
		t.Fatalf("Ash self spawn followed by %v, want %v", run, ashLooks)
	}

	guest := hello(t, url, "made-up-token", "Wanderer")
	ack, err = protocol.DecodeHelloAck(guest.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	guestID := ack.EntityID
	run, seen := looksUntil(t, guest, guestID)
	if len(run) != 0 {
		t.Fatalf("guest self spawn followed by %v, want nothing", run)
	}
	if !slices.Equal(seen[ashID], ashLooks) {
		t.Fatalf("guest socket: Ash's row carried %v, want %v", seen[ashID], ashLooks)
	}
	if run, _ := looksUntil(t, ash, guestID); len(run) != 0 {
		t.Fatalf("Ash socket: broadcast of the guest followed by %v", run)
	}

	tan := hello(t, url, "tok-tan", "ignored")
	ack, err = protocol.DecodeHelloAck(tan.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	tanID := ack.EntityID
	if run, _ := looksUntil(t, tan, tanID); !slices.Equal(run, tanLooks) {
		t.Fatalf("Tan self spawn followed by %v, want %v", run, tanLooks)
	}
	for name, ws := range map[string]*wsClient{"Ash": ash, "guest": guest} {
		if run, _ := looksUntil(t, ws, tanID); !slices.Equal(run, tanLooks) {
			t.Errorf("%s: broadcast of Tan followed by %v, want %v", name, run, tanLooks)
		}
	}

	late := hello(t, url, "another-guest", "Late")
	ack, err = protocol.DecodeHelloAck(late.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	_, seen = looksUntil(t, late, ack.EntityID)
	if !slices.Equal(seen[tanID], tanLooks) || !slices.Equal(seen[ashID], ashLooks) || len(seen[guestID]) != 0 || len(seen) != 2 {
		t.Fatalf("late joiner: look frames %v", seen)
	}
}

// Skin and suit are not equipment slots, as hair is not: the equip cmd
// refuses them (wrong_slot) and the armor slot walk never sees them.
func TestColoursAreNotEquipSlots(t *testing.T) {
	srv, _ := newTestServer(t)
	for _, slot := range []string{slotSkin, slotSuit} {
		for _, item := range []string{"skin.04", "suit.rust", ""} {
			p := &store.Player{Equipped: map[string]string{}}
			err := sim.Equip(p, slot, item, srv.reg)
			if err == nil || !strings.Contains(err.Error(), sim.ReasonWrongSlot) {
				t.Errorf("Equip(%s, %q) = %v, want %s", slot, item, err, sim.ReasonWrongSlot)
			}
		}
		if slices.Contains(wornSlots, slot) || slices.Contains(srv.reg.EquipSlots, slot) {
			t.Errorf("%s is an equipment slot", slot)
		}
	}
}
