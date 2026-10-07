package web

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"space-adventure/server/internal/store"
)

func TestPasswordHashRoundTrip(t *testing.T) {
	h, err := hashPassword("correct horse battery")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(h, "$argon2id$") {
		t.Fatalf("hash format: %q", h)
	}
	if !verifyPassword("correct horse battery", h) {
		t.Fatal("right password refused")
	}
	if verifyPassword("wrong", h) {
		t.Fatal("wrong password accepted")
	}
	if verifyPassword("anything", "garbage") {
		t.Fatal("garbage hash accepted")
	}
}

func newTestSite(t *testing.T) (*httptest.Server, *store.Store) {
	t.Helper()
	ts, s, _ := newTestSiteH(t)
	return ts, s
}

// newTestSiteH is newTestSite with the Handler, for a test that sets Kick
// or Retag before its first request.
func newTestSiteH(t *testing.T) (*httptest.Server, *store.Store, *Handler) {
	t.Helper()
	// TEST_DATABASE_URL (make test-pg's) runs this suite on Postgres too;
	// the tables are emptied per test since the database is shared.
	url := os.Getenv("TEST_DATABASE_URL")
	if url == "" {
		url = "sqlite://" + filepath.Join(t.TempDir(), "w.db")
	}
	s, err := store.Open(url)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	if err := s.Migrate(context.Background()); err != nil {
		t.Fatal(err)
	}
	for _, q := range []string{`DELETE FROM web_session`, `DELETE FROM player`, `DELETE FROM account`} {
		if _, err := s.DB.Exec(q); err != nil {
			t.Fatal(err)
		}
	}
	h := &Handler{
		Store: s,
		NewPlayer: func(token, name string) store.Player {
			return store.Player{Token: token, Name: name, Credits: 1000,
				Inventory: []store.Stack{{Item: "ammo.cell", Qty: 120}},
				Equipped:  map[string]string{}}
		},
		Online: func() int { return 3 },
	}
	mux := http.NewServeMux()
	h.Mount(mux)
	ts := httptest.NewServer(mux)
	t.Cleanup(ts.Close)
	return ts, s, h
}

// client with a cookie jar and the CSRF header.
type site struct {
	t  *testing.T
	ts *httptest.Server
	c  *http.Client
	ip string // CF-Connecting-IP, so two clients get two login buckets
}

func newJar() (http.CookieJar, error) { return cookiejar.New(nil) }

func (s *site) post(path string, body any, csrf bool) *http.Response {
	s.t.Helper()
	b, _ := json.Marshal(body)
	req, _ := http.NewRequest("POST", s.ts.URL+path, strings.NewReader(string(b)))
	req.Header.Set("Content-Type", "application/json")
	if csrf {
		req.Header.Set("X-Requested-With", "test")
	}
	if s.ip != "" {
		req.Header.Set("CF-Connecting-IP", s.ip)
	}
	resp, err := s.c.Do(req)
	if err != nil {
		s.t.Fatal(err)
	}
	return resp
}

// bearerReq is the game client's shape: no cookie jar, the session as a
// bearer.
func (s *site) bearerReq(method, path, sid string, body any) *http.Response {
	s.t.Helper()
	var rd io.Reader
	if body != nil {
		b, _ := json.Marshal(body)
		rd = strings.NewReader(string(b))
	}
	req, _ := http.NewRequest(method, s.ts.URL+path, rd)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-Requested-With", "test")
	if sid != "" {
		req.Header.Set("Authorization", "Bearer "+sid)
	}
	if s.ip != "" {
		req.Header.Set("CF-Connecting-IP", s.ip)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		s.t.Fatal(err)
	}
	return resp
}

func (s *site) get(path string) *http.Response {
	s.t.Helper()
	resp, err := s.c.Get(s.ts.URL + path)
	if err != nil {
		s.t.Fatal(err)
	}
	return resp
}

func decode(t *testing.T, r *http.Response, v any) {
	t.Helper()
	defer r.Body.Close()
	if err := json.NewDecoder(r.Body).Decode(v); err != nil {
		t.Fatal(err)
	}
}

