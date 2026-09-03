package web

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

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
	s, err := store.Open("sqlite://" + filepath.Join(t.TempDir(), "w.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	if err := s.Migrate(context.Background()); err != nil {
		t.Fatal(err)
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
	return ts, s
}

// client with a cookie jar and the CSRF header.
type site struct {
	t  *testing.T
	ts *httptest.Server
	c  *http.Client
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
	resp, err := s.c.Do(req)
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

	// Link code → redeem mints an owned player; redeem again (new code)
	// returns the SAME token.
	var mint struct {
		Code string `json:"code"`
	}
	decode(t, c.post("/api/link-code", nil, true), &mint)
	if len(mint.Code) != 8 {
		t.Fatalf("code = %q", mint.Code)
	}
	var red struct {
		Token string `json:"token"`
	}
	decode(t, c.post("/api/redeem", map[string]string{"code": mint.Code}, true), &red)
	if red.Token == "" {
		t.Fatal("no token from redeem")
	}
	// The code is single-use.
	if r := c.post("/api/redeem", map[string]string{"code": mint.Code}, true); r.StatusCode != http.StatusUnauthorized {
		t.Fatalf("re-redeem = %d", r.StatusCode)
	}
	decode(t, c.post("/api/link-code", nil, true), &mint)
	var red2 struct {
		Token string `json:"token"`
	}
	decode(t, c.post("/api/redeem", map[string]string{"code": mint.Code}, true), &red2)
	if red2.Token != red.Token {
		t.Fatal("second redeem minted a different player")
	}
	if p, _ := st.GetPlayer(context.Background(), red.Token); p == nil || p.Credits != 1000 {
		t.Fatalf("minted player = %+v", p)
	}

	// me now shows the pilot.
	decode(t, c.get("/api/me"), &me)
	if len(me.Players) != 1 {
		t.Fatalf("players = %d", len(me.Players))
	}

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
	if p, _ := st.GetPlayer(context.Background(), red.Token); p != nil {
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
