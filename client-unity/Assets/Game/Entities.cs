// U13 — remote entity views, and the render clock they are drawn on.
//
// THE RULE THIS FILE OWES THE SERVER: remotes are drawn at
// `serverClock - interp_delay`, on a clock synchronised to the server's. NOT
// at a fixed offset behind whenever a packet happened to arrive locally.
//
// That is not a stylistic choice. The server rewinds every shot by exactly
// interp_delay plus the one-way trip (GDD "Lag compensation"), so it is
// reconstructing this render point. Render on local arrival instead and the
// client sits a whole one-way trip further into the past than the server
// rewinds to — a player then misses every moving target, and no server-side
// test can see it. The retired TypeScript client had exactly that bug and C14
// caught it only at the wire level (docs/QA-STATUS.md).
//
// So: SnapshotTimeline estimates the server's tick from arrivals, and views
// interpolate between the two snapshots bracketing `serverTick - interpTicks`.

using System.Collections.Generic;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;
using UnityEngine;

namespace SpaceAdventure.Game
{
    /// <summary>One entity as the client draws it.</summary>
    public sealed class EntityView
    {
        public uint Id;
        public ushort Type;
        public string Label;
        public GameObject Root;
        public ushort Health;
        public bool Dead;
        public string EquippedItem = "";
    }

    /// <summary>
    /// Builds and updates the GameObjects for everything the server tells us
    /// about. Owns no networking and no input — it is handed decoded rows.
    /// </summary>
    public sealed class EntityViews
    {
        private readonly Dictionary<uint, EntityView> _views = new Dictionary<uint, EntityView>();
        private readonly Dictionary<uint, string> _pendingLabels = new Dictionary<uint, string>();
        private readonly Dictionary<uint, ushort> _pendingTypes = new Dictionary<uint, ushort>();
        private readonly Transform _parent;
        private readonly Material _material;

        public EntityViews(Transform parent, Material material)
        {
            _parent = parent;
            _material = material;
        }

        public IEnumerable<EntityView> All => _views.Values;

        public bool TryGet(uint id, out EntityView view) => _views.TryGetValue(id, out view);

        /// <summary>Records what a `spawn` said, for the row that follows it.</summary>
        public void OnSpawn(Spawn spawn)
        {
            _pendingTypes[spawn.EntityId] = spawn.EntityType;
            _pendingLabels[spawn.EntityId] = spawn.DataUtf8;
        }

        public void OnDespawn(uint id)
        {
            if (_views.TryGetValue(id, out var v))
            {
                Object.Destroy(v.Root);
                _views.Remove(id);
            }
            _pendingTypes.Remove(id);
            _pendingLabels.Remove(id);
        }

        /// <summary>Records an `equipped` event so a body can show its weapon.</summary>
        public void OnEquipped(uint id, string item)
        {
            if (_views.TryGetValue(id, out var v)) v.EquippedItem = item;
        }

        /// <summary>
        /// Places every entity at the interpolated pose, skipping
        /// <paramref name="selfId"/> — the local body is predicted, never
        /// interpolated, or it would lag its own input by interp_delay.
        /// </summary>
        public void Render(SnapshotTimeline timeline, uint selfId)
        {
            foreach (var kv in timeline.Interpolate())
            {
                uint id = kv.Key;
                if (id == selfId) continue;

                if (!_views.TryGetValue(id, out var view))
                {
                    view = Create(id);
                    _views[id] = view;
                }

                view.Health = kv.Value.Health;
                view.Dead = kv.Value.Dead;
                view.Root.transform.position = TerrainMesh.ToUnity(kv.Value.Pos);

                // Stand the body along its own local up. On a sphere "upright"
                // is a different direction at every point, so a body oriented
                // by yaw alone lies down as soon as it walks any distance.
                Vector3 up = TerrainMesh.ToUnity(kv.Value.Pos.Normalized());
                Vector3 fwd = TerrainMesh.ToUnity(kv.Value.Facing);
                if (fwd.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f)
                {
                    view.Root.transform.rotation = Quaternion.LookRotation(
                        Vector3.ProjectOnPlane(fwd, up).normalized, up);
                }
                view.Root.SetActive(!kv.Value.Dead);
            }
        }

        private EntityView Create(uint id)
        {
            _pendingTypes.TryGetValue(id, out ushort type);
            _pendingLabels.TryGetValue(id, out string label);

            var root = new GameObject($"entity-{id}");
            root.transform.SetParent(_parent, false);

            // Primitive capsules and cubes stand in for art. They are sized
            // from the hitbox the server actually resolves against, so what
            // you shoot at is what the server tests — a placeholder that is
            // the wrong SIZE teaches the wrong aim.
            var body = GameObject.CreatePrimitive(type == EntityType.Loot ? PrimitiveType.Cube : PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<UnityEngine.Collider>()); // collision is the sim's job, not PhysX's
            body.transform.SetParent(root.transform, false);

            float height = type == EntityType.Loot ? 0.4f : 1.8f;
            float radius = type == EntityType.Loot ? 0.4f : 0.7f;
            body.transform.localScale = type == EntityType.Loot
                ? new Vector3(height, height, height)
                : new Vector3(radius, height * 0.5f, radius);
            // A capsule's origin is its middle; entity positions are at the feet.
            body.transform.localPosition = type == EntityType.Loot
                ? new Vector3(0, height * 0.5f, 0)
                : new Vector3(0, height * 0.5f, 0);

            var mr = body.GetComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(_material) { color = ColorFor(type) };

            return new EntityView { Id = id, Type = type, Label = label ?? "", Root = root };
        }

