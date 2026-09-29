// The on-foot integrator — GDD "M1 on-foot movement" and "Integrator".
//
// Ported from server/internal/sim/sim.go, which is the reference C40 measures
// against. The step order below follows the GDD steps 1-9 exactly. Reordering
// a step or changing a threshold desyncs client and server, so do not tidy it.

using System;

namespace SpaceAdventure.Sim
{
    /// <summary>GDD "M1 on-foot movement" rule table. SI units, metres.</summary>
    public static class Rules
    {
        public const int TickHz = 20;
        public const double DT = 1.0 / TickHz;
        public const double WalkSpeed = 4.5;
        public const double SprintSpeed = 7.5;
        public const double AccelGround = 50.0;
        public const double FrictionGround = 8.0;
        public const double AccelAir = 8.0;
        public const double Gravity = 9.8;
        public const double JumpSpeed = 4.5;
        public const double TerminalSpeed = 60.0;
        public const double MaxStep = 0.3;
        public const double MaxSlope = 50.0 * Math.PI / 180.0;
        public const double GroundSnap = 0.15;
        public const double LookClamp = 1.0 * Math.PI / 180.0;
        public const double FacingHold = 0.1;
        public const double WallTol = 1e-3;
        public const double EpsDegen = 1e-6;
    }

    /// <summary>Per-tick movement mode partition (GDD integrator step 3).</summary>
    public enum Mode { Ground, Slide, Air }

    public static class Step
    {
        /// <summary>GDD spawn direction: the surface point along +Y.</summary>
        public static readonly Vec3 SpawnDir = new Vec3(0, 1, 0);

        /// <summary>
        /// Go's math.Hypot, not Sqrt(x*x + y*y).
        ///
        /// Go scales by the larger magnitude before squaring, which is a
        /// different expression and can differ in the last ulp. netstandard2.1
        /// has no Math.Hypot to borrow, so this replicates Go's algorithm
        /// rather than approximating it — the whole point of the port is that
        /// nothing differs by accident.
        /// </summary>
        public static double Hypot(double p, double q)
        {
            p = Math.Abs(p); q = Math.Abs(q);
            if (double.IsInfinity(p) || double.IsInfinity(q)) return double.PositiveInfinity;
            if (double.IsNaN(p) || double.IsNaN(q)) return double.NaN;
            if (p < q) { double tmp = p; p = q; q = tmp; }
            if (p == 0) return 0;
            q = q / p;
            return p * Math.Sqrt(1 + q * q);
        }

        /// <summary>Projects v onto the plane perpendicular to up.</summary>
        public static Vec3 Tangential(Vec3 v, Vec3 up) => v - up * Vec3.Dot(v, up);

        /// <summary>
        /// The spawn state (GDD "Spawn"). Deterministic: the same field gives
        /// the same state.
        /// </summary>
        public static State SpawnState(TerrainField t)
        {
            Vec3 up = SpawnDir.Normalized();
            Vec3 pos = up * t.SampleRadius(up);
            Vec3 look = Tangential(new Vec3(1, 0, 0), up);
            if (look.Length < Rules.EpsDegen) look = Tangential(new Vec3(0, 0, 1), up);
            look = look.Normalized();
            return new State { Pos = pos, Vel = Vec3.Zero, Grounded = t.Walkable(up), Facing = look };
        }

        /// <summary>
        /// One fixed-dt tick, applied to s in place. prevLook is the look of
        /// the last applied input (the spawn look before any input). Returns
        /// the look actually applied after sanitisation, so the caller can
        /// carry it forward as the next tick's prevLook.
        /// </summary>
        /// <summary>An efficacy multiplier: 0 (unset) is the 1.0 identity.</summary>
        public static double EffMult(double m) => m <= 0 ? 1 : m;

