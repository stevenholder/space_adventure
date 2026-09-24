// The camp and the range, drawn.
//
// The `colliders` message goes into the predictor; this draws the shapes the
// predictor walks into, so a wall you cannot pass is a wall you can see. The
// server owns those shapes (zone files in server/data/zones/), which is why
// they are not entities and never spawned.
//
// Visual only. The authoritative shape is the `colliders` message and nothing
// here feeds back into the sim — if a model and its collider ever disagree,
// the collider wins and the model is the bug.
//
// Positions and rotations cross from the Sim verbatim (Frame.cs): the Sim's
// frame IS Godot's, so a collider's quaternion is its basis, and the mirror
// the Unity build had to absorb no longer exists.

using System.Collections.Generic;
using Godot;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>
    /// Builds the visuals for the static colliders the server sends, and
    /// rebuilds them if a later `colliders` message replaces the set.
    /// </summary>
    public sealed class Structures
    {
        private readonly Node _parent;
        private readonly Material _material;
        private readonly AssetRegistry _assets;
        private Node3D _root;
        private Node3D _propRoot;

        public Structures(Node parent, Material material, AssetRegistry assets)
        {
            _parent = parent;
            _material = material;
            _assets = assets;
        }

        public void Build(Sim.Collider[] colliders)
        {
            _root?.QueueFree();
            _root = new Node3D { Name = "structures" };
            _parent.AddChild(_root);
            if (colliders == null) return;

            // BOX colliders get no visual here — zone layouts derive both the
            // colliders and the kit-piece props from one source (server
            // defs/layout.go), so the props message carries every wall's real
            // model. Spheres (cover posts) keep their path: no kit piece
            // replaces them yet.
            foreach (Sim.Collider c in colliders)
            {
                if (c.Kind == Sim.ColliderKind.Sphere) AddSphere(c);
            }
        }

        /// <summary>
        /// POI masts, harvested from the props (asset struct.mast.*): world
        /// position + whether the builder was Scrapyard. The compass gates
        /// discovery on these (GDD "Silhouette and the 23 m horizon").
        /// </summary>
        public readonly List<(Vector3 pos, bool scrap)> Masts = new List<(Vector3, bool)>();

        /// <summary>
        /// Zone dressing: barrels, a generator, a comms dish, bones. Authored
        /// in the zone files and composed to world space by the SERVER through
        /// the same transform the colliders go through. No collider of their
        /// own. Walk straight through a barrel.
        /// </summary>
        public void BuildProps(Prop[] props)
        {
            _propRoot?.QueueFree();
            _propRoot = new Node3D { Name = "props" };
            _parent.AddChild(_propRoot);
            Masts.Clear();
            if (props == null) return;

            foreach (Prop p in props)
            {
                float s = p.Scale <= 0f ? 1f : p.Scale;
                var q = new Quat(p.QuatX, p.QuatY, p.QuatZ, p.QuatW);
                var node = new Node3D
                {
                    Name = p.Asset.Replace('.', '_'),
                    Transform = new Transform3D(Frame.BasisOf(q).Scaled(new Vector3(s, s, s)),
                                                Frame.ToGodot(new Vec3(p.PosX, p.PosY, p.PosZ))),
                };
                _propRoot.AddChild(node);

                // No box fallback here, unlike walls. A wall you cannot see is
                // a wall you walk into; a barrel you cannot see is just not
                // there yet, and a grey cube standing in for it would be more
                // distracting than the gap.
                _assets.Attach(p.Asset, node, null);

                if (p.Asset.StartsWith("struct.mast."))
                    Masts.Add((node.GlobalPosition, p.Asset.EndsWith(".scrap")));
            }
            // Logged like the colliders beside them, so a headless run says
            // whether the zone dressing arrived at all.
            GD.Print($"props: {props.Length}");
        }

        /// <summary>
        /// struct.post is a unit-RADIUS sphere centred on the origin, so it
        /// takes the collider's centre directly and a uniform scale. Only
        /// Half.X carries the radius (Sim.Collider: "for a sphere only X is
        /// used").
        /// </summary>
        private void AddSphere(Sim.Collider c)
        {
            float r = (float)c.Half.X;
            Node3D node = Mount("struct.post", c);
            node.Transform = new Transform3D(Basis.Identity.Scaled(new Vector3(r, r, r)), Frame.ToGodot(c.Center));
        }

        /// <summary>
        /// A holder carrying the box fallback, with the real model attached
        /// over it when it loads. Scale and rotation live on the holder, so
        /// the swap costs nothing -- the .glb arrives as a child and inherits
        /// the transform already set here.
        /// </summary>
        private Node3D Mount(string assetId, Sim.Collider c)
        {
            var node = new Node3D { Name = assetId.Replace('.', '_') };
            _root.AddChild(node);
            ArrayMesh fallback = c.Kind == Sim.ColliderKind.Sphere ? Models.Post() : Models.Wall();
            MeshInstance3D box = BoxMesh.Attach(node, "model", fallback, _material, 0);
            _assets.Attach(assetId, node, _ => box?.QueueFree());
            return node;
        }
    }
}
