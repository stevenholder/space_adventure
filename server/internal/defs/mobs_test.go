package defs

import (
	"io/fs"
	"strings"
	"testing"
	"testing/fstest"

	"space-adventure/server/data"
)

// dataWith is the embedded server/data plus extra files, for load().
func dataWith(t *testing.T, extra map[string]string) fs.FS {
	t.Helper()
	m := fstest.MapFS{}
	err := fs.WalkDir(data.FS, ".", func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		b, err := fs.ReadFile(data.FS, path)
		m[path] = &fstest.MapFile{Data: b}
		return err
	})
	if err != nil {
		t.Fatal(err)
	}
	for path, body := range extra {
		m[path] = &fstest.MapFile{Data: []byte(body)}
	}
	return m
}

// An archetype without a size is a standing person: today's hitbox, body
// sphere and eye, so the shipped NPCs behave exactly as before.
func TestNPCSizeDefaults(t *testing.T) {
	var n NPC
	if n.Radius() != 0.35 || n.Height() != 1.8 || n.EyeHeight() != 1.7 {
		t.Errorf("defaults = %v/%v/%v, want 0.35/1.8/1.7", n.Radius(), n.Height(), n.EyeHeight())
	}
	n = NPC{RadiusM: 0.8, HeightM: 1.1, EyeHeightM: 0.9}
	if n.Radius() != 0.8 || n.Height() != 1.1 || n.EyeHeight() != 0.9 {
		t.Errorf("set = %v/%v/%v, want 0.8/1.1/0.9", n.Radius(), n.Height(), n.EyeHeight())
	}
	reg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	ent := reg.Entities["npc"]
	if ent.Hitbox.Radius != DefaultNPCRadius || ent.Hitbox.Height != DefaultNPCHeight {
		t.Errorf("items.json npc hitbox %v x %v drifted from the archetype defaults", ent.Hitbox.Radius, ent.Hitbox.Height)
	}
}

func TestMobsAppend(t *testing.T) {
	reg, err := load(dataWith(t, map[string]string{"mobs.json": `{"version":1,"npcs":[
		{"id":"mob.crawler","name":"Crawler","asset":"mob.crawler","radius":0.6,"height":0.9,"eye_height":0.5}]}`}))
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	m, ok := reg.NPCs["mob.crawler"]
	if !ok || m.Radius() != 0.6 || m.Height() != 0.9 || m.EyeHeight() != 0.5 {
		t.Fatalf("mob.crawler = %+v, %v", m, ok)
	}
	if _, ok := reg.NPCs["npc.quartermaster"]; !ok {
		t.Error("npcs.json archetypes lost when mobs.json is present")
	}
	if !strings.Contains(string(reg.Payload), `"radius":0.6,"height":0.9,"eye_height":0.5`) {
		t.Error("payload does not carry mob.crawler's size")
	}
}

func TestMobsDuplicateID(t *testing.T) {
	_, err := load(dataWith(t, map[string]string{"mobs.json": `{"version":1,"npcs":[{"id":"npc.quartermaster"}]}`}))
	if err == nil || !strings.Contains(err.Error(), "npc.quartermaster") {
		t.Fatalf("duplicate id across npcs.json and mobs.json: err = %v", err)
	}
}

func TestZoneUnknownNPC(t *testing.T) {
	_, err := load(dataWith(t, map[string]string{"zones/zz.json": `{"id":"zz",
		"entities":[{"type":"npc","def":"npc.nope","pos":[0,0,0]}]}`}))
	if err == nil || !strings.Contains(err.Error(), "npc.nope") {
		t.Fatalf("zone placing an unknown npc: err = %v", err)
	}
}
