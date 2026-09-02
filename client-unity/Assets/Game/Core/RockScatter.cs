// Where the rocks go. Pure arithmetic, no engine.
//
// GDD "Rocks": ~400 rocks, 0.3-1.5 m, three variants, none on slopes above 35
// degrees, denser in crater floors and along ridges.
//
// This lives under Assets/Game/Core, which is the half of the Game assembly
// that carries no UnityEngine reference and is compiled a second time by
// headless/GameCore.csproj -- so it can be checked with no Editor, which is
// the only kind of check this repo counts (CONVENTIONS.md rule 5). Rocks.cs
// turns what comes out of here into transforms and draws it.
//
// The scatter is client-side decoration and the server never mentions it: it
// sends `world_seed`, and every client runs this on it. That is the whole
// agreement mechanism -- no traffic, no authority, just the same function of
// the same two inputs. Which makes the ORDER the random numbers are drawn in
// the actual contract here, not the shape of the loop: one extra rng() call,
// or two drawn in the other order, and this client's rocks are somewhere else
// than every other client's. M1 rocks have no collision, so nothing would
// complain.
//
// Ported from the retired TypeScript client's scene/rocks.ts step for step
// for that reason (git history; ROADMAP U18). That
// client is retired (ROADMAP U18), so this is no longer about agreeing with
// IT -- it is that the TypeScript is the written-down spec and a literal port
// cannot drift from a spec it is a transliteration of.

using System;
using System.Collections.Generic;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>
    /// One rock, in sim space. Rotation is given as the surface direction plus
    /// a spin about it rather than as a quaternion: the renderer has to convert
    /// into a left-handed frame anyway, and handing it two vectors and an angle
    /// keeps the handedness flip in one place instead of two.
    /// </summary>
    public struct RockPlacement
    {
        /// <summary>Sim-space position, already seated into the surface.</summary>
        public Vec3 Pos;
        /// <summary>Unit surface direction; the rock's local +Y.</summary>
        public Vec3 Dir;
        /// <summary>Spin about <see cref="Dir"/>, in radians.</summary>
        public double Spin;
        public Vec3 Scale;
        /// <summary>0, 1 or 2 — prop.rock.a / b / c.</summary>
        public int Variant;
    }

    public static class RockScatter
    {
        /// <summary>GDD: rocks skip slopes above 35 degrees.</summary>
        public const double SlopeMax = 35.0 * Math.PI / 180.0;
        public const int TargetCount = 400;
        private const double MinSize = 0.3;
        private const double MaxSize = 1.5;

        /// <summary>
        /// Curvature thresholds (discrete Laplacian, m/rad^2) and the accept
        /// probability either side of them. Positive curvature is a basin (a
        /// crater floor), negative is a ridge crest; flat ground between them
        /// is sparse and the features are dense, which is what puts rocks
        /// where you are looking rather than evenly over a featureless ball.
        /// </summary>
        private const double BasinT = 20, RidgeT = -20;
        private const double PFlat = 0.1, PFeature = 0.9;
        private const int MaxTries = TargetCount * 60;

        public static List<RockPlacement> Scatter(TerrainField t, uint worldSeed)
        {
            Func<double> rng = Rng.Mulberry32(worldSeed ^ 0x5eedu);
            var placements = new List<RockPlacement>(TargetCount);

            int tries = 0;
            while (placements.Count < TargetCount && tries++ < MaxTries)
            {
                // Uniform random direction on the sphere.
                double z = rng() * 2 - 1;
                double th = rng() * Math.PI * 2;
                double r = Math.Sqrt(1 - z * z);
                Vec3 d = new Vec3(r * Math.Cos(th), z, r * Math.Sin(th)).Normalized();

                if (t.Slope(d) > SlopeMax) continue;

                double c = t.Curvature(d);
                double p = (c > BasinT || c < RidgeT) ? PFeature : PFlat;
                if (rng() > p) continue;

                double size = MinSize + rng() * (MaxSize - MinSize);
                double surf = t.SampleRadius(d);

                placements.Add(new RockPlacement
                {
                    // Sunk slightly: seated in the ground, not balanced on it.
                    Pos = d * (surf + size * 0.05),
                    Dir = d,
                    Spin = rng() * Math.PI * 2,
                    Scale = new Vec3(
                        size * (0.8 + rng() * 0.4),
                        size * (0.55 + rng() * 0.5),
                        size * (0.8 + rng() * 0.4)),
                    Variant = Math.Min(2, (int)Math.Floor(rng() * 3)),
                });
            }
            return placements;
        }
    }
}
