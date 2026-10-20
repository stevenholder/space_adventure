package server

// Phase 24 buildings and the dock over the real wire: a site's footprint
// and one-dock rule, deliver (owner, party, stranger, the bill and its
// percent in the row), the completion swap, a rover crafted at the dock,
// demolish, restart survival, and the shop no longer selling the ship.

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"strings"
	"testing"
	"time"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/skills"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// siteSpot is flatSpot for a site: the 6 m footprint and 8° rule hold at
// the target, and the ground 2 m past it (a second body) is clear.
func siteSpot(t *testing.T, s *Server) (pos, f sim.Vec) {
	t.Helper()
	s.mu.Lock()
	defer s.mu.Unlock()
	rule := placeRuleFor(s.reg.Items["site.dock"])
	spawn := sim.SpawnState(s.terrain).Pos
	up := terrain.Normalize(spawn)
	ref, right := yawBasis(up)
	for ring := 10.0; ring < 200; ring += 4 {
		for k := 0; k < 24; k++ {
			a := float64(k) * math.Pi / 12
			pos = ground(s, spawn.Add(ref.Scale(ring*math.Cos(a))).Add(right.Scale(ring*math.Sin(a))))
			u := terrain.Normalize(pos)
			f = terrain.Normalize(ref.Sub(u.Scale(ref.Dot(u))))
			target := placeTarget(s, pos, f)
			// Nothing within the footprint of either body either (a site
			// ignores only its placer).
			if s.roomForLocked(target, f, rule, nil) && s.roomAtLocked(pos, f) &&
				s.roomAtLocked(placeTarget(s, target, f), f) {
				return pos, f
			}
		}
	}
	t.Fatal("no site-sized flat clear spot near the spawn")
	return
}

type deliverOut struct {
	Bill      []defs.ItemQty `json:"bill"`
	Pct       int            `json:"pct"`
	Inventory []store.Stack  `json:"inventory"`
}

