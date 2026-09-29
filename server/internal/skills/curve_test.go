package skills

import "testing"

// The landmarks, pinned forever (C79). If these move, the curve is not
// RuneScape's and every player's level is wrong.
func TestCurveLandmarks(t *testing.T) {
	want := map[int]int64{
		1:  0,
		2:  83,
		10: 1154,
		25: 7842,
		50: 101333,
		75: 1210421,
		92: 6517253,
		99: 13034431,
	}
	for level, xp := range want {
		if got := PointsForLevel(level); got != xp {
			t.Errorf("PointsForLevel(%d) = %d, want %d", level, got, xp)
		}
	}
	// Level 92 is the halfway point of 99 — folklore that is true to
	// within 38 XP (6,517,253 vs an exact half of 6,517,215). Assert the
	// spirit: 92 sits within a rounding error of half, 91 and 93 do not.
	half := PointsForLevel(99) / 2
	if d := PointsForLevel(92) - half; d < -100 || d > 100 {
		t.Errorf("92 is %d XP from the halfway point, want within 100", d)
	}
	if PointsForLevel(91) > half-1000 || PointsForLevel(93) < half+1000 {
		t.Errorf("91/93 are too close to half: %d / %d vs %d",
			PointsForLevel(91), PointsForLevel(93), half)
	}
}

func TestLevelForXPInverts(t *testing.T) {
	for l := 1; l <= MaxLevel; l++ {
		at := PointsForLevel(l)
		if got := LevelForXP(at); got != l {
			t.Errorf("LevelForXP(%d) = %d, want %d (exact boundary)", at, got, l)
		}
		if l > 1 {
			if got := LevelForXP(at - 1); got != l-1 {
				t.Errorf("LevelForXP(%d) = %d, want %d (one below)", at-1, got, l-1)
			}
		}
	}
	if got := LevelForXP(999999999); got != MaxLevel {
		t.Errorf("overflow xp: level %d, want %d", got, MaxLevel)
	}
}
