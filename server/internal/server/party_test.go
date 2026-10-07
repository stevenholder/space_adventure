package server

// Party lifecycle over the real wire (C72): invite, toast, accept, roster
// events, leave, dissolve-at-one, disconnect cleanup, and the 4 cap.

import (
	"encoding/json"
	"fmt"
	"testing"

	"space-adventure/server/internal/protocol"
)

type partyRoster struct {
	Members []struct {
		ID   uint32 `json:"id"`
		Name string `json:"name"`
	} `json:"members"`
}

// pClient wraps the raw harness client with an EVENT BUFFER: the server
// sends roster events BEFORE the cmd_result that provoked them, and the
// harness's nextOf DISCARDS frames while skimming for a type — so a naive
// "await result, then await event" eats the event. Every read funnels
// through next(), which stashes events instead of dropping them.
type pClient struct {
	ws     *wsClient
	id     uint32
	events []protocol.Event
}

func (c *pClient) cmd(t *testing.T, seq uint16, op uint16, body string) protocol.CmdResult {
	t.Helper()
	c.ws.sendFrame(t, protocol.EncodeCmd(protocol.Cmd{Seq: seq, Opcode: op, Data: []byte(body)}))
	for i := 0; i < 500; i++ {
		typ, payload := c.ws.next(t)
		switch typ {
		case protocol.MsgEvent:
			ev, err := protocol.DecodeEvent(payload)
			if err != nil {
				t.Fatalf("event: %v", err)
			}
			c.events = append(c.events, ev)
		case protocol.MsgCmdResult:
			r, err := protocol.ParseCmdResult(payload)
			if err != nil {
				t.Fatal(err)
			}
			if r.Seq == seq {
				return r
			}
		}
	}
	t.Fatalf("no cmd_result for seq %d", seq)
	return protocol.CmdResult{}
}

func (c *pClient) cmdOK(t *testing.T, seq uint16, op uint16, body string) protocol.CmdResult {
	t.Helper()
	r := c.cmd(t, seq, op, body)
	if r.Status != protocol.StatusOK {
		t.Fatalf("op %#04x: status %d (%s), want OK", op, r.Status, r.Data)
	}
	return r
}

// event returns the next buffered-or-read event with the wanted id.
func (c *pClient) event(t *testing.T, want uint16) protocol.Event {
	t.Helper()
	for i, ev := range c.events {
		if ev.EventID == want {
			c.events = append(c.events[:i], c.events[i+1:]...)
			return ev
		}
	}
	for i := 0; i < 500; i++ {
		typ, payload := c.ws.next(t)
		if typ != protocol.MsgEvent {
			continue
		}
		ev, err := protocol.DecodeEvent(payload)
		if err != nil {
			t.Fatalf("event: %v", err)
		}
		if ev.EventID == want {
			return ev
		}
		c.events = append(c.events, ev)
	}
	t.Fatalf("no event %#04x", want)
	return protocol.Event{}
}

// awaitPublished returns once the server has published this client, so
// another player's cmd can target it. hello_ack goes out BEFORE join adds
// the client to s.clients; join runs on this connection's reader, so the
// pong to a ping sent now cannot leave until join has finished. Under
// full-suite load an invite sent on hello_ack alone found no target
// (status 5, not found). Events read on the way are buffered.
func (c *pClient) awaitPublished(t *testing.T) {
	t.Helper()
	c.ws.sendFrame(t, protocol.EncodePing(protocol.Ping{}))
	for i := 0; i < 500; i++ {
		typ, payload := c.ws.next(t)
		switch typ {
		case protocol.MsgPong:
			return
		case protocol.MsgEvent:
			ev, err := protocol.DecodeEvent(payload)
			if err != nil {
				t.Fatalf("event: %v", err)
			}
			c.events = append(c.events, ev)
		}
	}
	t.Fatal("no pong")
}

func joinPlayer(t *testing.T, url, name string) *pClient {
	t.Helper()
	c := dialWS(t, url)
	c.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: name}))
	ha, err := protocol.DecodeHelloAck(c.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatalf("hello_ack: %v", err)
	}
	return &pClient{ws: c, id: ha.EntityID}
}

func roster(t *testing.T, ev protocol.Event) partyRoster {
	t.Helper()
	var r partyRoster
	if err := json.Unmarshal(ev.Data, &r); err != nil {
		t.Fatalf("roster: %v (%s)", err, ev.Data)
	}
	return r
}

