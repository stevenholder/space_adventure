// Phase 4 — stepRover, mirrored from server/internal/sim/drive.go
// line-for-line (GDD "Phase 4 — ground drive model", steps 1–10). C30 diffs
// this against the Go sim at ≤ 1e-6 over ≥ 1000 ticks, so any change lands
// in both files or the gate goes red.
//
// No UnityEngine, project math types only (CONVENTIONS.md): normalize/lerp
// differ between engines, and this file exists to agree with Go, not Unity.

using System;

namespace SpaceAdventure.Sim
{
    /// <summary>GDD "Ground drive model" rule table.</summary>
    public static class DriveRules
    {
        public const double AccelDrive = 8.0;
        public const double VmaxDrive = 16.0;
        public const double SteerRate = 1.2;
        public const double Grip = 6.0;
        public const double DampDrive = 0.8;
        public const double HoldSpeed = 0.1;
        public const double DriveSlopeMax = 40.0 * Math.PI / 180.0;
    }

    /// <summary>The rover's mirrored state: the wire triplet plus carried grounded.</summary>
    public struct RoverState
    {
        public Vec3 Pos;
        public Vec3 Vel;
        public Quat Quat;
        public bool Grounded;
    }

    public static class Drive
    {
        /// <summary>One fixed-dt rover tick — the exact step order of drive.go.</summary>
        /// <summary>An efficacy multiplier: 0 (unset) is the 1.0 identity.</summary>
        public static double EffMult(double m) => m <= 0 ? 1 : m;

        public static void Apply(ref RoverState s, double throttle, double steer,
                                 TerrainField t, double dt, double effMult = 0, Collider[] colliders = null)
        {
            throttle = Sanitise(throttle);
            steer = Sanitise(steer);

            Vec3 pos = s.Pos, vel = s.Vel;

            // 1–2: local up, heading from the quat's forward projected to the
            // tangent plane. Fallback per the GDD: rotate(quat, +Y) projected.
            Vec3 up = pos.Normalized();
            Vec3 h = Tangential(Quat.Rotate(s.Quat, new Vec3(0, 0, 1)), up);
            if (h.Length < Rules.EpsDegen)
                h = Tangential(Quat.Rotate(s.Quat, new Vec3(0, 1, 0)), up);
            h = h.Normalized();

            // 3: steer, grounded only. Positive steer = toward local +X =
            // negative rotation about up (right-hand rule).
            if (s.Grounded && steer != 0)
                h = RotateAboutAxis(h, up, -steer * DriveRules.SteerRate * dt);

            // 4: throttle, grounded only, and under the slope cutoff OR
            // pointed downhill (drive.go step 4): past drive_slope_max uphill
            // throttle is refused, downhill still works, so a rover parked on
            // a scarp can always drive off it. Reverse at half accel.
            if (s.Grounded && throttle != 0 && DriveAllowed(t, up, h, throttle))
            {
                double a = (throttle < 0 ? DriveRules.AccelDrive * 0.5 : DriveRules.AccelDrive) * EffMult(effMult);
                vel += h * (throttle * a * dt);
            }

            // 5: gravity, always, along local −up.
            vel -= up * (Rules.Gravity * dt);

            // 6: grip and coast, tangent-frame split, grounded only.
            if (s.Grounded)
            {
                double vr0 = Vec3.Dot(vel, up);
                Vec3 vt0 = vel - up * vr0;
                double vf = Vec3.Dot(vt0, h);
                Vec3 vlat = vt0 - h * vf;
                vlat *= Math.Exp(-DriveRules.Grip * dt);
                if (throttle == 0)
                {
                    vf *= Math.Exp(-DriveRules.DampDrive * dt);
                    // Static friction, mirroring drive.go: parked is parked.
                    if (vf * vf + Vec3.Dot(vlat, vlat) < DriveRules.HoldSpeed * DriveRules.HoldSpeed)
                    {
                        vf = 0;
                        vlat = Vec3.Zero;
                    }
                }
                vel = up * vr0 + h * vf + vlat;
            }

            // 7: clamp tangential speed.
            double vr = Vec3.Dot(vel, up);
            Vec3 vt = vel - up * vr;
            double l = vt.Length;
            if (l > DriveRules.VmaxDrive)
                vel = vt * (DriveRules.VmaxDrive / l) + up * vr;

            // 8: integrate.
            pos += vel * dt;

            // 8b: the hull out of anything solid, by the tick's starting pose
            // (drive.go 8b).
            if (colliders != null) Collide.ResolveHull(ref pos, ref vel, s.Quat, Collide.RoverHull, colliders);

            // 9: terrain following — the origin point, like a body's foot.
            Vec3 u = pos.Normalized();
            double r = t.SampleRadius(u);
            if (pos.Length <= r + Rules.GroundSnap)
            {
                pos = u * r;
                double rad = Vec3.Dot(vel, u);
                if (rad < 0) vel -= u * rad;
                s.Grounded = true;
            }
            else
            {
                s.Grounded = false;
            }

            // 10: orientation from the ground normal (radial up when airborne).
            Vec3 n = s.Grounded ? t.SurfaceNormal(u) : u;
            Vec3 h2 = Tangential(h, n);
            if (h2.Length < Rules.EpsDegen)
                h2 = Tangential(Quat.Rotate(s.Quat, new Vec3(0, 1, 0)), n);
            h2 = h2.Normalized();

            s.Pos = pos;
            s.Vel = vel;
            s.Quat = Quat.FromBasis(Vec3.Cross(n, h2), n, h2);
        }

        // Step 4's slope rule, the exact expression of drive.go's
        // driveAllowed: slope ≤ drive_slope_max, or the drive direction (h,
        // reversed for negative throttle) has a positive component along
        // downhill = normalize(g − n·dot(g, n)), g = −up, n = surface normal.
        private static bool DriveAllowed(TerrainField t, Vec3 up, Vec3 h, double throttle)
        {
            if (t.Slope(up) <= DriveRules.DriveSlopeMax) return true;
            Vec3 n = t.SurfaceNormal(up);
            Vec3 g = up * -1.0;
            Vec3 downhill = (g - n * Vec3.Dot(g, n)).Normalized();
            Vec3 dir = h;
            if (throttle < 0) dir = h * -1.0;
            return Vec3.Dot(dir, downhill) > 0;
        }

        // Rodrigues, the exact expression of drive.go's rotateAboutAxis — not
        // a quaternion — so the two sims agree to C30's bar.
        private static Vec3 RotateAboutAxis(Vec3 v, Vec3 k, double ang)
        {
            double c = Math.Cos(ang), s = Math.Sin(ang);
            return v * c + Vec3.Cross(k, v) * s + k * (Vec3.Dot(k, v) * (1 - c));
        }

        private static Vec3 Tangential(Vec3 v, Vec3 up) => v - up * Vec3.Dot(v, up);

        private static double Sanitise(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
            if (x < -1) return -1;
            if (x > 1) return 1;
            return x;
        }
    }
}