        private static Color ColorFor(ushort type) => type switch
        {
            EntityType.Player => new Color(0.35f, 0.65f, 0.95f),
            EntityType.Npc => new Color(0.90f, 0.35f, 0.30f),
            EntityType.Target => new Color(0.95f, 0.80f, 0.25f),
            EntityType.Loot => new Color(0.55f, 0.95f, 0.45f),
            EntityType.Projectile => new Color(1.00f, 0.60f, 0.10f),
            _ => Color.gray,
        };
    }

    /// <summary>An entity's pose at the render instant.</summary>
    public struct Pose
    {
        public Vec3 Pos;
        public Vec3 Facing;
        public ushort Health;
        public bool Dead;
    }

    /// <summary>
    /// Keeps the last few snapshots and answers "where was everything at
    /// `serverClock - interp_delay`".
    ///
    /// The clock is estimated, not measured: a snapshot for tick T that
    /// arrives now means the server was at T one one-way trip ago, so it is at
    /// about T + oneWay now. Adding the elapsed time since arrival keeps the
    /// estimate moving between snapshots instead of stepping at 20 Hz.
    /// </summary>
    public sealed class SnapshotTimeline
    {
        /// <summary>
        /// interp_delay, in seconds. The GDD's number, and the server rewinds
        /// by it — changing this here alone silently breaks hit registration
        /// for this client only.
        /// </summary>
        public const double InterpDelaySeconds = 0.1;

        private const int MaxBuffered = 32;

        private readonly List<(uint Tick, float At, Dictionary<uint, Pose> Poses)> _buf =
            new List<(uint, float, Dictionary<uint, Pose>)>();

        private readonly Dictionary<uint, Pose> _out = new Dictionary<uint, Pose>();

        /// <summary>One-way trip in seconds, from the transport's RTT.</summary>
        public double OneWaySeconds { get; set; }

        public void Add(Snapshot snap, float atTime)
        {
            var poses = new Dictionary<uint, Pose>(snap.Entities.Length);
            foreach (var e in snap.Entities)
            {
                poses[e.Id] = new Pose
                {
                    Pos = new Vec3(e.PosX, e.PosY, e.PosZ),
                    Facing = FacingOf(e),
                    Health = e.Health,
                    Dead = e.Dead,
                };
            }
            _buf.Add((snap.Tick, atTime, poses));
            if (_buf.Count > MaxBuffered) _buf.RemoveAt(0);
        }

        public void Clear() => _buf.Clear();

        /// <summary>
        /// The world at `serverClock - interp_delay`, interpolated between the
        /// two snapshots that bracket it. Extrapolates forward when the render
        /// point is past the newest snapshot, which happens whenever the
        /// one-way trip exceeds interp_delay — i.e. on any connection worse
        /// than 200 ms round trip.
        /// </summary>
        public IEnumerable<KeyValuePair<uint, Pose>> Interpolate()
        {
            _out.Clear();
            if (_buf.Count == 0) return _out;

            var newest = _buf[_buf.Count - 1];
            double tickSeconds = 1.0 / Rules.TickHz;
            double elapsed = Time.time - newest.At;

            // Server tick now, then the render point interp_delay behind it,
            // expressed on the same tick timeline the snapshots carry.
            double serverNow = newest.Tick + (elapsed + OneWaySeconds) / tickSeconds;
            double renderTick = serverNow - InterpDelaySeconds / tickSeconds;

            if (_buf.Count == 1 || renderTick >= newest.Tick)
            {
                foreach (var kv in newest.Poses) _out[kv.Key] = kv.Value;
                return _out;
            }

            for (int i = _buf.Count - 1; i >= 1; i--)
            {
                var b = _buf[i];
                var a = _buf[i - 1];
                if (renderTick < a.Tick) continue;

                double span = b.Tick - a.Tick;
                float k = span > 0 ? (float)((renderTick - a.Tick) / span) : 1f;
                foreach (var kv in b.Poses)
                {
                    if (a.Poses.TryGetValue(kv.Key, out var from))
                    {
                        _out[kv.Key] = new Pose
                        {
                            Pos = Lerp(from.Pos, kv.Value.Pos, k),
                            Facing = Lerp(from.Facing, kv.Value.Facing, k),
                            Health = kv.Value.Health,
                            Dead = kv.Value.Dead,
                        };
                    }
                    else
                    {
                        _out[kv.Key] = kv.Value; // appeared this tick
                    }
                }
                return _out;
            }

            foreach (var kv in _buf[0].Poses) _out[kv.Key] = kv.Value;
            return _out;
        }

        private static Vec3 Lerp(Vec3 a, Vec3 b, float k) => a + (b - a) * k;

        /// <summary>
        /// The row's quaternion turned back into a facing direction, in SIM
        /// space — the caller converts.
        ///
        /// The rotation is applied with Sim.Quat, not UnityEngine.Quaternion.
        /// The wire quaternion describes a right-handed basis whose Z axis is
        /// the facing (Step.OrientationQuat), and feeding those components to
        /// a left-handed Quaternion mixes the two conventions in a way that
        /// happens to look plausible and points bodies the wrong way.
        /// </summary>
        internal static Vec3 FacingOf(EntityRow e)
        {
            if (e.QuatX == 0 && e.QuatY == 0 && e.QuatZ == 0 && e.QuatW == 0) return new Vec3(0, 0, 1);
            var q = new Quat(e.QuatX, e.QuatY, e.QuatZ, e.QuatW);
            return Quat.Rotate(q, new Vec3(0, 0, 1));
        }
    }
}
