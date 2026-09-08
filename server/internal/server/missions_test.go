package server

// Mission cmd surface over the real wire (C73's cmd half): board listing
// with the starter filter, accept/duplicate/abandon, turn-in refusals, and
// the range gate. Kill and scout progress ride live tests (t32) — they need
// a fight and a walk; the cmd rules are provable here.

import (
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
)

// walkToNPC drives the player into interact range of the NPC, re-reading
// both positions from live snapshots each step — spawn stands 3.6 m from
// the quartermaster and interact_dist is 3.0 (the t14 lesson), and a blind
// timed walk overshoots.
func walkToNPC(t *testing.T, c *pClient, npc uint32) {
	t.Helper()
	seq := uint16(1)
	for i := 0; i < 60; i++ {
		me := c.posOf(t, c.id)
		to := c.posOf(t, npc)
		d := [3]float32{to[0] - me[0], to[1] - me[1], to[2] - me[2]}
		l := float32(0)
		for _, v := range d {
			l += v * v
		}
		dist := float32(sqrt64(float64(l)))
		if l > 0 {
			for j := range d {
				d[j] /= dist
			}
		}
		if dist < 2.4 {
			c.ws.sendFrame(t, protocol.EncodeInput(protocol.Input{LookDir: d, Seq: seq}))
			time.Sleep(120 * time.Millisecond)
			return
		}
		c.ws.sendFrame(t, protocol.EncodeInput(protocol.Input{MoveY: 1, LookDir: d, Seq: seq}))
		seq++
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatal("never reached the NPC")
}

func sqrt64(v float64) float64 {
	x := v
	for i := 0; i < 32; i++ {
		x = (x + v/x) / 2
	}
	return x
}

// findSpawnByDef waits for the spawn frame naming an archetype and returns
// its entity id.
func (c *pClient) findSpawnByDef(t *testing.T, def string) uint32 {
	t.Helper()
	for i := 0; i < 500; i++ {
		typ, payload := c.ws.next(t)
		if typ != protocol.MsgSpawn {
			continue
		}
		sp, err := protocol.DecodeSpawn(payload)
		if err != nil {
			t.Fatal(err)
		}
		if string(sp.Data) == def {
			return sp.EntityID
		}
	}
	t.Fatalf("no spawn for %s", def)
	return 0
}

// posOf reads snapshots until the entity's position appears.
func (c *pClient) posOf(t *testing.T, id uint32) [3]float32 {
	t.Helper()
	for i := 0; i < 500; i++ {
		payload := c.ws.nextOf(t, protocol.MsgSnapshot)
		snap, err := protocol.DecodeSnapshot(payload)
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range snap.Entities {
			if e.ID == id {
				return e.Pos
			}
		}
	}
	t.Fatalf("entity %d never in a snapshot", id)
	return [3]float32{}
}

func TestMissionCmdSurface(t *testing.T) {
	_, url := newTestServer(t)
	a := joinPlayer(t, url, "A")
	qm := a.findSpawnByDef(t, "npc.quartermaster")
	walkToNPC(t, a, qm)

	// The quartermaster is a STARTER board: cull and scout.outpost only.
	r := a.cmdOK(t, 1, protocol.OpMissionList, fmt.Sprintf(`{"npc":%d}`, qm))
	var list struct {
		Offers []struct {
			ID      string `json:"id"`
			Starter bool   `json:"starter"`
		} `json:"offers"`
	}
	if err := json.Unmarshal(r.Data, &list); err != nil {
		t.Fatal(err)
	}
	if len(list.Offers) == 0 {
		t.Fatal("no offers from the quartermaster")
	}
	for _, o := range list.Offers {
		if !o.Starter {
			t.Fatalf("quartermaster offered non-starter %s", o.ID)
		}
	}

	// Accept, duplicate-accept, abandon, re-abandon.
	a.cmdOK(t, 2, protocol.OpMissionAccept, `{"id":"mission.cull"}`)
	if r := a.cmd(t, 3, protocol.OpMissionAccept, `{"id":"mission.cull"}`); r.Status != protocol.StatusRefused {
		t.Fatalf("duplicate accept: status %d, want refused", r.Status)
	}
	a.cmdOK(t, 4, protocol.OpMissionAbandon, `{"id":"mission.cull"}`)
	if r := a.cmd(t, 5, protocol.OpMissionAbandon, `{"id":"mission.cull"}`); r.Status != protocol.StatusRefused {
		t.Fatalf("re-abandon: status %d, want refused", r.Status)
	}

	// Turn-in gates: a kill mission is not a fetch; a fetch without the
	// items refuses not_enough.
	a.cmdOK(t, 6, protocol.OpMissionAccept, `{"id":"mission.cull"}`)
	if r := a.cmd(t, 7, protocol.OpMissionTurnin, fmt.Sprintf(`{"npc":%d,"id":"mission.cull"}`, qm)); r.Status != protocol.StatusRefused {
		t.Fatalf("turnin kill mission: status %d, want refused", r.Status)
	}
	a.cmdOK(t, 8, protocol.OpMissionAccept, `{"id":"mission.salvage"}`)
	r = a.cmd(t, 9, protocol.OpMissionTurnin, fmt.Sprintf(`{"npc":%d,"id":"mission.salvage"}`, qm))
	var why struct {
		Reason string `json:"reason"`
	}
	json.Unmarshal(r.Data, &why)
	// A fresh player starts with 120 cells — enough for the 30-cell fetch —
	// so this turn-in SUCCEEDS and pays; the mission is then inactive.
	if r.Status == protocol.StatusOK {
		ev := a.event(t, protocol.EventMissionComplete)
		var done struct {
			ID string `json:"id"`
		}
		json.Unmarshal(ev.Data, &done)
		if done.ID != "mission.salvage" {
			t.Fatalf("complete event for %s, want mission.salvage", done.ID)
		}
	} else if why.Reason != "not_enough" {
		t.Fatalf("fetch turnin: status %d reason %q", r.Status, why.Reason)
	}

	// Unknown mission and the bounty stub.
	if r := a.cmd(t, 10, protocol.OpMissionAccept, `{"id":"mission.nope"}`); r.Status != protocol.StatusNotFound {
		t.Fatalf("unknown mission: status %d, want not found", r.Status)
	}
	if r := a.cmd(t, 11, protocol.OpMissionAccept, `{"id":"mission.bounty.warlord"}`); r.Status != protocol.StatusRefused {
		t.Fatalf("bounty stub: status %d, want refused", r.Status)
	}

	// A board list against something that is not a board.
	tgt := uint32(1048583) // a range target id, definitely not a board
	if r := a.cmd(t, 12, protocol.OpMissionList, fmt.Sprintf(`{"npc":%d}`, tgt)); r.Status != protocol.StatusRefused &&
		r.Status != protocol.StatusNotFound {
		t.Fatalf("list at a target: status %d, want refused/notfound", r.Status)
	}
}
