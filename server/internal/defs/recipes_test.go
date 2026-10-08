package defs

import (
	"os"
	"os/exec"
	"testing"
)

// recipes.json is generated (tools/gen_recipes.py from items.json and
// craft.json; GDD "The refinery"). A hand edit to it drifts from the rule
// table the moment someone regenerates, so the committed file must equal
// a fresh run: `--check` exits non-zero and prints the diff otherwise.
func TestRecipesAreGenerated(t *testing.T) {
	if _, err := exec.LookPath("python3"); err != nil {
		t.Skip("python3 not on PATH")
	}
	cmd := exec.Command("python3", "-I", "../../../tools/gen_recipes.py", "--check")
	cmd.Env = append(os.Environ(), "PYTHONDONTWRITEBYTECODE=1")
	out, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("recipes.json is stale or the generator failed: %v\n%s\n(run: python3 tools/gen_recipes.py)", err, out)
	}
}
