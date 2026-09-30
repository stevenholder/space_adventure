// Remote entity views, and the render clock they are drawn on.
//
// THE RULE THIS FILE OWES THE SERVER: remotes are drawn at
// `serverClock - interp_delay`, on a clock synchronised to the server's. NOT
// at a fixed offset behind whenever a packet happened to arrive locally.
//
// The server rewinds every shot by exactly interp_delay plus the one-way trip
// (GDD "Lag compensation"), so it is reconstructing this render point. Render
// on local arrival instead and the client sits a whole one-way trip further
// into the past than the server rewinds to — a player then misses every
// moving target, and no server-side test can see it.
//
// So: SnapshotTimeline (shared/Core/Timeline.cs, engine-free so SimDump can
// verify it headless) estimates the server's tick from arrivals, and views
// here interpolate between the two snapshots bracketing that render point.

using System.Collections.Generic;
using Godot;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>One entity as the client draws it.</summary>
    public sealed class EntityView
    {
        public uint Id;
        public ushort Type;
        public string Label;
        public Node3D Root;
        public ushort Health;
        public bool Dead;
        public string EquippedItem = "";

        /// <summary>Set once the model loads, null for anything without clips.</summary>
        public CharacterAnim Anim;

        /// <summary>The loaded body, once it arrives; null while it is boxes.</summary>
        public Node3D Model;

        /// <summary>What is currently in this body's hand, and which item it is.</summary>
        public Node3D Held;
        public string HeldItem = "";

        /// <summary>When this body was first seen dead, for the death fade.</summary>
        public double DiedAt = -1;

        /// <summary>Occupancy from the snapshot; nonzero = seated, not drawn.</summary>
        public uint ParentId;

        /// <summary>Phase 12: the tint last applied, so it is set once per change.</summary>
        public bool DepletedDrawn;

        /// <summary>
        /// Where this body was drawn last frame, and how fast it is therefore
        /// moving. Speed comes from the drawn positions rather than from the
        /// wire because the drawn positions are what the animation has to
        /// agree with -- see CharacterAnim.
        /// </summary>
        public Vector3 LastDrawn;
        public bool HasLastDrawn;
        public float Speed;

        /// <summary>
        /// The most health this entity has ever been seen with. Entities spawn
        /// and respawn at full, so the high-water mark IS the maximum.
        /// </summary>
        public ushort MaxHealth;

        public float HealthFraction => MaxHealth == 0 ? 1f : Mathf.Clamp((float)Health / MaxHealth, 0f, 1f);

        /// <summary>Health bars are for the wounded; a full bar is noise. A node's health is yields, not wounds.</summary>
        public bool ShowHealthBar => Type != EntityType.Node && !Dead && MaxHealth > 0 && Health > 0 && Health < MaxHealth;

        /// <summary>Phase 12: a node with no yields left (snapshot health 0).</summary>
        public bool Depleted => Type == EntityType.Node && Health == 0;
    }

    /// <summary>
    /// Builds and updates the nodes for everything the server tells us about.
    /// Owns no networking and no input — it is handed decoded rows.
    /// </summary>
    public sealed class EntityViews
    {
        private readonly Dictionary<uint, EntityView> _views = new Dictionary<uint, EntityView>();
        private readonly Dictionary<uint, string> _pendingLabels = new Dictionary<uint, string>();
        private readonly Dictionary<uint, ushort> _pendingTypes = new Dictionary<uint, ushort>();
        private readonly Node _parent;
        private readonly Material _material;
        private readonly AssetRegistry _assets;

        /// <summary>
        /// The server's tables, which carry the `asset` id for every entity.
        /// Set when the `defs` message lands — entities can spawn before that,
        /// and those simply keep their box model.
        /// </summary>
        public Defs Defs { get; set; } = Defs.Empty;

        public EntityViews(Node parent, Material material, AssetRegistry assets)
        {
            _parent = parent;
            _material = material;
            _assets = assets;
        }

        public IEnumerable<EntityView> All => _views.Values;

        /// <summary>How many entities are currently drawn, for framestats.</summary>
        public int Count => _views.Count;

        /// <summary>
        /// Everything currently drawn, for the map. Positions come from the
        /// view nodes rather than from the snapshot, so the map shows what is
        /// on screen rather than a second opinion about it.
        /// </summary>
        public IEnumerable<MapMarker> Markers()
        {
            foreach (EntityView v in _views.Values)
            {
                if (v.Root == null || !v.Root.Visible) continue;
                yield return new MapMarker(v.Root.GlobalPosition, v.Type, MapLabel(v));
            }
        }

        /// <summary>
        /// What to write beside a marker. Only things worth walking to get a
        /// name: labelling thirty identical grunts hides the one thing you
        /// opened the map to find.
        /// </summary>
        private static string MapLabel(EntityView v) => v.Type switch
        {
            EntityType.Player => v.Label,
            EntityType.Npc => v.Label == "npc.quartermaster" ? "Quartermaster" : "",
            _ => "",
        };

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
                v.Root.QueueFree();
                _views.Remove(id);
            }
            _pendingTypes.Remove(id);
            _pendingLabels.Remove(id);
        }

        /// <summary>Records an `equipped` event so a body can show its weapon.</summary>
        public void OnEquipped(uint id, string item)
        {
            if (!_views.TryGetValue(id, out var v)) return;
            v.EquippedItem = item;
            Equip(v);
        }

        /// <summary>
        /// Puts the equipped weapon in a body's right hand. Which model to
        /// hold is the SERVER's answer: items.json gives weapon.pulse an
        /// `asset`, exactly as entity defs do. Called from both the equip
        /// event and the model load because either can arrive first.
        /// </summary>
        private void Equip(EntityView view)
        {
            if (view.Model == null) return;
            if (view.HeldItem == view.EquippedItem) return;

            if (view.Held != null) { view.Held.QueueFree(); view.Held = null; }
            view.HeldItem = view.EquippedItem;
            if (string.IsNullOrEmpty(view.EquippedItem)) return;

            string asset = Defs.ItemAsset(view.EquippedItem);
            if (string.IsNullOrEmpty(asset)) return;

            Node3D hand = AssetRegistry.FindNode(view.Model, "hand.r");
            if (hand == null) return;

            _assets.Attach(asset, hand, weapon =>
            {
                view.Held = weapon;

                // Undo the body's scale: `hand.r` lives inside the model, under
                // the wrapper that fits the character to 1.8 m, and a child
                // inherits that. The mount's POSITION should scale with the
                // body; only the weapon's own size should not.
                float s = hand.GlobalBasis.Scale.X;
                if (s > 1e-4f) weapon.Scale = Vector3.One / s;

                // Line the weapon's `grip` node up with the hand rather than
                // its origin (art/README.md puts `grip` where the hand holds
                // it; the model's origin is its centre). In WORLD space: the
                // holder has just been counter-scaled, so its local offset is
                // in neither the hand's units nor the weapon's.
                Node3D grip = AssetRegistry.FindNode(weapon, "grip");
                if (grip != null)
                    weapon.GlobalPosition += hand.GlobalPosition - grip.GlobalPosition;
            });
        }

        /// <summary>
        /// Places every entity at the interpolated pose, skipping
        /// <paramref name="selfId"/> — the local body is predicted, never
        /// interpolated, or it would lag its own input by interp_delay.
        /// </summary>
        public void Render(SnapshotTimeline timeline, uint selfId)
        {
            double now = Clock.Now;
            float dt = (float)Clock.Dt;
            foreach (var kv in timeline.Interpolate((float)now))
            {
                uint id = kv.Key;
                if (id == selfId) continue;

                if (!_views.TryGetValue(id, out var view))
                {
                    view = Create(id);
                    _views[id] = view;
                }

                // GDD "Seats and occupancy", binding client rule: a seated
                // body is not rendered — a standing character at a seat clips
                // the hull, and a seated pose is post-M2 art.
                view.ParentId = kv.Value.ParentId;
                if (kv.Value.ParentId != 0)
                {
                    view.Root.Visible = false;
                    continue;
                }

                view.Health = kv.Value.Health;
                if (kv.Value.Health > view.MaxHealth) view.MaxHealth = kv.Value.Health;
                view.Dead = kv.Value.Dead;
                Place(view.Root, kv.Value.Pos, kv.Value.Facing);
                if (view.Type == EntityType.Node)
                {
                    // GDD "Nodes": depleted draws the same model at 0.6 in the
                    // depleted tint; a node never dies, so this is the whole rule.
                    bool dep = view.Depleted;
                    view.Root.Scale = Vector3.One * (dep ? 0.6f : 1f);
                    if (dep != view.DepletedDrawn)
                    {
                        view.DepletedDrawn = dep;
                        foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(view.Root))
                            g.Transparency = dep ? 0.45f : 0f;
                    }
                    continue;
                }

                // Speed as DRAWN, smoothed. A snapshot arrives every few
                // frames and the pose between them is interpolated, so the
                // raw per-frame delta is spiky enough to flicker a body
                // between idle and walk while it moves steadily.
                Vector3 drawn = view.Root.GlobalPosition;
                if (view.HasLastDrawn && dt > 1e-5f)
                {
                    float instant = drawn.DistanceTo(view.LastDrawn) / dt;
                    view.Speed = Mathf.Lerp(view.Speed, instant, 0.25f);
                }
                view.LastDrawn = drawn;
                view.HasLastDrawn = true;
                view.Anim?.Drive(view.Speed, view.Dead);

                // Dying is allowed to take as long as the death clip, and not
                // one second longer: the server does not despawn a dead NPC
                // promptly, and a body left up indefinitely is a corpse
                // standing in the camp forever.
                if (kv.Value.Dead && view.DiedAt < 0) view.DiedAt = now;
                if (!kv.Value.Dead) view.DiedAt = -1;

                float linger = view.Anim?.DeathLength ?? 0f;
                bool stillDying = view.DiedAt >= 0 && now - view.DiedAt < linger;
                view.Root.Visible = !kv.Value.Dead || stillDying;
            }
        }

        /// <summary>
        /// Stands a body at `pos` facing `facing`, upright along its own
        /// local up. On a sphere "upright" is a different direction at every
        /// point, so a body oriented by yaw alone lies down as soon as it
        /// walks any distance. Same frame as Step.OrientationQuat.
        /// </summary>
        public static void Place(Node3D root, Vec3 pos, Vec3 facing)
        {
            Vec3 up = pos.Normalized();
            Vec3 fwd = facing.RejectFrom(up);
            if (fwd.Length > 1e-8 && up.Length > 1e-8)
                root.GlobalTransform = new Transform3D(Frame.OrientationBasis(pos, fwd.Normalized()), Frame.ToGodot(pos));
            else
                root.GlobalPosition = Frame.ToGodot(pos);
        }

        /// <summary>
        /// Screenshot rig only: a purely LOCAL body, never present in any
        /// snapshot, so Render never touches it. Same Create path as a real
        /// spawn — same model, same health-bar rules.
        /// </summary>
        public EntityView SpawnLocalDemo(uint id, ushort type, string label, Vec3 pos, Vec3 fwd)
        {
            OnSpawn(new Spawn { EntityId = id, EntityType = type, Data = WireReader.Utf8.GetBytes(label) });
            var view = Create(id);
            _views[id] = view;
            Place(view.Root, pos, fwd);
            return view;
        }

        private EntityView Create(uint id)
        {
            _pendingTypes.TryGetValue(id, out ushort type);
            _pendingLabels.TryGetValue(id, out string label);

            var root = new Node3D { Name = $"entity-{id}" };
            _parent.AddChild(root);

            // Box models, built once per archetype and shared. Their origin is
            // between the feet and their proportions come from the hitbox the
            // server resolves against. This is the FALLBACK, not the art: it
            // goes up first and comes down when the real model attaches, so a
            // missing .glb is never an error — worst case, an entity is boxes.
            MeshInstance3D box = BoxMesh.Attach(root, "model", MeshFor(type, label), _material, 0);

            var view = new EntityView { Id = id, Type = type, Label = label ?? "", Root = root };

            _assets.Attach(AssetFor(type, label), root, model =>
            {
                box?.QueueFree();
                view.Anim = CharacterAnim.For(model);
                view.Model = model;
                // The equip event usually beats the model here, so the weapon
                // is mounted once the thing to mount it ON exists.
                Equip(view);
            });

            return view;
        }

        /// <summary>
        /// The model for an entity. `def` is the archetype id from `spawn`,
        /// which is what tells a shopkeeper from a grunt — both are
        /// EntityTypeNPC on the wire.
        /// </summary>
        private static ArrayMesh MeshFor(ushort type, string def) => type switch
        {
            EntityType.Player => Models.Player(),
            EntityType.Target => Models.Target(),
            EntityType.Loot => Models.Loot(),
            EntityType.Projectile => Models.Projectile(),
            EntityType.Npc => def switch
            {
                "npc.grunt" => Models.Grunt(),
                "npc.gunner" => Models.Gunner(),
                _ => Models.Shopkeeper(),
            },
            _ => Models.Loot(),
        };

        /// <summary>
        /// The art/manifest.json model id for an entity, from the SERVER's own
        /// tables. NPCs go through the archetype because one entity def serves
        /// every NPC on the wire. Loot is the one id named here: the server has
        /// no entity_def for it (nothing to say about a crate), so what a
        /// dropped crate looks like is a client-side question.
        /// </summary>
        private string AssetFor(ushort type, string def) => type switch
        {
            EntityType.Player => Defs.EntityAsset("player"),
            EntityType.Target => Defs.EntityAsset("target"),
            EntityType.Npc => Fallback(Defs.NpcAsset(def), Defs.EntityAsset("npc")),
            EntityType.Vehicle => Defs.EntityAsset("vehicle"),
            EntityType.Ship => Defs.EntityAsset("ship"),
            EntityType.Loot => "prop.loot.crate",
            EntityType.Node => Defs.NodeAsset(def),
            _ => "",
        };

        private static string Fallback(string first, string second) =>
            string.IsNullOrEmpty(first) ? second : first;
    }

    /// <summary>A dot on the map: where, what kind, and (rarely) a name.</summary>
    public readonly struct MapMarker
    {
        public readonly Vector3 Pos;
        public readonly ushort Type;
        public readonly string Label;
        public MapMarker(Vector3 pos, ushort type, string label) { Pos = pos; Type = type; Label = label; }
    }
}
