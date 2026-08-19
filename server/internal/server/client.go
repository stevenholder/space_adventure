package server

import (
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/terrain"
)

// entity is one body in the world. The connection's input moves this
// entity — a lookup, not an identity: in M1 it is the player's own body,
// and from M2 a player in a pilot seat drives the vehicle instead.
type entity struct {
	ID       uint32
	Name     string
	State    sim.State
	PrevLook sim.Vec
}

// msg is one outbound frame on the client's outbound queue.
type msg struct {
	data   []byte
	pooled bool // data came from the server's snapPool
}

// client is one WebSocket connection and the entity it drives.
type client struct {
	srv    *Server
	conn   *websocket.Conn
	id     uint32
	entity *entity // nil until hello succeeds

	input  atomic.Pointer[protocol.Input] // latest command state (latest wins)
	ackSeq atomic.Uint32                  // seq of the input last applied

	out  chan msg
	done chan struct{}
	once sync.Once
}

// step advances this client's entity by one tick with its latest input.
// Called by the tick loop under the world lock.
func (c *client) step(t *terrain.Field) {
	var in sim.Input
	if w := c.input.Load(); w != nil {
		in = sim.Input{
			MoveX:      float64(w.MoveX),
			MoveY:      float64(w.MoveY),
			Look:       sim.Vec{float64(w.LookDir[0]), float64(w.LookDir[1]), float64(w.LookDir[2])},
			ActionMask: w.ActionMask,
		}
	}
	c.entity.PrevLook = sim.Step(&c.entity.State, in, c.entity.PrevLook, t, sim.DT)
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

// writer drains the outbound queue in order.
func (c *client) writer() {
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
// entity from the world (broadcasting despawn), and close the socket.
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
