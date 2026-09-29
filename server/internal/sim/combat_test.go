package sim

import (
	"math"
	"math/rand"
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
)

// testWeapon mirrors weapon.pulse (docs/GDD.md "Weapons" rule table).
func testWeapon() defs.Weapon {
	return defs.Weapon{
		Damage:       25,
		MaxRange:     120,
		FalloffStart: 40,
		FalloffEnd:   120,
		FalloffMin:   0.35,
	}
}

func targetDef() defs.EntityDef {
	d := defs.EntityDef{MaxHealth: 100, Damageable: true}
	d.Hitbox.Radius = 0.5
	d.Hitbox.Height = 1.8
	return d
}

func nonDamageableDef() defs.EntityDef {
	d := defs.EntityDef{MaxHealth: 100, Damageable: false}
	d.Hitbox.Radius = 0.5
	d.Hitbox.Height = 1.8
	return d
}

// defOf keys entity defs by Ent.Def, the same shape as the real
// defs.Registry.Entities lookup.
func defOf(defsByKey map[string]defs.EntityDef) func(*Ent) defs.EntityDef {
	return func(e *Ent) defs.EntityDef { return defsByKey[e.Def] }
}

// addTarget adds a damageable target entity to w and records its history at
// tick, standing straight up (up = +Y) at (x, 0, z).
func addTarget(w *World, h *History, id uint32, tick uint32, x, z float64) {
	w.Add(&Ent{ID: id, Kind: EntityKind(protocol.EntityTypeTarget), Health: 100, Def: "target"})
	h.Record(tick, id, [3]float64{x, 0, z}, [3]float64{0, 1, 0})
}

// newShooter builds a shooter entity plus its history record at (0,0,0),
// up = +Y, at tick.
func newShooter(w *World, h *History, id uint32, tick uint32) {
	w.Add(&Ent{ID: id, Kind: EntityKind(protocol.EntityTypePlayer), Health: 100, Def: "player"})
	h.Record(tick, id, [3]float64{0, 0, 0}, [3]float64{0, 1, 0})
}

func TestResolveShot(t *testing.T) {
	const shooterID = uint32(1)
	const tick = uint32(100)
	defsByKey := map[string]defs.EntityDef{
		"target":        targetDef(),
		"nondamageable": nonDamageableDef(),
	}
	wp := testWeapon()

	shotDown := func() Shot {
		return Shot{Shooter: shooterID, Dir: [3]float64{0, 0, 1}, Tick: tick, RewindTicks: 0}
	}

	t.Run("hit at 30m deals 25", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 30)

		_, hit, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if !ok {
			t.Fatalf("expected a hit")
		}
		if hit.Victim != 2 {
			t.Fatalf("expected victim 2, got %d", hit.Victim)
		}
		if hit.Damage != 25 {
			t.Fatalf("expected 25 damage at 30m (within falloff_start), got %d", hit.Damage)
		}
		if hit.HealthAfter != 75 {
			t.Fatalf("expected health 75, got %d", hit.HealthAfter)
		}
	})

	t.Run("1m lateral offset misses", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 1, 30) // 1m off the ray's x=0 axis, radius 0.5

		_, _, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if ok {
			t.Fatalf("expected a miss for a 1m lateral offset against a 0.5m radius capsule")
		}
	})

	t.Run("target beyond max_range is not hit", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 130) // beyond MaxRange 120

		_, _, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if ok {
			t.Fatalf("expected no hit beyond max_range")
		}
	})

	t.Run("falloff at 80m", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 80)

		_, hit, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if !ok {
			t.Fatalf("expected a hit")
		}
		// falloff = 1.0 + (80-40)/(120-40) * (0.35-1.0) = 0.675
		// damage = round(25 * 0.675) = round(16.875) = 17
		wantFalloff := 0.675
		if got := falloffAt(80, wp); math.Abs(got-wantFalloff) > 1e-9 {
			t.Fatalf("falloffAt(80) = %v, want %v", got, wantFalloff)
		}
		if hit.Damage != 17 {
			t.Fatalf("expected 17 damage at 80m, got %d", hit.Damage)
		}
	})

	t.Run("dead entity is skipped", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 30)
		w.Ents[2].Flags |= protocol.FlagDead

		_, _, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if ok {
			t.Fatalf("expected no hit against a dead entity")
		}
	})

	t.Run("non-damageable entity is skipped", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		w.Add(&Ent{ID: 2, Kind: EntityKind(protocol.EntityTypeNPC), Health: 100, Def: "nondamageable"})
		h.Record(tick, 2, [3]float64{0, 0, 30}, [3]float64{0, 1, 0})

		_, _, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if ok {
			t.Fatalf("expected no hit against a non-damageable entity")
		}
	})

	t.Run("nearest of two stacked targets wins", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 50) // far
		addTarget(w, h, 3, tick, 0, 30) // near, added second: order must not matter

		_, hit, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if !ok {
			t.Fatalf("expected a hit")
		}
		if hit.Victim != 3 {
			t.Fatalf("expected the nearer target (id 3) to be hit, got %d", hit.Victim)
		}
	})
}

