package store

import (
	"context"
	"errors"
	"testing"
)

func TestAccountLifecycle(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()

	a := &Account{ID: "acct1", Email: "p@example.com", PwHash: "argon2id$fake", CreatedMs: 1}
	if err := s.CreateAccount(ctx, a); err != nil {
		t.Fatal(err)
	}
	if err := s.CreateAccount(ctx, &Account{ID: "acct2", Email: "p@example.com", PwHash: "x", CreatedMs: 2}); !errors.Is(err, ErrEmailTaken) {
		t.Fatalf("duplicate email: err = %v, want ErrEmailTaken", err)
	}
	got, err := s.GetAccountByEmail(ctx, "p@example.com")
	if err != nil || got == nil || got.ID != "acct1" {
		t.Fatalf("GetAccountByEmail = %+v, %v", got, err)
	}
	if got, _ := s.GetAccountByEmail(ctx, "nobody@example.com"); got != nil {
		t.Fatal("absent email returned an account")
	}
}

func TestSessions(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()
	if err := s.CreateSession(ctx, "sess1", "acct1", 100, 200); err != nil {
		t.Fatal(err)
	}
	if id, _ := s.GetSession(ctx, "sess1", 150); id != "acct1" {
		t.Fatalf("live session = %q", id)
	}
	if id, _ := s.GetSession(ctx, "sess1", 250); id != "" {
		t.Fatal("expired session still valid")
	}
	// The expired row was reaped; a fresh one can be deleted explicitly.
	if err := s.CreateSession(ctx, "sess2", "acct1", 100, 1<<60); err != nil {
		t.Fatal(err)
	}
	if err := s.DeleteSession(ctx, "sess2"); err != nil {
		t.Fatal(err)
	}
	if id, _ := s.GetSession(ctx, "sess2", 150); id != "" {
		t.Fatal("deleted session still valid")
	}
}

func TestPasswordChangeDropsOtherSessions(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}
	must(s.CreateAccount(ctx, &Account{ID: "a", Email: "a@x", PwHash: "old", CreatedMs: 1}))
	must(s.CreateSession(ctx, "keep", "a", 1, 1<<60))
	must(s.CreateSession(ctx, "drop", "a", 1, 1<<60))
	must(s.SetPassword(ctx, "a", "new", "keep"))
	acc, _ := s.GetAccount(ctx, "a")
	if acc.PwHash != "new" {
		t.Fatal("hash not updated")
	}
	if id, _ := s.GetSession(ctx, "keep", 2); id != "a" {
		t.Fatal("the changing session was dropped")
	}
	if id, _ := s.GetSession(ctx, "drop", 2); id != "" {
		t.Fatal("other session survived a password change")
	}
}

func TestLinkCodes(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()
	if err := s.PutLinkCode(ctx, "AAAA1111", "a", 1, 100); err != nil {
		t.Fatal(err)
	}
	// Re-minting replaces: the old code dies.
	if err := s.PutLinkCode(ctx, "BBBB2222", "a", 2, 100); err != nil {
		t.Fatal(err)
	}
	if id, _ := s.RedeemLinkCode(ctx, "AAAA1111", 50); id != "" {
		t.Fatal("replaced code still redeemable")
	}
	if id, _ := s.RedeemLinkCode(ctx, "BBBB2222", 50); id != "a" {
		t.Fatal("live code refused")
	}
	// Single-use.
	if id, _ := s.RedeemLinkCode(ctx, "BBBB2222", 50); id != "" {
		t.Fatal("code redeemed twice")
	}
	// Expiry: consumed AND refused.
	if err := s.PutLinkCode(ctx, "CCCC3333", "a", 1, 100); err != nil {
		t.Fatal(err)
	}
	if id, _ := s.RedeemLinkCode(ctx, "CCCC3333", 200); id != "" {
		t.Fatal("expired code accepted")
	}
	if id, _ := s.RedeemLinkCode(ctx, "CCCC3333", 50); id != "" {
		t.Fatal("expired code survived its failed redeem")
	}
}

func TestAdoptAndDeleteCascade(t *testing.T) {
	s := openMigrated(t)
	ctx := context.Background()
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}
	must(s.CreateAccount(ctx, &Account{ID: "a", Email: "a@x", PwHash: "h", CreatedMs: 1}))
	guest := sample()
	must(s.PutPlayer(ctx, guest))

	// Adopt the guest; a second adopt (either direction) refuses.
	must(s.AdoptPlayer(ctx, "a", guest.Token))
	if err := s.AdoptPlayer(ctx, "a", guest.Token); err == nil {
		t.Fatal("account adopted a second player")
	}
	if tok, _ := s.AccountPlayerToken(ctx, "a"); tok != guest.Token {
		t.Fatalf("AccountPlayerToken = %q", tok)
	}
	players, err := s.AccountPlayers(ctx, "a")
	if err != nil || len(players) != 1 || players[0].Credits != 750 {
		t.Fatalf("AccountPlayers = %+v, %v", players, err)
	}

	// PutPlayer (the game's save path) must not strip ownership.
	guest.Credits = 900
	must(s.PutPlayer(ctx, guest))
	if tok, _ := s.AccountPlayerToken(ctx, "a"); tok != guest.Token {
		t.Fatal("a game save orphaned the account's player")
	}

	must(s.CreateSession(ctx, "sess", "a", 1, 1<<60))
	must(s.PutLinkCode(ctx, "DDDD4444", "a", 1, 1<<60))
	tokens, err := s.DeleteAccount(ctx, "a")
	must(err)
	if len(tokens) != 1 || tokens[0] != guest.Token {
		t.Fatalf("cascade tokens = %v", tokens)
	}
	if p, _ := s.GetPlayer(ctx, guest.Token); p != nil {
		t.Fatal("owned player survived account deletion")
	}
	if id, _ := s.GetSession(ctx, "sess", 2); id != "" {
		t.Fatal("session survived account deletion")
	}
	if acc, _ := s.GetAccount(ctx, "a"); acc != nil {
		t.Fatal("account survived its own deletion")
	}
	if n, _ := s.CountPlayers(ctx); n != 0 {
		t.Fatalf("CountPlayers = %d after cascade", n)
	}
}