func TestAccountFlow(t *testing.T) {
	ts, st := newTestSite(t)
	jar, _ := newJar()
	c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}}

	// CSRF: a mutating call without the header is refused.
	if r := c.post("/api/register", credsReq{"a@x.com", "longenough"}, false); r.StatusCode != http.StatusForbidden {
		t.Fatalf("no-CSRF register = %d", r.StatusCode)
	}

	// Register (auto-login), me.
	if r := c.post("/api/register", credsReq{"a@x.com", "longenough"}, true); r.StatusCode != 200 {
		t.Fatalf("register = %d", r.StatusCode)
	}
	var me struct {
		Email   string `json:"email"`
		Players []any  `json:"players"`
	}
	decode(t, c.get("/api/me"), &me)
	if me.Email != "a@x.com" || len(me.Players) != 0 {
		t.Fatalf("me = %+v", me)
	}

	// Duplicate email.
	c2s, _ := newJar()
	c2 := &site{t: t, ts: ts, c: &http.Client{Jar: c2s}}
	if r := c2.post("/api/register", credsReq{"a@x.com", "longenough"}, true); r.StatusCode != http.StatusConflict {
		t.Fatalf("dup register = %d", r.StatusCode)
	}

	// Wrong password refused; right password logs in.
	if r := c2.post("/api/login", credsReq{"a@x.com", "wrongwrong"}, true); r.StatusCode != http.StatusUnauthorized {
		t.Fatalf("wrong pw login = %d", r.StatusCode)
	}
	if r := c2.post("/api/login", credsReq{"a@x.com", "longenough"}, true); r.StatusCode != 200 {
		t.Fatalf("login = %d", r.StatusCode)
	}

	// The launcher's door: game-login mints nothing (the character select
	// makes the first); me sees the one the account then creates.
	sid := gameLogin(t, c, "a@x.com", "longenough")
	decode(t, c.get("/api/me"), &me)
	if len(me.Players) != 0 {
		t.Fatalf("players after game-login = %d, want 0", len(me.Players))
	}
	r := c.bearerReq("POST", "/api/characters", sid, map[string]string{"name": "Ayla", "body": "char.player"})
	if r.StatusCode != 200 {
		t.Fatalf("create = %d %s", r.StatusCode, body(t, r))
	}
	decode(t, c.get("/api/me"), &me)
	if len(me.Players) != 1 {
		t.Fatalf("players = %d", len(me.Players))
	}
	rows := listChars(t, c, sid)
	token := rows[0].Token

	// Password change from session c invalidates c2's session.
	if r := c.post("/api/password", map[string]string{"old": "longenough", "new": "evenlonger1"}, true); r.StatusCode != 200 {
		t.Fatalf("password = %d", r.StatusCode)
	}
	if r := c2.get("/api/me"); r.StatusCode != http.StatusUnauthorized {
		t.Fatalf("other session after pw change = %d", r.StatusCode)
	}

	// Delete cascades: player gone.
	if r := c.post("/api/delete", map[string]string{"password": "evenlonger1"}, true); r.StatusCode != 200 {
		t.Fatalf("delete = %d", r.StatusCode)
	}
	if p, _ := st.GetPlayer(context.Background(), token); p != nil {
		t.Fatal("owned player survived delete")
	}
	if r := c.get("/api/me"); r.StatusCode != http.StatusUnauthorized {
		t.Fatalf("me after delete = %d", r.StatusCode)
	}
}

func TestStatsPublicAndSiteServes(t *testing.T) {
	ts, _ := newTestSite(t)
	jar, _ := newJar()
	c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}}
	var stats struct {
		Players int `json:"players"`
		Online  int `json:"online"`
	}
	decode(t, c.get("/api/stats"), &stats)
	if stats.Online != 3 {
		t.Fatalf("online = %d", stats.Online)
	}
	r := c.get("/")
	if r.StatusCode != 200 {
		t.Fatalf("landing = %d", r.StatusCode)
	}
	// Anonymous /api/me leaks nothing.
	if r := c.get("/api/me"); r.StatusCode != http.StatusUnauthorized {
		t.Fatalf("anonymous me = %d", r.StatusCode)
	}
}

func TestLoginRateLimit(t *testing.T) {
	ts, _ := newTestSite(t)
	jar, _ := newJar()
	c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}}
	refused := 0
	for i := 0; i < 10; i++ {
		r := c.post("/api/login", credsReq{"x@x.com", "whateverpw"}, true)
		if r.StatusCode == http.StatusTooManyRequests {
			refused++
		}
	}
	if refused == 0 {
		t.Fatal("ten rapid logins never rate-limited")
	}
}