// TestResolveShotReturnsDeviatedRay: the returned Ray must be what the server
// actually fired, not what the client asked for.
//
// The `shot fired` event is built from this, and fire.ts draws its tracer from
// that event precisely because the server owns spread. If the ray echoed the
// client's aim, every tracer would follow a line the shot did not take and
// disagree with the hit markers — which reads as broken hit registration and
// sends you debugging the netcode instead of the renderer.
func TestResolveShotReturnsDeviatedRay(t *testing.T) {
	const shooterID = uint32(1)
	const tick = uint32(100)
	defsByKey := map[string]defs.EntityDef{"target": targetDef(), "player": targetDef()}
	wp := testWeapon()
	aim := [3]float64{0, 0, 1}

	shoot := func(cone float64) Ray {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 30)
		ray, _, _ := ResolveShot(w, h,
			Shot{Shooter: shooterID, Dir: aim, Tick: tick, ConeHalfAngle: cone},
			wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		return ray
	}

	// No spread: the ray is exactly the aim.
	if d := vecDist(shoot(0).Dir, aim); d > 1e-12 {
		t.Errorf("with no spread the ray deviated by %g, want the aim exactly", d)
	}

	// A wide cone must actually move it — if this equals the aim, spread is
	// being applied somewhere the broadcast cannot see.
	ray := shoot(0.15)
	if d := vecDist(ray.Dir, aim); d < 1e-9 {
		t.Error("with a 0.15 rad cone the ray equals the client's aim — spread is not reaching the broadcast")
	}
	if l := math.Sqrt(ray.Dir[0]*ray.Dir[0] + ray.Dir[1]*ray.Dir[1] + ray.Dir[2]*ray.Dir[2]); math.Abs(l-1) > 1e-9 {
		t.Errorf("deviated ray length %g, want 1", l)
	}
	// Origin is the shooter's rewound EYE: (0,0,0) + up*1.7.
	if math.Abs(ray.Origin[1]-eyeHeightMeters) > 1e-9 {
		t.Errorf("ray origin y = %g, want the eye height %g", ray.Origin[1], eyeHeightMeters)
	}
}

func vecDist(a, b [3]float64) float64 {
	dx, dy, dz := a[0]-b[0], a[1]-b[1], a[2]-b[2]
	return math.Sqrt(dx*dx + dy*dy + dz*dz)
}

// Phase 11 Marksmanship: DamageMult scales before rounding; 0 is the identity.
func TestResolveShotDamageMult(t *testing.T) {
	defsByKey := map[string]defs.EntityDef{"target": targetDef()}
	wp := testWeapon()
	for _, tc := range []struct {
		mult float64
		want int
	}{{0, 25}, {1, 25}, {1.196, 30}, {2, 50}} {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, 1, 100)
		addTarget(w, h, 2, 100, 0, 30)
		s := Shot{Shooter: 1, Dir: [3]float64{0, 0, 1}, Tick: 100, DamageMult: tc.mult}
		_, hit, ok := ResolveShot(w, h, s, wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if !ok || hit.Damage != tc.want {
			t.Fatalf("mult %v: ok=%v damage=%d, want %d", tc.mult, ok, hit.Damage, tc.want)
		}
	}
}
