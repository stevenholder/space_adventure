package server

import (
	"context"
	"math"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// testTimeout bounds every read in the integration test; a healthy server
// answers within one tick (50 ms), so this is generous.
const testTimeout = 3 * time.Second

type wsClient struct {
	t    *testing.T
	conn *websocket.Conn
}

func dialWS(t *testing.T, url string) *wsClient {
	t.Helper()
	d := websocket.Dialer{}
	conn, _, err := d.Dial(url, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	// The terrain frame (~50 KiB) exceeds the 4 KiB default read limit.
	conn.SetReadLimit(protocol.MaxMessageSize)
	return &wsClient{t: t, conn: conn}
}

func (c *wsClient) sendFrame(t *testing.T, frame []byte) {
	t.Helper()
	c.conn.SetWriteDeadline(time.Now().Add(testTimeout))
	if err := c.conn.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		t.Fatalf("send: %v", err)
	}
}

// next reads one frame and splits it into (type, payload).
func (c *wsClient) next(t *testing.T) (uint16, []byte) {
	t.Helper()
	c.conn.SetReadDeadline(time.Now().Add(testTimeout))
	_, data, err := c.conn.ReadMessage()
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	typ, payload, err := protocol.DecodeFrame(data)
	if err != nil {
		t.Fatalf("decode frame: %v", err)
	}
	return typ, payload
}

// nextOf skips frames of other types (snapshots, pongs) until want arrives.
func (c *wsClient) nextOf(t *testing.T, want uint16) []byte {
	t.Helper()
	for i := 0; i < 500; i++ {
		typ, payload := c.next(t)
		if typ == want {
			return payload
		}
	}
	t.Fatalf("no %04x frame within 500 frames", want)
	return nil
}

func newTestServer(t *testing.T) (*Server, string) {
	t.Helper()
	field := terrain.Generate(1337)
	world := New(field, 1337)
	mux := http.NewServeMux()
	mux.HandleFunc("/ws", world.HandleWS)
	ts := httptest.NewServer(mux)
	t.Cleanup(ts.Close)
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(func() {
		world.CloseAll()
		cancel()
	})
	go world.Run(ctx)
	return world, "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws"
}

func rad(p [3]float32) float64 {
	return math.Sqrt(float64(p[0])*float64(p[0]) + float64(p[1])*float64(p[1]) + float64(p[2])*float64(p[2]))
}

// TestJoinSnapshotAndAck exercises the full join flow for two players:
// handshake order, terrain delivery and round-trip, spawn broadcasts, the
// per-client ack_seq patch, client-driven movement, ping/pong, and despawn
// on disconnect.
func TestJoinSnapshotAndAck(t *testing.T) {
	_, url := newTestServer(t)

	// --- player A joins with an empty name (fallback "Player 1") ---
	a := dialWS(t, url)
	a.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: ""}))

	ha, err := protocol.DecodeHelloAck(a.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatalf("hello_ack: %v", err)
	}
	if ha.EntityID == 0 || ha.WorldSeed != 1337 || ha.TickHz != 20 || ha.ServerVer != protocol.VersionPhase2 {
		t.Fatalf("hello_ack = %+v, want nonzero id, seed=1337, tickHz=20", ha)
	}

	// Terrain must arrive before the first snapshot (client must not
	// simulate before the terrain).
	typ, terr := a.next(t)
	if typ != protocol.MsgTerrain {
		t.Fatalf("expected terrain before first snapshot, got %04x", typ)
	}
	tf, err := protocol.DecodeTerrain(terr)
	if err != nil {
		t.Fatalf("terrain: %v", err)
	}
	if tf.FaceGrid != terrain.FaceGrid || len(tf.Radii) != 6*int(terrain.FaceGrid)*int(terrain.FaceGrid) {
		t.Fatalf("terrain grid %d / %d radii, want %d", tf.FaceGrid, len(tf.Radii), terrain.FaceGrid)
	}
	if math.IsInf(float64(tf.RadiusMin), 0) || tf.RadiusMin >= tf.RadiusMax {
		t.Fatalf("terrain radius range [%g, %g] invalid", tf.RadiusMin, tf.RadiusMax)
	}
	// The decoded field must round-trip to the exact payload sent.
	// EncodeTerrain prepends the 2-byte type; compare the payload part.
	if re := protocol.EncodeTerrain(tf); len(re) != len(terr)+2 || string(re[2:]) != string(terr) {
		t.Fatalf("terrain payload does not round-trip (%d vs %d bytes)", len(re), len(terr))
	}

	// The world contains NPCs and targets placed from server/data/zones, so a
	// joining client receives a spawn for each of them alongside its own. The
	// property that matters is that THIS client's body arrives, not that the
	// world is empty — asserting "the first spawn is mine" made a test of the
	// join sequence into a test that no content exists, which then blocked
	// content from shipping.
	var sp protocol.Spawn
	for {
		got, err := protocol.DecodeSpawn(a.nextOf(t, protocol.MsgSpawn))
		if err != nil {
			t.Fatalf("spawn: %v", err)
		}
		if got.EntityID == ha.EntityID {
			sp = got
			break
		}
	}
	if sp.EntityType != protocol.EntityTypePlayer || string(sp.Data) != "Player 1" {
		t.Fatalf("own spawn = %+v, want type=player name=\"Player 1\"", sp)
	}

	// First snapshot: our own entity, at spawn, at rest.
	snap, err := protocol.DecodeSnapshot(a.nextOf(t, protocol.MsgSnapshot))
	if err != nil {
		t.Fatalf("snapshot: %v", err)
	}
	var e protocol.Entity
	found := false
	for _, row := range snap.Entities {
		if row.ID == ha.EntityID {
			e, found = row, true
			break
		}
	}
	if !found {
		t.Fatalf("own entity %d absent from first snapshot (%d entities)", ha.EntityID, len(snap.Entities))
	}
	r := rad(e.Pos)
	if r < 124 || r > 190 {
		t.Fatalf("spawn radius %f outside [124, 190]", r)
	}
	for _, v := range e.Vel {
		if math.Abs(float64(v)) > 1e-3 {
			t.Fatalf("spawn entity moving: vel=%v", e.Vel)
		}
	}
	spawnTick := snap.Tick
	if snap.Tick < 1 {
		t.Fatalf("first snapshot tick %d, want >= 1", snap.Tick)
	}

	// --- player B joins with a control-laden name ---
	b := dialWS(t, url)
	b.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: "\x01bob\x1f\x7f"}))

	haB, err := protocol.DecodeHelloAck(b.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatalf("hello_ack B: %v", err)
	}
	if haB.EntityID != 2 || haB.WorldSeed != 1337 {
		t.Fatalf("hello_ack B = %+v, want id=2", haB)
	}
	if typ, _ := b.next(t); typ != protocol.MsgTerrain {
		t.Fatalf("B: expected terrain before first snapshot, got %04x", typ)
	}
	// B learns the world's entities and A, then itself. Collect the player
	// spawns and assert on those: the world's NPCs and targets are also
	// announced here, and their order relative to players is not a contract.
	var spA, spB protocol.Spawn
	for spA.EntityID == 0 || spB.EntityID == 0 {
		got, err := protocol.DecodeSpawn(b.nextOf(t, protocol.MsgSpawn))
		if err != nil {
			t.Fatalf("spawn (for B): %v", err)
		}
		if got.EntityType != protocol.EntityTypePlayer {
			continue // world entity (NPC / target)
		}
		if got.EntityID == ha.EntityID {
			spA = got
		} else if got.EntityID == haB.EntityID {
			spB = got
		}
	}
	if string(spA.Data) != "Player 1" {
		t.Fatalf("B's spawn of A = %+v, want name %q", spA, "Player 1")
	}
	if string(spB.Data) != "bob" {
		t.Fatalf("B's own spawn = %+v, want name=bob (controls stripped)", spB)
	}

	// A learns about B.
	spAB, err := protocol.DecodeSpawn(a.nextOf(t, protocol.MsgSpawn))
	if err != nil {
		t.Fatalf("spawn B (for A): %v", err)
	}
	if spAB.EntityID != 2 || string(spAB.Data) != "bob" {
		t.Fatalf("A's spawn of B = %+v, want id=2 bob", spAB)
	}

	// --- A sends input seq=7; A's next snapshot acks it, B's same-tick
	// snapshot does not (per-client patch of the shared body). ---
	a.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveY: 1, LookDir: [3]float32{1, 0, 0}, Seq: 7}))
	snapA, err := protocol.DecodeSnapshot(a.nextOf(t, protocol.MsgSnapshot))
	if err != nil {
		t.Fatalf("snapshot A: %v", err)
	}
	// Both players must be in the snapshot. The exact row count is not a
	// contract: the world's NPCs and targets are in there too, and pinning the
	// number turns a "do both players appear" check into "no content exists".
	if !hasEntity(snapA, ha.EntityID) || !hasEntity(snapA, haB.EntityID) {
		t.Fatalf("A snapshot missing a player: ids %v, want both %d and %d",
			entityIDs(snapA), ha.EntityID, haB.EntityID)
	}
	snapB, err := protocol.DecodeSnapshot(b.nextOf(t, protocol.MsgSnapshot))
	if err != nil {
		t.Fatalf("snapshot B: %v", err)
	}
	// B may have already received this tick; wait for the matching one.
	for snapB.Tick != snapA.Tick {
		snapB, err = protocol.DecodeSnapshot(b.nextOf(t, protocol.MsgSnapshot))
		if err != nil {
			t.Fatalf("snapshot B: %v", err)
		}
	}
	if snapB.AckSeq != 0 {
		t.Fatalf("B ack_seq = %d, want 0 (B sent no input)", snapB.AckSeq)
	}

	// --- A's input is held: after ~1 s of walking, A has moved. ---
	// A's out-queue is FIFO (cap 32) and still holds snapshots from
	// before the sleep, so drain until at least 18 ticks past the ack.
	time.Sleep(time.Second)
	target := snapA.Tick + 18
	snapA, err = protocol.DecodeSnapshot(a.nextOf(t, protocol.MsgSnapshot))
	if err != nil {
		t.Fatalf("snapshot A (walk): %v", err)
	}
	for snapA.Tick < target {
		snapA, err = protocol.DecodeSnapshot(a.nextOf(t, protocol.MsgSnapshot))
		if err != nil {
			t.Fatalf("snapshot A (walk): %v", err)
		}
	}
	var ea *protocol.Entity
	for i := range snapA.Entities {
		if snapA.Entities[i].ID == 1 {
			ea = &snapA.Entities[i]
			break
		}
	}
	if ea == nil {
		t.Fatalf("A missing from snapshot after join")
	}
	if float64(ea.Pos[0]) < 1.0 {
		t.Fatalf("A did not walk forward: pos=%v (facing is +X at spawn)", ea.Pos)
	}
	if float64(ea.Vel[0]) < 1.0 {
		t.Fatalf("A not moving forward: vel=%v", ea.Vel)
	}
	r = rad(ea.Pos)
	if r < 124 || r > 190 {
		t.Fatalf("A left the planet: radius %f", r)
	}
	if snapA.Tick <= spawnTick {
		t.Fatalf("ticks did not advance: %d <= %d", snapA.Tick, spawnTick)
	}

	// --- heartbeat ---
	a.sendFrame(t, protocol.EncodePing(protocol.Ping{TSMs: 12345}))
	pong, err := protocol.DecodePong(a.nextOf(t, protocol.MsgPong))
	if err != nil {
		t.Fatalf("pong: %v", err)
	}
	if pong.TSMs != 12345 {
		t.Fatalf("pong ts = %d, want 12345", pong.TSMs)
	}

	// --- B leaves: A is told. ---
	b.conn.Close()
	dp, err := protocol.DecodeDespawn(a.nextOf(t, protocol.MsgDespawn))
	if err != nil {
		t.Fatalf("despawn: %v", err)
	}
	if dp.EntityID != 2 {
		t.Fatalf("despawn id = %d, want 2", dp.EntityID)
	}
	// And the next A snapshot still has A but no longer B. The world's NPCs
	// and targets remain — they are not players and do not leave.
	snapA, err = protocol.DecodeSnapshot(a.nextOf(t, protocol.MsgSnapshot))
	if err != nil {
		t.Fatalf("snapshot A (after leave): %v", err)
	}
	if !hasEntity(snapA, ha.EntityID) {
		t.Fatalf("after B left, A is missing from its own snapshot: ids %v", entityIDs(snapA))
	}
	if hasEntity(snapA, haB.EntityID) {
		t.Fatalf("after B left, B %d is still present: ids %v", haB.EntityID, entityIDs(snapA))
	}
}

// TestInputBeforeHelloIsRejected closes connections that send an input
// before the hello handshake (PROTOCOL: hello is the first message).
func TestInputBeforeHelloIsRejected(t *testing.T) {
	_, url := newTestServer(t)
	c := dialWS(t, url)
	c.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveY: 1, Seq: 1}))
	// The server answers with a protocol-error close, then the read fails.
	c.conn.SetReadDeadline(time.Now().Add(testTimeout))
	_, _, err := c.conn.ReadMessage()
	if err == nil {
		t.Fatalf("expected read failure after pre-hello input, got a message")
	}
}

// hasEntity reports whether a snapshot contains the given entity id.
func hasEntity(s protocol.Snapshot, id uint32) bool {
	for _, e := range s.Entities {
		if e.ID == id {
			return true
		}
	}
	return false
}

// entityIDs lists a snapshot's entity ids, for failure messages.
func entityIDs(s protocol.Snapshot) []uint32 {
	out := make([]uint32, 0, len(s.Entities))
	for _, e := range s.Entities {
		out = append(out, e.ID)
	}
	return out
}