        public static Vec3 Apply(ref State s, Input inp, Vec3 prevLook, TerrainField t, double dt)
        {
            // 1. Sanitise input.
            double mx = SanitiseAxis(inp.MoveX);
            double my = SanitiseAxis(inp.MoveY);
            double m = Hypot(mx, my);
            if (m > 1) { mx /= m; my /= m; }
            bool sprint = (inp.ActionMask & Action.Sprint) != 0;
            bool jump = (inp.ActionMask & Action.Jump) != 0;

            Vec3 look = inp.LookDir;
            if (!Finite(look) || look.Length < Rules.EpsDegen) look = prevLook;
            look = look.Normalized();

            // 2. Frame and facing.
            Vec3 up = s.Pos.Normalized();
            look = ClampLook(look, up, s.Facing);
            Vec3 tang = look - up * Vec3.Dot(look, up);
            if (tang.Length >= Rules.FacingHold) s.Facing = tang.Normalized();

            // right = facing x up, NOT up x facing. In a right-handed frame the
            // latter points LEFT, which is what inverted A and D for all of M1
            // in BOTH sims at once — they agreed, so conformance stayed green.
            Vec3 right = Vec3.Cross(s.Facing, up);

            // 3. Mode, from the state carried in.
            bool inContact = s.Pos.Length - t.SampleRadius(up) <= Rules.GroundSnap;
            Mode mode = s.Grounded ? Mode.Ground : (inContact ? Mode.Slide : Mode.Air);

            // 4. Acceleration.
            Vec3 wish = s.Facing * my + right * mx;
            bool hasTarget = wish.Length > Rules.EpsDegen;
            Vec3 target = Vec3.Zero;
            if (hasTarget)
                target = wish.Normalized() *
                    (sprint ? Rules.SprintSpeed * EffMult(inp.SprintMult) : Rules.WalkSpeed);

            switch (mode)
            {
                case Mode.Ground:
                    s.Vel = hasTarget
                        ? Approach(s.Vel, target, Rules.AccelGround * dt)
                        : s.Vel * Math.Exp(-Rules.FrictionGround * dt);
                    break;
                case Mode.Slide:
                    {
                        Vec3 n = t.SurfaceNormal(up);
                        Vec3 g = up * -Rules.Gravity;
                        s.Vel = s.Vel + (g - n * Vec3.Dot(g, n)) * dt;
                        break;
                    }
                case Mode.Air:
                    {
                        double vr = Vec3.Dot(s.Vel, up);
                        Vec3 vt = s.Vel - up * vr;
                        if (hasTarget) vt = Approach(vt, target, Rules.AccelAir * dt);
                        s.Vel = up * vr + vt - up * (Rules.Gravity * dt);
                        double vv = Vec3.Dot(s.Vel, up);
                        // Clamp the downward radial speed to exactly -terminal_speed.
                        if (vv < -Rules.TerminalSpeed) s.Vel = s.Vel - up * (vv + Rules.TerminalSpeed);
                        break;
                    }
            }

            // 5. Jump.
            if (mode == Mode.Ground && jump) s.Vel = s.Vel + up * Rules.JumpSpeed;

            // 6. Integrate.
            Vec3 pOld = s.Pos;
            s.Pos = s.Pos + s.Vel * dt;

            // 7. Terrain resolution.
            Resolve(ref s, pOld, mode, t);

            // 8-9. Static colliders, then terrain re-seat. up is re-derived from
            // step 7's possibly-adjusted position, matching Resolve's own
            // recomputation. An empty list is a documented no-op.
            up = s.Pos.Normalized();
            Collide.ResolveColliders(ref s.Pos, ref s.Vel, up, ref s.Grounded,
                inp.Colliders, d => t.SampleRadius(d));
            return look;
        }

