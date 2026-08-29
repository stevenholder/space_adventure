// The camp and the range, drawn.
//
// The `colliders` message has been arriving since Phase 2 and going straight
// into the predictor -- Boot decoded it, handed it to Collide.ResolveColliders
// and drew nothing. So the range walls, the backstop and the camp perimeter
// were all solid and all invisible: you walked into a wall that was not there,
// and took cover behind nothing. The server owns those shapes (zone files in
// server/data/zones/), which is why they are not entities and never spawned.
//
// This is visual only. The authoritative shape is still the `colliders`
// message and nothing here feeds back into the sim -- if a model and its
// collider ever disagree, the collider wins and the model is the bug.
//
// COORDINATE FRAMES. Two conversions have to be right, and only one of them is
// obvious.
//
//   Position is easy: TerrainMesh.ToUnity, same as everything else.
//
//   Rotation is not. ToUnity is (x, y, z) -> (x, y, -z), a handedness flip,
//   and a quaternion does not survive one unchanged. Rather than write down
//   the conjugation identity and hope, the collider's own local axes are
//   rotated in SIM space -- where Quat.Rotate is the same code the server and
//   the predictor use -- and then converted with the very function that is
//   already trusted for every vertex on screen. The Unity rotation is rebuilt
//   from those two converted axes. One conversion, used twice, instead of two
//   conversions that have to agree.
//
//   A mirror is not a rotation, so the rebuilt basis has its X axis negated
//   against the sim's. That is exact here and not a tolerated approximation:
//   both shapes involved -- a box centred on its own origin, a sphere -- map
//   onto themselves under x -> -x, and so does struct.wall.glb, whose
//   footprint is centred in XZ. A future structure that is NOT symmetric
//   about its local X would need the mirror handled rather than absorbed.

using UnityEngine;
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
        private readonly Transform _parent;
        private readonly Material _material;
        private readonly AssetRegistry _assets;
        private GameObject _root;
        private GameObject _propRoot;

        public Structures(Transform parent, Material material, AssetRegistry assets)
        {
            _parent = parent;
            _material = material;
            _assets = assets;
        }

        public void Build(Sim.Collider[] colliders)
        {
            if (_root != null) Object.Destroy(_root);
            _root = new GameObject("structures");
            _root.transform.SetParent(_parent, false);
            if (colliders == null) return;

            foreach (Sim.Collider c in colliders)
            {
                if (c.Kind == Sim.ColliderKind.Sphere) AddSphere(c);
                else AddBox(c);
            }
        }

        /// <summary>
        /// Zone dressing: barrels, a generator, a comms dish, bones.
        ///
        /// Authored in the zone files and composed to world space by the
        /// SERVER, through the very same transform the colliders go through --
        /// which is what lets an author put a barrel at (12, 0, 11) by reading
        /// the collider list. The client does not decide where any of this
        /// goes, and could not: the colliders arrive world-space with no zone
        /// identity, so nothing here can tell the camp's walls from the
        /// range's.
        ///
        /// No collider of their own. Walk straight through a barrel.
        /// </summary>
        public void BuildProps(Prop[] props)
        {
            if (_propRoot != null) Object.Destroy(_propRoot);
            _propRoot = new GameObject("props");
            _propRoot.transform.SetParent(_parent, false);
            if (props == null) return;

            foreach (Prop p in props)
            {
                var go = new GameObject(p.Asset);
                go.transform.SetParent(_propRoot.transform, false);
                go.transform.position = TerrainMesh.ToUnity(
                    new Vec3(p.PosX, p.PosY, p.PosZ));

                // Same handedness problem as a box collider, same answer:
                // rotate the prop's own axes in sim space and convert those,
                // rather than converting a quaternion across a mirror.
                var q = new Quat(p.QuatX, p.QuatY, p.QuatZ, p.QuatW);
                Vector3 up = TerrainMesh.ToUnity(Quat.Rotate(q, new Vec3(0, 1, 0)));
                Vector3 fwd = TerrainMesh.ToUnity(Quat.Rotate(q, new Vec3(0, 0, 1)));
                go.transform.rotation = Quaternion.LookRotation(fwd, up);

                float s = p.Scale <= 0f ? 1f : p.Scale;
                go.transform.localScale = new Vector3(s, s, s);

                // No box fallback here, unlike walls. A wall you cannot see is
                // a wall you walk into; a barrel you cannot see is just not
                // there yet, and a grey cube standing in for it would be more
                // distracting than the gap.
                _assets.Attach(p.Asset, go.transform, null);
            }
            // Logged like the colliders beside them, so a headless run says
            // whether the zone dressing arrived at all.
            Debug.Log($"props: {props.Length}");
        }

        /// <summary>
        /// struct.wall is a UNIT box with its base at the origin and a 1x1
        /// footprint (x,z in [-0.5, 0.5], y in [0, 1]) -- so scaling by the
        /// full extents gives the collider's size, but the model then stands
        /// ON its centre instead of being centred on it. The half-height is
        /// subtracted back off along the collider's own up axis, not world up:
        /// on a sphere world every wall has a different idea of which way is
        /// down.
        /// </summary>
        private void AddBox(Sim.Collider c)
        {
            Vector3 up = TerrainMesh.ToUnity(Quat.Rotate(c.Rot, new Vec3(0, 1, 0)));
            Vector3 fwd = TerrainMesh.ToUnity(Quat.Rotate(c.Rot, new Vec3(0, 0, 1)));

            GameObject go = Mount("struct.wall", c);
            go.transform.rotation = Quaternion.LookRotation(fwd, up);
            go.transform.position = TerrainMesh.ToUnity(c.Center) - up * (float)c.Half.Y;
            go.transform.localScale = new Vector3(
                (float)c.Half.X * 2f, (float)c.Half.Y * 2f, (float)c.Half.Z * 2f);
        }

        /// <summary>
        /// struct.post is a unit-RADIUS sphere centred on the origin, so it
        /// takes the collider's centre directly and a uniform scale. Only
        /// Half.X carries the radius (Sim.Collider: "for a sphere only X is
        /// used").
        /// </summary>
        private void AddSphere(Sim.Collider c)
        {
            GameObject go = Mount("struct.post", c);
            go.transform.position = TerrainMesh.ToUnity(c.Center);
            float r = (float)c.Half.X;
            go.transform.localScale = new Vector3(r, r, r);
        }

        /// <summary>
        /// A holder carrying the box fallback, with the real model attached
        /// over it when it loads. Scale and rotation live on the holder, so
        /// the swap costs nothing -- the .glb arrives as a child and inherits
        /// the transform already set here.
        /// </summary>
        private GameObject Mount(string assetId, Sim.Collider c)
        {
            var go = new GameObject(assetId);
            go.transform.SetParent(_root.transform, false);

            Mesh fallback = c.Kind == Sim.ColliderKind.Sphere ? Models.Post() : Models.Wall();
            GameObject box = BoxMesh.Attach(go.transform, "model", fallback, _material, 0);
            _assets.Attach(assetId, go.transform, _ => { if (box != null) Object.Destroy(box); });
            return go;
        }
    }
}
