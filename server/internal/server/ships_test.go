package server

import (
	"fmt"
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
)

func shipsInWorld(s *Server) []*sim.Ent {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []*sim.Ent
	for _, e := range s.worldEnts {
		if _, ok := e.Data.(*sim.ShipState); ok {
			out = append(out, e)
		}
	}
	return out
}

// waitShips polls until n ships are in the worldEnts cache — ensureShip
// adds to the world, and syncWorldEnts folds it in on the next tick.
func waitShips(t *testing.T, s *Server, n int) []*sim.Ent {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if ships := shipsInWorld(s); len(ships) == n {
			return ships
		}
		time.Sleep(20 * time.Millisecond)
	}
	ships := shipsInWorld(s)
	t.Fatalf("%d ships in world, want %d", len(ships), n)
	return ships
}

// aimAt points the player's look at a world position (the shop cmd's
// interaction re-check is eye-to-eye WITH the look cone, so a buyer must
// actually face the shopkeeper).
func aimAt(t *testing.T, s *Server, ws *wsClient, id uint32, target sim.Vec) {
	t.Helper()
	s.mu.Lock()
	me := s.clients[id].entity.State.Pos
	s.mu.Unlock()
	d := target.Sub(me)
	l := d.Len()
	if l < 1e-9 {
		return
	}
	look := d.Scale(1 / l)
	ws.sendFrame(t, protocol.EncodeInput(protocol.Input{
		LookDir: [3]float32{float32(look[0]), float32(look[1]), float32(look[2])}, Seq: 999,
	}))
	time.Sleep(120 * time.Millisecond) // two ticks: input applied, look live
}

func sendCmd(t *testing.T, ws *wsClient, seq uint16, opcode uint16, body string) protocol.CmdResult {
	t.Helper()
	ws.sendFrame(t, protocol.EncodeCmd(protocol.Cmd{Seq: seq, Opcode: opcode, Data: []byte(body)}))
	r, err := protocol.ParseCmdResult(ws.nextOf(t, protocol.MsgCmdResult))
	if err != nil {
		t.Fatal(err)
	}
	return r
}

// C33's server half: buy -> ship on the pad -> board the pilot seat, and
// the ship persists across a reconnect on the same token.
func TestShipPurchaseSpawnsAndPersists(t *testing.T) {
	s, url := newTestServer(t)

	token := fmt.Sprintf("ship-owner-%d", time.Now().UnixNano())
	a := dialWS(t, url)
	a.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: "buyer", Token: token}))
	ha, err := protocol.DecodeHelloAck(a.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	if n := len(shipsInWorld(s)); n != 0 {
		t.Fatalf("%d ships before anyone bought one", n)
	}

	// Stand at the quartermaster and buy. The shop needs proximity; find
	// the NPC and teleport within range (white-box, like the seat tests).
	s.mu.Lock()
	var npcID uint32
	var npcPos sim.Vec
	for _, e := range s.worldEnts {
		if e.Def == "npc.quartermaster" {
			npcID, npcPos = e.ID, sim.Vec(e.Pos)
		}
	}
	s.mu.Unlock()
	if npcID == 0 {
		t.Fatal("no quartermaster")
	}
	teleportNear(s, ha.EntityID, npcPos, 2)
	aimAt(t, s, a, ha.EntityID, npcPos)

	if r := sendCmd(t, a, 1, protocol.OpShopBuy,
		fmt.Sprintf(`{"npc":%d,"item":"ship.v1","qty":1}`, npcID)); r.Status != protocol.StatusOK {
		t.Fatalf("buy refused: %s", r.Data)
	}

	ships := waitShips(t, s, 1)
	// The test server is storeless, so the session is ephemeral by design
	// (join blanks the token) and the owner key falls back to the session
	// name. What matters: it is set, and stable enough that the reconnect
	// below does not duplicate the ship. Token-keyed ownership is exercised
	// wherever a store is attached (make test-pg / the deployed stack).
	if owner := ships[0].Data.(*sim.ShipState).Owner; owner == "" {
		t.Fatal("ship spawned with no owner key")
	}
	_ = token

	// Board the pilot seat (walk over first).
	teleportNear(s, ha.EntityID, sim.Vec(ships[0].Pos), 3)
	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: ships[0].ID, Seat: 1}))
	if r := seatResult(t, a); r.Result != protocol.SeatGranted {
		t.Fatalf("pilot seat: result %d", r.Result)
	}
	// Seat 3 exists on a ship (crew 3), unlike the rover.
	b := dialWS(t, url)
	bID := joinSeat(t, b, "crew")
	teleportNear(s, bID, sim.Vec(ships[0].Pos), 3)
	b.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: ships[0].ID, Seat: 3}))
	if r := seatResult(t, b); r.Result != protocol.SeatGranted {
		t.Fatalf("seat 3: result %d", r.Result)
	}

	// Reconnect on the same token: the ship must NOT duplicate.
	a.conn.Close()
	time.Sleep(100 * time.Millisecond)
	a2 := dialWS(t, url)
	a2.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: "buyer", Token: token}))
	if _, err := protocol.DecodeHelloAck(a2.nextOf(t, protocol.MsgHelloAck)); err != nil {
		t.Fatal(err)
	}
	if n := len(shipsInWorld(s)); n != 1 {
		t.Fatalf("%d ships after reconnect, want 1 (no duplicates)", n)
	}
}

// A ship pilot's input is read as flight fields and actually flies the
// ship — mode by occupancy and kind, not by the byte.
func TestPilotInputFliesShip(t *testing.T) {
	s, url := newTestServer(t)
	a := dialWS(t, url)
	aID := joinSeat(t, a, "pilot")

	// Conjure an owned ship directly; purchase is covered above.
	s.ensureShip("test-pilot")
	ship := waitShips(t, s, 1)[0]
	teleportNear(s, aID, sim.Vec(ship.Pos), 3)

	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: ship.ID, Seat: 1}))
	if r := seatResult(t, a); r.Result != protocol.SeatGranted {
		t.Fatalf("board: %d", r.Result)
	}

	start := sim.Vec(ship.Pos)
	// Mode 1: v = [thrust, roll, yaw_rate, pitch_rate, 0]; nose-up + thrust.
	for seq := uint16(1); seq <= 60; seq++ {
		a.sendFrame(t, protocol.EncodeInput(protocol.Input{
			MoveX: 1, LookDir: [3]float32{0, 2, 0}, Seq: seq, Mode: 1,
		}))
		rowOf(t, a, ship.ID)
	}
	s.mu.Lock()
	moved := sim.Vec(ship.Pos).Sub(start).Len()
	s.mu.Unlock()
	if moved < 2 {
		t.Fatalf("ship moved %.2f m under pilot input, want > 2", moved)
	}
}
