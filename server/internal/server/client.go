package server

import (
	"math"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// rttPingInterval is how often the server sends its own WebSocket-level
// ping control frame to measure this connection's RTT. This is distinct
// from the app-level ping/pong (PROTOCOL.md MsgPing/MsgPong): that pair
// carries a client-local clock the server only echoes, so it cannot be used
// as the server's own RTT measurement (PROTOCOL.md "fire" — rewind is
// bounded by "the server's own smoothed RTT/2 ... never a client value").
const rttPingInterval = 3 * time.Second

// rttSmoothing is the EWMA weight given to each new RTT sample (1/n).
const rttSmoothing = 5

// entity is one body in the world. The connection's input moves this
// entity — a lookup, not an identity: in M1 it is the player's own body,
// and from M2 a player in a pilot seat drives the vehicle instead.
type entity struct {
	ID       uint32
	Name     string
	State    sim.State
	PrevLook sim.Vec

	Health int // current hit points (PROTOCOL "health"); players are not
	// damageable in Phase 2 (GDD "Health and damage"), so this only ever
	// reflects the def's max_health.

	// Ephemeral per-connection weapon state (docs/PROTOCOL.md "fire").
	// Not persisted: store.Player carries no magazine/reserve field yet
	// (internal/server/cmd.go, OpReload).
	EquippedWeapon string // last primary item id observed, to detect a re-equip
	Magazine       int
	LastFireTick   uint32 // tick of the last accepted shot (0 = never fired)
	FiringTick     uint32 // tick a shot last resolved, for the snapshot's firing flag
}

// msg is one outbound frame on the client's outbound queue.
type msg struct {
	data   []byte
	pooled bool // data came from the server's snapPool
}

// client is one WebSocket connection and the entity it drives.
type client struct {
	// vitals is this player's health, death timer and regen state. Server-side
	// only: the client renders what the snapshot and events tell it, and never
	// decides it died (docs/GDD.md, "Player death and respawn").
	vitals sim.Vitals
	srv    *Server
	conn   *websocket.Conn
	id     uint32
	entity *entity   // nil until hello succeeds
	ident  *identity // nil until hello succeeds
	rate   *cmdRate  // nil until hello succeeds

	input  atomic.Pointer[protocol.Input] // latest command state (latest wins)
	ackSeq atomic.Uint32                  // seq of the input last applied

	// seatVehicle/seat are this body's occupancy (0 = on foot). Guarded by
	// srv.mu: written by board/disembark/freeSeat, read by the tick loop
	// and encodeSnapshot, all under the same lock.
	seatVehicle uint32
	seat        uint16

	// Phase 10 party state, guarded by srv.mu like the seat fields.
	// pendingInvite is the entity id of the latest inviter (newest wins),
	// 0 when none.
	party         *party
	pendingInvite uint32

	// cmdTicks records which input seq executed on which tick, for the last
	// rewind_max of ticks. A `fire` names the seq that was in effect when the
	// trigger was pulled (PROTOCOL.md "fire"), and this turns that name into
	// the server tick the shot was aimed on — the instant lag compensation
	// has to reconstruct. Written and read under the tick loop's own lock.
	cmdTicks [sim.HistoryTicks]cmdTick

	rttMu      sync.Mutex
	rttEWMA    time.Duration
	pingSentAt time.Time

	out  chan msg
	done chan struct{}
	once sync.Once
}

// cmdTick is one slot of the seq-to-tick ring: the input seq that executed
// on `tick`. Slot zero of a fresh ring reads as tick 0, which no live tick
// ever is (s.tickNo pre-increments), so an unwritten slot never matches.
type cmdTick struct {
	tick uint32
	seq  uint16
}

// recordCmdTick notes that this tick executed the currently-held input.
// Called from step(), i.e. once per client per tick, under the tick lock.
func (c *client) recordCmdTick(tick uint32) {
	seq := uint16(c.ackSeq.Load())
	c.cmdTicks[int(tick)%sim.HistoryTicks] = cmdTick{tick: tick, seq: seq}
}

// commandTick returns the EARLIEST tick within rewind_max that executed
// `seq`, and whether it was found.
//
// Earliest, not latest: a client sending inputs slower than the tick rate
// has one seq re-applied over several ticks, and the shot was aimed on the
// first of them — the frame that first showed the client the world it
// decided to shoot at. Taking the latest would quietly shorten the rewind
// by however long the client's input gap happened to be.
func (c *client) commandTick(seq uint16) (uint32, bool) {
	var best uint32
	found := false
	for _, s := range c.cmdTicks {
		if s.tick == 0 || s.seq != seq {
			continue
		}
		if !found || s.tick < best {
			best, found = s.tick, true
		}
	}
	return best, found
}

// step advances this client's entity by one tick with its latest input,
// against the shared static colliders (docs/PROTOCOL.md "colliders") so the
// server's own movement resolves against the exact geometry it ships to
// clients.
func (c *client) step(t *terrain.Field, colliders []protocol.Collider) {
	var in sim.Input
	if w := c.input.Load(); w != nil {
		in = sim.Input{
			MoveX:      float64(w.MoveX),
			MoveY:      float64(w.MoveY),
			Look:       sim.Vec{float64(w.LookDir[0]), float64(w.LookDir[1]), float64(w.LookDir[2])},
			ActionMask: w.ActionMask,
			Colliders:  colliders,
		}
	}
	c.entity.PrevLook = sim.Step(&c.entity.State, in, c.entity.PrevLook, t, sim.DT)
}

// lookDir is the last raw look direction this connection sent (normalized),
// falling back to the entity's held facing before any input has arrived.
// Used for the fire-adjacent interaction re-check (cmdWorld.Look) and for
// the snapshot's pitch_q.
func (c *client) lookDir() sim.Vec {
	look := c.entity.State.Facing
	if w := c.input.Load(); w != nil {
		l := sim.Vec{float64(w.LookDir[0]), float64(w.LookDir[1]), float64(w.LookDir[2])}
		if l.Len() > 1e-9 {
			look = terrain.Normalize(l)
		}
	}
	return look
}

// flags computes the snapshot entity row's flags byte (PROTOCOL.md "flags
// bits") from server-truth state: grounded/sprinting from movement, dead
// from health, firing when a shot resolved on this exact tick.
func (c *client) flags(tick uint32) uint8 {
	var f uint8
	if c.entity.State.Grounded {
		f |= protocol.FlagGrounded
	}
	if w := c.input.Load(); w != nil && w.ActionMask&protocol.ActionSprint != 0 {
		f |= protocol.FlagSprinting
	}
	if c.entity.Health <= 0 {
		f |= protocol.FlagDead
	}
	if c.entity.FiringTick == tick {
		f |= protocol.FlagFiring
	}
	return f
}

// pitchQ computes the snapshot entity row's pitch_q (PROTOCOL.md "pitch_q"):
// the look direction's angle off the local tangent plane, quantised to
// [-127, 127]. Visual only — hit resolution never reads it.
func (c *client) pitchQ() int8 {
	up := terrain.Normalize(c.entity.State.Pos)
	s := up.Dot(c.lookDir())
	if s > 1 {
		s = 1
	} else if s < -1 {
		s = -1
	}
	pitch := math.Asin(s)
	q := int(math.Round(pitch / (math.Pi / 2) * 127))
	if q > 127 {
		q = 127
	} else if q < -127 {
		q = -127
	}
	return int8(q)
}

// onPong is the WebSocket-level pong handler: it closes out this
// connection's own RTT measurement (see rttPingInterval) and folds it into
// an EWMA. Called on the connection's read goroutine per gorilla/websocket.
func (c *client) onPong(string) error {
	c.rttMu.Lock()
	defer c.rttMu.Unlock()
	if c.pingSentAt.IsZero() {
		return nil
	}
	sample := time.Since(c.pingSentAt)
	c.pingSentAt = time.Time{}
	if c.rttEWMA == 0 {
		c.rttEWMA = sample
	} else {
		c.rttEWMA += (sample - c.rttEWMA) / rttSmoothing
	}
	return nil
}

// rewindTicks is this connection's smoothed RTT/2, expressed in ticks and
// clamped to [0, sim.HistoryTicks] (docs/PROTOCOL.md "fire": "Rewind is
// bounded by the server's measurement, not the client's claim").
// rewindTicks is how far back from `now` a shot naming input `seq` must be
// resolved, so it hits what the shooter's screen was showing.
//
// Walk one shot along a single timeline. The client fires at t, displaying
// remote entities at t - interp_delay, because every client renders that far
// behind the simulation clock (GDD "Lag compensation"). The shot reaches the
// server one one-way trip later, at t + L, which is `now`. So the instant to
// reconstruct is interp_delay + L behind `now`:
//
//	rewind_ticks = staleness(seq) + L + interp_ticks
//
// `staleness` is normally zero — a client sends the input and the shot
// together, so the named seq is the one just executed. It is non-zero when
// the client pulled the trigger against an OLDER input than the server has
// since run, and then it is exactly the extra distance into the past that
// the shooter's screen was showing. That is what `seq` buys over RTT alone
// (PROTOCOL.md "fire": seq, "with the server's own RTT measurement, tells
// the server how far to rewind" — both terms, not either one).
//
// The rule this replaces was L alone, with neither the render offset nor
// the staleness. It landed on the target's PRESENT position rather than the
// past the client fired at: 0/8 hits on a moving NPC where aiming at the
// live position scored 8/8 (docs/QA-STATUS.md, C14).
//
// A client cannot buy rewind by naming an old seq. commandTick only matches
// ticks the SERVER chose to run that seq on, and the total is clamped to
// rewind_max — so the most a lie achieves is the clamp, which is the bound
// PROTOCOL.md promises ("bounded by the server's measurement, not the
// client's claim").
func (c *client) rewindTicks(now uint32, seq uint16) int {
	staleness := 0
	if at, ok := c.commandTick(seq); ok && at <= now {
		staleness = int(now - at)
	}
	return clampRewind(staleness + c.oneWayTicks() + sim.InterpTicks)
}

// oneWayTicks is L: half the smoothed round trip, in whole ticks. Half,
// because what is being undone is the single trip the shot made from the
// client to here.
func (c *client) oneWayTicks() int {
	c.rttMu.Lock()
	rtt := c.rttEWMA
	c.rttMu.Unlock()
	sec := rtt.Seconds() / 2
	if sec < 0 {
		sec = 0
	}
	return int(math.Round(sec * sim.TickHz))
}

// clampRewind holds a rewind inside [0, rewind_max]. rewind_max is the
// history ring's length, so a value past it names a tick the ring has
// already overwritten.
func clampRewind(ticks int) int {
	if ticks < 0 {
		return 0
	}
	if ticks > sim.HistoryTicks {
		return sim.HistoryTicks
	}
	return ticks
}

// reader is the connection's inbound loop: it dispatches protocol messages
// and enforces the 10 s silence rule via the read deadline.
func (c *client) reader() {
	defer c.teardown()
	for {
		_, data, err := c.conn.ReadMessage()
		if err != nil {
			return
		}
		c.conn.SetReadDeadline(time.Now().Add(silentTimeout))
		typ, payload, err := protocol.DecodeFrame(data)
		if err != nil {
			c.closeCode(websocket.CloseProtocolError)
			return
		}
		switch typ {
		case protocol.MsgHello:
			h, err := protocol.DecodeHello(payload)
			if err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			c.srv.join(c, h)
		case protocol.MsgInput:
			if c.entity == nil {
				c.closeCode(websocket.CloseProtocolError) // input before hello
				return
			}
			in, err := protocol.DecodeInput(payload)
			if err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			c.input.Store(&in)
			c.ackSeq.Store(uint32(in.Seq))
		case protocol.MsgFire:
			if c.entity == nil {
				c.closeCode(websocket.CloseProtocolError) // fire before hello
				return
			}
			f, err := protocol.ParseFire(payload)
			if err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			c.srv.fire(c, f)
		case protocol.MsgCmd:
			if c.entity == nil {
				c.closeCode(websocket.CloseProtocolError) // cmd before hello
				return
			}
			req, err := protocol.ParseCmd(payload)
			if err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			res := c.srv.doCmd(c, req)
			c.send(msg{data: protocol.EncodeCmdResult(res)})
		case protocol.MsgBoard:
			if c.entity == nil {
				c.closeCode(websocket.CloseProtocolError) // board before hello
				return
			}
			b, err := protocol.DecodeBoard(payload)
			if err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			c.srv.board(c, b)
		case protocol.MsgDisembark:
			if c.entity == nil {
				c.closeCode(websocket.CloseProtocolError) // disembark before hello
				return
			}
			if err := protocol.DecodeDisembark(payload); err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			c.srv.disembark(c)
		case protocol.MsgPing:
			p, err := protocol.DecodePing(payload)
			if err != nil {
				c.closeCode(websocket.CloseProtocolError)
				return
			}
			c.send(msg{data: protocol.EncodePong(protocol.Pong{TSMs: p.TSMs})})
		default:
			c.closeCode(websocket.CloseProtocolError) // wrong direction or unknown
			return
		}
	}
}

// writer drains the outbound queue in order, and periodically sends a
// WebSocket-level ping control frame to measure this connection's RTT (see
// onPong).
func (c *client) writer() {
	pinger := time.NewTicker(rttPingInterval)
	defer pinger.Stop()
	for {
		select {
		case m := <-c.out:
			c.conn.SetWriteDeadline(time.Now().Add(writeTimeout))
			err := c.conn.WriteMessage(websocket.BinaryMessage, m.data)
			if m.pooled {
				c.srv.snapPool.Put(m.data)
			}
			if err != nil {
				c.teardown()
				return
			}
		case <-pinger.C:
			c.rttMu.Lock()
			c.pingSentAt = time.Now()
			c.rttMu.Unlock()
			c.conn.SetWriteDeadline(time.Now().Add(writeTimeout))
			_ = c.conn.WriteControl(websocket.PingMessage, nil, time.Now().Add(writeTimeout))
		case <-c.done:
			// Drain and recycle any queued pooled buffers, then exit.
			for {
				select {
				case m := <-c.out:
					if m.pooled {
						c.srv.snapPool.Put(m.data)
					}
				default:
					return
				}
			}
		}
	}
}

// sendSnapshot copies the shared snapshot body into a pooled buffer,
// patches this client's ack_seq (payload offset 4 = frame offset 6), and
// enqueues it. A full queue drops this tick's snapshot: snapshots are full
// state, so the next tick replaces it.
func (c *client) sendSnapshot(body []byte, ack uint16) {
	b := c.srv.snapPool.Get().([]byte)
	if cap(b) < len(body) {
		b = make([]byte, 0, len(body))
	}
	b = b[:len(body)]
	copy(b, body)
	b[6] = byte(ack)
	b[7] = byte(ack >> 8)
	select {
	case c.out <- msg{data: b, pooled: true}:
	default:
		c.srv.snapPool.Put(b)
	}
}

// send enqueues a frame. It blocks only while the writer is alive and the
// queue is full (the writer makes progress or dies, and its teardown
// releases the send); a dying client never holds a goroutine.
func (c *client) send(m msg) {
	select {
	case c.out <- m:
	case <-c.done:
	}
}

// closeCode closes the WebSocket with an application close code.
func (c *client) closeCode(code int) {
	_ = c.conn.WriteControl(websocket.CloseMessage,
		websocket.FormatCloseMessage(code, ""), time.Now().Add(time.Second))
}

// teardown is the idempotent connection death: stop the writer, remove the
// entity from the world (broadcasting despawn, closing the identity), and
// close the socket.
func (c *client) teardown() {
	c.once.Do(func() {
		close(c.done)
		c.srv.leave(c)
		c.conn.Close()
	})
}

// fail tears the connection down after a protocol violation.
func (c *client) fail() {
	c.closeCode(websocket.CloseProtocolError)
	c.teardown()
}
