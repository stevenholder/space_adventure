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

        // Moving bodies as colliders -- collide.go "Moving bodies as
        // colliders", same constants. The client predicts its body against
        // the static colliders plus these, built from the bodies it draws.
        public const double RoverHalfX = 0.85, RoverHalfY = 0.55, RoverHalfZ = 1.12, RoverBoxH = 0.75;

        public const double ShipHalfX = 1.40, ShipHalfY = 1.30, ShipHalfZ = 3.60, ShipBoxH = 1.30;

        /// <summary>One sphere of a vehicle hull: offset in the vehicle frame (+Y up, +Z heading), radius.</summary>
        public readonly struct HullSphere
        {
            public readonly Vec3 Off;
            public readonly double R;
            public HullSphere(Vec3 off, double r) { Off = off; R = r; }
        }

        /// <summary>collide.go RoverHull / ShipHull, same numbers.</summary>
        public static readonly HullSphere[] RoverHull = { new HullSphere(new Vec3(0, 0.75, 0.55), 0.80), new HullSphere(new Vec3(0, 0.75, -0.55), 0.80) };
        public static readonly HullSphere[] ShipHull = { new HullSphere(new Vec3(0, 1.30, 2.40), 1.40), new HullSphere(new Vec3(0, 1.30, 0), 1.40), new HullSphere(new Vec3(0, 1.30, -2.40), 1.40) };

        /// <summary>
        /// collide.go ResolveHull, line for line: each hull sphere against each
        /// collider, in order; push out, drop inbound velocity.
        /// </summary>
        public static void ResolveHull(ref Vec3 pos, ref Vec3 vel, Quat q, HullSphere[] hull, Collider[] cs)
        {
            if (cs == null || cs.Length == 0) return;
            Vec3 up = pos.Normalized();
            foreach (HullSphere h in hull)
            {
                Vec3 off = Quat.Rotate(q, h.Off);
                for (int i = 0; i < cs.Length; i++)
                {
                    if (!Nearest(pos + off, h.R, cs[i], up, out Vec3 n, out double depth)) continue;
                    pos = pos + n * depth;
                    double d = Vec3.Dot(vel, n);
                    if (d < 0) vel = vel - n * d;
                }
            }
        }

        /// <summary>sim/props.go PropBoxes: solid props' model-space (min, max), same numbers.</summary>
        public static readonly System.Collections.Generic.Dictionary<string, (Vec3 Min, Vec3 Max)> PropBoxes =
            new System.Collections.Generic.Dictionary<string, (Vec3, Vec3)>
            {
                ["prop.barrel"] = (new Vec3(-0.32, 0, -0.32), new Vec3(0.32, 0.95, 0.32)),
                ["prop.barrels"] = (new Vec3(-0.65, 0, -0.86), new Vec3(0.65, 0.95, 0.49)),
                ["prop.generator"] = (new Vec3(-1.00, 0, -0.45), new Vec3(1.12, 1.34, 0.43)),
                ["prop.dish"] = (new Vec3(-0.95, 0, -0.93), new Vec3(0.95, 2.09, 0.66)),
                ["prop.loot.crate"] = (new Vec3(-0.22, 0, -0.17), new Vec3(0.22, 0.50, 0.17)),
            };

        /// <summary>A solid prop as a box (props.go PropCollider); false for walk-through dressing.</summary>
        public static bool PropCollider(string asset, Vec3 pos, Quat q, double scale, out Collider c)
        {
            c = default;
            if (!PropBoxes.TryGetValue(asset, out var b)) return false;
            double s = scale <= 0 ? 1 : scale;
            Vec3 mid = (b.Min + b.Max) * (0.5 * s);
            Vec3 half = (b.Max - b.Min) * (0.5 * s);
            Vec3 ctr = pos + Quat.Rotate(q, mid);
            c = new Collider
            {
                Kind = ColliderKind.Box,
                Center = new Vec3((float)ctr.X, (float)ctr.Y, (float)ctr.Z),
                Half = new Vec3((float)half.X, (float)half.Y, (float)half.Z),
                Rot = q,
            };
            return true;
        }

        /// <summary>A ship's hull box at pos, oriented by q (collide.go ShipCollider).</summary>
        public static Collider ShipCollider(Vec3 pos, Quat q) => new Collider
        {
            Kind = ColliderKind.Box,
            Center = pos + Quat.Rotate(q, new Vec3(0, ShipBoxH, 0)),
            Half = new Vec3(ShipHalfX, ShipHalfY, ShipHalfZ),
            Rot = q,
        };

        /// <summary>A standing body's collision sphere as a collider (collide.go BodyCollider); an NPC passes its archetype's radius.</summary>
        public static Collider BodyCollider(Vec3 pos, double radius = BodyRadius) => new Collider
        {
            Kind = ColliderKind.Sphere,
            Center = pos + pos.Normalized() * BodySphereH,
            Half = new Vec3(radius, 0, 0),
            Rot = new Quat(0, 0, 0, 1),
        };

        /// <summary>A rover's hull box at pos, oriented by q (collide.go RoverCollider).</summary>
        public static Collider RoverCollider(Vec3 pos, Quat q) => new Collider
        {
            Kind = ColliderKind.Box,
            Center = pos + Quat.Rotate(q, new Vec3(0, RoverBoxH, 0)),
            Half = new Vec3(RoverHalfX, RoverHalfY, RoverHalfZ),
            Rot = q,
        };
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
