// Package server is the authoritative game server: a WebSocket gateway on
// /ws (PROTOCOL.md) feeding a fixed-tick (20 Hz) simulation. The world is an
// in-memory map of entities, one per connected player in M1; the tick loop
// steps every entity against the shared terrain field and fans out one full
// snapshot per tick, patching the per-client ack_seq into a shared body.
package server

import (
	"context"
	"encoding/binary"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// Tunables (PROTOCOL "Semantics": heartbeat; GDD tick rate).
const (
	// silentTimeout is how long a connection may go without any inbound
	// message before the server drops it (PROTOCOL: "drops connections
	// silent for 10 s"). The client pings every 2 s when idle, so a live
	// client never trips it.
	silentTimeout = 10 * time.Second
	// writeTimeout bounds a single outbound write; a client that stops
	// draining its socket is dead.
	writeTimeout = 10 * time.Second
	// outQueue is the per-connection outbound buffer. Snapshots are full
	// state, so when the queue is full the newest snapshot is dropped
	// rather than blocking the tick loop.
	outQueue = 32
)

// Server owns the world. All methods are safe for concurrent use.
type Server struct {
	terrain  *terrain.Field
	seed     uint64
	tickHz   uint16
	terrainF []byte // pre-encoded terrain frame, sent on every join

	upgrader websocket.Upgrader

	mu       sync.Mutex
	nextID   uint32
	clients  map[uint32]*client
	closing  bool
	tickNo   uint32
	list     []*client // tick-scoped, reused
	snapBuf  []byte    // tick-scoped snapshot body, reused
	snapPool sync.Pool // []byte fan-out buffers (per-client ack_seq patch)
}

// New builds a Server over the generated terrain field.
func New(t *terrain.Field, seed uint64) *Server {
	s := &Server{
		terrain: t,
		seed:    seed,
		tickHz:  sim.TickHz,
		upgrader: websocket.Upgrader{
			CheckOrigin: func(*http.Request) bool { return true },
		},
		clients: make(map[uint32]*client),
	}
	// The terrain frame is pre-encoded once: it is just the 2-byte type
	// prefix over the field's canonical payload (PROTOCOL "terrain").
	payload := t.Encode()
	frame := make([]byte, 2+len(payload))
	binary.LittleEndian.PutUint16(frame[0:2], protocol.MsgTerrain)
	copy(frame[2:], payload)
	s.terrainF = frame
	s.snapPool.New = func() any { return []byte(nil) }
	return s
}

// HandleWS upgrades /ws and runs the connection's reader and writer.
func (s *Server) HandleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return // the upgrader already answered
	}
	// Oversized frames are rejected with close code 1009 by the library
	// (PROTOCOL: max message size 64 KiB).
	conn.SetReadLimit(protocol.MaxMessageSize)
	c := &client{srv: s, conn: conn, out: make(chan msg, outQueue), done: make(chan struct{})}
	conn.SetReadDeadline(time.Now().Add(silentTimeout))
	go c.writer()
	c.reader() // blocks until the connection dies
}

// Run starts the fixed-tick loop and stops it when ctx is done.
func (s *Server) Run(ctx context.Context) {
	t := time.NewTicker(time.Second / time.Duration(s.tickHz))
	defer t.Stop()
	var lastLog time.Time
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-t.C:
			s.tick()
			if now.Sub(lastLog) >= 5*time.Second {
				lastLog = now
				s.mu.Lock()
				n, tick := len(s.clients), s.tickNo
				s.mu.Unlock()
				log.Printf("tick %d: %d client(s), %d Hz", tick, n, s.tickHz)
			}
		}
	}
}

// CloseAll shuts the world down: stops admitting work, closes every
// connection, and broadcasts nothing (the server is going away).
func (s *Server) CloseAll() {
	s.mu.Lock()
	s.closing = true
	cs := make([]*client, 0, len(s.clients))
	for _, c := range s.clients {
		cs = append(cs, c)
	}
	s.clients = make(map[uint32]*client)
	s.mu.Unlock()
	for _, c := range cs {
		c.teardown()
	}
}