type charRow struct {
	Token    string `json:"token"`
	Name     string `json:"name"`
	Body     string `json:"body"`
	Hair     string `json:"hair"`
	Credits  int64  `json:"credits"`
	LastSeen int64  `json:"last_seen_ms"`
}

func gameLogin(t *testing.T, c *site, email, pw string) string {
	t.Helper()
	r := c.bearerReq("POST", "/api/game-login", "", credsReq{email, pw})
	if r.StatusCode != 200 {
		t.Fatalf("game-login = %d", r.StatusCode)
	}
	if len(r.Cookies()) != 0 {
		t.Fatal("game-login set a cookie")
	}
	var out struct {
		Session string `json:"session"`
		Name    string `json:"name"`
	}
	decode(t, r, &out)
	if out.Session == "" || out.Name != email {
		t.Fatalf("game-login = %+v", out)
	}
	return out.Session
}

func listChars(t *testing.T, c *site, sid string) []charRow {
	t.Helper()
	r := c.bearerReq("GET", "/api/characters", sid, nil)
	if r.StatusCode != 200 {
		t.Fatalf("characters = %d", r.StatusCode)
	}
	var rows []charRow
	decode(t, r, &rows)
	return rows
}

func body(t *testing.T, r *http.Response) string {
	t.Helper()
	defer r.Body.Close()
	b, _ := io.ReadAll(r.Body)
	return strings.TrimSpace(string(b))
}

