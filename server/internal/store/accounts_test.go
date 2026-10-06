package store

import (
	"context"
	"errors"
	"os"
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

func TestOwnAndDeleteCascade(t *testing.T) {
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

	must(s.SetPlayerAccount(ctx, guest.Token, "a"))
	players, err := s.AccountPlayers(ctx, "a")
	if err != nil || len(players) != 1 || players[0].Token != guest.Token || players[0].Credits != 750 {
		t.Fatalf("AccountPlayers = %+v, %v", players, err)
	}

	// PutPlayer (the game's save path) must not strip ownership.
	guest.Credits = 900
	must(s.PutPlayer(ctx, guest))
	if players, _ := s.AccountPlayers(ctx, "a"); len(players) != 1 || players[0].Token != guest.Token {
		t.Fatal("a game save orphaned the account's player")
	}

	must(s.CreateSession(ctx, "sess", "a", 1, 1<<60))
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

// eachEngine runs f on a migrated SQLite store and, under `make test-pg`,
// on Postgres too: the character index is engine-specific SQL in a portable
// file, so it is proven on both.
func eachEngine(t *testing.T, f func(t *testing.T, s *Store)) {
	t.Run("sqlite", func(t *testing.T) { f(t, openMigrated(t)) })
	t.Run("postgres", func(t *testing.T) {
		dsn := os.Getenv("TEST_DATABASE_URL")
		if dsn == "" {
			t.Skip("TEST_DATABASE_URL not set; run `make test-pg`")
		}
		s, err := Open(dsn)
		if err != nil {
			t.Fatalf("Open: %v", err)
		}
		drop := func() {
			for _, tbl := range []string{"player", "account", "web_session", "link_code", "schema_version"} {
				s.DB.Exec(`DROP TABLE IF EXISTS ` + tbl)
			}
		}
		drop()
		t.Cleanup(func() { drop(); s.Close() })
		if err := s.Migrate(context.Background()); err != nil {
			t.Fatalf("Migrate: %v", err)
		}
		f(t, s)
	})
}

func character(token, name, accountID string) *Player {
	p := sample()
	p.Token, p.Name, p.AccountID = token, name, accountID
	return p
}

func TestCharacterNamesUniqueAmongAccounts(t *testing.T) {
	eachEngine(t, func(t *testing.T, s *Store) {
		ctx := context.Background()
		if err := s.PutPlayer(ctx, character("t1", "Kade", "a")); err != nil {
			t.Fatal(err)
		}
		// A case variant on another account is refused, at the insert.
		if err := s.PutPlayer(ctx, character("t2", "kADE", "b")); !errors.Is(err, ErrNameTaken) {
			t.Fatalf("case-variant character: err = %v, want ErrNameTaken", err)
		}
		if p, _ := s.GetPlayer(ctx, "t2"); p != nil {
			t.Fatal("refused character was stored")
		}

		// Guests sit outside the index: two of them share the name, and
		// share it with the character.
		for _, tok := range []string{"g1", "g2"} {
			if err := s.PutPlayer(ctx, character(tok, "KADE", "")); err != nil {
				t.Fatalf("guest %s: %v", tok, err)
			}
		}
		// ...until one is claimed by an account.
		if err := s.SetPlayerAccount(ctx, "g1", "b"); !errors.Is(err, ErrNameTaken) {
			t.Fatalf("claiming a clashing guest: err = %v, want ErrNameTaken", err)
		}
		// Renaming a character onto a taken name is refused too.
		if err := s.PutPlayer(ctx, character("t3", "Ash", "b")); err != nil {
			t.Fatal(err)
		}
		if err := s.PutPlayer(ctx, character("t3", "kade", "b")); !errors.Is(err, ErrNameTaken) {
			t.Fatalf("rename onto a taken name: err = %v, want ErrNameTaken", err)
		}

		for name, want := range map[string]bool{"kade": true, "KaDe": true, "ash": true, "Nobody": false} {
			if got, err := s.CharacterNameTaken(ctx, name); err != nil || got != want {
				t.Errorf("CharacterNameTaken(%q) = %v, %v; want %v", name, got, err, want)
			}
		}
		// A guest-only name is free.
		if err := s.PutPlayer(ctx, character("g3", "Wren", "")); err != nil {
			t.Fatal(err)
		}
		if taken, _ := s.CharacterNameTaken(ctx, "wren"); taken {
			t.Error("a guest's name counts as a taken character name")
		}
	})
}

func TestCountAndListAccountPlayers(t *testing.T) {
	eachEngine(t, func(t *testing.T, s *Store) {
		ctx := context.Background()
		if n, err := s.CountAccountPlayers(ctx, "a"); err != nil || n != 0 {
			t.Fatalf("empty account count = %d, %v", n, err)
		}
		first := character("t1", "Kade", "a")
		first.Body = "char.ubc.f"
		for _, p := range []*Player{first, character("t2", "Ash", "a"), character("t3", "Wren", "b"), character("g", "Guest", "")} {
			if err := s.PutPlayer(ctx, p); err != nil {
				t.Fatal(err)
			}
		}
		if n, _ := s.CountAccountPlayers(ctx, "a"); n != 2 {
			t.Fatalf("CountAccountPlayers(a) = %d, want 2", n)
		}
		players, err := s.AccountPlayers(ctx, "a")
		if err != nil || len(players) != 2 {
			t.Fatalf("AccountPlayers = %+v, %v", players, err)
		}
		bodies := map[string]string{}
		for _, p := range players {
			if p.AccountID != "a" {
				t.Errorf("%s AccountID = %q", p.Name, p.AccountID)
			}
			bodies[p.Name] = p.Body
		}
		if bodies["Kade"] != "char.ubc.f" || bodies["Ash"] != DefaultBody {
			t.Errorf("bodies = %v", bodies)
		}
	})
}
