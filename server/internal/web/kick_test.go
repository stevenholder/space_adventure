package web

import (
	"context"
	"net/http/httptest"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"space-adventure/server/internal/store"
)

// Deleting an account kicks its live characters, with exactly the tokens
// the cascade removed (Phase 16: an unkicked session's save re-creates
// the row).
func TestDeleteAccount_KicksCharacters(t *testing.T) {
	ctx := context.Background()
	st, err := store.Open("sqlite://" + filepath.Join(t.TempDir(), "k.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { st.Close() })
	if err := st.Migrate(ctx); err != nil {
		t.Fatal(err)
	}
	hash, _ := hashPassword("longenough")
	if err := st.CreateAccount(ctx, &store.Account{ID: "acc1", Email: "k@x.com", PwHash: hash}); err != nil {
		t.Fatal(err)
	}
	for _, p := range []store.Player{
		{Token: "tok-a", Name: "Kade", AccountID: "acc1"},
		{Token: "tok-b", Name: "Ash", AccountID: "acc1"},
		{Token: "tok-guest", Name: "Drifter"},
	} {
		if err := st.PutPlayer(ctx, &p); err != nil {
			t.Fatal(err)
		}
	}
	var kicked []string
	h := &Handler{Store: st, Kick: func(tokens []string) { kicked = append(kicked, tokens...) }}

	w := httptest.NewRecorder()
	h.deleteAccount(w, httptest.NewRequest("POST", "/api/delete", strings.NewReader(`{"password":"longenough"}`)), accountCtx{id: "acc1"})
	if w.Code != 200 {
		t.Fatalf("delete = %d %s", w.Code, w.Body)
	}
	slices.Sort(kicked)
	if !slices.Equal(kicked, []string{"tok-a", "tok-b"}) {
		t.Fatalf("kicked %v, want [tok-a tok-b]", kicked)
	}
}