func TestGameLoginAndCharacters(t *testing.T) {
	ts, st := newTestSite(t)
	jar, _ := newJar()
	c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}}
	if r := c.post("/api/register", credsReq{"pilot@x.com", "longenough"}, true); r.StatusCode != 200 {
		t.Fatalf("register = %d", r.StatusCode)
	}

	// Right password → a session me accepts as a bearer, with no player:
	// game-login mints nothing, the character select makes the first.
	sid := gameLogin(t, c, "pilot@x.com", "longenough")
	var me struct {
		Email   string `json:"email"`
		Players []struct {
			Name string `json:"name"`
			Body string `json:"body"`
		} `json:"players"`
	}
	r := c.bearerReq("GET", "/api/me", sid, nil)
	if r.StatusCode != 200 {
		t.Fatalf("bearer me = %d", r.StatusCode)
	}
	decode(t, r, &me)
	if me.Email != "pilot@x.com" || len(me.Players) != 0 {
		t.Fatalf("me = %+v", me)
	}
	// The list is an empty array, not null: the select reads it as "make
	// your first". Twice → still none.
	sid2 := gameLogin(t, c, "pilot@x.com", "longenough")
	if r := c.bearerReq("GET", "/api/characters", sid2, nil); r.StatusCode != 200 || body(t, r) != "[]" {
		t.Fatalf("second game-login: characters not an empty array")
	}

	// Wrong password and unknown email: 401, one body.
	r1 := c.bearerReq("POST", "/api/game-login", "", credsReq{"pilot@x.com", "wrongwrong"})
	r2 := c.bearerReq("POST", "/api/game-login", "", credsReq{"nobody@x.com", "longenough"})
	b1, b2 := body(t, r1), body(t, r2)
	if r1.StatusCode != 401 || r2.StatusCode != 401 || b1 != b2 || b1 != "wrong email or password" {
		t.Fatalf("wrong pw %d %q, unknown %d %q", r1.StatusCode, b1, r2.StatusCode, b2)
	}
	// Bearer wins over a cookie: a garbage bearer is 401 even with a
	// good cookie in the jar.
	req, _ := http.NewRequest("GET", ts.URL+"/api/me", nil)
	req.Header.Set("Authorization", "Bearer nope")
	if r, _ := c.c.Do(req); r.StatusCode != 401 {
		t.Fatalf("bad bearer + cookie = %d", r.StatusCode)
	}

	create := func(name, bodyID string) (*http.Response, charRow) {
		r := c.bearerReq("POST", "/api/characters", sid, map[string]string{"name": name, "body": bodyID})
		var row charRow
		if r.StatusCode == 200 {
			decode(t, r, &row)
		}
		return r, row
	}

	// The first character: one row, the default body, a 32-hex token; me
	// sees it.
	if r, _ := create("pilot", "char.player"); r.StatusCode != 200 {
		t.Fatalf("create pilot = %d %s", r.StatusCode, body(t, r))
	}
	rows := listChars(t, c, sid)
	hex32 := regexp.MustCompile(`^[0-9a-f]{32}$`)
	if len(rows) != 1 || rows[0].Name != "pilot" || rows[0].Body != store.DefaultBody || !hex32.MatchString(rows[0].Token) {
		t.Fatalf("rows = %+v", rows)
	}
	first := rows[0].Token
	r = c.bearerReq("GET", "/api/me", sid, nil)
	decode(t, r, &me)
	if len(me.Players) != 1 || me.Players[0].Name != "pilot" || me.Players[0].Body != store.DefaultBody {
		t.Fatalf("me after create = %+v", me)
	}
	// The list is oldest first by created_ms, then token: a second create in
	// the same millisecond would order by a random token.
	time.Sleep(2 * time.Millisecond)
	r, kade := create("Kade", "char.ubc.f")
	if r.StatusCode != 200 || kade.Name != "Kade" || kade.Body != "char.ubc.f" || kade.Credits != 1000 {
		t.Fatalf("create Kade = %d %+v", r.StatusCode, kade)
	}
	rows = listChars(t, c, sid)
	if len(rows) != 2 || rows[0].Token != first || rows[1].Token != kade.Token || first == kade.Token {
		t.Fatalf("rows after Kade = %+v", rows)
	}
	if p, _ := st.GetPlayer(context.Background(), kade.Token); p == nil || p.AccountID == "" {
		t.Fatalf("Kade's row = %+v", p)
	}

	for _, tc := range []struct {
		name, body string
		code       int
		msg        string
	}{
		{"kade", "char.player", 409, "name taken"},
		{"K", "char.player", 400, "bad name"},
		{"Kade!", "char.player", 400, "bad name"},
		{"", "char.player", 400, "bad name"},
		{"Two  Spaces", "char.player", 400, "bad name"},
		{"Seventeen Letters", "char.player", 400, "bad name"},
		{"Nova", "char.nope", 400, "bad body"},
	} {
		r, _ := create(tc.name, tc.body)
		if got := body(t, r); r.StatusCode != tc.code || got != tc.msg {
			t.Fatalf("create %q/%q = %d %q", tc.name, tc.body, r.StatusCode, got)
		}
	}
	// Up to five; the sixth is refused.
	for _, n := range []string{"O'Neil", "Jo-Ann", "Rex 2"} {
		if r, _ := create(n, "char.player.f"); r.StatusCode != 200 {
			t.Fatalf("create %q = %d %s", n, r.StatusCode, body(t, r))
		}
	}
	if r, _ := create("Sixth", "char.ubc"); r.StatusCode != 409 || body(t, r) != "character limit" {
		t.Fatalf("sixth = %d", r.StatusCode)
	}
	// Another account cannot take a name either, whatever its case.
	jar2, _ := newJar()
	c2 := &site{t: t, ts: ts, c: &http.Client{Jar: jar2}, ip: "10.0.0.2"}
	c2.post("/api/register", credsReq{"other@x.com", "longenough"}, true)
	sidB := gameLogin(t, c2, "other@x.com", "longenough")
	r = c2.bearerReq("POST", "/api/characters", sidB, map[string]string{"name": "KADE", "body": "char.ubc"})
	if r.StatusCode != 409 {
		t.Fatalf("cross-account KADE = %d", r.StatusCode)
	}
	// A guest's name never collides.
	guest := store.Player{Token: "guest-1", Name: "Wanderer", Equipped: map[string]string{}}
	if err := st.PutPlayer(context.Background(), &guest); err != nil {
		t.Fatal(err)
	}
	if r := c2.bearerReq("POST", "/api/characters", sidB,
		map[string]string{"name": "Wanderer", "body": "char.ubc"}); r.StatusCode != 200 {
		t.Fatalf("name a guest holds = %d", r.StatusCode)
	}

	// Logout with the bearer ends that session only.
	if r := c.bearerReq("POST", "/api/logout", sid2, nil); r.StatusCode != 200 {
		t.Fatalf("bearer logout = %d", r.StatusCode)
	}
	if r := c.bearerReq("GET", "/api/me", sid2, nil); r.StatusCode != 401 {
		t.Fatalf("me after logout = %d", r.StatusCode)
	}

	// A password change from the site kills the game's session (C57).
	if r := c.post("/api/password", map[string]string{"old": "longenough", "new": "evenlonger1"}, true); r.StatusCode != 200 {
		t.Fatalf("password = %d", r.StatusCode)
	}
	if r := c.bearerReq("GET", "/api/characters", sid, nil); r.StatusCode != 401 {
		t.Fatalf("bearer after pw change = %d", r.StatusCode)
	}

	// Delete takes every character with it.
	if r := c.post("/api/delete", map[string]string{"password": "evenlonger1"}, true); r.StatusCode != 200 {
		t.Fatalf("delete = %d", r.StatusCode)
	}
	for _, tok := range []string{first, kade.Token} {
		if p, _ := st.GetPlayer(context.Background(), tok); p != nil {
			t.Fatalf("character %s survived delete", tok)
		}
	}
}

