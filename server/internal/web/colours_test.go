package web

import (
	"encoding/json"
	"errors"
	"io/fs"
	"maps"
	"net/http"
	"os"
	"slices"
	"testing"

	"space-adventure/server/internal/store"
)

// Skin and suit are optional on create ("" is the default), must be in
// the palette, ride every character row, and PATCH changes them (C179).
func TestCharacterColours(t *testing.T) {
	ts, _, h := newTestSiteH(t)
	var retagged []string
	h.Retag = func(token, name, hair, skin, suit string) { retagged = append(retagged, skin+"|"+suit) }
	jar, _ := newJar()
	c := &site{t: t, ts: ts, c: &http.Client{Jar: jar}}
	if r := c.post("/api/register", credsReq{"tone@x.com", "longenough"}, true); r.StatusCode != 200 {
		t.Fatalf("register = %d", r.StatusCode)
	}
	sid := gameLogin(t, c, "tone@x.com", "longenough")
	send := func(method, path string, req map[string]string) (int, string, charRow) {
		r := c.bearerReq(method, path, sid, req)
		var row charRow
		if r.StatusCode == 200 {
			decode(t, r, &row)
			return 200, "", row
		}
		return r.StatusCode, body(t, r), row
	}

	code, _, tan := send("POST", "/api/characters", map[string]string{"name": "Tan", "body": "char.ubc", "skin": "skin.04", "suit": "suit.rust"})
	if code != 200 || tan.Skin != "skin.04" || tan.Suit != "suit.rust" {
		t.Fatalf("create with skin.04/suit.rust = %d %+v", code, tan)
	}
	if code, _, row := send("POST", "/api/characters", map[string]string{"name": "Plain", "body": "char.player"}); code != 200 ||
		row.Skin != store.DefaultSkin || row.Suit != store.DefaultSuit {
		t.Fatalf("create without colours = %d %+v", code, row)
	}
	for _, tc := range []struct {
		method, path string
		req          map[string]string
		msg          string
	}{
		{"POST", "/api/characters", map[string]string{"name": "Nope", "body": "char.ubc", "skin": "skin.09"}, "bad skin"},
		{"POST", "/api/characters", map[string]string{"name": "Nope", "body": "char.ubc", "suit": "#ff00ff"}, "bad suit"},
		{"PATCH", "/api/characters/" + tan.Token, map[string]string{"skin": "suit.rust"}, "bad skin"},
		{"PATCH", "/api/characters/" + tan.Token, map[string]string{"suit": "suit.neon"}, "bad suit"},
	} {
		if code, msg, _ := send(tc.method, tc.path, tc.req); code != 400 || msg != tc.msg {
			t.Errorf("%s %v = %d %q, want 400 %q", tc.method, tc.req, code, msg, tc.msg)
		}
	}

	code, _, row := send("PATCH", "/api/characters/"+tan.Token, map[string]string{"skin": "skin.07", "suit": "suit.teal"})
	if code != 200 || row.Skin != "skin.07" || row.Suit != "suit.teal" || row.Hair != store.DefaultHair || row.Name != "Tan" {
		t.Fatalf("patch colours = %d %+v", code, row)
	}
	// Suit only leaves the skin alone.
	if code, _, row := send("PATCH", "/api/characters/"+tan.Token, map[string]string{"suit": "suit.bone"}); code != 200 || row.Skin != "skin.07" || row.Suit != "suit.bone" {
		t.Fatalf("patch suit only = %d %+v", code, row)
	}
	if !slices.Equal(retagged, []string{"skin.07|suit.teal", "skin.07|suit.bone"}) {
		t.Fatalf("retagged %v", retagged)
	}
	rows := listChars(t, c, sid)
	if len(rows) != 2 || rows[0].Skin != "skin.07" || rows[0].Suit != "suit.bone" || rows[1].Skin != store.DefaultSkin {
		t.Fatalf("rows = %+v", rows)
	}
}

// The skins and suits tables are art/manifest.json's `palettes` ids,
// exactly: the server does not read the manifest at runtime, so this is
// what keeps them from drifting. Skipped outside the repo layout.
func TestPalettesMatchManifest(t *testing.T) {
	raw, err := os.ReadFile("../../../art/manifest.json")
	if errors.Is(err, fs.ErrNotExist) {
		t.Skip("art/manifest.json not found (not in the repo layout)")
	}
	if err != nil {
		t.Fatal(err)
	}
	type entry struct {
		ID string `json:"id"`
	}
	var m struct {
		Palettes struct {
			Skin []entry `json:"skin"`
			Suit []entry `json:"suit"`
		} `json:"palettes"`
	}
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatalf("manifest: %v", err)
	}
	ids := func(es []entry) []string {
		var out []string
		for _, e := range es {
			out = append(out, e.ID)
		}
		slices.Sort(out)
		return out
	}
	for _, tc := range []struct {
		name  string
		table map[string]bool
		want  []string
		def   string
	}{
		{"skin", skins, ids(m.Palettes.Skin), store.DefaultSkin},
		{"suit", suits, ids(m.Palettes.Suit), store.DefaultSuit},
	} {
		if got := slices.Sorted(maps.Keys(tc.table)); !slices.Equal(got, tc.want) {
			t.Errorf("%ss = %v, manifest palettes.%s = %v", tc.name, got, tc.name, tc.want)
		}
		if !tc.table[tc.def] {
			t.Errorf("default %s %q not in the table", tc.name, tc.def)
		}
	}
}
