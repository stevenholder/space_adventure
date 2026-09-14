package sim

import (
	"reflect"
	"testing"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/store"
)

func testRegistry() *defs.Registry {
	return &defs.Registry{
		InvSlots: 2,
		Items: map[string]defs.Item{
			"weapon.pulse": {ID: "weapon.pulse", Slot: "primary", StackMax: 1},
			"ammo.cell":    {ID: "ammo.cell", Slot: "", StackMax: 300},
		},
	}
}

func testNPC() defs.NPC {
	return defs.NPC{
		ID:   "shopkeeper",
		Kind: "shop",
		Stock: []struct {
			Item  string `json:"item"`
			Price int64  `json:"price"`
		}{
			{Item: "weapon.pulse", Price: 250},
			{Item: "ammo.cell", Price: 2},
		},
	}
}

func clonePlayer(p *store.Player) *store.Player {
	c := *p
	c.Inventory = append([]store.Stack(nil), p.Inventory...)
	if p.Equipped != nil {
		c.Equipped = make(map[string]string, len(p.Equipped))
		for k, v := range p.Equipped {
			c.Equipped[k] = v
		}
	}
	return &c
}

func TestBuySuccess(t *testing.T) {
	reg := testRegistry()
	npc := testNPC()
	p := &store.Player{Credits: 1000}

	if err := Buy(p, npc, "weapon.pulse", 1, reg); err != nil {
		t.Fatalf("Buy: unexpected error %v", err)
	}
	if p.Credits != 750 {
		t.Fatalf("Credits = %d, want 750", p.Credits)
	}
	if len(p.Inventory) != 1 || p.Inventory[0] != (store.Stack{Item: "weapon.pulse", Qty: 1}) {
		t.Fatalf("Inventory = %+v, want [{weapon.pulse 1}]", p.Inventory)
	}
}

func TestBuyRefusalReasons(t *testing.T) {
	reg := testRegistry()
	npc := testNPC()

	cases := []struct {
		name   string
		item   string
		qty    int
		npc    defs.NPC
		reason string
	}{
		{"bad_qty_zero", "weapon.pulse", 0, npc, ReasonBadQty},
		{"bad_qty_too_big", "weapon.pulse", 1001, npc, ReasonBadQty},
		{"unknown_item", "weapon.laser", 1, npc, ReasonUnknownItem},
		{"no_stock", "ammo.cell", 1, defs.NPC{Kind: "shop", Stock: nil}, ReasonNoStock},
		{"not_a_shop", "weapon.pulse", 1, defs.NPC{Kind: "vendor", Stock: npc.Stock}, ReasonNoStock},
		{"insufficient_credits", "weapon.pulse", 1, npc, ReasonInsufficientCredits},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			p := &store.Player{Credits: 100}
			before := clonePlayer(p)

			err := Buy(p, c.npc, c.item, c.qty, reg)
			if err == nil {
				t.Fatalf("Buy: expected error, got nil")
			}
			re, ok := err.(RefusalError)
			if !ok {
				t.Fatalf("Buy: error %v is not a RefusalError", err)
			}
			if re.Reason != c.reason {
				t.Fatalf("Reason = %q, want %q", re.Reason, c.reason)
			}
			if !reflect.DeepEqual(p, before) {
				t.Fatalf("player mutated on failed Buy: got %+v, want %+v", p, before)
			}
		})
	}
}

func TestBuyNoSpace(t *testing.T) {
	reg := testRegistry()
	npc := testNPC()
	p := &store.Player{Credits: 100000}

	// Fill both inventory slots with unrelated stacks.
	p.Inventory = []store.Stack{{Item: "junk.a", Qty: 1}, {Item: "junk.b", Qty: 1}}
	before := clonePlayer(p)

	err := Buy(p, npc, "ammo.cell", 1, reg)
	if err == nil {
		t.Fatalf("Buy: expected no_space error, got nil")
	}
	re, ok := err.(RefusalError)
	if !ok || re.Reason != ReasonNoSpace {
		t.Fatalf("Reason = %v, want %q", err, ReasonNoSpace)
	}
	if !reflect.DeepEqual(p, before) {
		t.Fatalf("player mutated on failed Buy: got %+v, want %+v", p, before)
	}
}