// Two accounts both creating "sam": the first gets it, the second is
// refused by the name index — game-login no longer names anyone.
func TestCharacterNameClashAcrossAccounts(t *testing.T) {
	ts, _ := newTestSite(t)
	var codes []int
	for _, e := range []string{"sam@a.com", "sam@b.com"} {
		jar, _ := newJar()
		c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}, ip: e}
		c.post("/api/register", credsReq{e, "longenough"}, true)
		sid := gameLogin(t, c, e, "longenough")
		if rows := listChars(t, c, sid); len(rows) != 0 {
			t.Fatalf("%s: %d characters after game-login, want 0", e, len(rows))
		}
		r := c.bearerReq("POST", "/api/characters", sid, map[string]string{"name": "sam", "body": "char.player"})
		codes = append(codes, r.StatusCode)
		r.Body.Close()
	}
	if codes[0] != 200 || codes[1] != 409 {
		t.Fatalf("create sam = %v, want [200 409]", codes)
	}
}

func TestGameLoginRateLimit(t *testing.T) {
	ts, _ := newTestSite(t)
	c := &site{t: t, ts: ts, c: http.DefaultClient}
	var last int
	for i := 0; i < 10; i++ {
		last = c.bearerReq("POST", "/api/game-login", "", credsReq{"x@x.com", "whateverpw"}).StatusCode
	}
	if last != http.StatusTooManyRequests {
		t.Fatalf("tenth rapid game-login = %d", last)
	}
}

func TestOldRoutesGone(t *testing.T) {
	ts, _ := newTestSite(t)
	c := &site{t: t, ts: ts, c: http.DefaultClient}
	for _, p := range []string{"/api/link-code", "/api/redeem", "/api/import-token"} {
		if r := c.post(p, map[string]string{}, true); r.StatusCode != http.StatusNotFound {
			t.Fatalf("%s = %d", p, r.StatusCode)
		}
	}
}

