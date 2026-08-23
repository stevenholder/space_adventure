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

		hit, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
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

		_, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if ok {
			t.Fatalf("expected a miss for a 1m lateral offset against a 0.5m radius capsule")
		}
	})

	t.Run("target beyond max_range is not hit", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 130) // beyond MaxRange 120

		_, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if ok {
			t.Fatalf("expected no hit beyond max_range")
		}
	})

	t.Run("falloff at 80m", func(t *testing.T) {
		w := NewWorld()
		h := NewHistory(0)
		newShooter(w, h, shooterID, tick)
		addTarget(w, h, 2, tick, 0, 80)

		hit, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
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

		_, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
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

		_, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
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

		hit, ok := ResolveShot(w, h, shotDown(), wp, defOf(defsByKey), rand.New(rand.NewSource(1)))
		if !ok {
			t.Fatalf("expected a hit")
		}
		if hit.Victim != 3 {
			t.Fatalf("expected the nearer target (id 3) to be hit, got %d", hit.Victim)
		}
	})
}
