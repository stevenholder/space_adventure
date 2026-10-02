package server

import (
	"testing"

	"space-adventure/server/internal/protocol"
)

// Two inputs landing inside one tick both run, one per tick, in order --
// latest-wins dropped the first, a step the client had already predicted.
// A burst is shed down to inputBacklog so it cannot add standing latency.
func TestInputQueueAppliesOnePerTick(t *testing.T) {
	c := &client{}
	pop := func() uint16 { c.popInput(); return uint16(c.ackSeq.Load()) }

	c.pushInput(protocol.Input{Seq: 1})
	c.pushInput(protocol.Input{Seq: 2})
	if a, b := pop(), pop(); a != 1 || b != 2 {
		t.Fatalf("applied %d then %d, want 1 then 2", a, b)
	}
	if got := pop(); got != 2 {
		t.Fatalf("empty queue: applied %d, want 2 held", got)
	}

	for s := uint16(3); s <= 9; s++ { // a stall's burst
		c.pushInput(protocol.Input{Seq: s})
	}
	if got := pop(); got != 3 {
		t.Fatalf("burst: applied %d first, want 3", got)
	}
	if a, b := pop(), pop(); a != 8 || b != 9 {
		t.Fatalf("after burst: applied %d, %d, want 8, 9 (backlog %d)", a, b, inputBacklog)
	}

	for s := uint16(10); s < 30; s++ { // flood between ticks
		c.pushInput(protocol.Input{Seq: s})
	}
	if n := len(c.inQ); n != inputQueueMax {
		t.Fatalf("queue holds %d, want cap %d", n, inputQueueMax)
	}
}
