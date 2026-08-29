// Rotation quaternion in (x, y, z, w) component order — the wire order of the
// snapshot entity row. Ported from server/internal/sim/sim.go.

using System;

namespace SpaceAdventure.Sim
{
    public readonly struct Quat
    {
        public readonly double X, Y, Z, W;

        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        /// <summary>Apply q to v.</summary>
        public static Vec3 Rotate(Quat q, Vec3 v)
        {
            // t = 2 * cross(qv, v)
            double tx = 2 * (q.Y * v.Z - q.Z * v.Y);
            double ty = 2 * (q.Z * v.X - q.X * v.Z);
            double tz = 2 * (q.X * v.Y - q.Y * v.X);
            return new Vec3(
                v.X + q.W * tx + (q.Y * tz - q.Z * ty),
                v.Y + q.W * ty + (q.Z * tx - q.X * tz),
                v.Z + q.W * tz + (q.X * ty - q.Y * tx));
        }

        /// <summary>
        /// The quaternion whose rotation has the given column vectors
        /// (local X -> x, local Y -> y, local Z -> z).
        ///
        /// This same maths exists in the Go sim, in the retired TypeScript
        /// client's util/quat.ts and
        /// in defs/zone.go. This is a fourth copy, and it is deliberate for the
        /// same reason Vec3 is: Sim may not depend on anything, and the branch
        /// order below is load-bearing — a different branch selects a different
        /// but equivalent quaternion, and "equivalent" is not "identical" when
        /// the diff is byte-for-byte.
        /// </summary>
        public static Quat FromBasis(Vec3 x, Vec3 y, Vec3 z)
        {
            double m00 = x.X, m11 = y.Y, m22 = z.Z;
            double tr = m00 + m11 + m22;
            double qx, qy, qz, qw, s;

            if (tr > 0)
            {
                s = Math.Sqrt(tr + 1) * 2;
                qx = (y.Z - z.Y) / s; qy = (z.X - x.Z) / s; qz = (x.Y - y.X) / s; qw = 0.25 * s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                qx = 0.25 * s; qy = (x.Y + y.X) / s; qz = (z.X + x.Z) / s; qw = (y.Z - z.Y) / s;
            }
            else if (m11 > m22)
            {
                s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                qx = (y.X + x.Y) / s; qy = 0.25 * s; qz = (y.Z + z.Y) / s; qw = (z.X - x.Z) / s;
            }
            else
            {
                s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
                qx = (z.X + x.Z) / s; qy = (z.Y + y.Z) / s; qz = 0.25 * s; qw = (x.Y - y.X) / s;
            }

            double n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (n > 0) return new Quat(qx / n, qy / n, qz / n, qw / n);
            return new Quat(qx, qy, qz, qw);
        }

        public override string ToString() => $"({X:R}, {Y:R}, {Z:R}, {W:R})";
    }
}
