package server

// The award engine over the wire (C78's core): sprinting trains Athletics,
// buying trains Commerce, damage trains Marksmanship, discovery pays Recon
// once ever, events batch and carry levels, and the sheet answers OpSkills
// with everything persisted.

import (
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
)

func norm3f(v [3]float32) [3]float32 {
	l := float32(sqrt64(float64(v[0]*v[0] + v[1]*v[1] + v[2]*v[2])))
	if l < 1e-9 {
		return v
	}
	return [3]float32{v[0] / l, v[1] / l, v[2] / l}
}

type xpEvent struct {
	Skill   string `json:"skill"`
	XP      int64  `json:"xp"`
	Level   int    `json:"level"`
	NextAt  int64  `json:"next_at"`
	Leveled bool   `json:"leveled"`
}

func TestSkillAwards(t *testing.T) {
	_, url := newTestServer(t)
	a := joinPlayer(t, url, "A")

	// Sprint a real distance first: the shop is 3.6 m from spawn, and
	// Athletics pays per WHOLE 10 m. Three seconds of sprinting one way
	// banks ~20 m before walking back to the counter.
	qm := a.findSpawnByDef(t, "npc.quartermaster")

	// Commerce first, on the proven pattern: walk in and buy (250 cr = 50
	// XP at 1/5 cr).
	walkToNPC(t, a, qm)
	a.cmdOK(t, 1, protocol.OpShopBuy, fmt.Sprintf(`{"npc":%d,"item":"weapon.pulse","qty":1}`, qm))

	// Athletics: sprint a few seconds in the open (paid per whole 10 m).
	for i := 0; i < 80; i++ {
		a.ws.sendFrame(t, protocol.EncodeInput(protocol.Input{
			MoveY: 1, LookDir: [3]float32{1, 0, 0},
			ActionMask: protocol.ActionSprint, Seq: uint16(100 + i),
		}))
		time.Sleep(50 * time.Millisecond)
	}

	// The sweep flushes within a second; collect skill_xp events until both
	// skills have reported (spawn is inside the spawn zone, not a POI, so
	// no recon discovery fires here — the camp is 271 m away).
	deadline := time.Now().Add(5 * time.Second)
	got := map[string]xpEvent{}
	for time.Now().Before(deadline) {
		ev := a.event(t, protocol.EventSkillXP)
		var x xpEvent
		if err := json.Unmarshal(ev.Data, &x); err != nil {
			t.Fatal(err)
		}
		got[x.Skill] = x
		if _, ok := got["athletics"]; ok {
			if _, ok := got["commerce"]; ok {
				break
			}
		}
	}
	ath, commerce := got["athletics"], got["commerce"]
	if ath.XP <= 0 {
		t.Fatalf("athletics xp = %d, want > 0 (sprinted to the shop)", ath.XP)
	}
	if commerce.XP != 50 {
		t.Fatalf("commerce xp = %d, want 50 (250 cr at 1/5)", commerce.XP)
	}
	if ath.Level < 1 || ath.NextAt <= ath.XP {
		t.Fatalf("athletics event shape: %+v", ath)
	}

	// The sheet agrees with the events.
	r := a.cmdOK(t, 2, protocol.OpSkills, `{}`)
	var sheet struct {
		XP     map[string]int64 `json:"xp"`
		Levels map[string]int   `json:"levels"`
	}
	if err := json.Unmarshal(r.Data, &sheet); err != nil {
		t.Fatal(err)
	}
	if sheet.XP["commerce"] != 50 {
		t.Fatalf("sheet commerce = %d, want 50", sheet.XP["commerce"])
	}
	// >=, not ==: a sweep can flush more sprint metres between the last
	// event we read and the sheet query.
	if sheet.XP["athletics"] < ath.XP {
		t.Fatalf("sheet athletics = %d, below the %d we already saw", sheet.XP["athletics"], ath.XP)
	}
	if len(sheet.Levels) != 10 {
		t.Fatalf("levels for %d skills, want all 10", len(sheet.Levels))
	}
}
