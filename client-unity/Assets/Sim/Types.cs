// Pure simulation core — movement + terrain sampling.
//
// NO UnityEngine. This assembly is declared "noEngineReferences" in
// Sim.asmdef and is compiled headless by headless/Sim/Sim.csproj, because the
// conformance diff against the Go server has to run in CI with no Editor.
//
// Mirrors client/src/sim/types.ts field-for-field. Keeping it a
// transliteration rather than a rewrite is what makes a disagreement with Go
// traceable to one line in one file.
//
// EVERYTHING IS double. TypeScript numbers are f64 and the Go sim is float64;
// the conformance bar is 1e-10 m. A float here would blow it by orders of
// magnitude, and it would do so silently on the third decimal place of a
// position that still looks plausible.

using System;

namespace SpaceAdventure.Sim
{
    /// <summary>
    /// A 3-vector. Deliberately NOT UnityEngine.Vector3: that type is f32, and
    /// its Normalize and Slerp are not specified to the bit and have changed
    /// between engine versions.
    /// </summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => a * s;

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public double Length => Math.Sqrt(Dot(this, this));

        /// <summary>
        /// Unit vector, or Zero for a zero-length input. Returning Zero rather
        /// than NaN matches the TypeScript and Go sims: a NaN here does not
        /// stop, it propagates into a position that then gets persisted.
        /// </summary>
        public Vec3 Normalized()
        {
            double len = Length;
            return len > 0 ? this * (1.0 / len) : Zero;
        }

        /// <summary>Component of this vector perpendicular to unit vector n.</summary>
        public Vec3 RejectFrom(Vec3 n) => this - n * Dot(this, n);

        public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object o) => o is Vec3 v && Equals(v);
        public override int GetHashCode() => (X, Y, Z).GetHashCode();
        public override string ToString() => $"({X:R}, {Y:R}, {Z:R})";
    }

    /// <summary>action_mask bits (PROTOCOL.md constants).</summary>
    public static class Action
    {
        public const int Sprint = 0x0001;
        public const int Jump = 0x0002;
    }

    /// <summary>One `input` command (PROTOCOL 0x0003). Latest wins.</summary>
    public struct Input
    {
        /// <summary>Wish direction in the body tangent frame, each in [-1, 1].</summary>
        public double MoveX, MoveY;

        /// <summary>Absolute world-space unit direction the eyes point along.</summary>
        public Vec3 LookDir;

        public int ActionMask;
    }

    /// <summary>Simulated body state.</summary>
    public struct State
    {
        /// <summary>World-space Cartesian; the planet centre is the origin.</summary>
        public Vec3 Pos;
        public Vec3 Vel;

        /// <summary>Body facing: unit vector, tangent to the surface.</summary>
        public Vec3 Facing;
        public bool Grounded;
    }

    /// <summary>Decoded `terrain` message (PROTOCOL 0x000A). f64 internally.</summary>
    public sealed class Terrain
    {
        public int FaceGrid;
        public double RadiusMin;
        public double RadiusMax;

        /// <summary>radii[face*G*G + row*G + col], metres from the planet centre.</summary>
        public double[] Radii;
    }
}
