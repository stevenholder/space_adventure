package server

import (
	"fmt"
	"math"
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
)

// joinSeat runs the hello handshake and returns the client's entity id,
// draining frames up to the hello_ack.
func joinSeat(t *testing.T, ws *wsClient, name string) uint32 {
	t.Helper()
	ws.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: name}))
	ha, err := protocol.DecodeHelloAck(ws.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	return ha.EntityID
}

// roverOf finds the world's one rover under the server lock.
func roverOf(t *testing.T, s *Server) *sim.Ent {
	t.Helper()
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, e := range s.worldEnts {
		if e.Kind == sim.EntityKind(protocol.EntityTypeVehicle) {
			return e
		}
	}
	t.Fatal("no rover in the world")
	return nil
}

// teleportNear moves a player's body to dist metres from pos, radially
// projected. White-box on purpose: walking 20 m through the movement model
// makes a seat test into a movement test.
func teleportNear(s *Server, id uint32, pos sim.Vec, dist float64) {
	c := published(s, id)
	s.mu.Lock()
	defer s.mu.Unlock()
	up := pos.Scale(1 / pos.Len())
	// Nudge along an arbitrary tangent, then re-project to the surface.
	tang := sim.Vec{-up[2], 0, up[0]}
	p := pos.Add(tang.Scale(dist / tang.Len()))
	u2 := p.Scale(1 / p.Len())
	c.entity.State.Pos = u2.Scale(s.terrain.SampleRadius(u2))
}

// published waits for join to put id in s.clients. join sends hello_ack and
// the rest of the handshake BEFORE it publishes the client (the stream order
// PROTOCOL.md promises), so a test acting on hello_ack alone races it: the
// merge of #58 nil-panicked in teleportNear on CI. Call without s.mu held.
func published(s *Server, id uint32) *client {
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(5 * time.Millisecond) {
		s.mu.Lock()
		c := s.clients[id]
		s.mu.Unlock()
		if c != nil {
			return c
		}
	}
	panic(fmt.Sprintf("client %d never published", id))
}

func seatResult(t *testing.T, ws *wsClient) protocol.SeatResult {
	t.Helper()
	r, err := protocol.DecodeSeatResult(ws.nextOf(t, protocol.MsgSeatResult))
	if err != nil {
		t.Fatal(err)
	}
	return r
}

// rowOf scans snapshots until one carries the entity, then returns its row.
func rowOf(t *testing.T, ws *wsClient, id uint32) protocol.Entity {
	t.Helper()
	for i := 0; i < 100; i++ {
		snap, err := protocol.DecodeSnapshot(ws.nextOf(t, protocol.MsgSnapshot))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range snap.Entities {
			if e.ID == id {
				return e
			}
		}
	}
	t.Fatalf("entity %d never appeared in a snapshot", id)
	return protocol.Entity{}
}

// waitRow scans snapshots until the entity's row satisfies ok.
func waitRow(t *testing.T, ws *wsClient, id uint32, what string, ok func(protocol.Entity) bool) protocol.Entity {
	t.Helper()
	for i := 0; i < 100; i++ {
		row := rowOf(t, ws, id)
		if ok(row) {
			return row
		}
	}
	t.Fatalf("%s: never satisfied", what)
	return protocol.Entity{}
}

func dist32(a [3]float32, b [3]float32) float64 {
	dx := float64(a[0] - b[0])
	dy := float64(a[1] - b[1])
	dz := float64(a[2] - b[2])
	return math.Sqrt(dx*dx + dy*dy + dz*dz)
}

// C26 + drive + C32: board the driver seat, see the composed transform,
// drive the rover with mode-2 input, disembark onto the ground nearby.
func TestBoardDriveDisembark(t *testing.T) {
	s, url := newTestServer(t)
	a := dialWS(t, url)
	aID := joinSeat(t, a, "driver")

	rover := roverOf(t, s)
	teleportNear(s, aID, sim.Vec(rover.Pos), 3)

	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 1}))
	if r := seatResult(t, a); r.Result != protocol.SeatGranted {
		t.Fatalf("board: result %d, want granted", r.Result)
	}

	// C26: the snapshot shows occupancy and the composed seat position.
	row := waitRow(t, a, aID, "seated row", func(e protocol.Entity) bool {
		return e.ParentID == rover.ID && e.Seat == 1
	})
	roverRow := rowOf(t, a, rover.ID)
	want := sim.ComposeSeat(
		sim.EntityKind(protocol.EntityTypeVehicle),
		sim.Vec{float64(roverRow.Pos[0]), float64(roverRow.Pos[1]), float64(roverRow.Pos[2])},
		sim.Quat{float64(roverRow.Quat[0]), float64(roverRow.Quat[1]), float64(roverRow.Quat[2]), float64(roverRow.Quat[3])},
		1)
	got := sim.Vec{float64(row.Pos[0]), float64(row.Pos[1]), float64(row.Pos[2])}
	if d := got.Sub(want).Len(); d > 1e-2 {
		t.Fatalf("C26 composed seat position off by %g m (limit 1e-2)", d)
	}

	// Drive: full throttle, mode 2. The rover must actually move.
	start := roverRow.Pos
	for seq := uint16(1); seq <= 40; seq++ {
		a.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveX: 1, Seq: seq, Mode: 2}))
		rowOf(t, a, rover.ID) // consume one snapshot per input ≈ one tick
	}
	moved := rowOf(t, a, rover.ID)
	if d := dist32(moved.Pos, start); d < 1 {
		t.Fatalf("rover moved %.3f m under full throttle, want > 1", d)
	}

	// C32: disembark lands on the terrain, near the rover, unseated.
	a.sendFrame(t, protocol.EncodeDisembark())
	if r := seatResult(t, a); r.Result != protocol.SeatGranted || r.Seat != 1 {
		t.Fatalf("disembark: %+v, want granted seat 1", r)
	}
	out := waitRow(t, a, aID, "unseated row", func(e protocol.Entity) bool {
		return e.ParentID == 0 && e.Seat == 0
	})
	roverNow := rowOf(t, a, rover.ID)
	if d := dist32(out.Pos, roverNow.Pos); d > 10 {
		t.Fatalf("C32 disembarked %.2f m from the rover, want ≤ 10", d)
	}
}