// Phase 18: PATCH and DELETE /api/characters/<token> (C166–C168).
func TestEditCharacter(t *testing.T) {
	ctx := context.Background()
	ts, st, h := newTestSiteH(t)
	var kicked [][]string
	h.Kick = func(tokens []string) { kicked = append(kicked, slices.Clone(tokens)) }
	var retagged []string
	h.Retag = func(token, name, hair string) { retagged = append(retagged, token+"|"+name+"|"+hair) }

	login := func(email, ip string) (*site, string) {
		jar, _ := newJar()
		c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}, ip: ip}
		if r := c.post("/api/register", credsReq{email, "longenough"}, true); r.StatusCode != 200 {
			t.Fatalf("register %s = %d", email, r.StatusCode)
		}
		return c, gameLogin(t, c, email, "longenough")
	}
	c, sid := login("pilot@x.com", "10.0.0.1")
	c2, sidB := login("other@x.com", "10.0.0.2")
	create := func(c *site, sid, name string) charRow {
		r := c.bearerReq("POST", "/api/characters", sid, map[string]string{"name": name, "body": "char.ubc.f"})
		if r.StatusCode != 200 {
			t.Fatalf("create %s = %d %s", name, r.StatusCode, body(t, r))
		}
		var row charRow
		decode(t, r, &row)
		time.Sleep(2 * time.Millisecond) // list order is created_ms
		return row
	}
	kade := create(c, sid, "Kade")
	ash := create(c, sid, "Ash")
	nova := create(c2, sidB, "Nova")

	patch := func(sid, token string, req map[string]string) (int, string) {
		r := c.bearerReq("PATCH", "/api/characters/"+token, sid, req)
		return r.StatusCode, body(t, r)
	}

	// Rename + hair on an own token: the row comes back, the list shows it,
	// the body is untouched, and a live session is retagged.
	code, got := patch(sid, kade.Token, map[string]string{"name": "Kadence", "hair": "hair.buns"})
	var row charRow
	if code != 200 || json.Unmarshal([]byte(got), &row) != nil ||
		row.Token != kade.Token || row.Name != "Kadence" || row.Hair != "hair.buns" || row.Body != "char.ubc.f" {
		t.Fatalf("patch kade = %d %s", code, got)
	}
	if rows := listChars(t, c, sid); len(rows) != 2 || rows[0].Name != "Kadence" || rows[0].Hair != "hair.buns" {
		t.Fatalf("list after patch = %+v", rows)
	}
	if !slices.Equal(retagged, []string{kade.Token + "|Kadence|hair.buns"}) {
		t.Fatalf("retagged %v", retagged)
	}
	if p, _ := st.GetPlayer(ctx, kade.Token); p == nil || p.AccountID == "" || p.Credits != 1000 {
		t.Fatalf("row after patch = %+v", p)
	}
	// Hair only; then its own name in another case.
	if code, got := patch(sid, kade.Token, map[string]string{"hair": "hair.long"}); code != 200 || !strings.Contains(got, `"name":"Kadence"`) {
		t.Fatalf("hair only = %d %s", code, got)
	}
	if code, got := patch(sid, kade.Token, map[string]string{"name": "KADENCE"}); code != 200 || !strings.Contains(got, `"name":"KADENCE"`) {
		t.Fatalf("own name re-cased = %d %s", code, got)
	}

	for _, tc := range []struct {
		sid, token string
		req        map[string]string
		code       int
		msg        string
	}{
		{sid, kade.Token, map[string]string{"name": "nova"}, 409, "name taken"}, // another account's
		{sid, kade.Token, map[string]string{"name": "ash"}, 409, "name taken"},  // a sibling's
		{sid, kade.Token, map[string]string{"name": "K"}, 400, "bad name"},
		{sid, kade.Token, map[string]string{"name": "Kade!"}, 400, "bad name"},
		{sid, kade.Token, map[string]string{"hair": "hair.nope"}, 400, "bad hair"},
		{sid, nova.Token, map[string]string{"name": "Mine"}, 404, "no such character"},
		{sid, "0123456789abcdef0123456789abcdef", map[string]string{"name": "Mine"}, 404, "no such character"},
		{"", kade.Token, map[string]string{"name": "Mine"}, 401, "not logged in"},
	} {
		if code, got := patch(tc.sid, tc.token, tc.req); code != tc.code || got != tc.msg {
			t.Fatalf("patch %s %v = %d %q, want %d %q", tc.token, tc.req, code, got, tc.code, tc.msg)
		}
	}
	if p, _ := st.GetPlayer(ctx, nova.Token); p == nil || p.Name != "Nova" {
		t.Fatalf("Nova after foreign patch = %+v", p)
	}
	if p, _ := st.GetPlayer(ctx, kade.Token); p == nil || p.Name != "KADENCE" || p.Hair != "hair.long" {
		t.Fatalf("Kade after refused patches = %+v", p)
	}
	// No CSRF header: 403, like every mutating route.
	req, _ := http.NewRequest("PATCH", ts.URL+"/api/characters/"+kade.Token, strings.NewReader(`{"name":"Csrf"}`))
	req.Header.Set("Authorization", "Bearer "+sid)
	if r, err := http.DefaultClient.Do(req); err != nil || r.StatusCode != 403 {
		t.Fatalf("patch without X-Requested-With = %v %v", r, err)
	}
	if r := c.bearerReq("POST", "/api/characters/"+kade.Token, sid, map[string]string{}); r.StatusCode != 405 {
		t.Fatalf("POST on a token = %d", r.StatusCode)
	}

	del := func(c *site, sid, token string) (int, string) {
		r := c.bearerReq("DELETE", "/api/characters/"+token, sid, nil)
		return r.StatusCode, body(t, r)
	}
	// Another account's token: 404, the row stays, nobody kicked.
	if code, got := del(c, sid, nova.Token); code != 404 || got != "no such character" {
		t.Fatalf("delete foreign = %d %q", code, got)
	}
	if p, _ := st.GetPlayer(ctx, nova.Token); p == nil {
		t.Fatal("foreign delete removed Nova")
	}
	if len(kicked) != 0 {
		t.Fatalf("kicked %v before any delete", kicked)
	}
	// Own: 200, the list shrinks, the row is gone, exactly [token] kicked.
	if code, got := del(c, sid, kade.Token); code != 200 || got != `{"ok":true}` {
		t.Fatalf("delete kade = %d %q", code, got)
	}
	if rows := listChars(t, c, sid); len(rows) != 1 || rows[0].Token != ash.Token {
		t.Fatalf("list after delete = %+v", rows)
	}
	if p, _ := st.GetPlayer(ctx, kade.Token); p != nil {
		t.Fatalf("kade's row survived: %+v", p)
	}
	if len(kicked) != 1 || !slices.Equal(kicked[0], []string{kade.Token}) {
		t.Fatalf("kicked %v, want [[%s]]", kicked, kade.Token)
	}
	if code, _ := del(c, sid, kade.Token); code != 404 {
		t.Fatalf("delete again = %d", code)
	}
	if code, _ := patch(sid, kade.Token, map[string]string{"name": "Ghost"}); code != 404 {
		t.Fatalf("patch deleted = %d", code)
	}
	// The other account is untouched; deleting its own works for it.
	if rows := listChars(t, c2, sidB); len(rows) != 1 || rows[0].Name != "Nova" {
		t.Fatalf("other account's list = %+v", rows)
	}
	// The deleted name is free again.
	if r := c.bearerReq("POST", "/api/characters", sid, map[string]string{"name": "Kadence", "body": "char.ubc"}); r.StatusCode != 200 {
		t.Fatalf("re-create freed name = %d", r.StatusCode)
	}
}

