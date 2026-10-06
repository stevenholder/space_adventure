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
