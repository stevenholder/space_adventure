package server

import (
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

func TestHeldItem(t *testing.T) {
	both := map[string]string{"primary": "weapon.pulse", "melee": "melee.sword"}
	for _, c := range []struct {
		eq    map[string]string
		melee bool
		want  string
	}{
		{both, false, "weapon.pulse"},
		{both, true, "melee.sword"},
		{map[string]string{"primary": "weapon.pulse"}, true, "weapon.pulse"},
		{map[string]string{"melee": "melee.sword"}, false, "melee.sword"},
		{map[string]string{}, true, ""},
	} {
		if got := heldItem(c.eq, c.melee); got != c.want {
			t.Errorf("heldItem(%v, %v) = %q want %q", c.eq, c.melee, got, c.want)
		}
	}
}

// placeDummy puts a grunt-sized dummy (no AI) `ahead` metres in front of
// the player along a tangent, and returns its id and the facing to it.
func placeDummy(srv *Server, cl *client, ahead float64) (uint32, terrain.Vec) {
	srv.mu.Lock()
	defer srv.mu.Unlock()
	pos := terrain.Vec(cl.entity.State.Pos)
	up := terrain.Normalize(pos)
	fwd := terrain.Normalize(terrain.Cross(up, terrain.Vec{1, 0, 0}))
	at := pos.Add(fwd.Scale(ahead))
	at = terrain.Normalize(at).Scale(srv.terrain.SampleRadius(terrain.Normalize(at)))
	srv.worldID++
	srv.world.Add(&sim.Ent{ID: srv.worldID, Kind: sim.EntityKind(protocol.EntityTypeNPC), Def: "npc.grunt", Pos: [3]float64(at), Health: 500})
	return srv.worldID, fwd
}

func healthOf(srv *Server, id uint32) int {
	srv.mu.Lock()
	defer srv.mu.Unlock()
	return srv.world.Ents[id].Health
}

// Tab (wield) swaps the hand and says so as `equipped`; a `fire` with a
// blade in hand swings: the body in front takes the blade's damage, once
// per interval.
func TestWieldAndSwing(t *testing.T) {
	srv, url := newTestServer(t)
	a, id := joinClient(t, url, "fighter")
	cl := published(srv, id)
	cl.ident.Mutate(func(p *store.Player) { p.Equipped["primary"] = "weapon.pulse"; p.Equipped["melee"] = "melee.sword" })

	a.sendFrame(t, protocol.EncodeCmd(protocol.Cmd{Seq: 1, Opcode: protocol.OpWield, Data: []byte(`{"slot":"melee"}`)}))
	if got := a.nextEquipped(t, id); got != "melee.sword" {
		t.Fatalf("after wield melee: equipped %q", got)
	}
	dummy, fwd := placeDummy(srv, cl, 1.5)
	fire := protocol.EncodeFire(protocol.Fire{Seq: 1, Dir: [3]float32{float32(fwd[0]), float32(fwd[1]), float32(fwd[2])}})
	a.sendFrame(t, fire)
	a.sendFrame(t, fire) // inside the interval: dropped
	want := 500 - srv.reg.Items["melee.sword"].Melee.Damage
	deadline := time.Now().Add(time.Second)
	for time.Now().Before(deadline) && healthOf(srv, dummy) == 500 {
		time.Sleep(10 * time.Millisecond)
	}
	time.Sleep(100 * time.Millisecond)
	if got := healthOf(srv, dummy); got != want {
		t.Fatalf("dummy health %d after two quick swings, want %d (one hit)", got, want)
	}

	a.sendFrame(t, protocol.EncodeCmd(protocol.Cmd{Seq: 2, Opcode: protocol.OpWield, Data: []byte(`{"slot":"primary"}`)}))
	if got := a.nextEquipped(t, id); got != "weapon.pulse" {
		t.Fatalf("after wield primary: equipped %q", got)
	}
}

// A thrown charge bursts on whatever is near: every body inside the radius
// takes damage falling off with distance; a body outside takes none.
func TestBurstHurtsTheArea(t *testing.T) {
	srv, url := newTestServer(t)
	_, id := joinClient(t, url, "thrower")
	cl := published(srv, id)
	near, _ := placeDummy(srv, cl, 3)
	far, _ := placeDummy(srv, cl, 14)
	srv.mu.Lock()
	at := sim.Vec(srv.world.Ents[near].Pos)
	srv.burstLocked(at, id, "throw.frag")
	srv.mu.Unlock()
	if got := healthOf(srv, near); got != 500-srv.reg.Items["throw.frag"].Consumable.Throw.Damage {
		t.Fatalf("near dummy health %d", got)
	}
	if got := healthOf(srv, far); got != 500 {
		t.Fatalf("far dummy health %d, want untouched", got)
	}
}

// `use` on a thrown consumable launches it and spends one.
func TestUseThrows(t *testing.T) {
	f := &useFakes{health: 100}
	p := &store.Player{Inventory: []store.Stack{{Item: "throw.frag", Qty: 2}}, Equipped: map[string]string{}}
	w := useWorld(p, f)
	th := defs.Throw{Damage: 60, Radius: 5, Speed: 18}
	w.Reg.Items["throw.frag"] = defs.Item{ID: "throw.frag", Kind: "consumable", StackMax: 10, Consumable: &defs.Consumable{Cooldown: 1.5, Throw: &th}}
	thrown := ""
	w.Throw = func(item string, got defs.Throw) { thrown = item }
	st, m := use(w, "throw.frag")
	if st != protocol.StatusOK || thrown != "throw.frag" || sim.CountItem(p, "throw.frag") != 1 {
		t.Fatalf("status=%d body=%v thrown=%q left=%d", st, m, thrown, sim.CountItem(p, "throw.frag"))
	}
}

// A gunner carrying a blade draws it when its target is within reach:
// an `equipped` event names the blade, then its swings land on the player.
func TestNPCDrawsBladeUpClose(t *testing.T) {
	srv, url := newTestServer(t)
	a, id := joinClient(t, url, "prey")
	cl := published(srv, id)
	npc, _ := placeDummy(srv, cl, 1.2)
	srv.mu.Lock()
	e := srv.world.Ents[npc]
	e.Def, e.Health = "npc.gunner", 100
	e.Data = &sim.NPCState{Post: e.Pos}
	srv.npcAI = append(srv.npcAI, newNPCAI(e, srv.reg.NPCs["npc.gunner"]))
	srv.mu.Unlock()
	if got := a.nextEquipped(t, npc); got != "melee.dagger" {
		t.Fatalf("gunner up close holds %q, want melee.dagger", got)
	}
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		srv.mu.Lock()
		h := cl.vitals.Health
		srv.mu.Unlock()
		if h < 100 {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("the gunner's blade never landed")
}
