package defs

import (
	"math"
	"testing"

	"space-adventure/server/internal/protocol"
)

// unitSphereRadius is a constant-radius terrain sampler (a unit sphere
// scaled to radius 150), used so the composed geometry is exactly
// checkable.
func unitSphereRadius(dir [3]float64) float64 {
	return 150
}

// quatApply rotates v by quaternion q (x, y, z, w).
func quatApply(q quat, v [3]float64) [3]float64 {
	u := [3]float64{q[0], q[1], q[2]}
	s := q[3]

	uv := vCross(u, v)
	uuv := vCross(u, uv)
	// v + 2*s*uv + 2*uuv
	return vAdd(v, vAdd(vScale(uv, 2*s), vScale(uuv, 2)))
}

func almostEqual(a, b [3]float64, tol float64) bool {
	d := [3]float64{a[0] - b[0], a[1] - b[1], a[2] - b[2]}
	return vLen(d) <= tol
}

func TestComposeZone_OriginColliderLandsAtOriginDirTimesRadius(t *testing.T) {
	z := Zone{
		ID:        "test-zone",
		OriginDir: [3]float64{0, 1, 0},
		Colliders: []ZoneCollider{
			{Kind: "box", Pos: [3]float64{0, 0, 0}, Half: [3]float64{1, 1, 1}},
		},
	}

	colliders, _, _, err := ComposeZone(z, unitSphereRadius)
	if err != nil {
		t.Fatalf("ComposeZone: %v", err)
	}
	if len(colliders) != 1 {
		t.Fatalf("len(colliders) = %d, want 1", len(colliders))
	}

	want := vScale(vNormalize(z.OriginDir), 150)
	got := [3]float64{
		float64(colliders[0].Center[0]),
		float64(colliders[0].Center[1]),
		float64(colliders[0].Center[2]),
	}
	if !almostEqual(got, want, 1e-6) {
		t.Fatalf("collider at local origin = %v, want %v", got, want)
	}
}

// TestComposeZone_NoLeaningWall is the assertion that catches reusing the
// zone origin's frame instead of re-deriving each object's own frame: an
// object offset 40m east must stand up along ITS OWN radial direction, not
// the zone origin's up.
func TestComposeZone_NoLeaningWall(t *testing.T) {
	originDir := [3]float64{0, 1, 0}
	z := Zone{
		ID:        "test-zone",
		OriginDir: originDir,
		Entities: []ZoneEntity{
			{Type: "target", Def: "wall", Pos: [3]float64{40, 0, 0}, Yaw: 0},
		},
	}

	_, placements, _, err := ComposeZone(z, unitSphereRadius)
	if err != nil {
		t.Fatalf("ComposeZone: %v", err)
	}
	if len(placements) != 1 {
		t.Fatalf("len(placements) = %d, want 1", len(placements))
	}

	p := placements[0]
	q := quat{p.Quat[0], p.Quat[1], p.Quat[2], p.Quat[3]}
	worldUp := quatApply(q, [3]float64{0, 1, 0})

	wantUp := vNormalize(p.Pos)
	if !almostEqual(worldUp, wantUp, 1e-9) {
		t.Fatalf("world_quat's up = %v, want normalize(world pos) = %v (leaning-wall bug: reused zone origin frame)", worldUp, wantUp)
	}

	// And explicitly NOT the zone origin's up — this is what would pass if
	// the composition (wrongly) reused north/east instead of north_p/east_p.
	zoneUp := vNormalize(originDir)
	if almostEqual(worldUp, zoneUp, 1e-9) {
		t.Fatalf("world_quat's up = %v equals zone origin's up %v; frame was not re-derived at the object's own position", worldUp, zoneUp)
	}
}

func TestComposeZone_YIsHeightAboveGround(t *testing.T) {
	z := Zone{
		ID:        "test-zone",
		OriginDir: [3]float64{0, 1, 0},
		Colliders: []ZoneCollider{
			{Kind: "sphere", Pos: [3]float64{0, 2, 0}, Half: [3]float64{0.5, 0, 0}},
		},
	}

	colliders, _, _, err := ComposeZone(z, unitSphereRadius)
	if err != nil {
		t.Fatalf("ComposeZone: %v", err)
	}

	got := [3]float64{
		float64(colliders[0].Center[0]),
		float64(colliders[0].Center[1]),
		float64(colliders[0].Center[2]),
	}
	gotRadius := vLen(got)
	if math.Abs(gotRadius-152) > 1e-6 {
		t.Fatalf("radius of collider at local y=2 = %v, want 152 (150 ground + 2 height)", gotRadius)
	}
}

func TestComposeZone_UnknownColliderKindErrors(t *testing.T) {
	z := Zone{
		ID:        "test-zone",
		OriginDir: [3]float64{0, 1, 0},
		Colliders: []ZoneCollider{
			{Kind: "capsule", Pos: [3]float64{0, 0, 0}},
		},
	}

	if _, _, _, err := ComposeZone(z, unitSphereRadius); err == nil {
		t.Fatalf("ComposeZone: want error for unknown collider kind, got nil")
	}
}

