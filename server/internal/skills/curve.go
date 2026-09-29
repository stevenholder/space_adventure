// Package skills holds the XP curve — RuneScape's, exactly (docs/GDD.md
// "Skills"). Both ends of the wire compute levels from the same formula;
// nothing ships a table.
package skills

import "math"

// MaxLevel is the ladder's top. 99, as tradition demands.
const MaxLevel = 99

// PointsForLevel is the total XP required to BE level L:
//
//	points(L) = floor( Σ_{l=1}^{L−1} (l + 300·2^(l/7)) / 4 )
//
// computed in integer arithmetic over the summed floor terms, which is what
// reproduces the canonical table bit-for-bit (83 at 2; 101,333 at 50;
// 13,034,431 at 99; and 92 the halfway point of 99, to within the 38 XP
// the folklore rounds away).
func PointsForLevel(level int) int64 {
	if level <= 1 {
		return 0
	}
	if level > MaxLevel {
		level = MaxLevel
	}
	var pts int64
	for l := 1; l < level; l++ {
		pts += int64(float64(l) + 300*math.Pow(2, float64(l)/7))
	}
	return pts / 4
}

// LevelForXP inverts the curve: the highest level whose requirement xp
// meets. Linear scan — 98 iterations of trivial math, called on award
// batches, not per tick.
func LevelForXP(xp int64) int {
	for l := MaxLevel; l >= 2; l-- {
		if xp >= PointsForLevel(l) {
			return l
		}
	}
	return 1
}
