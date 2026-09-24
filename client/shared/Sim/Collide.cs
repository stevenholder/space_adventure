// Static collider resolution — GDD "Static colliders", integrator steps 8-9.
//
// Ported from server/internal/sim/collide.go. Go is the reference for C40.

using System;

namespace SpaceAdventure.Sim
{
    public enum ColliderKind { Box = 0, Sphere = 1 }

    /// <summary>One static collider (PROTOCOL `colliders`).</summary>
    public struct Collider
    {
        public ColliderKind Kind;
        public Vec3 Center;
        /// <summary>Box half-extents; for a sphere only X is used, as the radius.</summary>
        public Vec3 Half;
        /// <summary>Orientation in (x, y, z, w) order. Ignored by spheres.</summary>
        public Quat Rot;
    }

    public static class Collide
    {
        public const double BodyRadius = 0.35;
        public const double BodySphereH = 0.9;
        private const double DegenEps = 1e-9;

        /// <summary>
        /// Integrator steps 8 and 9.
        ///
        /// The empty-list short-circuit is load-bearing, not an optimisation.
        /// Step 9 exists ONLY to repair a push-out that sank the feet below the
        /// terrain. Running it with no colliders re-seats whenever |pos| sits an
        /// ulp under the sampled radius, which re-rounds pos and perturbs the
        /// trajectory by ~1e-13 per tick. That is harmless against C5's 5% bar
        /// but it voids the byte-identical property, and a drift you cannot
        /// distinguish from noise is a drift you stop noticing.
        /// </summary>
        public static void ResolveColliders(
            ref Vec3 pos, ref Vec3 vel, Vec3 up, ref bool grounded,
            Collider[] cs, Func<Vec3, double> radiusAt)
        {
            if (cs == null || cs.Length == 0) return;

            Vec3 p = pos, v = vel, u = up;
            bool g = grounded;
            double cosMaxSlope = Math.Cos(TerrainField.MaxSlope);

            // Step 8 — in the order received. Order matters: two colliders can
            // push a body to different places depending which resolves first.
            Vec3 c = p + u * BodySphereH;
            for (int i = 0; i < cs.Length; i++)
            {
                if (!Nearest(c, BodyRadius, cs[i], u, out Vec3 n, out double depth)) continue;
                p = p + n * depth;
                c = p + u * BodySphereH;
                double d = Vec3.Dot(v, n);
                if (d < 0) v = v - n * d;
                if (Vec3.Dot(n, u) >= cosMaxSlope) g = true;
            }

            // Step 9 — re-seat if step 8 pushed the feet under the terrain.
            double r = radiusAt(p.Normalized());
            if (p.Length < r)
            {
                p = p.Normalized() * r;
                double d = Vec3.Dot(v, u);
                if (d < 0) v = v - u * d;
            }

            pos = p; vel = v; grounded = g;
        }

        private static bool Nearest(Vec3 c, double radius, Collider col, Vec3 up,
                                    out Vec3 n, out double depth)
        {
            // Box and anything unrecognised resolve as a box, matching Go.
            return col.Kind == ColliderKind.Sphere
                ? NearestSphere(c, radius, col, up, out n, out depth)
                : NearestBox(c, radius, col, up, out n, out depth);
        }

        private static bool NearestSphere(Vec3 c, double radius, Collider col, Vec3 up,
                                          out Vec3 n, out double depth)
        {
            double colRadius = col.Half.X;
            double combined = radius + colRadius;
            Vec3 diff = c - col.Center;
            double dist = diff.Length;
            if (dist >= combined) { n = Vec3.Zero; depth = 0; return false; }
            if (dist < DegenEps) { n = up; depth = combined; return true; }
            n = diff * (1 / dist); depth = combined - dist; return true;
        }

        private static bool NearestBox(Vec3 c, double radius, Collider col, Vec3 up,
                                       out Vec3 n, out double depth)
        {
            Quat q = col.Rot;
            Quat qConj = new Quat(-q.X, -q.Y, -q.Z, q.W);

            Vec3 local = Quat.Rotate(qConj, c - col.Center);
            Vec3 clamped = new Vec3(
                Clamp(local.X, -col.Half.X, col.Half.X),
                Clamp(local.Y, -col.Half.Y, col.Half.Y),
                Clamp(local.Z, -col.Half.Z, col.Half.Z));
            Vec3 closest = Quat.Rotate(q, clamped) + col.Center;

            Vec3 diff = c - closest;
            double dist = diff.Length;
            if (dist >= radius) { n = Vec3.Zero; depth = 0; return false; }
            if (dist < DegenEps)
            {
                // The sphere centre coincides with its closest point in the box
                // (e.g. exactly at the box centre): no defined exit normal.
                // Push out along up by the full body radius rather than
                // propagate a NaN into a position that later gets persisted.
                n = up; depth = radius; return true;
            }
            n = diff * (1 / dist); depth = radius - dist; return true;
        }

        private static double Clamp(double x, double lo, double hi) => x < lo ? lo : (x > hi ? hi : x);
    }
}
