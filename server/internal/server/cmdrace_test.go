package server

import (
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
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
