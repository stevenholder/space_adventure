// Phase 8 — compass bearing math (ROADMAP task 6; C63). Engine-free so
// SimDump proves the arithmetic; the strip itself (UI/Compass.cs) only
// positions markers by these numbers.
//
// On a sphere there is no global north. The compass is therefore
// EGOCENTRIC: 0 is where you look, and a marker's bearing is the signed
// angle from your facing to the target, measured in your local tangent
// plane. That is also the only definition that never degenerates walking
// over a pole.

using System;

namespace SpaceAdventure.Game
{
    public static class Bearing
    {
        /// <summary>
        /// Signed angle in radians from `facing` to the direction of
        /// `target`, both projected into the tangent plane at `pos`
        /// (up = normalize(pos)). Positive = target is to the RIGHT.
        /// Returns NaN when degenerate (target overhead/at your feet or a
        /// zero facing) — callers hide the marker rather than lie.
        /// </summary>
        public static double To(Sim.Vec3 pos, Sim.Vec3 facing, Sim.Vec3 target)
        {
            Sim.Vec3 up = pos.Normalized();
            Sim.Vec3 f = facing.RejectFrom(up);
            Sim.Vec3 t = (target - pos).RejectFrom(up);
            if (f.Length < 1e-9 || t.Length < 1e-9) return double.NaN;
            f = f.Normalized();
            t = t.Normalized();

            double cos = Math.Clamp(Sim.Vec3.Dot(f, t), -1.0, 1.0);
            double ang = Math.Acos(cos);
            // Sign from the triple product: (f × t) · up > 0 means t is
            // counter-clockwise of f about up, i.e. to the LEFT.
            double sign = Sim.Vec3.Dot(Sim.Vec3.Cross(f, t), up);
            return sign > 0 ? -ang : ang;
        }
    }
}
