package server

// The bounty machine (C75/C76): the claim race is exactly-one, a party
// claim covers every member, abandon-by-all and expiry release and re-arm
// the post. Expiry is driven white-box (the clock is real time; the test
// moves the deadline, not the world).

import (
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/store"
)

func TestBountyRaceAndRelease(t *testing.T) {
	srv, url := newTestServer(t)
	a := joinPlayer(t, url, "A")
	b := joinPlayer(t, url, "B")

	// Both clients hear the offer (the join replay covers whoever arrived
	// after the broadcast).
	offerA := a.event(t, protocol.EventPriorityOffer)
	b.event(t, protocol.EventPriorityOffer)
	var offer struct {
		ID  string `json:"id"`
		Poi string `json:"poi"`
	}
	if err := json.Unmarshal(offerA.Data, &offer); err != nil {
		t.Fatal(err)
	}
	if offer.ID != "mission.bounty.warlord" || offer.Poi == "" {
		t.Fatalf("offer = %+v", offer)
	}

	// The race: A and B both accept. Exactly one claim.
	ra := a.cmd(t, 1, protocol.OpMissionAccept, fmt.Sprintf(`{"id":%q}`, offer.ID))
	rb := b.cmd(t, 1, protocol.OpMissionAccept, fmt.Sprintf(`{"id":%q}`, offer.ID))
	okCount := 0
	for _, r := range []protocol.CmdResult{ra, rb} {
		if r.Status == protocol.StatusOK {
			okCount++
		} else {
			var why struct {
				Reason string `json:"reason"`
			}
			json.Unmarshal(r.Data, &why)
			if why.Reason != "claimed" {
				t.Fatalf("loser refused with %q, want claimed", why.Reason)
			}
		}
	}
	if okCount != 1 {
		t.Fatalf("%d claims granted, want exactly 1", okCount)
	}
	winner := a
	if rb.Status == protocol.StatusOK {
		winner = b
	}

	// The winner abandons — the last claimant out releases the bounty and
	// arms the re-post; a fresh accept finds nothing to claim.
	winner.cmdOK(t, 2, protocol.OpMissionAbandon, fmt.Sprintf(`{"id":%q}`, offer.ID))
	srv.mu.Lock()
	released := srv.bounty == nil
	rearmed := !srv.bountyRepostAt.IsZero()
	srv.mu.Unlock()
	if !released || !rearmed {
		t.Fatalf("after abandon: released=%v rearmed=%v, want true/true", released, rearmed)
	}
	if r := winner.cmd(t, 3, protocol.OpMissionAccept, fmt.Sprintf(`{"id":%q}`, offer.ID)); r.Status != protocol.StatusRefused {
		t.Fatalf("accept after release: status %d, want refused", r.Status)
	}
}

func TestBountyExpiry(t *testing.T) {
	srv, url := newTestServer(t)
	a := joinPlayer(t, url, "A")
	a.event(t, protocol.EventPriorityOffer)
	a.cmdOK(t, 1, protocol.OpMissionAccept, `{"id":"mission.bounty.warlord"}`)

	// Move the deadline into the past and let the machine notice.
	srv.mu.Lock()
	if srv.bounty == nil || !srv.bounty.claimed {
		srv.mu.Unlock()
		t.Fatal("no claimed bounty to expire")
	}
	warlord := srv.bounty.entityID
	srv.bounty.expiresAt = time.Now().Add(-time.Second)
	srv.mu.Unlock()
	srv.bountyTick()

	srv.mu.Lock()
	released := srv.bounty == nil
	gone := srv.world.Ents[warlord] == nil
	srv.mu.Unlock()
	if !released || !gone {
		t.Fatalf("after expiry: released=%v warlordGone=%v, want true/true", released, gone)
	}
}

// A member who joins the party AFTER the claim completes the contract
// rather than stealing it, and gets paid beside the original claimant.
func TestBountyLateJoinerKill(t *testing.T) {
	srv, url := newTestServer(t)
	a := joinPlayer(t, url, "A")
	a.event(t, protocol.EventPriorityOffer)
	a.cmdOK(t, 1, protocol.OpMissionAccept, `{"id":"mission.bounty.warlord"}`)

	// B joins A's party after the claim.
	b := joinPlayer(t, url, "B")
	b.awaitPublished(t)
	a.cmdOK(t, 2, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, b.id))
	b.event(t, protocol.EventPartyInvited)
	b.cmdOK(t, 3, protocol.OpPartyRespond, `{"accept":true}`)
	a.event(t, protocol.EventPartyUpdate)

	srv.mu.Lock()
	warlord := srv.bounty.entityID
	bClient := srv.clients[b.id]
	aClient := srv.clients[a.id]
	srv.mu.Unlock()

	var aBefore, bBefore int64
	aClient.ident.Mutate(func(p *store.Player) { aBefore = p.Credits })
	bClient.ident.Mutate(func(p *store.Player) { bBefore = p.Credits })

	// The LATE JOINER lands the blow (driven directly; aiming a real shot
	// at an AI that strafes is t33's job, the rule is the point here).
	srv.bountyResolveKill(bClient, warlord)

	srv.mu.Lock()
	released := srv.bounty == nil
	srv.mu.Unlock()
	if !released {
		t.Fatal("bounty not released by the late joiner's kill")
	}
	var aAfter, bAfter int64
	aClient.ident.Mutate(func(p *store.Player) { aAfter = p.Credits })
	bClient.ident.Mutate(func(p *store.Player) { bAfter = p.Credits })
	if aAfter-aBefore != 500 || bAfter-bBefore != 500 {
		t.Fatalf("pay: A +%d B +%d, want +500 each", aAfter-aBefore, bAfter-bBefore)
	}

	// Both got the completion event.
	a.event(t, protocol.EventMissionComplete)
	b.event(t, protocol.EventMissionComplete)
}
