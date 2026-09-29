// The planet, built from the u16 radius grid the server sends.
//
// Six cube-sphere faces, each a FaceGrid x FaceGrid vertex lattice. A vertex
// sits at DirOf(face, u, v) * radius, where the radius comes from the SAME
// sampler the simulation uses. That shared sampler is the point: if the mesh
// were built from its own interpolation the player would visibly stand above
// or sink into ground the server thinks is somewhere else, and the mismatch
// would be worst exactly at the cube-face seams where it is hardest to debug.
//
// One ArrayMesh with six surfaces, UNINDEXED: every triangle carries its own
// three vertices, its own face normal and its own colour, so the ground is
// flat-shaded like everything else on it (art/README: flat-shaded, vertex
// colours, bold silhouettes). 6 x 64 x 64 x 2 triangles is ~147k vertices,
// built once.

using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public static class TerrainMesh
    {
        /// <summary>Builds the planet under `parent` and returns its node.</summary>
        public static MeshInstance3D Build(TerrainField field, Material material, Node parent, uint worldSeed)
        {
            // Visual only, so it never has to match the server -- but every
            // client seeds it the same way, so every client sees the same
            // patches. ~30 m features: big enough to read as ground types,
            // small enough that a walk crosses a few.
            _noise = new FastNoiseLite { Seed = (int)worldSeed, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.035f };
            var mesh = new ArrayMesh();
            for (int face = 0; face < Face.Count; face++)
            {
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, BuildFace(field, face));
                mesh.SurfaceSetMaterial(face, material);
            }
            var node = new MeshInstance3D { Name = "Planet", Mesh = mesh };
            parent.AddChild(node);
            return node;
        }

        private static FastNoiseLite _noise;

        private static Godot.Collections.Array BuildFace(TerrainField field, int face)
        {
            const int n = TerrainField.FaceGrid;
            var lattice = new Vector3[n * n];
            var dirs = new Vec3[n * n];
            for (int row = 0; row < n; row++)
            {
                for (int col = 0; col < n; col++)
                {
                    // The grid runs over [-1, 1] in both face axes; index
                    // ordering matches PROTOCOL's "Terrain sampling", so the
                    // mesh and the sim address the same cell for the same
                    // direction.
                    double u = 2.0 * col / (n - 1) - 1.0;
                    double v = 2.0 * row / (n - 1) - 1.0;
                    Vec3 dir = TerrainField.DirOf(face, u, v);
                    int i = row * n + col;
                    dirs[i] = dir;
                    lattice[i] = Frame.ToGodot(dir * field.SampleRadius(dir));
                }
            }

            // DirOf's (u, v) handedness is not the same on every face, so a
            // uniform winding leaves some faces inside-out -- which shows up
            // only as the ground vanishing when you walk onto them. Rather
            // than hard-code which faces flip, MEASURE one triangle: Godot's
            // front face is counter-clockwise, so cross(b-a, c-a) is the
            // front normal; if it points into the planet, reverse this face.
            bool flip = FacesInward(lattice, new[] { 0, 1, n });

            int triCount = (n - 1) * (n - 1) * 2;
            var verts = new Vector3[triCount * 3];
            var normals = new Vector3[triCount * 3];
            var colors = new Color[triCount * 3];
            int t = 0;
            void Tri(int a, int b, int c)
            {
                if (flip) (b, c) = (c, b);
                Vector3 pa = lattice[a], pb = lattice[b], pc = lattice[c];
                Vector3 normal = (pb - pa).Cross(pc - pa).Normalized();
                Vec3 centre = (dirs[a] + dirs[b] + dirs[c]).Normalized();
                Color color = Shade(field, centre, (pa + pb + pc) / 3f, t);
                verts[t * 3] = pa; verts[t * 3 + 1] = pb; verts[t * 3 + 2] = pc;
                normals[t * 3] = normal; normals[t * 3 + 1] = normal; normals[t * 3 + 2] = normal;
                colors[t * 3] = color; colors[t * 3 + 1] = color; colors[t * 3 + 2] = color;
                t++;
            }
            for (int row = 0; row < n - 1; row++)
            {
                for (int col = 0; col < n - 1; col++)
                {
                    int a = row * n + col, b = a + 1, c = a + n, d = c + 1;
                    Tri(a, b, c);
                    Tri(b, d, c);
                }
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            return arrays;
        }

        // The palette. Bands by height (h is 0..1 over RadiusMin..RadiusMax;
        // the 150 m datum sits at 0.39), each with a second colour the noise
        // blends toward so a plain is not one flat green; steep ground goes to
        // scree whatever its height. Keep these next to the world style guide
        // in docs/GDD.md ("Shared planet palette").
        private static readonly Color FloorA = new Color(0.52f, 0.47f, 0.40f), FloorB = new Color(0.46f, 0.36f, 0.30f);   // crater dust / rust dust
        private static readonly Color PlainA = new Color(0.43f, 0.55f, 0.36f), PlainB = new Color(0.31f, 0.42f, 0.29f);   // sage / moss
        private static readonly Color HighA = new Color(0.60f, 0.51f, 0.34f), HighB = new Color(0.50f, 0.44f, 0.36f);    // ochre / dun
        private static readonly Color PeakA = new Color(0.74f, 0.73f, 0.70f), PeakB = new Color(0.62f, 0.63f, 0.66f);    // pale / frost
        private static readonly Color Scree = new Color(0.36f, 0.30f, 0.26f);

        /// <summary>
        /// One flat colour per triangle: the height band, the noise patch
        /// inside it, scree on the steep, and a per-triangle jitter so a
        /// plain reads as faceted ground rather than a smooth ramp.
        /// `pos` is the triangle's centre in engine metres, `index` its
        /// number in the face (the jitter's only input).
        /// </summary>
        public static Color Shade(TerrainField field, Vec3 dir, Vector3 pos, int index)
        {
            double radius = field.SampleRadius(dir);
            float h = Mathf.Clamp((float)((radius - TerrainField.RadiusMin) /
                                          (TerrainField.RadiusMax - TerrainField.RadiusMin)), 0f, 1f);
            float patch = _noise == null ? 0.5f : 0.5f + 0.5f * _noise.GetNoise3D(pos.X, pos.Y, pos.Z);
            patch = Mathf.SmoothStep(0.35f, 0.65f, patch); // patches with edges, not a gradient

            Color floor = FloorA.Lerp(FloorB, patch);
            Color plain = PlainA.Lerp(PlainB, patch);
            Color high = HighA.Lerp(HighB, patch);
            Color peak = PeakA.Lerp(PeakB, patch);
            Color c = floor.Lerp(plain, Mathf.SmoothStep(0.30f, 0.36f, h));
            c = c.Lerp(high, Mathf.SmoothStep(0.50f, 0.58f, h));
            c = c.Lerp(peak, Mathf.SmoothStep(0.72f, 0.80f, h));

            float slope = (float)(field.Slope(dir) / TerrainField.MaxSlope);
            c = c.Lerp(Scree, Mathf.SmoothStep(0.45f, 0.75f, slope));

            // +-6% value, a different amount on every triangle.
            uint hash = (uint)index * 2654435761u ^ 0x9E3779B9u;
            hash ^= hash >> 15; hash *= 0x2C1B3C6Du; hash ^= hash >> 12;
            float jitter = 0.94f + 0.12f * ((hash & 0xFFFF) / 65535f);
            return new Color(c.R * jitter, c.G * jitter, c.B * jitter);
        }

        /// <summary>True when the first triangle's front normal points at the planet's centre.</summary>
        public static bool FacesInward(Vector3[] verts, int[] tris)
        {
            Vector3 a = verts[tris[0]], b = verts[tris[1]], c = verts[tris[2]];
            Vector3 normal = (b - a).Cross(c - a);
            return normal.Dot(a) < 0f; // `a` doubles as the outward direction
        }
    }
}
