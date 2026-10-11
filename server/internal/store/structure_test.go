package store

import (
	"context"
	"math"
	"reflect"
	"testing"
)

func TestStructureRoundTrip(t *testing.T) {
	eachEngine(t, func(t *testing.T, s *Store) {
		ctx := context.Background()
		if got, err := s.ListStructures(ctx); err != nil || len(got) != 0 {
			t.Fatalf("empty ListStructures = %v, %v", got, err)
		}
		chest := &Structure{ID: "s_2_b", Owner: "tok-a", OwnerName: "Kade", Item: "structure.chest",
			Pos: [3]float64{1.5, 150.25, -3}, Yaw: 1.25, PlacedMs: 2,
			Stacks: []Stack{{Item: "mat.parts", Qty: 7}}}
		bench := &Structure{ID: "s_1_a", Owner: "tok-b", OwnerName: "Ana", Item: "structure.bench",
			Pos: [3]float64{0, 150, 0}, PlacedMs: 1}
		for _, st := range []*Structure{chest, bench} {
			if err := s.PutStructure(ctx, st); err != nil {
				t.Fatal(err)
			}
		}
		got, err := s.ListStructures(ctx)
		if err != nil || len(got) != 2 {
			t.Fatalf("ListStructures = %v, %v", got, err)
		}
		if got[0].ID != "s_1_a" || got[1].ID != "s_2_b" {
			t.Fatalf("order %s, %s; want oldest first", got[0].ID, got[1].ID)
		}
		if len(got[0].Stacks) != 0 || got[0].Stacks == nil {
			t.Fatalf("a station's stacks = %#v, want empty", got[0].Stacks)
		}
		c := got[1]
		// REAL is float4 on Postgres: compare positions to a centimetre.
		for i := range c.Pos {
			if math.Abs(c.Pos[i]-chest.Pos[i]) > 0.01 {
				t.Fatalf("pos %v, want %v", c.Pos, chest.Pos)
			}
		}
		if c.Owner != "tok-a" || c.OwnerName != "Kade" || c.Item != "structure.chest" ||
			math.Abs(c.Yaw-1.25) > 1e-4 || c.PlacedMs != 2 || !reflect.DeepEqual(c.Stacks, chest.Stacks) {
			t.Fatalf("round trip %+v", c)
		}

		// Upsert: a chest change rewrites the stacks.
		chest.Stacks = []Stack{{Item: "mat.parts", Qty: 3}, {Item: "mat.scrap", Qty: 1}}
		if err := s.PutStructure(ctx, chest); err != nil {
			t.Fatal(err)
		}
		got, _ = s.ListStructures(ctx)
		if len(got) != 2 || !reflect.DeepEqual(got[1].Stacks, chest.Stacks) {
			t.Fatalf("after upsert %+v", got)
		}

		if err := s.DeleteStructure(ctx, "s_1_a"); err != nil {
			t.Fatal(err)
		}
		if err := s.DeleteStructure(ctx, "absent"); err != nil {
			t.Fatalf("delete absent: %v", err)
		}
		if err := s.DeleteStructuresByOwner(ctx, "tok-a"); err != nil {
			t.Fatal(err)
		}
		if got, _ := s.ListStructures(ctx); len(got) != 0 {
			t.Fatalf("left %+v", got)
		}
	})
}

// Deleting a character or its account takes the structures it placed, in
// the same transaction; another owner's stay.
func TestStructureDeleteCascades(t *testing.T) {
	eachEngine(t, func(t *testing.T, s *Store) {
		ctx := context.Background()
		must := func(err error) {
			t.Helper()
			if err != nil {
				t.Fatal(err)
			}
		}
		must(s.CreateAccount(ctx, &Account{ID: "a", Email: "a@x", PwHash: "h", CreatedMs: 1}))
		must(s.PutPlayer(ctx, character("t1", "Kade", "a")))
		must(s.PutPlayer(ctx, character("t2", "Ana", "a")))
		must(s.PutPlayer(ctx, character("t3", "Other", "b")))
		for i, owner := range []string{"t1", "t1", "t2", "t3"} {
			must(s.PutStructure(ctx, &Structure{ID: "s" + string(rune('0'+i)), Owner: owner,
				OwnerName: owner, Item: "structure.bench", PlacedMs: int64(i)}))
		}
		owners := func() []string {
			got, err := s.ListStructures(ctx)
			must(err)
			var out []string
			for _, st := range got {
				out = append(out, st.Owner)
			}
			return out
		}

		must(s.DeletePlayer(ctx, "t1"))
		if got := owners(); !reflect.DeepEqual(got, []string{"t2", "t3"}) {
			t.Fatalf("after DeletePlayer(t1): owners %v", got)
		}
		if _, err := s.DeleteAccount(ctx, "a"); err != nil {
			t.Fatal(err)
		}
		if got := owners(); !reflect.DeepEqual(got, []string{"t3"}) {
			t.Fatalf("after DeleteAccount(a): owners %v", got)
		}
		if p, _ := s.GetPlayer(ctx, "t2"); p != nil {
			t.Fatal("t2 survived its account")
		}
	})
}