func TestComposeZone_ColliderKindMapping(t *testing.T) {
	z := Zone{
		ID:        "test-zone",
		OriginDir: [3]float64{0, 1, 0},
		Colliders: []ZoneCollider{
			{Kind: "box", Pos: [3]float64{0, 0, 0}},
			{Kind: "sphere", Pos: [3]float64{0, 0, 0}},
		},
	}

	colliders, _, _, err := ComposeZone(z, unitSphereRadius)
	if err != nil {
		t.Fatalf("ComposeZone: %v", err)
	}
	if colliders[0].Kind != protocol.ColliderBox {
		t.Fatalf("colliders[0].Kind = %d, want ColliderBox", colliders[0].Kind)
	}
	if colliders[1].Kind != protocol.ColliderSphere {
		t.Fatalf("colliders[1].Kind = %d, want ColliderSphere", colliders[1].Kind)
	}
}

// TestComposeZoneRejectsNonFinite: zone files are hand-authored, so this is
// the likelier source of a bad number than the wire. A non-finite value
// survives composition into the sim's push-out and ends up written to the
// player row, outliving the session. Failing the load makes it a startup
// error instead.
func TestComposeZoneRejectsNonFinite(t *testing.T) {
	nan := math.NaN()
	base := func() Zone {
		return Zone{
			ID: "t", OriginDir: [3]float64{0, 1, 0},
			Colliders: []ZoneCollider{{Kind: "box", Pos: [3]float64{1, 0, 1}, Half: [3]float64{1, 1, 1}}},
			Entities:  []ZoneEntity{{Type: "target", Def: "target", Pos: [3]float64{2, 0, 0}}},
		}
	}
	r := func([3]float64) float64 { return 150 }

	if _, _, _, err := ComposeZone(base(), r); err != nil {
		t.Fatalf("precondition: clean zone failed: %v", err)
	}

	cases := map[string]func(*Zone){
		"collider pos":  func(z *Zone) { z.Colliders[0].Pos[1] = nan },
		"collider half": func(z *Zone) { z.Colliders[0].Half[0] = nan },
		"collider yaw":  func(z *Zone) { z.Colliders[0].Yaw = nan },
		"entity pos":    func(z *Zone) { z.Entities[0].Pos[2] = nan },
		"entity yaw":    func(z *Zone) { z.Entities[0].Yaw = nan },
		"origin_dir":    func(z *Zone) { z.OriginDir[0] = nan },
	}
	for name, poison := range cases {
		t.Run(name, func(t *testing.T) {
			z := base()
			poison(&z)
			if _, _, _, err := ComposeZone(z, r); err == nil {
				t.Errorf("non-finite %s accepted, want an error", name)
			}
		})
	}
}

// A prop and a collider authored at the SAME local point must land at the same
// world point. That is the property the whole zone format rests on: an author
// reads the collider list to decide where a barrel goes, and the two sets of
// numbers are only comparable if they go through the same transform. Composing
// props separately -- or forgetting the terrain radius under them -- would put
// the dressing somewhere plausible-looking and subtly wrong, sunk into the
// ground or hovering over it, with nothing to compare against.
func TestComposeZone_PropSharesTheColliderFrame(t *testing.T) {
	at := [3]float64{7, 0, -4}
	z := Zone{
		ID:        "test-zone",
		OriginDir: [3]float64{0.3, 0.9, -0.2},
		Colliders: []ZoneCollider{
			{Kind: "box", Pos: at, Half: [3]float64{1, 1, 1}, Yaw: 33},
		},
		Props: []ZoneProp{
			{Asset: "prop.barrel", Pos: at, Yaw: 33},
		},
	}

	colliders, _, props, err := ComposeZone(z, unitSphereRadius)
	if err != nil {
		t.Fatalf("ComposeZone: %v", err)
	}
	if len(props) != 1 {
		t.Fatalf("len(props) = %d, want 1", len(props))
	}

	for i := 0; i < 3; i++ {
		if math.Abs(float64(props[0].Pos[i]-colliders[0].Center[i])) > 1e-5 {
			t.Fatalf("prop pos %v != collider centre %v", props[0].Pos, colliders[0].Center)
		}
	}
	for i := 0; i < 4; i++ {
		if math.Abs(float64(props[0].Quat[i]-colliders[0].Quat[i])) > 1e-5 {
			t.Fatalf("prop quat %v != collider quat %v", props[0].Quat, colliders[0].Quat)
		}
	}
	if props[0].Asset != "prop.barrel" {
		t.Fatalf("asset = %q", props[0].Asset)
	}
	// Omitted scale means 1, not 0 -- a prop scaled to nothing is invisible
	// and looks exactly like a prop that failed to load.
	if props[0].Scale != 1 {
		t.Fatalf("default scale = %v, want 1", props[0].Scale)
	}
}

// A zone file is hand-edited, so a prop with no asset id is a likely typo and
// has to fail the load rather than ship an unnameable model to every client.
func TestComposeZone_RejectsPropWithNoAsset(t *testing.T) {
	z := Zone{
		ID:        "test-zone",
		OriginDir: [3]float64{0, 1, 0},
		Props:     []ZoneProp{{Pos: [3]float64{1, 0, 1}}},
	}
	if _, _, _, err := ComposeZone(z, unitSphereRadius); err == nil {
		t.Fatal("ComposeZone accepted a prop with no asset id")
	}
}
