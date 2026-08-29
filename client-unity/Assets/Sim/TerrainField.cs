// Cube-sphere surface sampling.
//
// Ported from server/internal/terrain/terrain.go, NOT from the TypeScript.
// C40 measures this assembly against the Go sim, so Go is the reference, and
// where the two existing implementations differ in the last ulp (Go's
// Sqrt(dot) against TypeScript's Math.hypot) this follows Go.
//
// The field is six square grids of surface radii, face order +X, -X, +Y, -Y,
// +Z, -Z. A sample direction d picks its face by the largest-magnitude
// component; the other two divided by that magnitude give (u, v) in [-1, 1],
// taken in ascending axis order:
//
//     +X/-X: u = y/max, v = z/max
//     +Y/-Y: u = x/max, v = z/max
//     +Z/-Z: u = x/max, v = y/max
//
// Indexing is radii[face*grid^2 + row*grid + col], col = (u+1)/2*(grid-1),
// row = (v+1)/2*(grid-1). Shared edges are duplicated between adjacent faces
// and carry identical values, which is why seam crossings need no special
// case anywhere in the sim.

using System;

namespace SpaceAdventure.Sim
{
    public static class Face
    {
        public const int PX = 0, NX = 1, PY = 2, NY = 3, PZ = 4, NZ = 5;
        public const int Count = 6;
    }

    public sealed partial class TerrainField
    {
        public const int FaceGrid = 65;
        public const double RadiusMin = 124.0;
        public const double RadiusMax = 190.0;
        public const double PlanetRadius = 150.0;

        /// <summary>max_slope 50 degrees, in radians. Steeper ground is slid, not walked.</summary>
        public const double MaxSlope = 50.0 * Math.PI / 180.0;

        /// <summary>
        /// normal_eps 2 degrees (GDD rule table): the finite-difference angular
        /// step for SurfaceNormal. At the nominal radius it spans about 5 m,
        /// roughly 1.5 grid cells, so the slope test averages across the
        /// bilinear kinks at grid lines instead of snapping to them.
        /// </summary>
        public const double NormalEps = 2.0 * Math.PI / 180.0;

        /// <summary>radii[face*FaceGrid^2 + row*FaceGrid + col], metres from the centre.</summary>
        public readonly double[] Radii;

        public TerrainField(double[] radii)
        {
            int n = Face.Count * FaceGrid * FaceGrid;
            if (radii == null || radii.Length != n)
                throw new ArgumentException($"terrain needs {n} radii, got {radii?.Length ?? 0}");
            Radii = radii;
        }

        /// <summary>
        /// Decode the u16 wire field, exactly as terrain.Decode does in Go and
        /// decodeTerrain does in TypeScript: min + code/65535 * (max - min).
        ///
        /// The conformance dump MUST run on the quantised field, not on the f64
        /// one the generator produces. The Go dump round-trips through this
        /// encoding for the same reason: otherwise the diff measures the
        /// representation gap on top of any real divergence. Zone flattening
        /// made that visible, because a smoothstep blend band quantises
        /// unevenly.
        /// </summary>
        public static TerrainField FromWire(ushort[] codes, double radiusMin, double radiusMax)
        {
            int n = Face.Count * FaceGrid * FaceGrid;
            if (codes == null || codes.Length < n)
                throw new ArgumentException($"terrain: {codes?.Length ?? 0} radii, expected {n}");
            double span = radiusMax - radiusMin;
            var radii = new double[n];
            for (int i = 0; i < n; i++) radii[i] = radiusMin + (codes[i] / 65535.0) * span;
            return new TerrainField(radii);
        }

        /// <summary>
        /// Face index and (u, v) in [-1, 1] for direction d. d need not be
        /// normalised: the magnitude division handles that.
        /// </summary>
        public static void FaceOf(Vec3 d, out int face, out double u, out double v)
        {
            double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
            if (ax >= ay && ax >= az)
            {
                face = d.X > 0 ? Face.PX : Face.NX;
                u = d.Y / ax; v = d.Z / ax;
            }
            else if (ay >= ax && ay >= az)
            {
                face = d.Y > 0 ? Face.PY : Face.NY;
                u = d.X / ay; v = d.Z / ay;
            }
            else
            {
                face = d.Z > 0 ? Face.PZ : Face.NZ;
                u = d.X / az; v = d.Y / az;
            }
        }

        /// <summary>Inverts FaceOf: the unit direction at face coordinates (u, v).</summary>
        public static Vec3 DirOf(int face, double u, double v)
        {
            switch (face)
            {
                case Face.PX: return new Vec3(1, u, v).Normalized();
                case Face.NX: return new Vec3(-1, u, v).Normalized();
                case Face.PY: return new Vec3(u, 1, v).Normalized();
                case Face.NY: return new Vec3(u, -1, v).Normalized();
                case Face.PZ: return new Vec3(u, v, 1).Normalized();
                default: return new Vec3(u, v, -1).Normalized();
            }
        }

