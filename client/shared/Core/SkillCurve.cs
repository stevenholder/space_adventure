// Phase 11 — the XP curve, RuneScape's exactly (GDD "Skills"), mirrored
// from server/internal/skills/curve.go term for term: both ends compute
// levels from the formula and nothing ships a table. Engine-free so the
// headless self-test can pin the landmarks (83 / 101,333 / 13,034,431).

using System;

namespace SpaceAdventure.Game
{
    public static class SkillCurve
    {
        public const int MaxLevel = 99;

        /// <summary>Total XP required to BE `level`; 0 at or below 1.</summary>
        public static long PointsForLevel(int level)
        {
            if (level <= 1) return 0;
            if (level > MaxLevel) level = MaxLevel;
            long pts = 0;
            for (int l = 1; l < level; l++)
                pts += (long)(l + 300 * Math.Pow(2, l / 7.0)); // truncated per term, like Go
            return pts / 4;
        }

        /// <summary>The highest level `xp` meets.</summary>
        public static int LevelForXP(long xp)
        {
            for (int l = MaxLevel; l >= 2; l--)
                if (xp >= PointsForLevel(l)) return l;
            return 1;
        }
    }
}
