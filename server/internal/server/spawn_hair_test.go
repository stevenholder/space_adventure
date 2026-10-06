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

// wireStep is one spawn row or one `worn` event, in arrival order.
type wireStep struct {
	spawn bool
	id    uint32
	data  string // spawn data, or the worn event's "slot=item"
}

// stepsUntilSpawn reads ws (snapshots and other events skipped) until the
// spawn row for id, then returns every spawn and worn event seen on the
// way plus the very next frame of any type — so a caller can check what
// immediately follows that spawn. Strict: the self spawn and its hair are
// both enqueued before the joiner is published (no snapshot yet), and the
// broadcast pair under one s.mu hold, the lock snapshots go out under.
func stepsUntilSpawn(t *testing.T, ws *wsClient, id uint32) (steps []wireStep, after wireStep, afterTyp uint16) {
	t.Helper()
	seen := false
	for i := 0; i < 2000; i++ {
		typ, payload := ws.next(t)
		if typ == protocol.MsgSnapshot && !seen {
			continue
		}
		var st wireStep
		switch typ {
		case protocol.MsgSpawn:
			sp, err := protocol.DecodeSpawn(payload)
			if err != nil {
				t.Fatalf("spawn: %v", err)
			}
			st = wireStep{spawn: true, id: sp.EntityID, data: string(sp.Data)}
		case protocol.MsgEvent:
			ev, err := protocol.DecodeEvent(payload)
			if err != nil {
				t.Fatalf("event: %v", err)
			}
			if ev.EventID != protocol.EventWorn {
				if seen {
					return steps, wireStep{id: ev.EntityID}, typ
				}
				continue
			}
			st = wireStep{id: ev.EntityID, data: string(ev.Data)}
		default: // a snapshot right after the spawn ends the read too
			if seen {
				return steps, wireStep{}, typ
			}
			continue
		}
		if seen {
			return steps, st, typ
		}
		steps = append(steps, st)
		if st.spawn && st.id == id {
			seen = true
		}
	}
	t.Fatalf("no spawn for %d (or nothing after it)", id)
	return
}

// hairOf is every hair worn event in steps, by entity.
func hairOf(steps []wireStep) map[uint32]string {
	out := map[uint32]string{}
	for _, s := range steps {
		if !s.spawn && len(s.data) > 5 && s.data[:5] == "hair=" {
			out[s.id] = s.data[5:]
		}
	}
	return out
}

// followedBy reports whether the spawn for id in steps is immediately
// followed by the worn frame want ("" = by no hair frame at all). last is
// what came after the final step.
func followedBy(steps []wireStep, last wireStep, id uint32, want string) bool {
	all := append(append([]wireStep(nil), steps...), last)
	for i, s := range all {
		if s.spawn && s.id == id {
			var next wireStep
			if i+1 < len(all) {
				next = all[i+1]
			}
			isHair := !next.spawn && next.id == id && len(next.data) > 5 && next.data[:5] == "hair="
			if want == "" {
				return !isHair
			}
			return isHair && next.data == "hair="+want
		}
	}
	return false
}

// C163: a character's hair goes out as a `worn` event (slot hair) right
// after each of its spawn rows — the self spawn, the row a later joiner
// gets, and the broadcast of the join — on every socket. hair.none and a
// guest send no hair frame. Hair is not an equipment slot: an equip cmd
// naming it is refused.
func TestSpawn_HairWornFrame(t *testing.T) {
	st := openTestStore(t)
	ctx := context.Background()
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-ash", Name: "Ash", AccountID: "acc1"}); err != nil {
		t.Fatalf("PutPlayer Ash: %v", err)
	}
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-kade", Name: "Kade", AccountID: "acc2", Body: "char.ubc.f", Hair: "hair.buns"}); err != nil {
		t.Fatalf("PutPlayer Kade: %v", err)
	}
	url := newStrictServer(t, st, true)

	// Ash (hair.none) first: nothing follows its self spawn.
	ash := hello(t, url, "tok-ash", "ignored")
	ashID, _ := seatedRows(t, ash)

	// A guest: its self spawn on its own socket carries no hair either,
	// nor does the broadcast of it on Ash's.
	guest := hello(t, url, "made-up-token", "Wanderer")
	ack, err := protocol.DecodeHelloAck(guest.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	guestID := ack.EntityID
	steps, last, _ := stepsUntilSpawn(t, guest, guestID)
	if !followedBy(steps, last, guestID, "") || !followedBy(steps, last, ashID, "") {
		t.Fatalf("guest socket: unexpected hair frame: %+v then %+v", steps, last)
	}
	if h := hairOf(steps); len(h) != 0 {
		t.Fatalf("guest socket: hair frames %v, want none", h)
	}

	// Kade (hair.buns) joins: its self spawn, then its hair, on its own
	// socket; Ash's and the guest's join-time rows carry none.
	kade := hello(t, url, "tok-kade", "ignored")
	ack, err = protocol.DecodeHelloAck(kade.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	kadeID := ack.EntityID
	steps, last, _ = stepsUntilSpawn(t, kade, kadeID)
	if !followedBy(steps, last, kadeID, "hair.buns") {
		t.Fatalf("Kade self spawn not followed by hair=hair.buns: %+v then %+v", steps, last)
	}
	if !followedBy(steps, last, ashID, "") || !followedBy(steps, last, guestID, "") {
		t.Fatalf("Kade socket: hair after a bald/guest row: %+v", steps)
	}

	// The broadcast of Kade's join on Ash's and the guest's sockets: the
	// spawn, then the hair frame. Ash's socket since its own join carried
	// no hair for Ash or the guest.
	for name, ws := range map[string]*wsClient{"Ash": ash, "guest": guest} {
		steps, last, _ = stepsUntilSpawn(t, ws, kadeID)
		if !followedBy(steps, last, kadeID, "hair.buns") {
			t.Errorf("%s: broadcast of Kade not followed by hair=hair.buns: %+v then %+v", name, steps, last)
		}
		h := hairOf(steps)
		if _, ok := h[ashID]; ok {
			t.Errorf("%s: hair frame for Ash (hair.none): %v", name, h)
		}
		if _, ok := h[guestID]; ok {
			t.Errorf("%s: hair frame for the guest: %v", name, h)
		}
	}

	// A later joiner's row for Kade carries the hair too.
	late := hello(t, url, "another-guest", "Late")
	ack, err = protocol.DecodeHelloAck(late.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	steps, last, _ = stepsUntilSpawn(t, late, ack.EntityID)
	if !followedBy(steps, last, kadeID, "hair.buns") {
		t.Fatalf("late joiner: Kade's row not followed by hair=hair.buns: %+v", steps)
	}
	if h := hairOf(append(steps, last)); len(h) != 1 {
		t.Fatalf("late joiner: hair frames %v, want Kade's only", h)
	}
}

// Hair is not an equipment slot: the shipped registry does not declare it,
// so the equip cmd refuses to set or clear it (wrong_slot), and it is not
// among the armor slots that equip/sync iterate.
func TestHairIsNotAnEquipSlot(t *testing.T) {
	srv, _ := newTestServer(t)
	for _, item := range []string{"hair.buns", ""} {
		p := &store.Player{Equipped: map[string]string{}}
		err := sim.Equip(p, slotHair, item, srv.reg)
		if err == nil || !strings.Contains(err.Error(), sim.ReasonWrongSlot) {
			t.Errorf("Equip(hair, %q) = %v, want %s", item, err, sim.ReasonWrongSlot)
		}
	}
	if slices.Contains(wornSlots, slotHair) {
		t.Error("wornSlots contains hair")
	}
}
