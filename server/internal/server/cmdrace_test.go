package server

import (
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// TestCmdWhileTicking: a cmd built from cmdWorld (inventory) arriving while
// the tick is stepping the requester must read Pos/look under s.mu. Run
// with -race; before the fix this tripped sim.Step vs (*client).lookDir.
func TestCmdWhileTicking(t *testing.T) {
	_, url := newTestServer(t)
	ws, _ := joinClient(t, url, "racer")
	for i := uint16(1); i <= 40; i++ {
		// Walk, so the tick is writing State every step, and cmd between.
		ws.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveY: 1, LookDir: [3]float32{0, 0, 1}, Seq: i}))
		time.Sleep(60 * time.Millisecond) // ~a tick between cmds; 40 stay under the cmd rate (20 burst + 10/s)
		if r := sendCmd(t, ws, i, protocol.OpInventory, `{}`); r.Status != protocol.StatusOK {
			t.Fatalf("inventory cmd %d status %d", i, r.Status)
		}
	}
}

// TestReloadWhileTicking: `fire` spends a round under s.mu and `reload`
// refills the magazine from a cmd; both must touch Magazine under s.mu.
func TestReloadWhileTicking(t *testing.T) {
	srv, url := newTestServer(t)
	ws, id := joinClient(t, url, "reloader")
	cl := published(srv, id)
	cl.ident.Mutate(func(p *store.Player) {
		p.Equipped["primary"] = "weapon.pulse"
		_ = sim.AddItem(p, "ammo.cell", 200, srv.reg)
	})
	for i := uint16(1); i <= 40; i++ {
		ws.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveY: 1, LookDir: [3]float32{0, 0, 1}, Seq: i}))
		ws.sendFrame(t, protocol.EncodeFire(protocol.Fire{Seq: i, Dir: [3]float32{0, 0, 1}}))
		time.Sleep(60 * time.Millisecond)
		if r := sendCmd(t, ws, i, protocol.OpReload, `{}`); r.Status != protocol.StatusOK && r.Status != protocol.StatusRefused {
			t.Fatalf("reload cmd %d status %d", i, r.Status)
		}
	}
	snap := cl.ident.Snapshot()
	if left := sim.CountItem(&snap, "ammo.cell"); left >= 200 {
		t.Fatalf("reserve %d: no shot was spent and reloaded", left)
	}
}
