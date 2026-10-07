package server

import (
	"context"
	"strings"
	"testing"
	"time"

	dto "github.com/prometheus/client_model/go"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
)

// chatLine is one decoded chat event.
type chatLine struct {
	id   uint32
	data string
}

// chatCount reads chatLines directly, like refusedCount.
func chatCount(t *testing.T) float64 {
	t.Helper()
	var m dto.Metric
	if err := chatLines.Write(&m); err != nil {
		t.Fatal(err)
	}
	return m.GetCounter().GetValue()
}

// collectChat appends a frame's chat event, if it is one.
func collectChat(t *testing.T, typ uint16, payload []byte, into *[]chatLine) {
	t.Helper()
	if typ != protocol.MsgEvent {
		return
	}
	ev, err := protocol.DecodeEvent(payload)
	if err != nil {
		t.Fatalf("event: %v", err)
	}
	if ev.EventID == protocol.EventChat {
		*into = append(*into, chatLine{ev.EntityID, string(ev.Data)})
	}
}

// chatSend sends one chat cmd and reads to its result, keeping any chat
// events seen on the way (the speaker's own copy precedes the result).
func chatSend(t *testing.T, ws *wsClient, seq uint16, body string, seen *[]chatLine) protocol.CmdResult {
	t.Helper()
	ws.sendFrame(t, protocol.EncodeCmd(protocol.Cmd{Seq: seq, Opcode: protocol.OpChat, Data: []byte(body)}))
	for i := 0; i < 500; i++ {
		typ, payload := ws.next(t)
		if typ == protocol.MsgCmdResult {
			r, err := protocol.ParseCmdResult(payload)
			if err != nil {
				t.Fatal(err)
			}
			if r.Seq == seq {
				return r
			}
			continue
		}
		collectChat(t, typ, payload, seen)
	}
	t.Fatalf("no cmd_result for seq %d", seq)
	return protocol.CmdResult{}
}

// drainChat reads every frame for d (snapshots at 20 Hz keep the reads
// moving, so no read ever times out) and keeps the chat events.
func drainChat(t *testing.T, ws *wsClient, d time.Duration, seen *[]chatLine) {
	t.Helper()
	end := time.Now().Add(d)
	for time.Now().Before(end) {
		typ, payload := ws.next(t)
		collectChat(t, typ, payload, seen)
	}
}

// joinedID reads the hello_ack and returns the entity id.
func joinedID(t *testing.T, ws *wsClient) uint32 {
	t.Helper()
	ack, err := protocol.DecodeHelloAck(ws.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	return ack.EntityID
}

// C174/C175: a chat line reaches every client (speaker included) as event
// 0x0011 name NUL text; empty, oversized and over-rate lines are refused
// with 3/2/4 and reach nobody; control characters are stripped; the
// counter counts accepted lines only; the general cmd bucket is untouched.
func TestChat_BroadcastAndBounds(t *testing.T) {
	st := openTestStore(t)
	ctx := context.Background()
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-ash", Name: "Ash", AccountID: "acc1"}); err != nil {
		t.Fatal(err)
	}
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-kade", Name: "Kade", AccountID: "acc2"}); err != nil {
		t.Fatal(err)
	}
	url := newStrictServer(t, st, true)
	before := chatCount(t)

	ash := hello(t, url, "tok-ash", "ignored")
	ashID := joinedID(t, ash)
	kade := hello(t, url, "tok-kade", "ignored")
	kadeID := joinedID(t, kade)
	guest := hello(t, url, "made-up-token", "Wanderer")
	guestID := joinedID(t, guest)
	all := []*wsClient{ash, kade, guest}

	// expect drains each socket and demands exactly want (in order).
	expect := func(step string, pre [][]chatLine, want []chatLine) {
		t.Helper()
		for i, ws := range all {
			got := append([]chatLine(nil), pre[i]...)
			drainChat(t, ws, 300*time.Millisecond, &got)
			if len(got) != len(want) {
				t.Fatalf("%s: socket %d got %d chat events %+v, want %+v", step, i, len(got), got, want)
			}
			for j := range want {
				if got[j] != want[j] {
					t.Fatalf("%s: socket %d event %d = %+v, want %+v", step, i, j, got[j], want[j])
				}
			}
		}
	}

	// One line: every socket, speaker included, gets exactly one.
	var mine []chatLine
	if r := chatSend(t, ash, 1, `{"text":"over here"}`, &mine); r.Status != protocol.StatusOK || string(r.Data) != "{}" {
		t.Fatalf("over here: status %d data %q", r.Status, r.Data)
	}
	expect("over here", [][]chatLine{mine, nil, nil}, []chatLine{{ashID, "Ash\x00over here"}})

	// Blank: refused empty, nothing sent.
	mine = nil
	r := chatSend(t, ash, 2, `{"text":"  "}`, &mine)
	if r.Status != protocol.StatusRefused || !strings.Contains(string(r.Data), `"reason":"empty"`) {
		t.Fatalf("blank: status %d data %q", r.Status, r.Data)
	}
	// 201 bytes: malformed, nothing sent.
	r = chatSend(t, ash, 3, `{"text":"`+strings.Repeat("x", 201)+`"}`, &mine)
	if r.Status != protocol.StatusMalformed {
		t.Fatalf("201 bytes: status %d, want 2", r.Status)
	}
	expect("refusals", [][]chatLine{mine, nil, nil}, nil)

	// A burst of four from a fresh bucket: three go, the fourth is 4.
	mine = nil
	for i := uint16(1); i <= 4; i++ {
		r := chatSend(t, kade, i, `{"text":"go"}`, &mine)
		want := protocol.StatusOK
		if i == 4 {
			want = protocol.StatusRateLimited
		}
		if r.Status != want {
			t.Fatalf("burst %d: status %d, want %d", i, r.Status, want)
		}
	}
	// The general cmd bucket is untouched by chat.
	if r := sendCmd(t, kade, 5, protocol.OpSkills, `{}`); r.Status != protocol.StatusOK {
		t.Fatalf("skills after chat burst: status %d", r.Status)
	}
	line := chatLine{kadeID, "Kade\x00go"}
	expect("burst", [][]chatLine{nil, mine, nil}, []chatLine{line, line, line})

	// Control characters never reach anyone.
	mine = nil
	if r := chatSend(t, guest, 1, `{"text":"a\u0007b"}`, &mine); r.Status != protocol.StatusOK {
		t.Fatalf("a<BEL>b: status %d", r.Status)
	}
	expect("controls", [][]chatLine{nil, nil, mine}, []chatLine{{guestID, "Wanderer\x00ab"}})

	if got := chatCount(t) - before; got != 5 {
		t.Fatalf("space_adventure_chat_total rose by %v, want 5", got)
	}
}