// C28 (sequential race), C29 (refusals), C31 (passenger input inert).
func TestSeatRaceRefusalsPassenger(t *testing.T) {
	s, url := newTestServer(t)
	a := dialWS(t, url)
	aID := joinSeat(t, a, "a")
	b := dialWS(t, url)
	bID := joinSeat(t, b, "b")

	rover := roverOf(t, s)

	// C29: boarding from beyond board_dist refuses with 2. (Bodies spawn
	// ~rover_spawn_dist from the rover, which is > board_dist.)
	b.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 1}))
	if r := seatResult(t, b); r.Result != protocol.SeatOutOfRange {
		t.Fatalf("far board: result %d, want out-of-range", r.Result)
	}
	// C29: disembark while not seated refuses with 3.
	b.sendFrame(t, protocol.EncodeDisembark())
	if r := seatResult(t, b); r.Result != protocol.SeatInvalid {
		t.Fatalf("unseated disembark: result %d, want invalid", r.Result)
	}

	teleportNear(s, aID, sim.Vec(rover.Pos), 3)
	teleportNear(s, bID, sim.Vec(rover.Pos), 3)

	// Nonexistent seat refuses with 3; a non-vehicle id refuses with 3.
	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 5}))
	if r := seatResult(t, a); r.Result != protocol.SeatInvalid {
		t.Fatalf("seat 5: result %d, want invalid", r.Result)
	}
	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: 424242, Seat: 1}))
	if r := seatResult(t, a); r.Result != protocol.SeatInvalid {
		t.Fatalf("bad vehicle: result %d, want invalid", r.Result)
	}

	// C28: both want seat 1; exactly one gets it.
	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 1}))
	if r := seatResult(t, a); r.Result != protocol.SeatGranted {
		t.Fatalf("first board: result %d, want granted", r.Result)
	}
	b.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 1}))
	if r := seatResult(t, b); r.Result != protocol.SeatOccupied {
		t.Fatalf("second board: result %d, want occupied", r.Result)
	}

	// C31: B takes the passenger seat; B's movement input must leave the
	// rover invariant and B composed at the seat.
	b.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 2}))
	if r := seatResult(t, b); r.Result != protocol.SeatGranted {
		t.Fatalf("passenger board: result %d, want granted", r.Result)
	}
	before := rowOf(t, b, rover.ID)
	for seq := uint16(1); seq <= 20; seq++ {
		b.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveX: 1, MoveY: 1, Seq: seq, Mode: 0}))
		rowOf(t, b, rover.ID)
	}
	after := rowOf(t, b, rover.ID)
	if d := dist32(after.Pos, before.Pos); d > 1e-3 {
		t.Fatalf("C31 passenger input moved the rover %.4f m", d)
	}
	bRow := rowOf(t, b, bID)
	if bRow.ParentID != rover.ID || bRow.Seat != 2 {
		t.Fatalf("passenger row not composed: %+v", bRow)
	}

	// Playtest 2: the passenger shoots, the driver does not. FiringTick is
	// set only by an accepted shot.
	arm := func(id uint32) *client {
		s.mu.Lock()
		c := s.clients[id]
		s.mu.Unlock()
		c.ident.Mutate(func(p *store.Player) {
			if err := sim.AddItem(p, "weapon.pulse", 1, s.reg); err != nil {
				t.Fatalf("AddItem: %v", err)
			}
			p.Equipped[slotPrimary] = "weapon.pulse"
		})
		return c
	}
	ca, cb := arm(aID), arm(bID)
	fired := func(c *client) bool {
		s.mu.Lock()
		defer s.mu.Unlock()
		return c.entity.FiringTick != 0
	}
	s.fireLocked(ca, protocol.Fire{Seq: 1, Dir: [3]float32{0, 0, 1}})
	s.fireLocked(cb, protocol.Fire{Seq: 1, Dir: [3]float32{0, 0, 1}})
	if fired(ca) {
		t.Error("the driver's shot was accepted, want dropped")
	}
	if !fired(cb) {
		t.Error("the passenger's shot was dropped, want accepted")
	}
}

// Task 10: the driver's disconnect frees the seat and the rover coasts —
// another player can take seat 1 afterward.
func TestDriverDisconnectFreesSeat(t *testing.T) {
	s, url := newTestServer(t)
	a := dialWS(t, url)
	aID := joinSeat(t, a, "a")
	b := dialWS(t, url)
	bID := joinSeat(t, b, "b")

	rover := roverOf(t, s)
	teleportNear(s, aID, sim.Vec(rover.Pos), 3)
	teleportNear(s, bID, sim.Vec(rover.Pos), 3)

	a.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 1}))
	if r := seatResult(t, a); r.Result != protocol.SeatGranted {
		t.Fatalf("board: %d", r.Result)
	}
	a.conn.Close()

	// B polls for the seat; the leave path frees it.
	granted := false
	for i := 0; i < 50 && !granted; i++ {
		b.sendFrame(t, protocol.EncodeBoard(protocol.Board{VehicleID: rover.ID, Seat: 1}))
		granted = seatResult(t, b).Result == protocol.SeatGranted
	}
	if !granted {
		t.Fatal("seat 1 never freed after the driver disconnected")
	}
	_ = s
}
