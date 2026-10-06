package server

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	dto "github.com/prometheus/client_model/go"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// newStrictServer is newTestServer with st and guests wired BEFORE Run and
// the listener start: setting them after would race the join goroutines.
func newStrictServer(t *testing.T, st *store.Store, guests bool) string {
	t.Helper()
	_, url := newStrictWorld(t, st, guests)
	return url
}

func newStrictWorld(t *testing.T, st *store.Store, guests bool) (*Server, string) {
	t.Helper()
	world, err := New(terrain.Generate(1337), 1337)
	if err != nil {
		t.Fatal(err)
	}
	if st != nil {
		world.SetStore(st)
	}
	world.SetGuests(guests)
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

// putRows writes an account-owned row (PutPlayer sets account_id on
// insert) and a guest row, returning their tokens.
func putRows(t *testing.T, st *store.Store) (owned, guest string) {
	t.Helper()
	ctx := context.Background()
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-kade", Name: "Kade", AccountID: "acc1", Body: store.DefaultBody}); err != nil {
		t.Fatalf("PutPlayer owned: %v", err)
	}
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-guest", Name: "Drifter"}); err != nil {
		t.Fatalf("PutPlayer guest: %v", err)
	}
	return "tok-kade", "tok-guest"
}

// refusedCount reads joinRefused directly (testutil would add a module).
func refusedCount(t *testing.T) float64 {
	t.Helper()
	var m dto.Metric
	if err := joinRefused.Write(&m); err != nil {
		t.Fatal(err)
	}
	return m.GetCounter().GetValue()
}

func hello(t *testing.T, url, token, name string) *wsClient {
	t.Helper()
	ws := dialWS(t, url)
	ws.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Token: token, Name: name}))
	return ws
}

// wantRefused reads until the close and demands 1008 with nothing before it.
func wantRefused(t *testing.T, ws *wsClient) {
	t.Helper()
	ws.conn.SetReadDeadline(time.Now().Add(testTimeout))
	_, data, err := ws.conn.ReadMessage()
	if err == nil {
		typ, _, _ := protocol.DecodeFrame(data)
		t.Fatalf("got frame %04x, want a 1008 close before any spawn/hello_ack", typ)
	}
	var ce *websocket.CloseError
	if !errors.As(err, &ce) || ce.Code != websocket.ClosePolicyViolation {
		t.Fatalf("read err = %v, want close 1008", err)
	}
}

// wantClosed skips frames (a seated client gets snapshots) until the
// close, and demands it be 1008.
func wantClosed(t *testing.T, ws *wsClient) {
	t.Helper()
	ws.conn.SetReadDeadline(time.Now().Add(testTimeout))
	for {
		if _, _, err := ws.conn.ReadMessage(); err != nil {
			var ce *websocket.CloseError
			if !errors.As(err, &ce) || ce.Code != websocket.ClosePolicyViolation {
				t.Fatalf("read err = %v, want close 1008", err)
			}
			return
		}
	}
}

// wantSeated reads hello_ack, then the self spawn, and returns its name.
func wantSeated(t *testing.T, ws *wsClient) string {
	t.Helper()
	ack, err := protocol.DecodeHelloAck(ws.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatalf("hello_ack: %v", err)
	}
	for i := 0; i < 500; i++ {
		sp, err := protocol.DecodeSpawn(ws.nextOf(t, protocol.MsgSpawn))
		if err != nil {
			t.Fatalf("spawn: %v", err)
		}
		if sp.EntityID == ack.EntityID {
			return string(sp.Data)
		}
	}
	t.Fatal("no self spawn")
	return ""
}

func TestStrictJoin_AccountsOnly(t *testing.T) {
	st := openTestStore(t)
	owned, guest := putRows(t, st)
	url := newStrictServer(t, st, false)
	before := refusedCount(t)

	wantRefused(t, hello(t, url, "made-up-token", "Mallory"))
	wantRefused(t, hello(t, url, guest, "Drifter"))
	wantRefused(t, hello(t, url, "", "NoToken"))
	if got := refusedCount(t) - before; got != 3 {
		t.Errorf("join_refused grew by %v, want 3", got)
	}

	if name := wantSeated(t, hello(t, url, owned, "ignored-name")); name != "Kade" {
		t.Errorf("character seated as %q, want the row's name Kade", name)
	}
}

func TestStrictJoin_GuestsAllowed(t *testing.T) {
	st := openTestStore(t)
	owned, guest := putRows(t, st)
	url := newStrictServer(t, st, true)

	if name := wantSeated(t, hello(t, url, owned, "ignored-name")); name != "Kade" {
		t.Errorf("character seated as %q, want Kade", name)
	}
	// A guest row keeps today's behaviour: the hello name, not the row's.
	if name := wantSeated(t, hello(t, url, guest, "Wanderer")); name != "Wanderer" {
		t.Errorf("guest seated as %q, want its hello name", name)
	}
	if name := wantSeated(t, hello(t, url, "made-up-token", "Newbie")); name != "Newbie" {
		t.Errorf("new guest seated as %q, want its hello name", name)
	}
}

func TestStrictJoin_NoStoreSeatsAnyone(t *testing.T) {
	url := newStrictServer(t, nil, false)
	if name := wantSeated(t, hello(t, url, "made-up-token", "Bare")); name != "Bare" {
		t.Errorf("storeless join seated as %q, want Bare", name)
	}
}
