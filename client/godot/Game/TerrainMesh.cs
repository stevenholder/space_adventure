// The planet, built from the u16 radius grid the server sends.
//
// Six cube-sphere faces, each a FaceGrid x FaceGrid vertex lattice. A vertex
// sits at DirOf(face, u, v) * radius, where the radius comes from the SAME
// sampler the simulation uses. That shared sampler is the point: if the mesh
// were built from its own interpolation the player would visibly stand above
// or sink into ground the server thinks is somewhere else, and the mismatch
// would be worst exactly at the cube-face seams where it is hardest to debug.
//
// One ArrayMesh with six surfaces: 6 x 65 x 65 is 25,350 vertices, and Godot
// picks the index width itself.

using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public static class TerrainMesh
    {
        /// <summary>Builds the planet under `parent` and returns its node.</summary>
        public static MeshInstance3D Build(TerrainField field, Material material, Node parent)
        {
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

        private static Godot.Collections.Array BuildFace(TerrainField field, int face)
        {
            const int n = TerrainField.FaceGrid;
            var verts = new Vector3[n * n];
            var normals = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            var colors = new Color[n * n];

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
                    double radius = field.SampleRadius(dir);

                    int i = row * n + col;
                    verts[i] = Frame.ToGodot(dir * radius);
                    normals[i] = Frame.ToGodot(field.SurfaceNormal(dir));
                    uvs[i] = new Vector2((float)((u + 1) * 0.5), (float)((v + 1) * 0.5));
                    colors[i] = Shade(field, dir, radius);
                }
            }

            var tris = new int[(n - 1) * (n - 1) * 6];
            int t = 0;
            for (int row = 0; row < n - 1; row++)
            {
                for (int col = 0; col < n - 1; col++)
                {
                    int a = row * n + col, b = a + 1, c = a + n, d = c + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }
            }

            // DirOf's (u, v) handedness is not the same on every face, so a
            // uniform winding leaves some faces inside-out — which shows up
            // only as the ground vanishing when you walk onto them. Rather
            // than hard-code which faces flip, MEASURE one triangle: Godot's
            // front face is counter-clockwise, so cross(b−a, c−a) is the
            // front normal; if it points into the planet, reverse this face.
            if (FacesInward(verts, tris))
            {
                for (int i = 0; i < tris.Length; i += 3)
                {
                    (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
                }
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.Index] = tris;
            return arrays;
        }

        /// <summary>
        /// Vertex colour stands in for a material library: height gives the
        /// base tint and slope darkens it, so the terrain reads as terrain
        /// without an art pipeline.
        /// </summary>
        private static Color Shade(TerrainField field, Vec3 dir, double radius)
        {
            float h = (float)((radius - TerrainField.RadiusMin) /
                              (TerrainField.RadiusMax - TerrainField.RadiusMin));
            h = Mathf.Clamp(h, 0f, 1f);
            var low = new Color(0.22f, 0.28f, 0.20f);
            var high = new Color(0.55f, 0.52f, 0.45f);
            Color c = low.Lerp(high, h);

            float slope = (float)(field.Slope(dir) / TerrainField.MaxSlope);
            return c.Lerp(c * 0.55f, Mathf.Clamp(slope, 0f, 1f));
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
