package defs

import (
	"encoding/json"
	"testing"
)

func TestLoad(t *testing.T) {
	reg, err := Load()
	if err != nil {
		t.Fatalf("Load() error: %v", err)
	}

	weapon, ok := reg.Items["weapon.pulse"]
	if !ok {
		t.Fatal("weapon.pulse not found in Items")
	}
	if weapon.Weapon == nil {
		t.Fatal("weapon.pulse has no weapon rule table")
	}
	if weapon.Weapon.Damage != 25 {
		t.Errorf("damage = %v, want 25", weapon.Weapon.Damage)
	}
	if weapon.Weapon.FireInterval != 0.15 {
		t.Errorf("fire_interval = %v, want 0.15", weapon.Weapon.FireInterval)
	}
	if weapon.Weapon.Magazine != 30 {
		t.Errorf("magazine = %v, want 30", weapon.Weapon.Magazine)
	}

	rangeZone, ok := reg.Zones["range"]
	if !ok {
		t.Fatal("range zone not found")
	}
	if len(rangeZone.Colliders) != 4 {
		t.Errorf("range colliders = %d, want 4", len(rangeZone.Colliders))
	}
	targetCount := 0
	for _, e := range rangeZone.Entities {
		if e.Type == "target" {
			targetCount++
		}
	}
	if targetCount != 5 {
		t.Errorf("range target placements = %d, want 5", targetCount)
	}

	if !json.Valid(reg.Payload) {
		t.Fatal("Payload is not valid JSON")
	}
	if len(reg.Payload) >= MaxPayload {
		t.Errorf("Payload is %d bytes, want < %d (64 KiB)", len(reg.Payload), MaxPayload)
	}

	var decoded map[string]json.RawMessage
	if err := json.Unmarshal(reg.Payload, &decoded); err != nil {
		t.Fatalf("Payload does not decode as a JSON object: %v", err)
	}
	for _, key := range []string{"items", "entities", "npcs", "constants"} {
		if _, ok := decoded[key]; !ok {
			t.Errorf("Payload missing key %q", key)
		}
	}
}