// TestKDFGate: six logins from six addresses never run more than two argon2
// verifies at once, and all six succeed by waiting; a saturated gate 503s.
func TestKDFGate(t *testing.T) {
	ts, _ := newTestSite(t)
	var inFlight, peak atomic.Int32
	kdfHeld = func() {
		n := inFlight.Add(1)
		for p := peak.Load(); n > p && !peak.CompareAndSwap(p, n); p = peak.Load() {
		}
		time.Sleep(30 * time.Millisecond) // widen the window so overlap shows
		inFlight.Add(-1)
	}
	t.Cleanup(func() { kdfHeld = func() {} })

	var wg sync.WaitGroup
	codes := make([]int, 6)
	for i := range codes {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			c := &site{t: t, ts: ts, c: &http.Client{}, ip: fmt.Sprintf("10.0.0.%d", i+1)}
			codes[i] = c.post("/api/login", credsReq{"nobody@x.com", "whateverpw"}, true).StatusCode
		}(i)
	}
	wg.Wait()
	for i, code := range codes {
		if code != http.StatusUnauthorized { // unknown email: the dummy-hash path ran
			t.Fatalf("login %d = %d, want 401", i, code)
		}
	}
	if p := peak.Load(); p > 2 || p < 1 {
		t.Fatalf("peak concurrent verifies = %d, want <= 2", p)
	}

	// Saturated: both slots taken, a short wait, 503 + Retry-After.
	kdfSlots <- struct{}{}
	kdfSlots <- struct{}{}
	old := kdfWait
	kdfWait = 50 * time.Millisecond
	t.Cleanup(func() { kdfWait = old; <-kdfSlots; <-kdfSlots })
	c := &site{t: t, ts: ts, c: &http.Client{}, ip: "10.0.1.1"}
	r := c.post("/api/login", credsReq{"nobody@x.com", "whateverpw"}, true)
	if r.StatusCode != http.StatusServiceUnavailable || r.Header.Get("Retry-After") != "1" {
		t.Fatalf("saturated login = %d Retry-After=%q, want 503 / 1", r.StatusCode, r.Header.Get("Retry-After"))
	}
}
