package web

import (
	"encoding/json"
	"errors"
	"io/fs"
	"maps"
	"net/http"
	"os"
	"slices"
	"strings"
	"testing"
	"time"

	"space-adventure/server/internal/store"
)

// Hair is optional on create ("" is hair.none), must be in the table, and
// rides every character row: the list, the create reply and me (C163).
func TestCharacterHair(t *testing.T) {
	ts, _ := newTestSite(t)
	jar, _ := newJar()
	c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}}
	if r := c.post("/api/register", credsReq{"hair@x.com", "longenough"}, true); r.StatusCode != 200 {
		t.Fatalf("register = %d", r.StatusCode)
	}
	sid := gameLogin(t, c, "hair@x.com", "longenough")
	create := func(req map[string]string) (int, string, charRow) {
		r := c.bearerReq("POST", "/api/characters", sid, req)
		var row charRow
		if r.StatusCode == 200 {
			decode(t, r, &row)
			return 200, "", row
		}
		return r.StatusCode, body(t, r), row
	}

	if code, _, row := create(map[string]string{"name": "Buns", "body": "char.ubc.f", "hair": "hair.buns"}); code != 200 || row.Hair != "hair.buns" {
		t.Fatalf("create with hair.buns = %d %+v", code, row)
	}
	if code, msg, _ := create(map[string]string{"name": "Nope", "body": "char.ubc", "hair": "hair.nope"}); code != 400 || msg != "bad hair" {
		t.Fatalf("hair.nope = %d %q, want 400 bad hair", code, msg)
	}
	time.Sleep(2 * time.Millisecond) // the list orders by created_ms, token: two rows in one ms would tie
	if code, _, row := create(map[string]string{"name": "Bald", "body": "char.player"}); code != 200 || row.Hair != store.DefaultHair {
		t.Fatalf("create without hair = %d %+v", code, row)
	}

	rows := listChars(t, c, sid)
	if len(rows) != 2 || rows[0].Name != "Buns" || rows[0].Hair != "hair.buns" || rows[1].Hair != store.DefaultHair {
		t.Fatalf("rows = %+v", rows)
	}
	var me struct {
		Players []struct {
			Name string `json:"name"`
			Hair string `json:"hair"`
		} `json:"players"`
	}
	decode(t, c.bearerReq("GET", "/api/me", sid, nil), &me)
	if len(me.Players) != 2 || me.Players[0].Hair != "hair.buns" || me.Players[1].Hair != store.DefaultHair {
		t.Fatalf("me = %+v", me)
	}
}

// The hairs table is art/manifest.json's `hair.*` ids, exactly: the server
// does not read the manifest at runtime, so this is what keeps them from
// drifting. Skipped outside the repo layout.
func TestHairsMatchManifest(t *testing.T) {
	raw, err := os.ReadFile("../../../art/manifest.json")
	if errors.Is(err, fs.ErrNotExist) {
		t.Skip("art/manifest.json not found (not in the repo layout)")
	}
	if err != nil {
		t.Fatal(err)
	}
	var m struct {
		Assets []struct {
			ID string `json:"id"`
		} `json:"assets"`
	}
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatalf("manifest: %v", err)
	}
	// The base ids only: `hair.<style>@<body>` are the per-head fits of
	// one pick.
	var want []string
	for _, a := range m.Assets {
		if strings.HasPrefix(a.ID, "hair.") && !strings.Contains(a.ID, "@") {
			want = append(want, a.ID)
		}
	}
	slices.Sort(want)
	got := slices.Sorted(maps.Keys(hairs))
	if !slices.Equal(got, want) {
		t.Fatalf("hairs = %v, manifest hair.* = %v", got, want)
	}
}
