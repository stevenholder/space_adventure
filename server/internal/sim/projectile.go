// NPC ranged-attack projectile (docs/GDD.md "Phase 3 — NPC combat at an
// encampment" -> NPC archetype table, `projectile_speed`). A projectile is
// just another Ent (Kind EntityTypeProjectile) whose Data is *ProjectileState
// and whose Pos/Vel already carry its position and direction of travel —
// this file only adds the per-tick behaviour: move, collide, expire.
package sim

import (
	"encoding/binary"
	"math"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/terrain"
)

// ProjectileState rides in Ent.Data.
type ProjectileState struct {
	Owner     uint32 // never damaged by its own shot
	Damage    int
	Speed     float64
	LifeTicks int // remaining
}

func init() {
	RegisterStep(EntityKind(protocol.EntityTypeProjectile), StepProjectile)
}

// LifeTicksForRange derives LifeTicks from a weapon's max_range and
// projectile_speed, in whole ticks (GDD "LifeTicks is derived from
// range/speed in TICKS, not accumulated seconds" — summing dt instead
// would land a hair short/long and need an epsilon to paper over it).
func LifeTicksForRange(rangeM, speed float64) int {
	if speed <= 0 {
		return 0
	}
	return int(math.Ceil(rangeM / speed * TickHz))
}

// StepProjectile advances one projectile by dt (docs/GDD.md, weapon
// `projectile_speed`):
//
//  1. Move Speed*dt along Vel's direction, in a straight line — no gravity.
//  2. Test the SWEPT SEGMENT from the previous to the new position against
//     every damageable, alive entity's capsule except Owner and itself; the
//     nearest hit along the segment wins. A point test at the new position
//     alone would miss a body the projectile passed through mid-tick.
//  3. Stop on the terrain (|newPos| below the sampled radius) or a static
//     collider, whichever the tick's endpoint lands past.
//  4. On a hit: apply damage, emit EventHit, remove itself.
//  5. Otherwise, if LifeTicks has just reached 0: remove itself silently.
//  6. Otherwise: commit the new position.
func StepProjectile(e *Ent, dt float64, ctx StepCtx) {
	state, ok := e.Data.(*ProjectileState)
	if !ok || state == nil {
		return
	}

	prev := Vec(e.Pos)
	dir := terrain.Normalize(Vec(e.Vel))
	segLen := state.Speed * dt
	next := prev.Add(dir.Scale(segLen))

	// Step 1 — the wall between here and there. Step 3 used to test only
	// the END point, and a gunner's round covers more than a wall's
	// thickness per tick, so shots tunnelled through the kit walls and hit
	// whoever stood behind them. The swept test gives the distance, so a
	// capsule beyond the wall is not a hit.
	wallT, wallHit := SegmentColliderHit([3]float64(prev), [3]float64(dir), segLen, ctx.Colliders)

	// Step 2 — swept segment vs. damageable capsules (except Owner/self).
	if ctx.World != nil && ctx.DefOf != nil && dir != (Vec{}) {
		bestT := math.Inf(1)
		if wallHit {
			bestT = wallT
		}
		var bestID uint32
		found := false
		for _, id := range ctx.World.order {
			if id == e.ID || id == state.Owner {
				continue
			}
			victim := ctx.World.Ents[id]
			if victim == nil || victim.Health <= 0 || victim.Flags&protocol.FlagDead != 0 {
				continue
			}
			def := ctx.DefOf(victim)
			if !def.Damageable {
				continue
			}
			feet := Vec(victim.Pos)
			up := terrain.Normalize(feet)
			head := feet.Add(up.Scale(def.Hitbox.Height))
			t, hit := rayCapsule(prev, dir, feet, head, def.Hitbox.Radius)
			if !hit || t < 0 || t > segLen || t >= bestT {
				continue
			}
			bestT, bestID, found = t, id, true
		}
		if found {
			victim := ctx.World.Ents[bestID]
			before := victim.Health
			victim.Health -= state.Damage
			if victim.Health < 0 {
				victim.Health = 0
			}
			if ctx.Events != nil {
				// PROTOCOL.md gives `hit` a 20-byte body. This emitted the
				// header alone, which reads as a truncated frame to anything
				// that parses the documented layout. It went unnoticed because
				// the branch needs a projectile to strike a damageable WORLD
				// entity, and until camp NPCs became damageable the only one
				// was a range dummy no gunner can reach.
				point := prev.Add(dir.Scale(bestT))
				data := make([]byte, 0, 20)
				data = binary.LittleEndian.AppendUint32(data, state.Owner)
				for i := 0; i < 3; i++ {
					data = binary.LittleEndian.AppendUint32(data, math.Float32bits(float32(point[i])))
				}
				data = binary.LittleEndian.AppendUint16(data, uint16(before-victim.Health))
				data = binary.LittleEndian.AppendUint16(data, uint16(victim.Health))
				*ctx.Events = append(*ctx.Events, protocol.Event{
					EntityID: bestID,
					EventID:  protocol.EventHit,
					Data:     data,
				})
			}
			ctx.World.Remove(e.ID)
			return
		}
	}
	if wallHit {
		if ctx.World != nil {
			ctx.World.Remove(e.ID)
		}
		return
	}

	// Step 3 — stop on the terrain or a static collider at the new position.
	if ctx.Terrain != nil {
		up := terrain.Normalize(next)
		if next.Len() < ctx.Terrain.SampleRadius(up) {
			if ctx.World != nil {
				ctx.World.Remove(e.ID)
			}
			return
		}
	}
	if len(ctx.Colliders) > 0 {
		up := terrain.Normalize(next)
		for i := range ctx.Colliders {
			if hit, _, _ := nearest(next, 0, ctx.Colliders[i], up); hit {
				if ctx.World != nil {
					ctx.World.Remove(e.ID)
				}
				return
			}
		}
	}

	// Step 5 — expiry.
	state.LifeTicks--
	if state.LifeTicks <= 0 {
		if ctx.World != nil {
			ctx.World.Remove(e.ID)
		}
		return
	}

	// Step 6 — commit.
	e.Pos = [3]float64(next)
	e.Vel = [3]float64(dir.Scale(state.Speed))
}