        private static void Resolve(ref State s, Vec3 pOld, Mode mode, TerrainField t)
        {
            Vec3 up = s.Pos.Normalized();
            double h = s.Pos.Length - t.SampleRadius(up);
            if (h < 0)
            {
                if (-h <= Rules.MaxStep)
                {
                    s.Pos = up * t.SampleRadius(up);
                }
                else
                {
                    s.Pos = WallSlide(pOld, s.Pos, t);
                    up = s.Pos.Normalized();
                }
                s.Vel = s.Vel - up * Vec3.Dot(s.Vel, up);
                s.Grounded = t.Walkable(up);
                return;
            }
            if (mode == Mode.Air) { s.Grounded = false; return; }

            // Glue band: ground_snap plus the per-step downhill drop on the
            // contact slope, so a body on steep ground stays glued while the
            // surface recedes faster than ground_snap per tick. Flat ground
            // keeps the band exactly ground_snap. |vel| is the TANGENTIAL
            // speed: radial velocity is zeroed every contact tick, and on a
            // jump tick the jump's radial rise must not widen the band.
            double slopeDrop = Tangential(s.Vel, up).Length * Rules.DT
                               * Math.Max(0, Math.Sin(t.Slope(up)));
            if (h <= Rules.GroundSnap + slopeDrop)
            {
                s.Pos = up * t.SampleRadius(up);
                s.Vel = s.Vel - up * Vec3.Dot(s.Vel, up);
                s.Grounded = t.Walkable(up);
            }
            else
            {
                s.Grounded = false;
            }
        }

        /// <summary>
        /// Bisection-locates the last point of [pOld, pNew] at or above the
        /// surface. pOld is at or above it; pNew is embedded deeper than max_step.
        /// </summary>
        private static Vec3 WallSlide(Vec3 pOld, Vec3 pNew, TerrainField t)
        {
            Vec3 dir = pNew - pOld;
            double lo = 0.0, hi = 1.0;
            while (hi - lo > Rules.WallTol)
            {
                double mid = (lo + hi) / 2;
                Vec3 p = pOld + dir * mid;
                Vec3 d = p.Normalized();
                if (p.Length - t.SampleRadius(d) >= 0) lo = mid; else hi = mid;
            }
            return pOld + dir * lo;
        }

        /// <summary>
        /// Clamps l away from local up/down by look_clamp. prevFacing is the
        /// azimuth fallback when the look points exactly along +-up.
        /// </summary>
        public static Vec3 ClampLook(Vec3 l, Vec3 up, Vec3 prevFacing)
        {
            l = l.Normalized();
            double c = Vec3.Dot(l, up);
            double cl = Math.Cos(Rules.LookClamp);
            if (c >= cl)
            {
                Vec3 tt = l - up * c;
                if (tt.Length < Rules.EpsDegen) tt = prevFacing;
                return up * cl + tt.Normalized() * Math.Sin(Rules.LookClamp);
            }
            if (c <= -cl)
            {
                Vec3 tt = l - up * c;
                if (tt.Length < Rules.EpsDegen) tt = prevFacing;
                return up * -cl + tt.Normalized() * Math.Sin(Rules.LookClamp);
            }
            return l;
        }

        /// <summary>Moves v toward target by at most maxDelta.</summary>
        public static Vec3 Approach(Vec3 v, Vec3 target, double maxDelta)
        {
            Vec3 d = target - v;
            if (d.Length <= maxDelta) return target;
            return v + d.Normalized() * maxDelta;
        }

        /// <summary>
        /// Orientation as (x, y, z, w): local (+X right, +Y up, +Z forward)
        /// mapped to (right = up x facing, up = normalize(pos), forward = facing).
        /// Note this is up x facing, unlike the movement frame's facing x up:
        /// the render basis and the input basis are genuinely different, and
        /// that asymmetry is in the GDD, not a bug.
        /// </summary>
        public static Quat OrientationQuat(in State s)
        {
            Vec3 up = s.Pos.Normalized();
            return Quat.FromBasis(Vec3.Cross(up, s.Facing), up, s.Facing);
        }

        private static double SanitiseAxis(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
            if (x < -1) return -1;
            if (x > 1) return 1;
            return x;
        }

        private static bool Finite(Vec3 v) =>
            !(double.IsNaN(v.X) || double.IsInfinity(v.X) ||
              double.IsNaN(v.Y) || double.IsInfinity(v.Y) ||
              double.IsNaN(v.Z) || double.IsInfinity(v.Z));
    }
}