// tick advances the whole world by one step and fans out the snapshot.
// Step + encode happen under the world lock (both are fast and lock-free
// internally); fan-out is non-blocking channel sends.
func (s *Server) tick() {
	t0 := time.Now()
	s.mu.Lock()
	s.tickNo++
	tick := s.tickNo
	s.list = s.list[:0]
	for _, c := range s.clients {
		c.step(s.terrain)
		s.list = append(s.list, c)
	}
	body := s.encodeSnapshot(tick)
	// Fan out under the lock: sendSnapshot is a non-blocking channel
	// send, and the shared body must not be recycled by the next tick
	// while copies are in flight.
	for _, c := range s.list {
		c.sendSnapshot(body, uint16(c.ackSeq.Load()))
	}
	s.mu.Unlock()
	step := time.Since(t0)
	if step > 5*time.Millisecond {
		log.Printf("slow tick %d: step %s with %d entities", tick, step, len(s.list))
	}
}

// encodeSnapshot renders the full snapshot frame once (body shared by all
// clients; ack_seq is patched per client in sendSnapshot). Must be called
// with s.mu held.
func (s *Server) encodeSnapshot(tick uint32) []byte {
	n := len(s.list)
	size := 2 + 8 + n*protocol.EntitySize // type | tick,ack,count | rows
	if cap(s.snapBuf) < size {
		s.snapBuf = make([]byte, 0, size)
	}
	b := s.snapBuf[:0]
	// AppendSnapshotHeader emits the type prefix + tick/ack/count.
	b = protocol.AppendSnapshotHeader(b, tick, 0, uint16(n))
	for _, c := range s.list {
		q := c.entity.State.OrientationQuat()
		p, v := c.entity.State.Pos, c.entity.State.Vel
		b = protocol.AppendEntity(b, protocol.Entity{
			ID:   c.entity.ID,
			Pos:  [3]float32{float32(p[0]), float32(p[1]), float32(p[2])},
			Quat: [4]float32{float32(q[0]), float32(q[1]), float32(q[2]), float32(q[3])},
			Vel:  [3]float32{float32(v[0]), float32(v[1]), float32(v[2])},
		})
	}
	return b
}

// join registers c as a new player: allocates the entity, enqueues the join
// handshake (hello_ack, terrain, spawn for every existing entity, spawn for
// self), then publishes c to the world so the next tick includes it. The
// handshake is fully enqueued before publication, so the client's stream
// order is hello_ack, terrain, spawn(s), then snapshots (FIFO per
// connection).
func (s *Server) join(c *client, h protocol.Hello) {
	s.mu.Lock()
	if c.entity != nil || s.closing {
		s.mu.Unlock()
		c.fail() // already joined or server shutting down
		return
	}
	s.nextID++
	id := s.nextID
	c.id = id
	name := SanitizeName(h.Name, id)
	c.entity = &entity{
		ID:    id,
		Name:  name,
		State: sim.SpawnState(s.terrain),
	}
	c.entity.PrevLook = c.entity.State.Facing
	// Collect existing players' spawn rows before publishing self.
	others := make([]msg, 0, len(s.clients))
	for _, oc := range s.clients {
		others = append(others, msg{data: protocol.EncodeSpawn(protocol.Spawn{
			EntityID:   oc.entity.ID,
			EntityType: protocol.EntityTypePlayer,
			Data:       []byte(oc.entity.Name),
		})})
	}
	s.mu.Unlock()

	self := protocol.EncodeSpawn(protocol.Spawn{
		EntityID:   id,
		EntityType: protocol.EntityTypePlayer,
		Data:       []byte(name),
	})
	c.send(msg{data: protocol.EncodeHelloAck(protocol.HelloAck{
		ServerVer: protocol.VersionM1,
		TickHz:    s.tickHz,
		WorldSeed: uint32(s.seed),
		EntityID:  id,
	})})
	c.send(msg{data: s.terrainF})
	for _, m := range others {
		c.send(m)
	}
	c.send(msg{data: self})

	// Publish, then tell the existing clients a body appeared.
	s.mu.Lock()
	s.clients[id] = c
	for _, oc := range s.clients {
		if oc != c {
			oc.send(msg{data: self})
		}
	}
	s.mu.Unlock()
}

// leave removes c's entity from the world and tells the remaining clients.
func (s *Server) leave(c *client) {
	s.mu.Lock()
	if c.entity == nil {
		s.mu.Unlock()
		return
	}
	id := c.entity.ID
	delete(s.clients, id)
	s.mu.Unlock()

	f := protocol.EncodeDespawn(protocol.Despawn{EntityID: id})
	s.mu.Lock()
	for _, oc := range s.clients {
		oc.send(msg{data: f})
	}
	s.mu.Unlock()
}