func TestPartyLifecycle(t *testing.T) {
	_, url := newTestServer(t)
	a := joinPlayer(t, url, "A")
	b := joinPlayer(t, url, "B")
	c := joinPlayer(t, url, "C")

	// Self-invite and unknown targets refuse before any state moves.
	if r := a.cmd(t, 1, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, a.id)); r.Status != protocol.StatusRefused {
		t.Fatalf("self invite: status %d, want refused", r.Status)
	}
	if r := a.cmd(t, 2, protocol.OpPartyInvite, `{"target":999999}`); r.Status != protocol.StatusNotFound {
		t.Fatalf("unknown invite: status %d, want not found", r.Status)
	}
	if r := a.cmd(t, 3, protocol.OpPartyRespond, `{"accept":true}`); r.Status != protocol.StatusRefused {
		t.Fatalf("respond with no invite: status %d, want refused", r.Status)
	}

	// A invites B; B sees the toast naming A, accepts, both get the roster.
	b.awaitPublished(t)
	a.cmdOK(t, 4, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, b.id))
	inv := b.event(t, protocol.EventPartyInvited)
	if inv.EntityID != a.id {
		t.Fatalf("invite from %d, want %d", inv.EntityID, a.id)
	}
	b.cmdOK(t, 5, protocol.OpPartyRespond, `{"accept":true}`)
	if got := roster(t, a.event(t, protocol.EventPartyUpdate)); len(got.Members) != 2 {
		t.Fatalf("A roster = %d members, want 2", len(got.Members))
	}
	if got := roster(t, b.event(t, protocol.EventPartyUpdate)); len(got.Members) != 2 {
		t.Fatalf("B roster = %d members, want 2", len(got.Members))
	}

	// B invites C; roster reaches all three.
	c.awaitPublished(t)
	b.cmdOK(t, 6, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, c.id))
	c.event(t, protocol.EventPartyInvited)
	c.cmdOK(t, 7, protocol.OpPartyRespond, `{"accept":true}`)
	for _, m := range []*pClient{a, b, c} {
		got := roster(t, m.event(t, protocol.EventPartyUpdate))
		if m == a || m == b {
			// A and B saw the 2-roster first; drain to the 3-roster.
			if len(got.Members) == 2 {
				got = roster(t, m.event(t, protocol.EventPartyUpdate))
			}
		}
		if len(got.Members) != 3 {
			t.Fatalf("roster = %d members, want 3", len(got.Members))
		}
	}

	// A leaves: A gets an empty roster, B and C get 2.
	a.cmdOK(t, 8, protocol.OpPartyLeave, `{}`)
	if got := roster(t, a.event(t, protocol.EventPartyUpdate)); len(got.Members) != 0 {
		t.Fatalf("leaver roster = %d, want 0", len(got.Members))
	}
	if got := roster(t, b.event(t, protocol.EventPartyUpdate)); len(got.Members) != 2 {
		t.Fatalf("B roster after leave = %d, want 2", len(got.Members))
	}

	// B disconnects: the party is one member, which is no party — C gets
	// the empty roster.
	c.event(t, protocol.EventPartyUpdate) // drain C's 2-roster from A's leave
	b.ws.conn.Close()
	if got := roster(t, c.event(t, protocol.EventPartyUpdate)); len(got.Members) != 0 {
		t.Fatalf("C roster after dissolve = %d, want 0", len(got.Members))
	}
}

func TestPartyCap(t *testing.T) {
	_, url := newTestServer(t)
	members := make([]*pClient, 5)
	for i := range members {
		members[i] = joinPlayer(t, url, fmt.Sprintf("P%d", i))
	}
	// P0 invites P1..P3 — a full four.
	seq := uint16(10)
	for i := 1; i <= 3; i++ {
		members[i].awaitPublished(t)
		members[0].cmdOK(t, seq, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, members[i].id))
		seq++
		members[i].event(t, protocol.EventPartyInvited)
		members[i].cmdOK(t, seq, protocol.OpPartyRespond, `{"accept":true}`)
		seq++
	}
	// A fifth invite refuses at the door.
	members[4].awaitPublished(t)
	if r := members[0].cmd(t, seq, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, members[4].id)); r.Status != protocol.StatusRefused {
		t.Fatalf("fifth invite: status %d, want refused", r.Status)
	}
}
