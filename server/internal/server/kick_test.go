package server

import (
	"context"
	"testing"
	"time"
)

// A deleted account's live character is kicked 1008 and its disconnect
// save does NOT re-create the row (kind t28: PutPlayer re-INSERTed it,
// owned by an account that no longer existed).
func TestKick_DiscardsDeletedCharacter(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	owned, _ := putRows(t, st)
	world, url := newStrictWorld(t, st, false)

	ws := hello(t, url, owned, "")
	if name := wantSeated(t, ws); name != "Kade" {
		t.Fatalf("seated as %q", name)
	}
	if _, err := st.DeleteAccount(ctx, "acc1"); err != nil {
		t.Fatal(err)
	}
	world.Kick([]string{owned})
	wantClosed(t, ws)

	// The teardown save runs on the server's reader goroutine; give it
	// time to have happened before asking whether it wrote.
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(20 * time.Millisecond) {
		if world.OnlineCount() == 0 {
			break
		}
	}
	time.Sleep(100 * time.Millisecond)
	if p, err := st.GetPlayer(ctx, owned); err != nil || p != nil {
		t.Fatalf("row after kick = %+v, %v; want gone", p, err)
	}
}

// waitOffline waits for teardown (and its disconnect save) to have run.
func waitOffline(t *testing.T, world *Server) {
	t.Helper()
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(20 * time.Millisecond) {
		if world.OnlineCount() == 0 {
			break
		}
	}
	time.Sleep(100 * time.Millisecond)
}

// Phase 18 (C167): deleting ONE character the way the web handler does —
// DeletePlayer, Kick([token]), DeletePlayer again — closes its live
// session 1008 and its disconnect save does not re-create the row; the
// account and its session-less rows are untouched.
func TestDeleteCharacter_KicksLive(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	owned, guest := putRows(t, st)
	world, url := newStrictWorld(t, st, false)

	ws := hello(t, url, owned, "")
	if name := wantSeated(t, ws); name != "Kade" {
		t.Fatalf("seated as %q", name)
	}
	if err := st.DeletePlayer(ctx, owned); err != nil {
		t.Fatal(err)
	}
	world.Kick([]string{owned})
	if err := st.DeletePlayer(ctx, owned); err != nil {
		t.Fatal(err)
	}
	wantClosed(t, ws)
	waitOffline(t, world)
	if p, err := st.GetPlayer(ctx, owned); err != nil || p != nil {
		t.Fatalf("row after delete+kick = %+v, %v; want gone", p, err)
	}
	if p, _ := st.GetPlayer(ctx, guest); p == nil {
		t.Fatal("an unrelated row went with it")
	}
}

// Phase 18 (C166): a web edit of a connected character survives that
// session's saves — Retag, then the edit written once more, the way the
// web handler does; without Retag the disconnect save writes the old name
// back.
func TestRetag_EditSurvivesLiveSave(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	owned, _ := putRows(t, st)
	world, url := newStrictWorld(t, st, false)

	ws := hello(t, url, owned, "")
	if name := wantSeated(t, ws); name != "Kade" {
		t.Fatalf("seated as %q", name)
	}
	if err := st.EditCharacter(ctx, owned, "Kadence", "hair.buns", "skin.07", "suit.teal"); err != nil {
		t.Fatal(err)
	}
	world.Retag(owned, "Kadence", "hair.buns", "skin.07", "suit.teal")
	if err := st.EditCharacter(ctx, owned, "Kadence", "hair.buns", "skin.07", "suit.teal"); err != nil {
		t.Fatal(err)
	}
	ws.conn.Close()
	waitOffline(t, world)
	p, err := st.GetPlayer(ctx, owned)
	if err != nil || p == nil || p.Name != "Kadence" || p.Hair != "hair.buns" || p.Skin != "skin.07" || p.Suit != "suit.teal" || p.AccountID != "acc1" {
		t.Fatalf("row after disconnect save = %+v, %v", p, err)
	}
}
