// Drawing the rock scatter.
//
// Where the rocks go is RockScatter, in shared/Core, engine-free and checked
// headless. This file is only the part that needs the engine: turning
// placements into instance transforms and submitting them once.
//
// The asteroid is a 150 m ball with a 23 m horizon and nothing on it but six
// landmarks, so without these there is no near-field detail anywhere --
// nothing to judge speed or distance against while walking, which on a sphere
// this small is most of what tells you that you are moving.
//
// MultiMesh, not nodes: four hundred rocks is three draw calls and no
// transforms to cull and re-evaluate every frame, for props that never move.
// The transforms are set once at build; nothing runs per frame.

using System.Collections.Generic;
using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class Rocks
    {
        private static readonly string[] VariantAssets =
            { "prop.rock.a", "prop.rock.b", "prop.rock.c" };

        private readonly Node _parent;
        private readonly Material _material;
        private readonly AssetRegistry _assets;
        private Node3D _root;
        private readonly List<Vector3> _positions = new List<Vector3>(); // every rock, for the screenshot rig

        public Rocks(Node parent, Material material, AssetRegistry assets)
        {
            _parent = parent;
            _material = material;
            _assets = assets;
        }

        /// <summary>The rock nearest `from`, or null before Build: `-uiFace rock`.</summary>
        public Vector3? Nearest(Vector3 from)
        {
            Vector3? best = null; float bestD = float.MaxValue;
            foreach (Vector3 r in _positions)
            {
                float d = r.DistanceSquaredTo(from);
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        /// <summary>
        /// Scatter for this world, then load the three rock models.
        ///
        /// A variant whose .glb is missing is simply not drawn. That is
        /// deliberately unlike every other asset here, which shows a box until
        /// its model lands: four hundred cubes strewn across the planet read as
        /// a bug, and they would be the most visible thing on screen.
        /// </summary>
        public void Build(TerrainField terrain, uint worldSeed)
        {
            _root?.QueueFree();
            _root = new Node3D { Name = "rocks" };
            _parent.AddChild(_root);

            _positions.Clear();
            var batches = new List<Transform3D>[] { new List<Transform3D>(), new List<Transform3D>(), new List<Transform3D>() };
            foreach (RockPlacement p in RockScatter.Scatter(terrain, worldSeed))
            {
                // Local +Y along the surface direction, spun about that same
                // axis, scaled on the rock's own axes. Sim frame is Godot's,
                // so `up` and the spin sense cross over unchanged.
                Vector3 up = Frame.ToGodot(p.Dir).Normalized();
                Basis align = up.Dot(Vector3.Up) < -0.9999f
                    ? new Basis(Vector3.Right, Mathf.Pi)
                    : new Basis(new Quaternion(Vector3.Up, up));
                Basis basis = align.Rotated(up, (float)p.Spin)
                            * Basis.FromScale(Frame.ToGodot(p.Scale));
                batches[p.Variant].Add(new Transform3D(basis, Frame.ToGodot(p.Pos)));
                _positions.Add(Frame.ToGodot(p.Pos));
            }

            for (int v = 0; v < 3; v++)
            {
                if (batches[v].Count == 0) continue;
                List<Transform3D> batch = batches[v];
                _assets.Mesh(VariantAssets[v], mesh =>
                {
                    var mm = new MultiMesh
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        Mesh = mesh,
                        InstanceCount = batch.Count,
                    };
                    for (int i = 0; i < batch.Count; i++) mm.SetInstanceTransform(i, batch[i]);
                    _root.AddChild(new MultiMeshInstance3D
                    {
                        Name = VariantAssets[v].Replace('.', '_'),
                        Multimesh = mm,
                        MaterialOverride = _material,
                    });
                });
            }
            GD.Print($"rocks: {batches[0].Count}/{batches[1].Count}/{batches[2].Count}");
        }
    }
}
