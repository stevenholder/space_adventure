package server

// Phase 23 placeable stations over the real wire: place and its refusals,
// craft on a placed forge, chest put/take/peek with and without a party,
// the pick-up channel, restart survival and the Kick path.

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"math/rand"
	"strings"
	"testing"
	"time"

	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// structureRows writes two account characters (Kade on acc1, Ana on acc2)
// and a guest row; returns their tokens.
func structureRows(t *testing.T, st *store.Store) (kade, ana, guest string) {
	t.Helper()
	kade, guest = putRows(t, st)
	if err := st.PutPlayer(context.Background(), &store.Player{Token: "tok-ana", Name: "Ana", AccountID: "acc2"}); err != nil {
		t.Fatal(err)
	}
	return kade, "tok-ana", guest
}

// joinToken seats token and waits until it is published.
func joinToken(t *testing.T, s *Server, url, token string) (*pClient, *client) {
	t.Helper()
	ws := hello(t, url, token, "")
	ha, err := protocol.DecodeHelloAck(ws.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	p := &pClient{ws: ws, id: ha.EntityID}
	p.awaitPublished(t)
	s.mu.Lock()
	c := s.clients[p.id]
	s.mu.Unlock()
	return p, c
}

// standAt puts c's body at pos facing f, looking along look (nil = f).
func standAt(s *Server, c *client, pos, f sim.Vec, look sim.Vec) {
	if look == (sim.Vec{}) {
		look = f
	}
	s.mu.Lock()
	c.entity.State.Pos, c.entity.State.Vel, c.entity.State.Facing = pos, sim.Vec{}, f
	c.entity.State.Grounded = true
	c.input.Store(&protocol.Input{LookDir: [3]float32{float32(look[0]), float32(look[1]), float32(look[2])}})
	s.mu.Unlock()
}

// aimLook is the look from a body at pos to a structure at target.
func aimLook(pos, target sim.Vec) sim.Vec {
	eye := pos.Add(terrain.Normalize(pos).Scale(eyeHeightMeters))
	aim := target.Add(terrain.Normalize(target).Scale(structureAimHeight))
	return terrain.Normalize(aim.Sub(eye))
}

// ground is the surface point under dir.
func ground(s *Server, dir sim.Vec) sim.Vec {
	d := terrain.Normalize(dir)
	return d.Scale(s.terrain.SampleRadius(d))
}

// placeTarget mirrors placeLocked's target for a body at pos facing f.
func placeTarget(s *Server, pos, f sim.Vec) sim.Vec {
	return ground(s, pos.Add(f.Scale(placeReach)))
}

// flatSpot finds a body position and facing near the spawn where a
// structure would go down, and the ground behind the target (for a
// second body facing the other way) is clear too.
func flatSpot(t *testing.T, s *Server) (pos, f sim.Vec) {
	t.Helper()
	s.mu.Lock()
	defer s.mu.Unlock()
	spawn := sim.SpawnState(s.terrain).Pos
	up := terrain.Normalize(spawn)
	ref, right := yawBasis(up)
	for ring := 6.0; ring < 80; ring += 3 {
		for k := 0; k < 16; k++ {
			a := float64(k) * math.Pi / 8
			pos = ground(s, spawn.Add(ref.Scale(ring*math.Cos(a))).Add(right.Scale(ring*math.Sin(a))))
			u := terrain.Normalize(pos)
			f = terrain.Normalize(ref.Sub(u.Scale(ref.Dot(u))))
			target := placeTarget(s, pos, f)
			other := placeTarget(s, target, f)
			if s.roomAtLocked(target, f) && s.roomAtLocked(other, f) && s.roomAtLocked(pos, f) {
				return pos, f
			}
		}
	}
	t.Fatal("no flat clear spot near the spawn")
	return
}

func give(c *client, stacks ...store.Stack) {
	c.ident.Mutate(func(p *store.Player) { p.Inventory = append([]store.Stack(nil), stacks...) })
}

func refusal(t *testing.T, r protocol.CmdResult) string {
	t.Helper()
	if r.Status != protocol.StatusRefused {
		t.Fatalf("status %d (%s), want refused", r.Status, r.Data)
	}
	var b struct {
		Reason string `json:"reason"`
	}
	_ = json.Unmarshal(r.Data, &b)
	return b.Reason
}

func placedID(t *testing.T, r protocol.CmdResult) uint32 {
	t.Helper()
	var b struct {
		Structure uint32        `json:"structure"`
		Inventory []store.Stack `json:"inventory"`
	}
	if err := json.Unmarshal(r.Data, &b); err != nil || b.Structure == 0 {
		t.Fatalf("place result %s", r.Data)
	}
	return b.Structure
}

// waitRows polls the store until want(rows) holds (persists after a
// pick-up run in the background).
func waitRows(t *testing.T, st *store.Store, want func([]store.Structure) bool) []store.Structure {
	t.Helper()
	var rows []store.Structure
	for deadline := time.Now().Add(3 * time.Second); time.Now().Before(deadline); time.Sleep(20 * time.Millisecond) {
		var err error
		if rows, err = st.ListStructures(context.Background()); err != nil {
			t.Fatal(err)
		}
		if want(rows) {
			return rows
		}
	}
	t.Fatalf("store rows never matched: %+v", rows)
	return nil
}

func TestPlaceStructure(t *testing.T) {
	st := openTestStore(t)
	kade, _, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, c := joinToken(t, s, url, kade)
	pos, f := flatSpot(t, s)
	standAt(s, c, pos, f, sim.Vec{})

	if r := refusal(t, a.cmd(t, 1, protocol.OpPlace, `{"item":"mat.parts"}`)); r != "unknown_item" {
		t.Fatalf("a material: %s", r)
	}
	if r := refusal(t, a.cmd(t, 2, protocol.OpPlace, `{"item":"structure.bench"}`)); r != "not_owned" {
		t.Fatalf("none in the bag: %s", r)
	}
	give(c, store.Stack{Item: "structure.bench", Qty: 1}, store.Stack{Item: "structure.chest", Qty: 1})
	id := placedID(t, a.cmdOK(t, 3, protocol.OpPlace, `{"item":"structure.bench"}`))
	if count(c, "structure.bench") != 0 {
		t.Fatal("the bench stayed in the bag")
	}
	target := placeTarget(s, pos, f)
	s.mu.Lock()
	e := s.world.Ents[id]
	if !isStructure(e) || e.Def != "structure.bench" || structureOf(e).Owner != kade || structureOf(e).OwnerName != "Kade" {
		s.mu.Unlock()
		t.Fatalf("placed ent %+v", e)
	}
	if e.Pos != [3]float64(target) {
		t.Fatalf("placed at %v, want %v", e.Pos, target)
	}
	// It faces the placer: its forward (local +Z) points back at the body.
	fwd := sim.Rotate(sim.Quat(e.Quat), sim.Vec{0, 0, 1})
	if fwd.Dot(f) > -0.99 {
		t.Fatalf("forward %v, want toward the body (-%v)", fwd, f)
	}
	if string(spawnData(e)) != "structure.bench\x00Kade" {
		t.Fatalf("spawn data %q", spawnData(e))
	}
	s.mu.Unlock()
	rows := waitRows(t, st, func(r []store.Structure) bool { return len(r) == 1 })
	if rows[0].Item != "structure.bench" || rows[0].Owner != kade {
		t.Fatalf("row %+v", rows[0])
	}

	// Same spot again: the bench is in the way. Nothing left the bag.
	if r := refusal(t, a.cmd(t, 4, protocol.OpPlace, `{"item":"structure.chest"}`)); r != "no_room" {
		t.Fatalf("on top of the bench: %s", r)
	}
	if count(c, "structure.chest") != 1 {
		t.Fatal("a refused place took the item")
	}

	// The bench is solid: it is in the colliders a body steps against.
	deadline := time.Now().Add(2 * time.Second)
	for {
		s.mu.Lock()
		found := false
		for i, o := range s.dynOwners {
			if o == id && s.dynColliders[i].Kind == protocol.ColliderBox {
				found = true
			}
		}
		s.mu.Unlock()
		if found {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("the bench never joined the dynamic colliders")
		}
		time.Sleep(20 * time.Millisecond)
	}

	// Steep ground: find a body/facing whose target sits on > 15°.
	s.mu.Lock()
	rng := rand.New(rand.NewSource(7))
	var steepPos, steepF sim.Vec
	for i := 0; i < 200000 && steepPos == (sim.Vec{}); i++ {
		d := terrain.Normalize(sim.Vec{rng.NormFloat64(), rng.NormFloat64(), rng.NormFloat64()})
		p := d.Scale(s.terrain.SampleRadius(d))
		ref, _ := yawBasis(d)
		if s.terrain.Slope(terrain.Normalize(placeTarget(s, p, ref))) > placeSlopeMax+0.05 {
			steepPos, steepF = p, ref
		}
	}
	s.mu.Unlock()
	if steepPos == (sim.Vec{}) {
		t.Fatal("no steep ground on the planet")
	}
	standAt(s, c, steepPos, steepF, sim.Vec{})
	if r := refusal(t, a.cmd(t, 5, protocol.OpPlace, `{"item":"structure.chest"}`)); r != "no_room" {
		t.Fatalf("on a slope: %s", r)
	}

	// The cap: five placed (four more injected) refuses the sixth.
	s.mu.Lock()
	for i := 0; i < 4; i++ {
		s.world.Add(s.newStructureEnt("structure.bench", ground(s, sim.Vec{0, 0, 1}),
			&structureState{Key: fmt.Sprintf("cap%d", i), Owner: kade, OwnerName: "Kade"}))
	}
	s.mu.Unlock()
	standAt(s, c, pos, f, sim.Vec{})
	if r := refusal(t, a.cmd(t, 6, protocol.OpPlace, `{"item":"structure.chest"}`)); r != "too_many" {
		t.Fatalf("a sixth: %s", r)
	}
}

func TestPlaceRefusesGuest(t *testing.T) {
	st := openTestStore(t)
	_, _, guest := structureRows(t, st)
	s, url := newStrictWorld(t, st, true)
	a, c := joinToken(t, s, url, guest)
	pos, f := flatSpot(t, s)
	standAt(s, c, pos, f, sim.Vec{})
	give(c, store.Stack{Item: "structure.bench", Qty: 1})
	if r := refusal(t, a.cmd(t, 1, protocol.OpPlace, `{"item":"structure.bench"}`)); r != "no_account" {
		t.Fatalf("guest: %s", r)
	}
	if count(c, "structure.bench") != 1 {
		t.Fatal("a refused place took the item")
	}
}

// A placed forge is a forge to anyone; a chest is no station.
func TestCraftOnPlacedStructure(t *testing.T) {
	st := openTestStore(t)
	kade, ana, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, ca := joinToken(t, s, url, kade)
	b, cb := joinToken(t, s, url, ana)
	pos, f := flatSpot(t, s)
	standAt(s, ca, pos, f, sim.Vec{})
	give(ca, store.Stack{Item: "structure.forge", Qty: 1})
	forge := placedID(t, a.cmdOK(t, 1, protocol.OpPlace, `{"item":"structure.forge"}`))
	target := placeTarget(s, pos, f)
	// Ana, the other side of it, smelts iron at Kade's forge.
	standAt(s, ca, pos.Add(pos.Sub(target).Scale(3)), f, sim.Vec{}) // Kade steps away
	bpos := placeTarget(s, target, f)
	standAt(s, cb, bpos, f.Scale(-1), aimLook(bpos, target))
	give(cb, store.Stack{Item: "mat.ore.iron", Qty: 2})
	b.cmdOK(t, 1, protocol.OpCraft, fmt.Sprintf(`{"npc":%d,"recipe":"recipe.mat.ingot.iron","qty":1}`, forge))
	if e := nextCraftEnd(t, b); e.Reason != "done" || e.Item != "mat.ingot.iron" {
		t.Fatalf("craft at a placed forge: %+v", e)
	}

	// A chest serves no recipe.
	standAt(s, ca, pos, f, sim.Vec{})
	s.mu.Lock()
	s.world.Remove(forge)
	s.mu.Unlock()
	give(ca, store.Stack{Item: "structure.chest", Qty: 1})
	chest := placedID(t, a.cmdOK(t, 2, protocol.OpPlace, `{"item":"structure.chest"}`))
	standAt(s, cb, bpos, f.Scale(-1), aimLook(bpos, target))
	give(cb, store.Stack{Item: "mat.ore.iron", Qty: 2})
	if r := refusal(t, b.cmd(t, 2, protocol.OpCraft, fmt.Sprintf(`{"npc":%d,"recipe":"recipe.mat.ingot.iron","qty":1}`, chest))); r != "wrong_station" {
		t.Fatalf("craft at a chest: %s", r)
	}
	// Out of reach is out_of_range.
	far := pos.Add(pos.Sub(target).Scale(5))
	standAt(s, cb, far, f, aimLook(far, target))
	if r := refusal(t, b.cmd(t, 3, protocol.OpCraft, fmt.Sprintf(`{"npc":%d,"recipe":"recipe.mat.ingot.iron","qty":1}`, chest))); r != "out_of_range" {
		t.Fatalf("far: %s", r)
	}
}

type chestResult struct {
	Inventory []store.Stack `json:"inventory"`
	Chest     []store.Stack `json:"chest"`
}

func chestOf(t *testing.T, r protocol.CmdResult) chestResult {
	t.Helper()
	var out chestResult
	if err := json.Unmarshal(r.Data, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func TestChestPutTakeParty(t *testing.T) {
	st := openTestStore(t)
	kade, ana, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, ca := joinToken(t, s, url, kade)
	b, cb := joinToken(t, s, url, ana)
	pos, f := flatSpot(t, s)
	target := placeTarget(s, pos, f)
	standAt(s, ca, pos, f, sim.Vec{})
	give(ca, store.Stack{Item: "structure.chest", Qty: 1}, store.Stack{Item: "mat.parts", Qty: 10})
	chest := placedID(t, a.cmdOK(t, 1, protocol.OpPlace, `{"item":"structure.chest"}`))
	standAt(s, ca, pos, f, aimLook(pos, target))

	body := func(item string, qty int) string {
		return fmt.Sprintf(`{"structure":%d,"item":%q,"qty":%d}`, chest, item, qty)
	}
	if r := a.cmd(t, 2, protocol.OpChestPut, `{"structure":999999,"item":"mat.parts","qty":1}`); r.Status != protocol.StatusNotFound {
		t.Fatalf("unknown structure: status %d", r.Status)
	}
	if r := refusal(t, a.cmd(t, 3, protocol.OpChestPut, body("mat.parts", 0))); r != "bad_qty" {
		t.Fatalf("qty 0 put: %s", r)
	}
	if r := refusal(t, a.cmd(t, 4, protocol.OpChestPut, body("mat.scrap", 1))); r != "not_owned" {
		t.Fatalf("put what I lack: %s", r)
	}
	got := chestOf(t, a.cmdOK(t, 5, protocol.OpChestPut, body("mat.parts", 7)))
	if len(got.Chest) != 1 || got.Chest[0] != (store.Stack{Item: "mat.parts", Qty: 7}) || count(ca, "mat.parts") != 3 {
		t.Fatalf("after put: %+v, bag parts %d", got, count(ca, "mat.parts"))
	}
	if r := refusal(t, a.cmd(t, 6, protocol.OpChestTake, body("mat.parts", 8))); r != "not_owned" {
		t.Fatalf("take more than is in it: %s", r)
	}
	// Peek: no transfer, the chest's contents.
	got = chestOf(t, a.cmdOK(t, 7, protocol.OpChestTake, fmt.Sprintf(`{"structure":%d,"item":"","qty":0}`, chest)))
	if len(got.Chest) != 1 || got.Chest[0].Qty != 7 || count(ca, "mat.parts") != 3 {
		t.Fatalf("peek: %+v", got)
	}
	waitRows(t, st, func(r []store.Structure) bool {
		return len(r) == 1 && len(r[0].Stacks) == 1 && r[0].Stacks[0].Qty == 7
	})

	// Ana, not in Kade's party: not_yours, peek included.
	bpos := placeTarget(s, target, f)
	standAt(s, cb, bpos, f.Scale(-1), aimLook(bpos, target))
	if r := refusal(t, b.cmd(t, 1, protocol.OpChestTake, fmt.Sprintf(`{"structure":%d,"item":"","qty":0}`, chest))); r != "not_yours" {
		t.Fatalf("stranger peek: %s", r)
	}
	if r := refusal(t, b.cmd(t, 2, protocol.OpChestTake, body("mat.parts", 1))); r != "not_yours" {
		t.Fatalf("stranger take: %s", r)
	}
	// In the party, she may.
	a.cmdOK(t, 8, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, b.id))
	b.event(t, protocol.EventPartyInvited)
	b.cmdOK(t, 3, protocol.OpPartyRespond, `{"accept":true}`)
	got = chestOf(t, b.cmdOK(t, 4, protocol.OpChestTake, body("mat.parts", 2)))
	if len(got.Chest) != 1 || got.Chest[0].Qty != 5 || count(cb, "mat.parts") != 2 {
		t.Fatalf("party take: %+v", got)
	}
	// Her bag full: no_space, nothing moved.
	full := make([]store.Stack, 0, 20)
	for i := 0; i < 20; i++ {
		full = append(full, store.Stack{Item: "ammo.cell", Qty: 1})
	}
	full[0] = store.Stack{Item: "mat.scrap", Qty: 1}
	give(cb, full...)
	if r := refusal(t, b.cmd(t, 5, protocol.OpChestTake, body("mat.parts", 1))); r != "no_space" {
		t.Fatalf("full bag: %s", r)
	}
	// Out of reach.
	far := bpos.Add(bpos.Sub(target).Scale(5))
	standAt(s, cb, far, f.Scale(-1), aimLook(far, target))
	if r := refusal(t, b.cmd(t, 6, protocol.OpChestTake, body("mat.parts", 1))); r != "out_of_range" {
		t.Fatalf("far: %s", r)
	}
}

type pickUpEnd struct {
	Structure uint32        `json:"structure"`
	OK        bool          `json:"ok"`
	Reason    string        `json:"reason"`
	Inventory []store.Stack `json:"inventory"`
}

func nextPickUpEnd(t *testing.T, a *pClient) pickUpEnd {
	t.Helper()
	var e pickUpEnd
	if err := json.Unmarshal(a.event(t, protocol.EventPickUpEnd).Data, &e); err != nil {
		t.Fatal(err)
	}
	return e
}

func TestPickUp(t *testing.T) {
	st := openTestStore(t)
	kade, ana, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, ca := joinToken(t, s, url, kade)
	b, cb := joinToken(t, s, url, ana)
	pos, f := flatSpot(t, s)
	target := placeTarget(s, pos, f)
	standAt(s, ca, pos, f, sim.Vec{})
	give(ca, store.Stack{Item: "structure.chest", Qty: 1}, store.Stack{Item: "mat.parts", Qty: 1})
	chest := placedID(t, a.cmdOK(t, 1, protocol.OpPlace, `{"item":"structure.chest"}`))
	standAt(s, ca, pos, f, aimLook(pos, target))
	pick := fmt.Sprintf(`{"structure":%d}`, chest)

	// Not the owner.
	bpos := placeTarget(s, target, f)
	standAt(s, cb, bpos, f.Scale(-1), aimLook(bpos, target))
	if r := refusal(t, b.cmd(t, 1, protocol.OpPickUp, pick)); r != "not_yours" {
		t.Fatalf("stranger pick-up: %s", r)
	}
	// A chest with something in it.
	a.cmdOK(t, 2, protocol.OpChestPut, fmt.Sprintf(`{"structure":%d,"item":"mat.parts","qty":1}`, chest))
	if r := refusal(t, a.cmd(t, 3, protocol.OpPickUp, pick)); r != "not_empty" {
		t.Fatalf("full chest: %s", r)
	}
	a.cmdOK(t, 4, protocol.OpChestTake, fmt.Sprintf(`{"structure":%d,"item":"mat.parts","qty":1}`, chest))

	// Started, then a step past 0.5 m breaks it; the chest stays.
	res := a.cmdOK(t, 5, protocol.OpPickUp, pick)
	if !strings.Contains(string(res.Data), `"seconds":1.5`) {
		t.Fatalf("pick_up result %s", res.Data)
	}
	if r := refusal(t, a.cmd(t, 6, protocol.OpPickUp, pick)); r != "busy" {
		t.Fatalf("second pick-up: %s", r)
	}
	moved := pos.Sub(f.Scale(0.8))
	standAt(s, ca, ground(s, moved), f, sim.Vec{})
	if e := nextPickUpEnd(t, a); e.OK || e.Reason != "moved" || e.Structure != chest {
		t.Fatalf("moved: %+v", e)
	}
	// gather_cancel cancels it too.
	standAt(s, ca, pos, f, aimLook(pos, target))
	a.cmdOK(t, 7, protocol.OpPickUp, pick)
	a.cmdOK(t, 8, protocol.OpGatherCancel, `{}`)
	if e := nextPickUpEnd(t, a); e.OK || e.Reason != "cancel" {
		t.Fatalf("cancel: %+v", e)
	}
	s.mu.Lock()
	if !isStructure(s.world.Ents[chest]) {
		t.Fatal("a broken pick-up removed the chest")
	}
	s.mu.Unlock()

	// A full bag refuses at the start.
	full := make([]store.Stack, 0, 20)
	for i := 0; i < 20; i++ {
		full = append(full, store.Stack{Item: "ammo.cell", Qty: 1})
	}
	full[0] = store.Stack{Item: "mat.scrap", Qty: 1}
	give(ca, full...)
	if r := refusal(t, a.cmd(t, 9, protocol.OpPickUp, pick)); r != "no_space" {
		t.Fatalf("full bag: %s", r)
	}

	// Held still for 1.5 s: the chest is back in the bag, gone from the
	// world and the store.
	give(ca)
	a.cmdOK(t, 10, protocol.OpPickUp, pick)
	e := nextPickUpEnd(t, a)
	if !e.OK || len(e.Inventory) != 1 || e.Inventory[0] != (store.Stack{Item: "structure.chest", Qty: 1}) {
		t.Fatalf("done: %+v", e)
	}
	s.mu.Lock()
	gone := s.world.Ents[chest] == nil
	s.mu.Unlock()
	if !gone {
		t.Fatal("the chest stayed in the world")
	}
	waitRows(t, st, func(r []store.Structure) bool { return len(r) == 0 })
}

// Stored structures come back on a fresh server: same item, owner, place
// and chest contents, announced to a joiner as item NUL owner.
func TestStructuresSurviveRestart(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	kade, _, _ := structureRows(t, st)
	w0, err := New(terrain.Generate(1337), 1337)
	if err != nil {
		t.Fatal(err)
	}
	p := ground(w0, sim.Vec{0, 1, 0})
	for _, row := range []store.Structure{
		{ID: "s_1", Owner: kade, OwnerName: "Kade", Item: "structure.forge", Pos: p, Yaw: 0.5, PlacedMs: 1},
		{ID: "s_2", Owner: kade, OwnerName: "Kade", Item: "structure.chest", Pos: p.Add(sim.Vec{3, 0, 0}), PlacedMs: 2,
			Stacks: []store.Stack{{Item: "mat.parts", Qty: 4}}},
		{ID: "s_3", Owner: kade, OwnerName: "Kade", Item: "mat.parts", PlacedMs: 3}, // not a structure: skipped
	} {
		if err := st.PutStructure(ctx, &row); err != nil {
			t.Fatal(err)
		}
	}
	s, url := newStrictWorld(t, st, false)
	s.mu.Lock()
	byKey := map[string]*sim.Ent{}
	for _, e := range s.world.Ents {
		if sst := structureOf(e); sst != nil {
			byKey[sst.Key] = e
		}
	}
	s.mu.Unlock()
	if len(byKey) != 2 || byKey["s_1"].Def != "structure.forge" || byKey["s_2"].Def != "structure.chest" {
		t.Fatalf("loaded %v", byKey)
	}
	if byKey["s_1"].Pos != [3]float64(p) || math.Abs(structureOf(byKey["s_1"]).Yaw-0.5) > 1e-9 {
		t.Fatalf("forge at %v yaw %v", byKey["s_1"].Pos, structureOf(byKey["s_1"]).Yaw)
	}
	if got := structureOf(byKey["s_2"]).Stacks; len(got) != 1 || got[0].Qty != 4 {
		t.Fatalf("chest contents %+v", got)
	}

	ws := hello(t, url, kade, "")
	seen := map[string]bool{}
	for i := 0; i < 500 && len(seen) < 2; i++ {
		sp, err := protocol.DecodeSpawn(ws.nextOf(t, protocol.MsgSpawn))
		if err != nil {
			t.Fatal(err)
		}
		if sp.EntityType == protocol.EntityTypeStructure {
			seen[string(sp.Data)] = true
		}
	}
	if !seen["structure.forge\x00Kade"] || !seen["structure.chest\x00Kade"] {
		t.Fatalf("structure spawns %v", seen)
	}
}

// Deleting the character the way the web handler does takes its
// structures out of the world and the store; another owner's stay.
func TestKickRemovesStructures(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	kade, ana, _ := structureRows(t, st)
	for _, row := range []store.Structure{
		{ID: "k1", Owner: kade, OwnerName: "Kade", Item: "structure.bench", Pos: [3]float64{0, 150, 0}},
		{ID: "a1", Owner: ana, OwnerName: "Ana", Item: "structure.bench", Pos: [3]float64{0, -150, 0}},
	} {
		if err := st.PutStructure(ctx, &row); err != nil {
			t.Fatal(err)
		}
	}
	s, _ := newStrictWorld(t, st, false)
	if err := st.DeletePlayer(ctx, kade); err != nil {
		t.Fatal(err)
	}
	s.Kick([]string{kade})
	s.mu.Lock()
	var owners []string
	for _, e := range s.world.Ents {
		if sst := structureOf(e); sst != nil {
			owners = append(owners, sst.Owner)
		}
	}
	s.mu.Unlock()
	if len(owners) != 1 || owners[0] != ana {
		t.Fatalf("world owners after kick %v", owners)
	}
	rows := waitRows(t, st, func(r []store.Structure) bool { return len(r) == 1 })
	if rows[0].ID != "a1" {
		t.Fatalf("rows %+v", rows)
	}
}