func TestBuyNegativeQtyLeavesCreditsUnchanged(t *testing.T) {
	reg := testRegistry()
	npc := testNPC()
	p := &store.Player{Credits: 500}
	before := clonePlayer(p)

	err := Buy(p, npc, "ammo.cell", -1, reg)
	if err == nil {
		t.Fatalf("Buy: expected error for qty=-1, got nil")
	}
	re, ok := err.(RefusalError)
	if !ok || re.Reason != ReasonBadQty {
		t.Fatalf("Reason = %v, want %q", err, ReasonBadQty)
	}
	if p.Credits != 500 {
		t.Fatalf("Credits = %d, want unchanged 500", p.Credits)
	}
	if !reflect.DeepEqual(p, before) {
		t.Fatalf("player mutated on failed Buy: got %+v, want %+v", p, before)
	}
}

func TestBuyStacksThenRefusesNoSpace(t *testing.T) {
	reg := testRegistry()
	npc := testNPC()
	p := &store.Player{Credits: 100000}

	// StackMax for ammo.cell is 300; buy 150 twice to fill the stack.
	if err := Buy(p, npc, "ammo.cell", 150, reg); err != nil {
		t.Fatalf("Buy #1: unexpected error %v", err)
	}
	if err := Buy(p, npc, "ammo.cell", 150, reg); err != nil {
		t.Fatalf("Buy #2: unexpected error %v", err)
	}
	if len(p.Inventory) != 1 || p.Inventory[0].Qty != 300 {
		t.Fatalf("Inventory = %+v, want one stack of 300", p.Inventory)
	}

	before := clonePlayer(p)
	err := Buy(p, npc, "ammo.cell", 1, reg)
	if err == nil {
		t.Fatalf("Buy #3: expected no_space error, got nil")
	}
	re, ok := err.(RefusalError)
	if !ok || re.Reason != ReasonNoSpace {
		t.Fatalf("Reason = %v, want %q", err, ReasonNoSpace)
	}
	if !reflect.DeepEqual(p, before) {
		t.Fatalf("player mutated on failed Buy: got %+v, want %+v", p, before)
	}
}

func TestEquipSuccess(t *testing.T) {
	reg := testRegistry()
	p := &store.Player{Inventory: []store.Stack{{Item: "weapon.pulse", Qty: 1}}}

	if err := Equip(p, "primary", "weapon.pulse", reg); err != nil {
		t.Fatalf("Equip: unexpected error %v", err)
	}
	if p.Equipped["primary"] != "weapon.pulse" {
		t.Fatalf("Equipped[primary] = %q, want weapon.pulse", p.Equipped["primary"])
	}
}

func TestEquipRefusals(t *testing.T) {
	reg := testRegistry()

	t.Run("unknown_item", func(t *testing.T) {
		p := &store.Player{}
		err := Equip(p, "primary", "weapon.laser", reg)
		assertRefusal(t, err, ReasonUnknownItem)
	})

	t.Run("not_owned", func(t *testing.T) {
		p := &store.Player{}
		err := Equip(p, "primary", "weapon.pulse", reg)
		assertRefusal(t, err, ReasonNotOwned)
	})

	t.Run("wrong_slot", func(t *testing.T) {
		p := &store.Player{Inventory: []store.Stack{{Item: "ammo.cell", Qty: 10}}}
		err := Equip(p, "primary", "ammo.cell", reg)
		assertRefusal(t, err, ReasonWrongSlot)
	})
}

func assertRefusal(t *testing.T, err error, reason string) {
	t.Helper()
	if err == nil {
		t.Fatalf("expected error, got nil")
	}
	re, ok := err.(RefusalError)
	if !ok || re.Reason != reason {
		t.Fatalf("Reason = %v, want %q", err, reason)
	}
}

func TestAddItem(t *testing.T) {
	reg := testRegistry()
	p := &store.Player{}

	if err := AddItem(p, "ammo.cell", 120, reg); err != nil {
		t.Fatalf("AddItem: unexpected error %v", err)
	}
	if len(p.Inventory) != 1 || p.Inventory[0] != (store.Stack{Item: "ammo.cell", Qty: 120}) {
		t.Fatalf("Inventory = %+v, want [{ammo.cell 120}]", p.Inventory)
	}
}

// Phase 11 Commerce: BuyAt rounds the unit price to the nearest credit.
func TestBuyAtDiscount(t *testing.T) {
	reg := testRegistry()
	npc := testNPC()
	p := &store.Player{Credits: 1000}
	if err := BuyAt(p, npc, "weapon.pulse", 1, reg, 0.902); err != nil {
		t.Fatal(err)
	}
	if p.Credits != 1000-226 { // 250 × 0.902 = 225.5 → 226
		t.Fatalf("Credits = %d, want 774", p.Credits)
	}
}