func deliverOf(t *testing.T, r protocol.CmdResult) deliverOut {
	t.Helper()
	var out deliverOut
	if err := json.Unmarshal(r.Data, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func billLeft(b []defs.ItemQty, item string) int {
	for _, q := range b {
		if q.Item == item {
			return q.Qty
		}
	}
	return -1
}

func entOf(s *Server, id uint32) (def string, health int, st *structureState) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e := s.world.Ents[id]
	if e == nil {
		return "", 0, nil
	}
	return e.Def, e.Health, structureOf(e)
}

func TestSitePlaceFootprintAndOneDock(t *testing.T) {
	st := openTestStore(t)
	kade, _, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, c := joinToken(t, s, url, kade)
	pos, f := siteSpot(t, s)
	target := placeTarget(s, pos, f)
	standAt(s, c, pos, f, sim.Vec{})

	// A dock is never put down by hand.
	give(c, store.Stack{Item: "structure.dock", Qty: 1})
	if r := refusal(t, a.cmd(t, 1, protocol.OpPlace, `{"item":"structure.dock"}`)); r != "unknown_item" {
		t.Fatalf("placing a dock: %s", r)
	}

	// A bench 2.5 m from the centre is inside the 3 m footprint.
	up := terrain.Normalize(target)
	right := terrain.Cross(up, f)
	s.mu.Lock()
	bench := s.newStructureEnt("structure.bench", ground(s, target.Add(right.Scale(2.5))),
		&structureState{Key: "near", Owner: "someone", OwnerName: "Someone"})
	s.world.Add(bench)
	s.mu.Unlock()
	give(c, store.Stack{Item: "site.dock", Qty: 1})
	if r := refusal(t, a.cmd(t, 2, protocol.OpPlace, `{"item":"site.dock"}`)); r != "no_room" {
		t.Fatalf("2.5 m from a bench: %s", r)
	}
	if count(c, "site.dock") != 1 {
		t.Fatal("a refused place took the site")
	}
	s.mu.Lock()
	s.world.Remove(bench.ID)
	// Five benches already placed do not stop a site: the cap is theirs.
	for i := 0; i < 5; i++ {
		s.world.Add(s.newStructureEnt("structure.bench", ground(s, sim.Vec{0, 0, 1}),
			&structureState{Key: fmt.Sprintf("cap%d", i), Owner: kade, OwnerName: "Kade"}))
	}
	s.mu.Unlock()

	id := placedID(t, a.cmdOK(t, 3, protocol.OpPlace, `{"item":"site.dock"}`))
	def, health, sst := entOf(s, id)
	if def != "site.dock" || health != 0 || sst == nil || sst.Owner != kade || sst.Stacks == nil {
		t.Fatalf("placed site %s health %d state %+v", def, health, sst)
	}
	s.mu.Lock()
	if got := string(spawnData(s.world.Ents[id])); got != "site.dock\x00Kade" {
		s.mu.Unlock()
		t.Fatalf("spawn data %q", got)
	}
	s.mu.Unlock()

	// A second site: one site-or-dock per character.
	give(c, store.Stack{Item: "site.dock", Qty: 1})
	if r := refusal(t, a.cmd(t, 4, protocol.OpPlace, `{"item":"site.dock"}`)); r != "too_many" {
		t.Fatalf("second site: %s", r)
	}
	// Still too_many once it is a dock.
	s.mu.Lock()
	s.world.Ents[id].Def = "structure.dock"
	s.mu.Unlock()
	if r := refusal(t, a.cmd(t, 5, protocol.OpPlace, `{"item":"site.dock"}`)); r != "too_many" {
		t.Fatalf("site beside a dock: %s", r)
	}
}

// spawnSwap reads ws until it has seen a despawn of id followed by a spawn
// of id, and returns that spawn's data.
func spawnSwap(t *testing.T, ws *wsClient, id uint32) string {
	t.Helper()
	gone := false
	for i := 0; i < 2000; i++ {
		typ, payload := ws.next(t)
		switch typ {
		case protocol.MsgDespawn:
			if d, err := protocol.DecodeDespawn(payload); err == nil && d.EntityID == id {
				gone = true
			}
		case protocol.MsgSpawn:
			if sp, err := protocol.DecodeSpawn(payload); err == nil && sp.EntityID == id {
				if !gone {
					t.Fatalf("spawn of %d before its despawn", id)
				}
				if sp.EntityType != protocol.EntityTypeStructure {
					t.Fatalf("respawned as type %d", sp.EntityType)
				}
				return string(sp.Data)
			}
		}
	}
	t.Fatalf("no despawn+spawn of %d", id)
	return ""
}

func TestDeliverBillPartyAndCompletion(t *testing.T) {
	st := openTestStore(t)
	kade, ana, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, ca := joinToken(t, s, url, kade)
	b, cb := joinToken(t, s, url, ana)
	pos, f := siteSpot(t, s)
	target := placeTarget(s, pos, f)
	standAt(s, ca, pos, f, sim.Vec{})
	give(ca, store.Stack{Item: "site.dock", Qty: 1})
	site := placedID(t, a.cmdOK(t, 1, protocol.OpPlace, `{"item":"site.dock"}`))
	standAt(s, ca, pos, f, aimLook(pos, target))
	bpos := placeTarget(s, target, f)
	standAt(s, cb, bpos, f.Scale(-1), aimLook(bpos, target))
	body := func(item string, qty int) string {
		return fmt.Sprintf(`{"structure":%d,"item":%q,"qty":%d}`, site, item, qty)
	}

	// Not a site: status 5.
	if r := a.cmd(t, 2, protocol.OpDeliver, `{"structure":999999,"item":"mat.core","qty":1}`); r.Status != protocol.StatusNotFound {
		t.Fatalf("unknown: status %d", r.Status)
	}
	// A stranger: not_yours, peek included.
	give(cb, store.Stack{Item: "mat.wiring", Qty: 24})
	if r := refusal(t, b.cmd(t, 1, protocol.OpDeliver, body("", 0))); r != "not_yours" {
		t.Fatalf("stranger peek: %s", r)
	}
	if r := refusal(t, b.cmd(t, 2, protocol.OpDeliver, body("mat.wiring", 24))); r != "not_yours" {
		t.Fatalf("stranger: %s", r)
	}
	// The owner's peek: the whole bill, 0 %.
	got := deliverOf(t, a.cmdOK(t, 3, protocol.OpDeliver, body("", 0)))
	if got.Pct != 0 || len(got.Bill) != 4 || billLeft(got.Bill, "mat.plate.steel") != 60 || billLeft(got.Bill, "mat.core") != 4 {
		t.Fatalf("peek %+v", got)
	}
	if r := refusal(t, a.cmd(t, 4, protocol.OpDeliver, body("mat.parts", 1))); r != "not_wanted" {
		t.Fatalf("not in the bill: %s", r)
	}
	if r := refusal(t, a.cmd(t, 5, protocol.OpDeliver, body("mat.plate.steel", 5))); r != "not_owned" {
		t.Fatalf("none carried: %s", r)
	}
	if r := refusal(t, a.cmd(t, 6, protocol.OpDeliver, body("mat.plate.steel", -1))); r != "bad_qty" {
		t.Fatalf("negative: %s", r)
	}

	// 70 plates offered, 60 wanted: clamped, 10 stay in the bag.
	give(ca, store.Stack{Item: "mat.plate.steel", Qty: 50}, store.Stack{Item: "mat.plate.steel", Qty: 20},
		store.Stack{Item: "mat.ingot.iron", Qty: 20}, store.Stack{Item: "mat.core", Qty: 4})
	got = deliverOf(t, a.cmdOK(t, 7, protocol.OpDeliver, body("mat.plate.steel", 100)))
	if billLeft(got.Bill, "mat.plate.steel") != 0 || billLeft(got.Bill, "mat.wiring") != 24 || got.Pct != 60*100/108 {
		t.Fatalf("after plates %+v", got)
	}
	if count(ca, "mat.plate.steel") != 10 {
		t.Fatalf("bag plates %d, want 10", count(ca, "mat.plate.steel"))
	}
	if _, h, _ := entOf(s, site); h != 55 {
		t.Fatalf("row health %d, want 55", h)
	}
	waitRows(t, st, func(r []store.Structure) bool {
		return len(r) == 1 && len(r[0].Stacks) == 1 && r[0].Stacks[0] == (store.Stack{Item: "mat.plate.steel", Qty: 60})
	})
	if r := refusal(t, a.cmd(t, 8, protocol.OpDeliver, body("mat.plate.steel", 1))); r != "not_wanted" {
		t.Fatalf("a full line: %s", r)
	}

	// In the party, Ana's wiring counts.
	a.cmdOK(t, 9, protocol.OpPartyInvite, fmt.Sprintf(`{"target":%d}`, b.id))
	b.event(t, protocol.EventPartyInvited)
	b.cmdOK(t, 3, protocol.OpPartyRespond, `{"accept":true}`)
	got = deliverOf(t, b.cmdOK(t, 4, protocol.OpDeliver, body("mat.wiring", 24)))
	if got.Pct != 84*100/108 || count(cb, "mat.wiring") != 0 {
		t.Fatalf("party delivery %+v", got)
	}
	got = deliverOf(t, a.cmdOK(t, 10, protocol.OpDeliver, body("mat.ingot.iron", 20)))
	if got.Pct != 104*100/108 {
		t.Fatalf("after iron %+v", got)
	}
	xp0 := skillXP(ca, "construction")

	// The last unit: the same id becomes the dock.
	got = deliverOf(t, a.cmdOK(t, 11, protocol.OpDeliver, body("mat.core", 4)))
	if got.Pct != 100 || billLeft(got.Bill, "mat.core") != 0 {
		t.Fatalf("completion %+v", got)
	}
	// Ana's socket has not been read since her delivery: the swap is on it.
	if data := spawnSwap(t, b.ws, site); data != "structure.dock\x00Kade" {
		t.Fatalf("swap spawn data %q", data)
	}
	def, health, sst := entOf(s, site)
	if def != "structure.dock" || health != 0 || sst.Stacks != nil {
		t.Fatalf("after the swap %s health %d stacks %v", def, health, sst.Stacks)
	}
	waitRows(t, st, func(r []store.Structure) bool {
		return len(r) == 1 && r[0].Item == "structure.dock" && len(r[0].Stacks) == 0
	})
	// Construction XP Σ value × qty / 2 to the last deliverer.
	want := int64((60*34 + 24*20 + 20*14 + 4*90) / 2)
	for deadline := time.Now().Add(3 * time.Second); skillXP(ca, "construction")-xp0 != want; time.Sleep(50 * time.Millisecond) {
		if time.Now().After(deadline) {
			t.Fatalf("construction XP +%d, want +%d", skillXP(ca, "construction")-xp0, want)
		}
	}
	if skillXP(cb, "construction") != 0 {
		t.Fatal("XP went to a deliverer who did not finish it")
	}
	// A dock is no site.
	if r := a.cmd(t, 12, protocol.OpDeliver, body("mat.core", 1)); r.Status != protocol.StatusNotFound {
		t.Fatalf("deliver to a dock: status %d", r.Status)
	}
	if r := refusal(t, a.cmd(t, 13, protocol.OpChestTake, body("", 0))); r != "wrong_station" {
		t.Fatalf("a dock as a chest: %s", r)
	}
}

func skillXP(c *client, skill string) int64 {
	var n int64
	c.ident.Mutate(func(p *store.Player) { n = p.Skills.XP[skill] })
	return n
}

// placeDock injects owner's finished dock at the target of a body at pos
// facing f, the way a completed site leaves it.
func placeDock(s *Server, pos, f sim.Vec, owner, name string) uint32 {
	target := placeTarget(s, pos, f)
	s.mu.Lock()
	defer s.mu.Unlock()
	e := s.newStructureEnt("structure.dock", target, &structureState{Key: "dock-" + owner, Owner: owner, OwnerName: name,
		Yaw: yawOf(f.Scale(-1), terrain.Normalize(target))})
	s.world.Add(e)
	return e.ID
}

// ownedRovers are the rovers owner holds in the world.
func ownedRovers(s *Server, owner string) []*sim.Ent {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []*sim.Ent
	for _, e := range s.world.Ents {
		if v, ok := e.Data.(*sim.VehicleState); ok && v.Owner == owner {
			out = append(out, e)
		}
	}
	return out
}

func TestDockCraftsRover(t *testing.T) {
	st := openTestStore(t)
	kade, _, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, c := joinToken(t, s, url, kade)
	pos, f := siteSpot(t, s)
	target := placeTarget(s, pos, f)
	dock := placeDock(s, pos, f, kade, "Kade")
	standAt(s, c, pos, f, aimLook(pos, target))
	c.ident.Mutate(func(p *store.Player) {
		if p.Skills.XP == nil {
			p.Skills.XP = map[string]int64{}
		}
		p.Skills.XP["construction"] = skills.PointsForLevel(10)
	})
	give(c, store.Stack{Item: "mat.plate.steel", Qty: 40}, store.Stack{Item: "mat.wiring", Qty: 16},
		store.Stack{Item: "mat.ingot.iron", Qty: 20})
	craft := fmt.Sprintf(`{"npc":%d,"recipe":"recipe.vehicle.rover","qty":1}`, dock)

	// The world rover is nobody's.
	if n := len(ownedRovers(s, "")); n != 1 {
		t.Fatalf("%d unowned rovers, want the world's one", n)
	}
	a.cmdOK(t, 1, protocol.OpCraft, craft)
	s.mu.Lock()
	c.craft.ticks = 1 // the 30 s channel is the craft tests' business
	s.mu.Unlock()
	if e := nextCraftEnd(t, a); e.Reason != "done" || e.Item != "vehicle.rover" {
		t.Fatalf("craft at the dock: %+v", e)
	}
	if count(c, "vehicle.rover") != 1 {
		t.Fatal("no rover in the bag")
	}
	var rovers []*sim.Ent
	for deadline := time.Now().Add(2 * time.Second); len(rovers) == 0; time.Sleep(20 * time.Millisecond) {
		if time.Now().After(deadline) {
			t.Fatal("no owned rover spawned")
		}
		rovers = ownedRovers(s, kade)
	}
	if len(rovers) != 1 {
		t.Fatalf("%d owned rovers", len(rovers))
	}
	r := rovers[0]
	if r.Kind != sim.EntityKind(protocol.EntityTypeVehicle) || r.Def != "vehicle" {
		t.Fatalf("rover ent kind %d def %q", r.Kind, r.Def)
	}
	d := sim.Vec(r.Pos).Sub(target)
	if d.Len() > 8 {
		t.Fatalf("rover %.1f m from the dock, want <= 8", d.Len())
	}
	// In front of the dock (its forward points back at the placer), facing
	// away from it.
	fwd := yawForward(yawOf(f.Scale(-1), terrain.Normalize(target)), terrain.Normalize(target))
	if d.Dot(fwd) < 4 {
		t.Fatalf("rover %.1f m along the dock's forward, want ~5.5", d.Dot(fwd))
	}
	if rf := sim.Rotate(sim.Quat(r.Quat), sim.Vec{0, 0, 1}); rf.Dot(fwd) < 0.9 {
		t.Fatalf("rover forward %v, want along the dock's %v", rf, fwd)
	}

	// One per owner: the second unit has no room in the bag.
	if r := refusal(t, a.cmd(t, 2, protocol.OpCraft, craft)); r != "no_space" {
		t.Fatalf("second rover: %s", r)
	}
	// A sync is idempotent; one gone from the world comes back on the next
	// (the join hook).
	s.syncOwnedVehicles(c)
	s.syncOwnedVehicles(c)
	if n := len(ownedRovers(s, kade)); n != 1 {
		t.Fatalf("%d rovers after two syncs", n)
	}
	s.mu.Lock()
	s.world.Remove(r.ID)
	s.mu.Unlock()
	s.syncOwnedVehicles(c)
	if n := len(ownedRovers(s, kade)); n != 1 {
		t.Fatalf("%d rovers after a resync", n)
	}
	if !hasVehicleItem(c.ident.Snapshot(), "vehicle.rover") {
		t.Fatal("hasVehicleItem misses the rover")
	}
}

// With no dock an owned rover parks beside the world rover, not on it.
func TestOwnedRoverWithoutDock(t *testing.T) {
	s, _ := newTestServer(t)
	s.ensureVehicle("someone", "vehicle.rover")
	s.ensureVehicle("other", "vehicle.rover")
	world := ownedRovers(s, "")
	mine, theirs := ownedRovers(s, "someone"), ownedRovers(s, "other")
	if len(world) != 1 || len(mine) != 1 || len(theirs) != 1 {
		t.Fatalf("rovers: world %d, someone %d, other %d", len(world), len(mine), len(theirs))
	}
	for _, p := range [][2]*sim.Ent{{world[0], mine[0]}, {world[0], theirs[0]}, {mine[0], theirs[0]}} {
		if d := sim.Vec(p[0].Pos).Sub(sim.Vec(p[1].Pos)).Len(); d < 6 {
			t.Fatalf("two rovers %.1f m apart", d)
		}
	}
}

func TestDemolish(t *testing.T) {
	st := openTestStore(t)
	kade, ana, _ := structureRows(t, st)
	s, url := newStrictWorld(t, st, false)
	a, ca := joinToken(t, s, url, kade)
	b, cb := joinToken(t, s, url, ana)
	pos, f := siteSpot(t, s)
	target := placeTarget(s, pos, f)
	standAt(s, ca, pos, f, sim.Vec{})
	give(ca, store.Stack{Item: "site.dock", Qty: 1}, store.Stack{Item: "mat.core", Qty: 4})
	site := placedID(t, a.cmdOK(t, 1, protocol.OpPlace, `{"item":"site.dock"}`))
	standAt(s, ca, pos, f, aimLook(pos, target))
	a.cmdOK(t, 2, protocol.OpDeliver, fmt.Sprintf(`{"structure":%d,"item":"mat.core","qty":2}`, site))
	pick := fmt.Sprintf(`{"structure":%d}`, site)

	bpos := placeTarget(s, target, f)
	standAt(s, cb, bpos, f.Scale(-1), aimLook(bpos, target))
	if r := refusal(t, b.cmd(t, 1, protocol.OpPickUp, pick)); r != "not_yours" {
		t.Fatalf("stranger demolish: %s", r)
	}

	// A half-delivered site demolishes: 3 s, nothing back, not even the
	// cores (and a full bag does not matter).
	full := make([]store.Stack, 0, 20)
	for i := 0; i < 20; i++ {
		full = append(full, store.Stack{Item: "ammo.cell", Qty: 1})
	}
	full[0] = store.Stack{Item: "mat.core", Qty: 2}
	give(ca, full...)
	t0 := time.Now()
	res := a.cmdOK(t, 3, protocol.OpPickUp, pick)
	if !strings.Contains(string(res.Data), `"seconds":3`) {
		t.Fatalf("demolish result %s", res.Data)
	}
	e := nextPickUpEnd(t, a)
	if el := time.Since(t0); el < 2800*time.Millisecond {
		t.Fatalf("demolish took %s, want 3 s", el)
	}
	if !e.OK || e.Structure != site || len(e.Inventory) != 20 || count(ca, "mat.core") != 2 || count(ca, "site.dock") != 0 {
		t.Fatalf("demolish end %+v", e)
	}
	if def, _, _ := entOf(s, site); def != "" {
		t.Fatal("the site stayed in the world")
	}
	waitRows(t, st, func(r []store.Structure) bool { return len(r) == 0 })

	// The slot is free again (Ana steps off the footprint).
	standAt(s, cb, bpos.Add(bpos.Sub(target).Scale(4)), f, sim.Vec{})
	give(ca, store.Stack{Item: "site.dock", Qty: 1})
	standAt(s, ca, pos, f, sim.Vec{})
	placedID(t, a.cmdOK(t, 4, protocol.OpPlace, `{"item":"site.dock"}`))
}

// A half-built site comes back on a fresh server with its delivered bill
// and its percent in the row health.
func TestSiteSurvivesRestart(t *testing.T) {
	ctx := context.Background()
	st := openTestStore(t)
	kade, _, _ := structureRows(t, st)
	w0, err := New(terrain.Generate(1337), 1337)
	if err != nil {
		t.Fatal(err)
	}
	p := ground(w0, sim.Vec{0, 1, 0})
	row := store.Structure{ID: "site1", Owner: kade, OwnerName: "Kade", Item: "site.dock", Pos: p, PlacedMs: 1,
		Stacks: []store.Stack{{Item: "mat.plate.steel", Qty: 30}, {Item: "mat.core", Qty: 4}}}
	if err := st.PutStructure(ctx, &row); err != nil {
		t.Fatal(err)
	}
	s, _ := newStrictWorld(t, st, false)
	s.mu.Lock()
	var got *sim.Ent
	for _, e := range s.world.Ents {
		if sst := structureOf(e); sst != nil && sst.Key == "site1" {
			got = e
		}
	}
	s.mu.Unlock()
	if got == nil || got.Def != "site.dock" || got.Health != 34*100/108 {
		t.Fatalf("restored site %+v", got)
	}
	if sst := structureOf(got); len(sst.Stacks) != 2 || sst.Stacks[0].Qty != 30 {
		t.Fatalf("restored bill %+v", sst.Stacks)
	}
}

// Vex no longer sells the ship: the dock makes it.
func TestShopSellsNoShip(t *testing.T) {
	s, url := newTestServer(t)
	a := dialWS(t, url)
	a.sendFrame(t, protocol.EncodeHello(protocol.Hello{ClientVer: protocol.VersionPhase2, Name: "browser"}))
	ha, err := protocol.DecodeHelloAck(a.nextOf(t, protocol.MsgHelloAck))
	if err != nil {
		t.Fatal(err)
	}
	s.mu.Lock()
	var npcID uint32
	var npcPos sim.Vec
	for _, e := range s.worldEnts {
		if e.Def == "npc.quartermaster" {
			npcID, npcPos = e.ID, sim.Vec(e.Pos)
		}
	}
	s.mu.Unlock()
	teleportNear(s, ha.EntityID, npcPos, 2)
	aimAt(t, s, a, ha.EntityID, npcPos)
	r := sendCmd(t, a, 1, protocol.OpShopList, fmt.Sprintf(`{"npc":%d}`, npcID))
	if r.Status != protocol.StatusOK {
		t.Fatalf("shop_list status %d %s", r.Status, r.Data)
	}
	if strings.Contains(string(r.Data), `"ship.v1"`) || strings.Contains(string(r.Data), `"vehicle.rover"`) {
		t.Fatalf("Vex still sells a vehicle: %s", r.Data)
	}
}
