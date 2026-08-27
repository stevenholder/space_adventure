package server

import (
	"testing"
	"time"

	"space-adventure/server/internal/sim"
)

// stepSeqs runs `seqs` through a client's per-tick record, one seq per tick,
// starting at tick 1, and returns the last tick used.
func stepSeqs(c *client, seqs []uint16) uint32 {
	var tick uint32
	for _, seq := range seqs {
		tick++
		c.ackSeq.Store(uint32(seq))
		c.recordCmdTick(tick)
	}
	return tick
}

// TestRewindIsOneWayPlusRenderOffset is C14's rule for the ordinary shot:
// the client sent the input and the trigger together, so the only distance
// to undo is the trip the shot made (L) plus the offset every client renders
// behind the simulation clock (GDD "Lag compensation").
func TestRewindIsOneWayPlusRenderOffset(t *testing.T) {
	c := &client{}
	now := stepSeqs(c, []uint16{1, 2, 3, 4, 5})
	c.rttEWMA = 100 * time.Millisecond // L = 50 ms = 1 tick

	if got, want := c.rewindTicks(now, 5), 1+sim.InterpTicks; got != want {
		t.Errorf("rewindTicks(newest seq) = %d, want %d (L + interp)", got, want)
	}
}

// TestRewindAddsStalenessForAnOlderSeq: the client pulled the trigger
// against an input the server has since moved past, so its screen was
// showing that much further into the past. Without the term, a shot fired
// during an input gap is resolved against a world the shooter never saw.
func TestRewindAddsStalenessForAnOlderSeq(t *testing.T) {
	c := &client{}
	now := stepSeqs(c, []uint16{1, 2, 3, 4, 5})
	c.rttEWMA = 100 * time.Millisecond // L = 1 tick

	// seq 3 ran on tick 3, now is tick 5: two ticks of staleness on top.
	if got, want := c.rewindTicks(now, 3), 2+1+sim.InterpTicks; got != want {
		t.Errorf("rewindTicks(seq 3) = %d, want %d (staleness + L + interp)", got, want)
	}
}

// TestRewindTakesTheEarliestTickForARepeatedSeq covers a client sending
// inputs slower than the tick rate: one seq is re-applied over several
// ticks, and the shot was aimed on the first of them. Taking the latest
// would silently shorten the rewind by the client's input gap.
func TestRewindTakesTheEarliestTickForARepeatedSeq(t *testing.T) {
	c := &client{}
	now := stepSeqs(c, []uint16{7, 7, 7, 8})
	c.rttEWMA = 100 * time.Millisecond // L = 1 tick

	if got, want := c.rewindTicks(now, 7), 3+1+sim.InterpTicks; got != want {
		t.Errorf("rewindTicks(repeated seq) = %d, want %d (earliest of the three ticks)", got, want)
	}
}

// TestRewindIsBoundedByRewindMax is PROTOCOL.md's "bounded by the server's
// measurement, not the client's claim". A client naming an ancient seq to
// shoot deep into the past gets the clamp, and the clamp is the ring.
func TestRewindIsBoundedByRewindMax(t *testing.T) {
	c := &client{}
	seqs := make([]uint16, sim.HistoryTicks*3)
	for i := range seqs {
		seqs[i] = uint16(i + 1)
	}
	now := stepSeqs(c, seqs)

	// Every slot the ring still holds, and every seq it has forgotten, must
	// land inside the window — nothing may name a tick the ring overwrote.
	for _, seq := range []uint16{1, 2, uint16(sim.HistoryTicks), uint16(now) - 1, uint16(now)} {
		if got := c.rewindTicks(now, seq); got < 0 || got > sim.HistoryTicks {
			t.Errorf("rewindTicks(seq %d) = %d, outside [0, %d]", seq, got, sim.HistoryTicks)
		}
	}
}

// TestRewindDropsStalenessForAnUnknownSeq: a seq the ring no longer holds
// is older than rewind_max, so history cannot honour it anyway. It degrades
// to the ordinary shot rather than to an unbounded one.
func TestRewindDropsStalenessForAnUnknownSeq(t *testing.T) {
	c := &client{}
	now := stepSeqs(c, []uint16{1, 2, 3})
	c.rttEWMA = 100 * time.Millisecond // L = 1 tick

	if got, want := c.rewindTicks(now, 9999), 1+sim.InterpTicks; got != want {
		t.Errorf("rewindTicks(unknown seq) = %d, want %d (L + interp)", got, want)
	}

	c.rttEWMA = 10 * time.Second // absurd RTT must not escape the ring
	if got := c.rewindTicks(now, 9999); got != sim.HistoryTicks {
		t.Errorf("rewindTicks with a 10 s RTT = %d, want the %d-tick clamp", got, sim.HistoryTicks)
	}
}
