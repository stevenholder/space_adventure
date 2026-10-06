package server

import (
	"context"
	"testing"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
)

// seatedRows reads hello_ack and every spawn up to and including the self
// spawn: the joiner's id and each entity's spawn data by id.
func seatedRows(t *testing.T, ws *wsClient) (uint32, map[uint32]string) {
	t.Helper()
	ack, err := protocol.DecodeHelloAck(ws.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatalf("hello_ack: %v", err)
	}
	rows := map[uint32]string{}
	for i := 0; i < 500; i++ {
		sp, err := protocol.DecodeSpawn(ws.nextOf(t, protocol.MsgSpawn))
		if err != nil {
			t.Fatalf("spawn: %v", err)
		}
		rows[sp.EntityID] = string(sp.Data)
		if sp.EntityID == ack.EntityID {
			return ack.EntityID, rows
		}
	}
	t.Fatal("no self spawn")
	return 0, nil
}

// spawnOf reads ws until the spawn row for id arrives (a broadcast join).
func spawnOf(t *testing.T, ws *wsClient, id uint32) string {
	t.Helper()
	for i := 0; i < 500; i++ {
		sp, err := protocol.DecodeSpawn(ws.nextOf(t, protocol.MsgSpawn))
		if err != nil {
			t.Fatalf("spawn: %v", err)
		}
		if sp.EntityID == id {
			return string(sp.Data)
		}
	}
	t.Fatalf("no spawn for %d", id)
	return ""
}

// A player spawn row carries the body after a NUL when it is not the
// default (PROTOCOL "spawn"), at every encode: the self spawn, the rows a
// joiner gets for those already in, and the broadcast of a joiner. A
// char.player character and a guest stay exactly the name.
func TestSpawnData_CarriesBody(t *testing.T) {
	st := openTestStore(t)
	ctx := context.Background()
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-kade", Name: "Kade", AccountID: "acc1", Body: "char.ubc.f"}); err != nil {
		t.Fatalf("PutPlayer Kade: %v", err)
	}
	if err := st.PutPlayer(ctx, &store.Player{Token: "tok-ash", Name: "Ash", AccountID: "acc2", Body: store.DefaultBody}); err != nil {
		t.Fatalf("PutPlayer Ash: %v", err)
	}
	url := newStrictServer(t, st, true)

	const kadeData = "Kade\x00char.ubc.f"
	kade := hello(t, url, "tok-kade", "ignored")
	kadeID, rows := seatedRows(t, kade)
	if got := rows[kadeID]; got != kadeData {
		t.Fatalf("Kade self spawn data = %q, want %q", got, kadeData)
	}

	ash := hello(t, url, "tok-ash", "ignored")
	ashID, rows := seatedRows(t, ash)
	if got := rows[ashID]; got != "Ash" {
		t.Errorf("Ash self spawn data = %q, want exactly %q", got, "Ash")
	}
	if got := rows[kadeID]; got != kadeData {
		t.Errorf("Ash's row for Kade = %q, want %q", got, kadeData)
	}
	if got := spawnOf(t, kade, ashID); got != "Ash" {
		t.Errorf("Kade's broadcast row for Ash = %q, want exactly %q", got, "Ash")
	}

	guest := hello(t, url, "made-up-token", "Wanderer")
	guestID, rows := seatedRows(t, guest)
	if got := rows[guestID]; got != "Wanderer" {
		t.Errorf("guest self spawn data = %q, want exactly %q", got, "Wanderer")
	}
	if got := rows[kadeID]; got != kadeData {
		t.Errorf("guest's row for Kade = %q, want %q", got, kadeData)
	}
	if got := rows[ashID]; got != "Ash" {
		t.Errorf("guest's row for Ash = %q, want exactly %q", got, "Ash")
	}
	if got := spawnOf(t, kade, guestID); got != "Wanderer" {
		t.Errorf("Kade's broadcast row for the guest = %q, want exactly %q", got, "Wanderer")
	}
}
