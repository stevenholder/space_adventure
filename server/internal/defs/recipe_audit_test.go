package defs

import (
	"strings"
	"testing"
)

// TestAuditRecipeFields pins the Phase 22 recipe audit: a skill that is not
// a skills.json id, a station that is not hand|bench|forge, or a channel of
// no seconds kills the load with the recipe's name.
func TestAuditRecipeFields(t *testing.T) {
	good := Recipe{ID: "recipe.x", Skill: "smithing", Station: "forge", Seconds: 4,
		Inputs: []ItemQty{{Item: "mat.a", Qty: 1}}, Output: ItemQty{Item: "mat.b", Qty: 1}}
	reg := func(r Recipe) *Registry {
		return &Registry{
			Skills:  []Skill{{ID: "smithing"}},
			Items:   map[string]Item{"mat.a": {ID: "mat.a"}, "mat.b": {ID: "mat.b"}},
			Recipes: map[string]Recipe{r.ID: r},
		}
	}
	if err := auditArtisan(reg(good)); err != nil {
		t.Fatalf("good recipe refused: %v", err)
	}
	for name, c := range map[string]struct {
		mut  func(r *Recipe)
		want string
	}{
		"skill":   {func(r *Recipe) { r.Skill = "smelting" }, `skill "smelting" unknown`},
		"station": {func(r *Recipe) { r.Station = "oven" }, `station "oven"`},
		"blank":   {func(r *Recipe) { r.Station = "" }, `station ""`},
		"seconds": {func(r *Recipe) { r.Seconds = 0 }, "seconds must be positive"},
	} {
		r := good
		c.mut(&r)
		err := auditArtisan(reg(r))
		if err == nil || !strings.Contains(err.Error(), c.want) || !strings.Contains(err.Error(), "recipe.x") {
			t.Errorf("%s: err = %v, want %q", name, err, c.want)
		}
	}
}