        /// <summary>Bilinearly sampled surface radius for direction d.</summary>
        public double SampleRadius(Vec3 d)
        {
            FaceOf(d, out int face, out double u, out double v);
            // Map [-1, 1] to [0, grid-1] so face edges sit exactly on grid
            // lines; generation fills the same way.
            double gu = (u + 1) * 0.5 * (FaceGrid - 1);
            double gv = (v + 1) * 0.5 * (FaceGrid - 1);
            if (gu < 0) gu = 0;
            if (gu > FaceGrid - 1) gu = FaceGrid - 1;
            if (gv < 0) gv = 0;
            if (gv > FaceGrid - 1) gv = FaceGrid - 1;

            int c0 = (int)gu;
            int r0 = (int)gv;
            if (c0 > FaceGrid - 2) c0 = FaceGrid - 2;
            if (r0 > FaceGrid - 2) r0 = FaceGrid - 2;
            double fu = gu - c0;
            double fv = gv - r0;

            int b = face * FaceGrid * FaceGrid;
            double i00 = Radii[b + r0 * FaceGrid + c0];
            double i10 = Radii[b + r0 * FaceGrid + c0 + 1];
            double i01 = Radii[b + (r0 + 1) * FaceGrid + c0];
            double i11 = Radii[b + (r0 + 1) * FaceGrid + c0 + 1];
            double top = i00 + (i10 - i00) * fu;
            double bot = i01 + (i11 - i01) * fu;
            return top + (bot - top) * fv;
        }

        /// <summary>
        /// Surface normal at unit direction d, by finite differences of the
        /// sampled radius. Both ends must derive the same normal from the same
        /// field, so this follows the GDD formula step for step rather than
        /// doing anything cleverer. Points outward (positive dot with d).
        /// </summary>
        public Vec3 SurfaceNormal(Vec3 d)
        {
            Vec3 k = Math.Abs(d.X) <= 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            Vec3 e1 = (k - d * Vec3.Dot(k, d)).Normalized();
            Vec3 e2 = Vec3.Cross(d, e1);
            Vec3 p0 = d * SampleRadius(d);
            Vec3 d1 = (d + e1 * NormalEps).Normalized();
            Vec3 d2 = (d + e2 * NormalEps).Normalized();
            Vec3 p1 = d1 * SampleRadius(d1);
            Vec3 p2 = d2 * SampleRadius(d2);
            Vec3 n = Vec3.Cross(p1 - p0, p2 - p0);
            if (Vec3.Dot(n, d) < 0) n = -n;
            return n.Normalized();
        }

        /// <summary>Angle in radians between the surface normal and local up at d.</summary>
        public double Slope(Vec3 d)
        {
            double c = Vec3.Dot(SurfaceNormal(d), d);
            if (c > 1) c = 1;
            if (c < -1) c = -1;
            return Math.Acos(c);
        }

        public bool Walkable(Vec3 d) => Slope(d) <= MaxSlope;

        /// <summary>Radians. Sub-cell on purpose -- this measures the shape of
        /// a crater floor, not of a grid cell.</summary>
        private const double CurvStep = 0.02;

        /// <summary>
        /// Discrete Laplacian of the radius field (m/rad^2) at d. Positive is
        /// locally a basin (a crater floor); negative is a ridge crest.
        ///
        /// This one has NO Go counterpart and is not part of C40 conformance.
        /// It exists only to bias client-side prop scatter, never for
        /// collision -- the server does not know props exist. It lives here
        /// anyway because the TypeScript sim/terrain.ts it was ported from put
        /// it here, and
        /// CONVENTIONS.md wants the port to stay a transliteration rather than
        /// become a rewrite.
        /// </summary>
        public double Curvature(Vec3 d)
        {
            Vec3 k = Math.Abs(d.X) <= 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            Vec3 e1 = (k - d * Vec3.Dot(k, d)).Normalized();
            Vec3 e2 = Vec3.Cross(d, e1);
            double r0 = SampleRadius(d);
            Vec3 d1 = (d + e1 * CurvStep).Normalized();
            Vec3 d2 = (d + e2 * CurvStep).Normalized();
            Vec3 d3 = (d - e1 * CurvStep).Normalized();
            Vec3 d4 = (d - e2 * CurvStep).Normalized();
            double sum = SampleRadius(d1) + SampleRadius(d2)
                       + SampleRadius(d3) + SampleRadius(d4);
            return (sum - 4 * r0) / (CurvStep * CurvStep);
        }
    }
}
