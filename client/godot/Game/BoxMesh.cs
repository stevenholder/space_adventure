// A model is a list of coloured boxes, welded into one mesh.
//
// A character built from primitives is a dozen nodes, a dozen materials and a
// dozen draw calls, and it looks like a dozen primitives. Baking the boxes into
// one mesh with per-vertex colour gives one instance, one material, one draw
// call, and hard flat shading, which is the low-poly look the GDD asks for.
//
// Each box gets its own 24 vertices rather than sharing the 8 corners: shared
// corners force averaged normals, which rounds off every edge.
//
// Colours ride in the vertex stream, so the whole model renders with the same
// vertex-colour material the terrain uses.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game
{
    /// <summary>One box in a model: a centre, a size, a colour, and Euler degrees.</summary>
    public readonly struct Box
    {
        public readonly Vector3 Centre;
        public readonly Vector3 Size;
        public readonly Color Colour;
        public readonly Vector3 Euler;

        public Box(Vector3 centre, Vector3 size, Color colour, Vector3 euler = default)
        {
            Centre = centre;
            Size = size;
            Colour = colour;
            Euler = euler;
        }
    }

    public static class BoxMesh
    {
        // The six faces, each as an outward normal plus the two in-plane axes
        // that sweep its corners. Written once here so the winding is right
        // once here. Godot's Forward is −Z and Back is +Z; the pairs below are
        // the same axes the Unity build used, spelled in Godot's names.
        private static readonly Vector3[] Normals =
        {
            Vector3.Right, Vector3.Left, Vector3.Up,
            Vector3.Down, Vector3.Back, Vector3.Forward,
        };

        private static readonly Vector3[] Tangents =
        {
            Vector3.Back, Vector3.Forward, Vector3.Right,
            Vector3.Right, Vector3.Left, Vector3.Right,
        };

        public static ArrayMesh Build(IReadOnlyList<Box> boxes, string name)
        {
            int n = boxes.Count;
            var verts = new Vector3[n * 24];
            var norms = new Vector3[n * 24];
            var colours = new Color[n * 24];
            var tris = new int[n * 36];

            int v = 0, t = 0;
            foreach (Box box in boxes)
            {
                Basis rot = box.Euler == Vector3.Zero
                    ? Basis.Identity
                    : Basis.FromEuler(box.Euler * (Mathf.Pi / 180f));
                Vector3 half = box.Size * 0.5f;

                for (int f = 0; f < 6; f++)
                {
                    Vector3 nrm = Normals[f];
                    Vector3 tan = Tangents[f];
                    Vector3 bit = nrm.Cross(tan);

                    // Face centre, then its four corners swept by the two
                    // in-plane axes. Scaling happens before rotation so a
                    // rotated box keeps its own proportions.
                    Vector3 fc = nrm * half;
                    Vector3 a = tan * half;
                    Vector3 b = bit * half;

                    verts[v + 0] = box.Centre + rot * (fc - a - b);
                    verts[v + 1] = box.Centre + rot * (fc + a - b);
                    verts[v + 2] = box.Centre + rot * (fc + a + b);
                    verts[v + 3] = box.Centre + rot * (fc - a + b);

                    Vector3 worldNormal = rot * nrm;
                    for (int i = 0; i < 4; i++)
                    {
                        norms[v + i] = worldNormal;
                        colours[v + i] = box.Colour;
                    }

                    // Wound so that Cross(v1-v0, v2-v0) points along the
                    // face's own outward normal: Godot's front face is
                    // counter-clockwise, and that is what this produces.
                    tris[t + 0] = v + 0; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                    tris[t + 3] = v + 0; tris[t + 4] = v + 2; tris[t + 5] = v + 3;

                    v += 4;
                    t += 6;
                }
            }

            if (!FacesOutward(verts, norms, tris))
                GD.PushError($"{name}: winding disagrees with the face normals — the model will render inside out");

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = norms;
            arrays[(int)Mesh.ArrayType.Color] = colours;
            arrays[(int)Mesh.ArrayType.Index] = tris;
            var mesh = new ArrayMesh { ResourceName = name };
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }

        /// <summary>
        /// True when the first triangle is wound to match its declared normal.
        /// Every face is built by the same formula, so one is enough.
        /// </summary>
        public static bool FacesOutward(Vector3[] verts, Vector3[] norms, int[] tris)
        {
            if (tris.Length < 3) return true;
            Vector3 a = verts[tris[0]], b = verts[tris[1]], c = verts[tris[2]];
            return (b - a).Cross(c - a).Dot(norms[tris[0]]) > 0f;
        }

        /// <summary>Renders a built mesh under a new child node. Layers 0 means "the default layer".</summary>
        public static MeshInstance3D Attach(Node3D parent, string name, Mesh mesh, Material material, uint layers)
        {
            var mi = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = material };
            if (layers != 0)
            {
                mi.Layers = layers;
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }
            parent.AddChild(mi);
            return mi;
        }
    }
}
