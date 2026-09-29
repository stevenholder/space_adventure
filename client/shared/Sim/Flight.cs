// Phase 5 — stepShip, mirrored from server/internal/sim/flight.go
// line-for-line (GDD flight model + Phase 5 space regime and landing).
// C34 diffs the two at ≤ 1e-6 over ≥ 1000 ticks, so any change lands in
// both files or the gate goes red.
//
// No UnityEngine, project math types only (CONVENTIONS.md).

using System;

namespace SpaceAdventure.Sim
{
    /// <summary>GDD flight + Phase 5 rule tables.</summary>
    public static class FlightRules
    {
        public const double Accel = 12.0;
        public const double AccelBoost = 24.0;
        public const double Vmax = 40.0;
        public const double VmaxBoost = 80.0;
        public const double Damp = 0.5;
        public const double AngvelMax = 4.0;
        public const double AngvelMaxRoll = 2.0;
        public const double AngvelTau = 0.15;

        public const double SpaceRadius = 260.0;
        public const double SpaceHyst = 10.0;
        public const double LandSpeedMax = 8.0;
        public const double BounceK = 0.3;
        public const double LandGrip = 3.0;
    }

    /// <summary>Pilot input for one tick (PROTOCOL mode 1).</summary>
    public struct FlightInput
    {
        public double Thrust, Roll, YawRate, PitchRate;
        public bool Boost;
        /// <summary>Piloting efficacy (Phase 11), 0 = 1.0 identity.</summary>
        public double EffMult;
    }

    /// <summary>The ship's mirrored state: wire triplet + carried ω/regime.</summary>
    public struct ShipSimState
    {
        public Vec3 Pos;
        public Vec3 Vel;
        public Quat Quat;
        public Vec3 Omega; // ship frame, about local +X/+Y/+Z
        public bool Grounded;
        public bool Space;
    }

    public static class Flight
    {
        /// <summary>One fixed-dt ship tick — the exact step order of flight.go.</summary>
        public static void Apply(ref ShipSimState s, FlightInput inp, TerrainField t, double dt)
        {
            double thrust = SanitiseAxis(inp.Thrust);
            double roll = SanitiseAxis(inp.Roll);
            double yawRate = ClampRate(inp.YawRate);
            double pitchRate = ClampRate(inp.PitchRate);

            Vec3 pos = s.Pos, vel = s.Vel;
            Quat q = s.Quat;
            Vec3 up = pos.Normalized();

            // 1. Rotation — first-order toward the target, ship frame.
            var wt = new Vec3(pitchRate, yawRate, roll * FlightRules.AngvelMaxRoll);
            double k = Math.Exp(-dt / FlightRules.AngvelTau);
            var w = new Vec3(
                wt.X + (s.Omega.X - wt.X) * k,
                wt.Y + (s.Omega.Y - wt.Y) * k,
                wt.Z + (s.Omega.Z - wt.Z) * k);
            double ang = w.Length * dt;
            if (ang > 1e-12)
                q = QuatNormalize(QuatMul(q, QuatAxisAngle(w * (1 / w.Length), ang)));

            // 2. Translation — thrust along ship forward; damp in atmosphere.
            Vec3 fwd = Quat.Rotate(q, new Vec3(0, 0, 1));
            double em = inp.EffMult <= 0 ? 1 : inp.EffMult;
            double accel = FlightRules.Accel * em, cap = FlightRules.Vmax;
            if (inp.Boost) { accel = FlightRules.AccelBoost * em; cap = FlightRules.VmaxBoost; }
            double factor = thrust < 0 ? 0.5 * thrust : thrust;
            vel += fwd * (accel * factor * dt);
            if (thrust == 0 && !s.Space) vel *= Math.Exp(-FlightRules.Damp * dt);
            double l = vel.Length;
            if (l > cap) vel *= cap / l;

            // 3. Gravity — airborne and in atmosphere only.
            if (!s.Grounded && !s.Space) vel -= up * (Rules.Gravity * dt);

            // Grounded grip (GDD "Landing").
            if (s.Grounded)
            {
                double vr0 = Vec3.Dot(vel, up);
                Vec3 vt = vel - up * vr0;
                vt *= Math.Exp(-FlightRules.LandGrip * dt);
                if (vt.Length < DriveRules.HoldSpeed) vt = Vec3.Zero;
                vel = vt + up * vr0;
            }

            // 4. Integrate + origin-point contact with the landing rules.
            pos += vel * dt;
            Vec3 dir = pos.Normalized();
            double r = t.SampleRadius(dir);
            if (pos.Length < r)
            {
                pos = dir * r;
                double vr = Vec3.Dot(vel, dir);
                if (vr < 0)
                {
                    if (-vr <= FlightRules.LandSpeedMax)
                    {
                        vel -= dir * vr;
                        s.Grounded = true;
                    }
                    else
                    {
                        vel -= dir * (vr * (1 + FlightRules.BounceK));
                        s.Grounded = false;
                    }
                }
            }
            if (pos.Length > r + Rules.GroundSnap) s.Grounded = false;

            // Space regime, hysteresis on the radius.
            if (pos.Length >= FlightRules.SpaceRadius + FlightRules.SpaceHyst / 2) s.Space = true;
            else if (pos.Length <= FlightRules.SpaceRadius - FlightRules.SpaceHyst / 2) s.Space = false;

            s.Pos = pos;
            s.Vel = vel;
            s.Quat = q;
            s.Omega = w;
        }

        private static double ClampRate(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
            if (x > FlightRules.AngvelMax) return FlightRules.AngvelMax;
            if (x < -FlightRules.AngvelMax) return -FlightRules.AngvelMax;
            return x;
        }

        private static double SanitiseAxis(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
            if (x < -1) return -1;
            if (x > 1) return 1;
            return x;
        }

        // The same maths as flight.go's private copies, char-for-char.
        private static Quat QuatAxisAngle(Vec3 axis, double ang)
        {
            double half = ang / 2, s = Math.Sin(half);
            return new Quat(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(half));
        }

        private static Quat QuatMul(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        private static Quat QuatNormalize(Quat q)
        {
            double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (n == 0) return Quat.Identity;
            return new Quat(q.X / n, q.Y / n, q.Z / n, q.W / n);
        }
    }
}
