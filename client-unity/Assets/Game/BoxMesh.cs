// A model is a list of coloured boxes, welded into one mesh.
//
// Why this instead of GameObject.CreatePrimitive: a character built from
// primitives is a dozen GameObjects, a dozen renderers, a dozen materials and
// a dozen draw calls, and it looks like a dozen primitives — smooth-shaded
// capsules that read as a pill rather than a person. Baking the boxes into one
// mesh with per-vertex colour gives one renderer, one material, one draw call,
// and hard flat shading, which is the low-poly look the GDD actually asks for.
//
// Each box gets its own 24 vertices rather than sharing the 8 corners: shared
// corners force averaged normals, which rounds off every edge. Twenty-four is
// what makes a cube look like a cube.
//
// Colours ride in the vertex stream, so the whole model renders with the same
// vertex-colour shader the terrain uses. No textures, no materials per part.

using System.Collections.Generic;
using UnityEngine;

namespace SpaceAdventure.Game
{
    /// <summary>One box in a model: a centre, a size, a colour, and a yaw.</summary>
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
        // once here.
        private static readonly Vector3[] Normals =
        {
            Vector3.right, Vector3.left, Vector3.up,
            Vector3.down, Vector3.forward, Vector3.back,
        };

        private static readonly Vector3[] Tangents =
        {
            Vector3.forward, Vector3.back, Vector3.right,
            Vector3.right, Vector3.left, Vector3.right,
        };

        public static Mesh Build(IReadOnlyList<Box> boxes, string name)
        {
            int n = boxes.Count;
            var verts = new Vector3[n * 24];
            var norms = new Vector3[n * 24];
            var colours = new Color[n * 24];
            var tris = new int[n * 36];

            int v = 0, t = 0;
            foreach (Box box in boxes)
            {
                Quaternion rot = box.Euler == Vector3.zero
                    ? Quaternion.identity
                    : Quaternion.Euler(box.Euler);
                Vector3 half = box.Size * 0.5f;

                for (int f = 0; f < 6; f++)
                {
                    Vector3 nrm = Normals[f];
                    Vector3 tan = Tangents[f];
                    Vector3 bit = Vector3.Cross(nrm, tan);

                    // Face centre, then its four corners swept by the two
                    // in-plane axes. Scaling happens before rotation so a
                    // rotated box keeps its own proportions.
                    Vector3 fc = Vector3.Scale(nrm, half);
                    Vector3 a = Vector3.Scale(tan, half);
                    Vector3 b = Vector3.Scale(bit, half);

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
                    // face's own outward normal. That is the convention the
                    // terrain mesh already demonstrates in this project; the
                    // other order culls every face and the model vanishes.
                    tris[t + 0] = v + 0; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                    tris[t + 3] = v + 0; tris[t + 4] = v + 2; tris[t + 5] = v + 3;

                    v += 4;
                    t += 6;
                }
            }

            Debug.Assert(FacesOutward(verts, norms, tris),
                $"{name}: winding disagrees with the face normals — the model will render inside out");

            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.colors = colours;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// True when the first triangle is wound to match its declared normal.
        /// Every face is built by the same formula, so one is enough — and a
        /// silent disagreement here is invisible in code and total on screen.
        /// </summary>
        private static bool FacesOutward(Vector3[] verts, Vector3[] norms, int[] tris)
        {
            if (tris.Length < 3) return true;
            Vector3 a = verts[tris[0]], b = verts[tris[1]], c = verts[tris[2]];
            return Vector3.Dot(Vector3.Cross(b - a, c - a), norms[tris[0]]) > 0f;
        }

        /// <summary>Renders a built mesh under a new child object.</summary>
        public static GameObject Attach(Transform parent, string name, Mesh mesh, Material material, int layer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            if (layer != 0)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            return go;
        }
    }
}
