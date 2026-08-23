// Lag compensation position history (GDD "Health and damage" -> "Lag
// compensation"). The server keeps rewind_max of position history per
// entity at tick granularity so a shot can be resolved against target
// positions rewound by the server's own smoothed RTT/2, clamped to
// [0, rewind_max]. See docs/tasks/phase2-wave2.md "W2-10".
package sim

// RewindMaxSeconds is rewind_max (GDD "Health and damage" -> "Lag
// compensation"): the longest a shot may be rewound.
const RewindMaxSeconds = 0.5

// HistoryTicks is the ring length: rewind_max expressed in ticks at the
// documented tick rate (sim.TickHz), not a bare constant.
const HistoryTicks = int(RewindMaxSeconds * TickHz) // 0.5s * 20Hz = 10 ticks

// sample is one recorded tick for one entity.
type sample struct {
	tick uint32
	pos  [3]float64
	up   [3]float64
	set  bool // true once this slot holds a real record for its entity
}

// entityRing is a fixed-size ring buffer of samples for a single entity.
type entityRing struct {
	buf [HistoryTicks]sample
}

// History is a ring buffer of per-entity position/up history, sized to
// rewind_max at the tick rate. It is used to resolve shots against where
// targets actually were (GDD "Health and damage" -> "Lag compensation").
//
// `up` is stored alongside pos because a hitbox capsule stands along the
// entity's OWN radial up, which differs per entity on a round world and
// cannot be re-derived from a rewound position alone without re-sampling
// the terrain. Storing it is what makes rewound hit tests correct on the
// far side of the planet.
type History struct {
	// entities grows only when a NEW entity id first appears; the
	// steady-state path (Record for a known id) allocates nothing.
	entities map[uint32]*entityRing
}

// NewHistory creates a History. ticks is accepted for API symmetry with the
// contract but the ring length is fixed at HistoryTicks (rewind_max at
// TickHz); ticks <= 0 or ticks == HistoryTicks both use that fixed size.
func NewHistory(ticks int) *History {
	return &History{entities: make(map[uint32]*entityRing)}
}

// Record stores pos/up for entity id at tick, overwriting whatever
// previously occupied that slot in the ring. Records age out of the window
// naturally as the ring wraps back around to their slot.
func (h *History) Record(tick uint32, id uint32, pos, up [3]float64) {
	r := h.entities[id]
	if r == nil {
		r = &entityRing{}
		h.entities[id] = r
	}
	slot := int(tick) % HistoryTicks
	r.buf[slot] = sample{tick: tick, pos: pos, up: up, set: true}
}

// At returns the recorded pos/up for id at the tick nearest to `tick`, and
// ok == false when that tick is outside the retained window (or id/tick was
// never recorded).
//
// Tick is a wrapping uint32 counter, so ages are computed with signed
// arithmetic (int32(tick-recordedTick)), never raw < or > comparisons,
// which would misbehave across the wraparound at 2^32-1.
func (h *History) At(tick uint32, id uint32) (pos, up [3]float64, ok bool) {
	r := h.entities[id]
	if r == nil {
		return pos, up, false
	}
	slot := int(tick) % HistoryTicks
	s := r.buf[slot]
	if !s.set {
		return pos, up, false
	}
	age := int32(tick - s.tick)
	if age < 0 || age >= int32(HistoryTicks) {
		return pos, up, false
	}
	return s.pos, s.up, true
}

// Forget drops all history for an entity. Call it on despawn.
//
// The ring itself is fixed-size, so an entity's samples age out logically —
// but its map entry never does. Every entity id that has ever existed would
// otherwise hold its ring forever, and ids are not reused: a reconnect mints a
// new one (docs/PROTOCOL.md, "Reconnect"). On a server that runs for days that
// is an unbounded map keyed by a monotonically increasing id, which is a leak
// rather than a bounded buffer.
//
// Forgetting an id that was never recorded is a no-op, so despawn paths do not
// need to know whether the entity was ever shot at.
func (h *History) Forget(id uint32) {
	delete(h.entities, id)
}
