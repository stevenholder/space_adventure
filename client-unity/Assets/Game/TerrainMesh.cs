// U12 — the planet, built from the u16 radius grid the server sends.
//
// Six cube-sphere faces, each a FaceGrid x FaceGrid vertex lattice. A vertex
// sits at DirOf(face, u, v) * radius, where the radius comes from the SAME
// sampler the simulation uses. That shared sampler is the point: if the mesh
// were built from its own interpolation the player would visibly stand above
// or sink into ground the server thinks is somewhere else, and the mismatch
// would be worst exactly at the cube-face seams where it is hardest to debug.
//
// One mesh per face rather than one for the planet: 6 x 65 x 65 is 25,350
// vertices, comfortably inside the 16-bit index limit per mesh (65,535) with
// no need for a 32-bit index buffer.

using SpaceAdventure.Sim;
using UnityEngine;

namespace SpaceAdventure.Game
{
    public static class TerrainMesh
    {
        /// <summary>
        /// Builds the six face meshes under one parent and returns it.
        /// </summary>
        public static GameObject Build(TerrainField field, Material material, Transform parent = null)
        {
            var root = new GameObject("Planet");
            if (parent != null) root.transform.SetParent(parent, false);

            for (int face = 0; face < Face.Count; face++)
            {
                var go = new GameObject($"Face{face}");
                go.transform.SetParent(root.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = BuildFace(field, face);
                mr.sharedMaterial = material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            return root;
        }

        private static Mesh BuildFace(TerrainField field, int face)
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
                    verts[i] = ToUnity(dir * radius);
                    normals[i] = ToUnity(field.SurfaceNormal(dir));
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
            // than hard-code which faces flip, MEASURE one triangle: if its
            // normal points into the planet, reverse this face's indices.
            if (FacesInward(verts, tris))
            {
                for (int i = 0; i < tris.Length; i += 3)
                {
                    (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
                }
            }

            var mesh = new Mesh { name = $"planet-face-{face}" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Vertex colour stands in for a material library: height gives the
        /// base tint and slope darkens it, so the terrain reads as terrain
        /// without an art pipeline. Replaced when real materials land.
        /// </summary>
        private static Color Shade(TerrainField field, Vec3 dir, double radius)
        {
            float h = (float)((radius - TerrainField.RadiusMin) /
                              (TerrainField.RadiusMax - TerrainField.RadiusMin));
            h = Mathf.Clamp01(h);
            var low = new Color(0.22f, 0.28f, 0.20f);
            var high = new Color(0.55f, 0.52f, 0.45f);
            Color c = Color.Lerp(low, high, h);

            float slope = (float)(field.Slope(dir) / TerrainField.MaxSlope);
            return Color.Lerp(c, c * 0.55f, Mathf.Clamp01(slope));
        }

        /// <summary>
        /// Sim coordinates ARE Unity coordinates, component for component. No
        /// axis is flipped.
        ///
        /// It is tempting to negate Z for Unity's left-handedness, and it is a
        /// trap: mirroring one axis flips the sign of every cross product, so
        /// the body's "right" becomes its left and strafe inverts. Every
        /// position, normal and collider in this client comes from the same
        /// sim, so leaving the axes alone keeps the whole world
        /// self-consistent and keeps these numbers bit-comparable with Go.
        /// The world's chirality is unobservable without an outside reference;
        /// inverted strafe is not.
        /// </summary>
        public static Vector3 ToUnity(Vec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        /// <summary>The inverse of <see cref="ToUnity"/>.</summary>
        public static Vec3 ToSim(Vector3 v) => new Vec3(v.x, v.y, v.z);

        /// <summary>True when the first triangle's normal points at the planet's centre.</summary>
        private static bool FacesInward(Vector3[] verts, int[] tris)
        {
            Vector3 a = verts[tris[0]], b = verts[tris[1]], c = verts[tris[2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            return Vector3.Dot(normal, a) < 0f; // `a` doubles as the outward direction
        }
    }
}
