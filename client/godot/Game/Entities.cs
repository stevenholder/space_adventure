using System;
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
        /// <summary>Screenshot rig only (-uiDie): play the death without the server's say-so.</summary>
        public bool RigDead;

        /// <summary>Set once the model loads, null for anything without clips.</summary>
        public CharacterAnim Anim;

        /// <summary>The loaded body, once it arrives; null while it is boxes.</summary>
        public Node3D Model;

        /// <summary>What is currently in this body's hand, and which item it is.</summary>
        public Node3D Held;
        public string HeldItem = "";

        /// <summary>Armor by slot: what the wire says is worn, and what is drawn.</summary>
        public readonly Dictionary<string, string> Worn = new Dictionary<string, string>();
        public readonly Dictionary<string, Node3D> WornNodes = new Dictionary<string, Node3D>();
        public readonly Dictionary<string, string> WornDrawn = new Dictionary<string, string>();

        /// <summary>When this body was first seen dead, for the death fade.</summary>
        public double DiedAt = -1;

        /// <summary>Occupancy from the snapshot; nonzero = seated, not drawn.</summary>
        public uint ParentId;
        public ushort Seat;
        /// <summary>The local player's own view: only ever drawn seated, headless.</summary>
        public bool IsSelf;
        public bool HeadHidden;

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
        /// <summary>Metres walked since the last footstep sound.</summary>
        public float StepDist;
        public float Speed;

        /// <summary>A rover's dash speed bar, grown from the model's `dash.speed` mount; null until found.</summary>
        public Node3D DashBar;
        public bool DashLooked;

        /// <summary>
        /// The most health this entity has ever been seen with. Entities spawn
        /// and respawn at full, so the high-water mark IS the maximum.
        /// </summary>
        public ushort MaxHealth;

        public float HealthFraction => MaxHealth == 0 ? 1f : Mathf.Clamp((float)Health / MaxHealth, 0f, 1f);

        /// <summary>Health bars are for the wounded; a full bar is noise. A node's health is yields, not wounds.</summary>
        public bool ShowHealthBar => !IsSelf && Type != EntityType.Node && !Dead && MaxHealth > 0 && Health > 0 && Health < MaxHealth;

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
        // Equipment that arrived before the view existed. A view is created on
        // the first SNAPSHOT row, but the join replay sends `equipped` and
        // `worn` right after the spawn, before any snapshot -- so without this
        // every peer's weapon and every NPC's armor would be dropped at join.
        private readonly Dictionary<uint, string> _pendingEquipped = new Dictionary<uint, string>();
        private readonly Dictionary<uint, Dictionary<string, string>> _pendingWorn = new Dictionary<uint, Dictionary<string, string>>();
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
                if (v.Root == null || !v.Root.Visible || v.IsSelf) continue;
                yield return new MapMarker(v.Root.GlobalPosition, v.Type, MapLabel(v), MapIcon(v), v.Depleted || v.Dead);
            }
        }

        /// <summary>The glyph a thing gets on the map (Map.cs draws these).</summary>
        private static string MapIcon(EntityView v) => v.Type switch
        {
            EntityType.Player => "player",
            EntityType.Npc => v.Label switch
            {
                "npc.quartermaster" => "shop",
                "npc.dispatcher" => "board",
                "npc.workbench" => "bench",
                _ => "hostile",
            },
            EntityType.Node => v.Label.Contains("copper") ? "copper" : v.Label.Contains("wreck") ? "wreck" : "iron",
            EntityType.Loot => "loot",
            EntityType.Vehicle => "rover",
            EntityType.Ship => "ship",
            EntityType.Target => "target",
            _ => "",
        };

        /// <summary>
        /// What to write beside a marker. Only things worth walking to get a
        /// name: labelling thirty identical grunts hides the one thing you
        /// opened the map to find.
        /// </summary>
        private static string MapLabel(EntityView v) => v.Type switch
        {
            EntityType.Player => v.Label,
            EntityType.Npc => v.Label switch { "npc.quartermaster" => "Quartermaster", "npc.dispatcher" => "Dispatcher", "npc.workbench" => "Workbench", _ => "" },
            EntityType.Node => v.Label.Contains("copper") ? "Copper" : "",
            _ => "",
        };

        /// <summary>Footsteps for every walking body (set by Boot).</summary>
        public Sfx Sfx;

        public bool TryGet(uint id, out EntityView view) => _views.TryGetValue(id, out view);

        /// <summary>A `hit` event named this body as the victim: flinch.</summary>
        public void OnHit(uint victim)
        {
            if (_views.TryGetValue(victim, out var v)) v.Anim?.Hit(Clock.Now);
        }

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
            _pendingEquipped.Remove(id);
            _pendingWorn.Remove(id);
        }

        /// <summary>Records a `worn` event ("slot=item") so a body can show its armor.</summary>
        public void OnWorn(uint id, string slot, string item)
        {
            if (!_views.TryGetValue(id, out var v))
            {
                if (!_pendingWorn.TryGetValue(id, out var pending)) _pendingWorn[id] = pending = new Dictionary<string, string>();
                pending[slot] = item ?? "";
                return;
            }
            v.Worn[slot] = item ?? "";
            Dress(v);
        }

        /// <summary>
        /// Hangs each worn slot's model on the body's skeleton, taking down
        /// whatever that slot showed before. Called from the worn event and
        /// from the model load, because either can arrive first.
        /// </summary>
        private void Dress(EntityView view) => Dress(_assets, Defs.ItemAsset, view.Model, view.Worn, view.WornDrawn, view.WornNodes);

        /// <summary>
        /// The one dressing rule, shared with the local body (ViewModel).
        /// `asset` maps an item id to its model id (the local body is handed
        /// asset ids already and passes identity).
        /// </summary>
        public static void Dress(AssetRegistry assets, Func<string, string> asset, Node3D model, Dictionary<string, string> worn,
                                 Dictionary<string, string> drawn, Dictionary<string, Node3D> nodes, bool local = false,
                                 Func<string, bool> keep = null, uint layers = 0, Material material = null)
        {
            if (model == null) return;
            Skeleton3D skeleton = null;
            foreach (Skeleton3D sk in AssetRegistry.Descendants<Skeleton3D>(model)) { skeleton = sk; break; }
            if (skeleton == null) return;
            string wearer = model.HasMeta("asset") ? (string)model.GetMeta("asset") : null;
            bool changed = false;
            foreach (var kv in worn)
            {
                drawn.TryGetValue(kv.Key, out string was);
                if (was == kv.Value) continue;
                changed = true;
                if (nodes.TryGetValue(kv.Key, out Node3D old)) { old.QueueFree(); nodes.Remove(kv.Key); }
                drawn[kv.Key] = kv.Value;
                if (string.IsNullOrEmpty(kv.Value)) continue;
                string id = asset(kv.Value);
                if (keep != null && !keep(id)) continue;   // recorded in `drawn`, not hung (first-person arms)
                // A piece built for this wearer's body ("armor.suit.scout@npc.grunt",
                // armor.py BODIES) when there is one; the player-build piece otherwise.
                if (wearer != null && assets.Has(id + "@" + wearer)) id = id + "@" + wearer;
                Node3D piece = assets.AttachSkinned(id, skeleton);
                if (piece == null) continue;
                nodes[kv.Key] = piece;
                // The local body: the camera is inside the head, and the arms
                // the eye should see are the first-person ones. A helmet or a
                // sleeve drawn here is a box over the view or a second pair of
                // arms -- shadows-only, exactly like the head and arms.
                if (local && (kv.Key == "head" || CoversArms(assets, id)))
                    foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(piece))
                        g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                if (material != null) ViewModel.FpOverride(piece);
                if (layers != 0)
                {
                    AssetRegistry.SetLayers(piece, layers);
                    foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(piece))
                        g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                }
            }
            if (changed) Cover(assets, asset, model, drawn);
        }

        /// <summary>A worn piece that covers the arms ("arms/…" in its manifest `covers`).</summary>
        public static bool CoversArms(AssetRegistry assets, string asset) =>
            assets.OnArms(asset) || Array.Exists(assets.Covers(asset), c => c.StartsWith("arms/"));

        /// <summary>
        /// WoW's rule: the body is not drawn under what it wears. A worn
        /// piece's manifest row says which of the wearer's surfaces it covers
        /// ("body/suit"); those surfaces get a collapse shader. Recomputed
        /// from the whole worn set on every change, so taking a piece off
        /// uncovers exactly what nothing else still covers.
        /// </summary>
        private static void Cover(AssetRegistry assets, Func<string, string> asset, Node3D model, Dictionary<string, string> drawn)
        {
            string wearer = model.HasMeta("asset") ? (string)model.GetMeta("asset") : null;
            if (wearer == null) return;
            var covered = new HashSet<string>();
            foreach (string item in drawn.Values)
                if (!string.IsNullOrEmpty(item))
                    foreach (string c in assets.Covers(asset(item))) covered.Add(c);
            foreach (MeshInstance3D mi in AssetRegistry.Descendants<MeshInstance3D>(model))
            {
                string[] names = assets.Surfaces(wearer, mi.Name);
                if (names == null || mi.Mesh == null) continue;
                for (int i = 0; i < mi.Mesh.GetSurfaceCount() && i < names.Length; i++)
                {
                    // Remember what the surface drew with before it was ever
                    // hidden, and put exactly that back: null would fall back
                    // to the importer's material, not the shared one (or the
                    // first-person one) the registry and the view model set.
                    string key = "sa_base_" + i;
                    // Setting a meta to null DELETES it (imported pbr surfaces
                    // have no override), so "none" is stored as false.
                    if (!mi.HasMeta(key))
                    {
                        Material had = mi.GetSurfaceOverrideMaterial(i);
                        mi.SetMeta(key, had != null ? (Variant)had : false);
                    }
                    Variant v = mi.GetMeta(key);
                    var baseMat = v.VariantType == Variant.Type.Object ? v.As<Material>() : null;
                    mi.SetSurfaceOverrideMaterial(i, covered.Contains(mi.Name + "/" + names[i]) ? Hidden : baseMat);
                }
            }
        }

        /// <summary>Collapses a surface off-screen: cheaper than transparency, and casts no shadow.</summary>
        private static readonly ShaderMaterial Hidden = new ShaderMaterial
        {
            Shader = new Shader { Code = "shader_type spatial; void vertex() { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }" },
        };

        /// <summary>Records an `equipped` event so a body can show its weapon.</summary>
        public void OnEquipped(uint id, string item)
        {
            if (!_views.TryGetValue(id, out var v)) { _pendingEquipped[id] = item; return; }
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
            if (view.Anim != null) { view.Anim.Armed = false; view.Anim.Class = Defs.HoldSuffix(view.EquippedItem ?? ""); }
            if (string.IsNullOrEmpty(view.EquippedItem)) return;

            string asset = Defs.ItemAsset(view.EquippedItem);
            if (string.IsNullOrEmpty(asset)) return;

            Node3D hand = AssetRegistry.FindNode(view.Model, "hand.r");
            if (hand == null) return;

            _assets.Attach(asset, hand, weapon =>
            {
                view.Held = weapon;
                if (view.Anim != null) view.Anim.Armed = true;

                // Point the barrel down the forearm -- two frames from now.
                // The mount is a BoneAttachment3D, and it is not posed until
                // the skeleton has updated; aligned at attach time the barrel
                // landed wherever the unposed mount happened to be (up, once).
                weapon.AddChild(new AlignToForearm { Weapon = weapon, Hand = hand, Model = view.Model });

                // Undo the body's scale: `hand.r` lives inside the model, under
                // the wrapper that fits the character to 1.8 m, and a child
                // inherits that. The mount's POSITION should scale with the
                // body; only the weapon's own size should not.
                float s = hand.GlobalBasis.Scale.X;
                if (s > 1e-4f) weapon.Scale = Vector3.One / s;
                // A held weapon casts no shadow: under a low sun its sights and
                // rail threw long thin wedges back along the receiver, which
                // read as broken geometry. It still receives the body's shadow.
                foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(weapon))
                    g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
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

        /// <summary>The right elbow in world space: the forearm bone's origin, or null without a skeleton.</summary>
        internal static Vector3? ElbowOf(Node3D model)
        {
            foreach (Skeleton3D sk in AssetRegistry.Descendants<Skeleton3D>(model))
                for (int i = 0; i < sk.GetBoneCount(); i++)
                    if (sk.GetBoneName(i) is var n && (n.StartsWith("forearm.r") || n.StartsWith("forearm_r") || n.StartsWith("lowerarm_r")))
                        return sk.GlobalTransform * sk.GetBoneGlobalPose(i).Origin;
            return null;
        }

        /// <summary>
        /// Places every entity at the interpolated pose, skipping
        /// <paramref name="selfId"/> — the local body is predicted, never
        /// interpolated, or it would lag its own input by interp_delay.
        /// </summary>
        public void Render(SnapshotTimeline timeline, uint selfId)
        {
            _seated.Clear();
            double now = Clock.Now;
            float dt = (float)Clock.Dt;
            foreach (var kv in timeline.Interpolate((float)now))
            {
                uint id = kv.Key;
                // Our own body is predicted, never drawn from snapshots --
                // except seated, where it sits in the seat like anyone else's
                // (headless, under our camera: PlaceSeated).
                if (id == selfId && kv.Value.ParentId == 0)
                {
                    if (_views.TryGetValue(id, out var me) && me.Root != null) me.Root.Visible = false;
                    continue;
                }

                if (!_views.TryGetValue(id, out var view))
                {
                    view = Create(id);
                    _views[id] = view;
                }

                // Seated bodies are posed in their seat by PlaceSeated, once
                // the vehicles have moved this frame (playtest 2026-10-02: the
                // old rule drew no seated body at all, after one that drew it
                // standing on the roof).
                view.ParentId = kv.Value.ParentId;
                view.Seat = kv.Value.Seat;
                view.IsSelf = id == selfId;
                if (kv.Value.ParentId != 0)
                {
                    view.Health = kv.Value.Health;
                    view.Dead = kv.Value.Dead;
                    _seated.Add(view);
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
                view.Anim?.Drive(view.Speed, view.Dead || view.RigDead);
                if (view.Type == EntityType.Vehicle) UpdateDash(view);
                if (view.Anim != null && !view.Dead && view.Speed > 0.5f && Sfx != null)
                {
                    view.StepDist += view.Speed * dt;
                    if (view.StepDist > (view.Speed > CharacterAnim.SprintAt ? 1.0f : 0.75f))
                    {
                        view.StepDist = 0;
                        Sfx.StepAt(drawn, (int)(view.Id + (uint)(drawn.X * 10)));
                    }
                }

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

        private readonly List<EntityView> _seated = new List<EntityView>();

        /// <summary>
        /// How far a seated body's eye sits under a standing one (art
        /// tools/bpy/human.py SIT_DROP): the sit clips drop the whole body
        /// this much, and the seat's eye mount is where the seated eye goes.
        /// </summary>
        internal const float SitDrop = 0.45f;

        /// <summary>
        /// Puts every seated body (Render's list) in its seat: its eye on the
        /// seat's eye mount, facing the vehicle's way, playing `sit_drive`
        /// at a rover's wheel, `sit_armed` with a gun in hand, `sit` else.
        /// Call after the vehicles are placed for the frame -- Boot moves the
        /// one you drive itself -- or a seated body trails its seat by a
        /// frame at speed. Your own seated body drops its head (the camera
        /// is in it) and gives way to the first-person arms when armed.
        /// </summary>
        public void PlaceSeated()
        {
            foreach (EntityView view in _seated)
            {
                if (view.Root == null) continue;
                if (!_views.TryGetValue(view.ParentId, out var veh) || veh.Model == null) { view.Root.Visible = false; continue; }
                bool rover = veh.Type == EntityType.Vehicle;
                string mountName = rover ? (view.Seat == 1 ? "seat.driver" : "seat.passenger.0")
                                         : (view.Seat == 1 ? "seat.pilot" : view.Seat == 2 ? "seat.passenger.0" : "seat.passenger.1");
                Node3D mount = AssetRegistry.FindNode(veh.Model, mountName);
                if (mount == null) { view.Root.Visible = false; continue; }

                string clip = rover && view.Seat == 1 ? "sit_drive" : view.Held != null && rover ? "sit_armed" : "sit";
                // Our own gunner sees the first-person arms instead.
                bool show = !(view.IsSelf && clip == "sit_armed");
                view.Root.Visible = show && !view.Dead;
                if (!view.Root.Visible) continue;

                Basis b = veh.Root.GlobalBasis.Orthonormalized();
                view.Root.GlobalTransform = new Transform3D(b,
                    mount.GlobalPosition - b.Y * (FpsController.EyeHeight - SitDrop));
                view.Anim?.Sit(clip);
                if (view.Held != null) view.Held.Visible = clip == "sit_armed";
                if (view.IsSelf && !view.HeadHidden && view.Model != null)
                {
                    view.HeadHidden = true;
                    if (AssetRegistry.FindNode(view.Model, "head") is GeometryInstance3D head) head.Visible = false;
                }
            }
        }

        /// <summary>
        /// The rover's dash speed bar: amber, growing from the mount's left
        /// end over a dark track as the drawn speed climbs to vmax_drive.
        /// Everyone sees it, so a passenger reads the same dash as the driver.
        /// </summary>
        private void UpdateDash(EntityView view)
        {
            if (!view.DashLooked && view.Model != null)
            {
                view.DashLooked = true;
                Node3D mount = AssetRegistry.FindNode(view.Model, "dash.speed");
                if (mount != null)
                {
                    const float len = 0.46f;
                    BoxMesh.Attach(mount, "speedtrack", BoxMesh.Build(new[]
                        { new Box(new Vector3(len / 2, 0, 0.004f), new Vector3(len + 0.02f, 0.07f, 0.01f), new Color(0.08f, 0.08f, 0.09f)) }, "speedtrack"), _material, 0);
                    view.DashBar = new Node3D { Name = "speedbar" };
                    mount.AddChild(view.DashBar);
                    BoxMesh.Attach(view.DashBar, "bar", BoxMesh.Build(new[]
                        { new Box(new Vector3(len / 2, 0, 0.012f), new Vector3(len, 0.045f, 0.01f), new Color(1.0f, 0.62f, 0.12f)) }, "speedbar"), _material, 0);
                }
            }
            if (view.DashBar != null)
                view.DashBar.Scale = new Vector3(Mathf.Clamp(view.Speed / (float)DriveRules.VmaxDrive, 0.02f, 1f), 1, 1);
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
            if (_pendingEquipped.TryGetValue(id, out string held)) { view.EquippedItem = held; _pendingEquipped.Remove(id); }
            if (_pendingWorn.TryGetValue(id, out var wornEarly))
            {
                foreach (var kv in wornEarly) view.Worn[kv.Key] = kv.Value;
                _pendingWorn.Remove(id);
            }

            _assets.Attach(AssetFor(type, label), root, model =>
            {
                box?.QueueFree();
                view.Anim = CharacterAnim.For(model);
                if (view.Anim != null) view.Anim.Armed = view.Held != null;
                view.Model = model;
                // The equip event usually beats the model here, so the weapon
                // is mounted once the thing to mount it ON exists.
                Equip(view);
                Dress(view);
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

    /// <summary>
    /// A mark on the map: where, what kind, a name (rarely), and the glyph
    /// the map draws — "shop", "board", "bench", "hostile", "iron", "copper",
    /// "wreck", "loot", "rover", "ship", "player", "spawn", "poi", "target".
    /// </summary>
    public readonly struct MapMarker
    {
        public readonly Vector3 Pos;
        public readonly ushort Type;
        public readonly string Label;
        public readonly string Icon;
        public readonly bool Dim;
        public MapMarker(Vector3 pos, ushort type, string label, string icon = "", bool dim = false)
        { Pos = pos; Type = type; Label = label; Icon = icon; Dim = dim; }
    }

        /// <summary>
    /// Turns a held weapon so its `grip` -> `muzzle` line runs along the
    /// wearer's elbow -> wrist line, once the bone attachment carrying it
    /// has been posed. Assumes nothing about either model's axes: the
    /// mount's frame turned out not to be the bone's, and two guessed
    /// fixed turns put the barrel vertical. Frees itself when done.
    /// </summary>
    public sealed partial class AlignToForearm : Node
    {
        public Node3D Weapon, Hand, Model;
        /// <summary>
        /// First-person aim: the camera, a weight 0..1 (blends hip -> aimed)
        /// and the recoil kick in metres. When set and the weight is above
        /// zero the rifle is placed by its SIGHTS on the eye line, not by the
        /// hands; the view model then pulls the arms to the gun.
        /// </summary>
        public Func<(Transform3D eye, float w, float kick)?> Ads;

        /// <summary>Eye to rear sight when aimed: a shouldered long gun 0.20 m, a pistol at arm's length.</summary>
        public float SightDistance = 0.20f;

        /// <summary>Hip fire: the bore converges on the crosshair this far out.</summary>
        public const float Converge = 15f;
        private int _frames;
        private Node3D _left;

        /// <summary>
        /// Every frame: the barrel (grip -> muzzle) runs from the right hand
        /// toward the LEFT hand (`hand.l`, the fore-end) when the body has
        /// one -- a two-handed hold whatever the clip does -- or along the
        /// forearm (elbow -> wrist) when it does not. Then the weapon's top
        /// is rolled to the wearer's up and its grip seated in the hand.
        /// </summary>
        /// <summary>
        /// Blend from where the hands put the gun to the aimed placement: the
        /// line of sight (rear notch -> front post) down the view, nudged up
        /// by the kick, the rear sight SightDistance ahead of the eye (back by
        /// the kick), the gun's top to the camera's up.
        /// </summary>
        private void Aim()
        {
            var a = Ads?.Invoke();
            if (!a.HasValue) return;
            Node3D sightN = AssetRegistry.FindNode(Weapon, "sight");
            Node3D frontN = AssetRegistry.FindNode(Weapon, "front");
            Node3D gripN = AssetRegistry.FindNode(Weapon, "grip");
            if (sightN == null || frontN == null || gripN == null) return;
            Transform3D eye = a.Value.eye;
            Vector3 fwd = -eye.Basis.Z.Normalized(), up = eye.Basis.Y.Normalized();

            // Hip: converge. Turn the gun about its grip (the right hand keeps
            // it) so the bore points at the spot under the crosshair, Converge
            // metres out -- shots leave the muzzle toward where the crosshair
            // says they go. A small turn: the hip pose already points ahead.
            Vector3 pivot = gripN.GlobalPosition;
            Vector3 aimAt = eye.Origin + fwd * Converge;
            Vector3 bore = (frontN.GlobalPosition - sightN.GlobalPosition).Normalized();
            Vector3 toAim = (aimAt - frontN.GlobalPosition).Normalized();
            Vector3 cax = bore.Cross(toAim);
            if (cax.LengthSquared() > 1e-10f)
            {
                var turn = new Basis(cax.Normalized(), bore.AngleTo(toAim));
                Weapon.GlobalBasis = turn * Weapon.GlobalBasis;
                Weapon.GlobalPosition = pivot + turn * (Weapon.GlobalPosition - pivot);
            }
            if (a.Value.w <= 0.001f) return;
            Transform3D hip = Weapon.GlobalTransform;
            Vector3 want = (fwd + up * a.Value.kick * 1.2f).Normalized();
            Vector3 los = (frontN.GlobalPosition - sightN.GlobalPosition).Normalized();
            Vector3 ax = los.Cross(want);
            if (ax.LengthSquared() > 1e-8f)
                Weapon.GlobalBasis = new Basis(ax.Normalized(), los.AngleTo(want)) * Weapon.GlobalBasis;
            Vector3 w1 = (up - want * up.Dot(want)).Normalized();
            Vector3 h1 = Weapon.GlobalBasis.Y.Normalized();
            h1 = (h1 - want * h1.Dot(want)).Normalized();
            if (w1.LengthSquared() > 0.5f && h1.LengthSquared() > 0.5f)
                Weapon.GlobalBasis = new Basis(want, h1.SignedAngleTo(w1, want)) * Weapon.GlobalBasis;
            Vector3 target = eye.Origin + fwd * (SightDistance - a.Value.kick) - up * 0.012f;
            Weapon.GlobalPosition += target - sightN.GlobalPosition;
            Transform3D aimed = Weapon.GlobalTransform;
            Weapon.GlobalTransform = hip.InterpolateWith(aimed, a.Value.w);
        }

        public override void _Process(double delta)
        {
            if (++_frames < 3) return;
            if (!IsInstanceValid(Weapon) || !IsInstanceValid(Model)) { QueueFree(); return; }
            Node3D grip = AssetRegistry.FindNode(Weapon, "grip");
            Node3D muzzle = AssetRegistry.FindNode(Weapon, "muzzle");
            if (grip == null || muzzle == null) { QueueFree(); return; }
            if (_frames == 3) _left = AssetRegistry.FindNode(Model, "hand.l");
            Vector3 arm;
            // Two contact points when the gun has a `fore` and the body a left
            // hand: grip -> fore runs to hand.r -> hand.l, so the rifle sits in
            // BOTH hands, not just pointed between them.
            Node3D fore = _left != null ? AssetRegistry.FindNode(Weapon, "fore") : null;
            // A pistol's `fore` (the support palm under the fist) is too close
            // to the grip to steer the barrel: it rides the forearm instead.
            bool pistol = fore != null && (fore.GlobalPosition - grip.GlobalPosition).Length() < 0.15f;
            if (pistol) fore = null;
            if (fore != null)
            {
                Vector3 hands = _left.GlobalPosition - Hand.GlobalPosition;
                Vector3 gun = fore.GlobalPosition - grip.GlobalPosition;
                if (hands.LengthSquared() < 1e-6f || gun.LengthSquared() < 1e-6f) return;
                // Turn the gun's grip->fore onto the hands' line; the barrel
                // follows at whatever angle it makes with that line.
                Vector3 hn = hands.Normalized(), gn = gun.Normalized();
                Vector3 ax = gn.Cross(hn);
                if (ax.LengthSquared() > 1e-8f)
                    Weapon.GlobalBasis = new Basis(ax.Normalized(), gn.AngleTo(hn)) * Weapon.GlobalBasis;
                // Roll about that line so the gun's top faces the wearer's up.
                Vector3 up0 = Model.GlobalBasis.Y.Normalized();
                Vector3 want0 = (up0 - hn * up0.Dot(hn)).Normalized();
                Vector3 have0 = Weapon.GlobalBasis.Y.Normalized();
                have0 = (have0 - hn * have0.Dot(hn)).Normalized();
                if (want0.LengthSquared() > 0.5f && have0.LengthSquared() > 0.5f)
                    Weapon.GlobalBasis = new Basis(hn, have0.SignedAngleTo(want0, hn)) * Weapon.GlobalBasis;
                Weapon.GlobalPosition += Hand.GlobalPosition - grip.GlobalPosition;
                Aim();
                return;
            }
            else if (_left != null && !pistol)
                arm = _left.GlobalPosition - Hand.GlobalPosition;
            else
            {
                Vector3? elbow = EntityViews.ElbowOf(Model);
                if (!elbow.HasValue) { QueueFree(); return; }
                arm = Hand.GlobalPosition - elbow.Value;
            }
            if (arm.LengthSquared() < 1e-6f) return;
            arm = arm.Normalized();
            Vector3 barrel = (muzzle.GlobalPosition - grip.GlobalPosition).Normalized();
            Vector3 axis = barrel.Cross(arm);
            if (axis.LengthSquared() > 1e-8f)
                Weapon.GlobalBasis = new Basis(axis.Normalized(), barrel.AngleTo(arm)) * Weapon.GlobalBasis;
            else if (barrel.Dot(arm) < 0)
                Weapon.GlobalBasis = new Basis(Hand.GlobalBasis.Y.Normalized(), Mathf.Pi) * Weapon.GlobalBasis;
            Vector3 up = Model.GlobalBasis.Y.Normalized();
            Vector3 want = (up - arm * up.Dot(arm)).Normalized();
            Vector3 have = Weapon.GlobalBasis.Y.Normalized();
            have = (have - arm * have.Dot(arm)).Normalized();
            if (want.LengthSquared() > 0.5f && have.LengthSquared() > 0.5f)
                Weapon.GlobalBasis = new Basis(arm, have.SignedAngleTo(want, arm)) * Weapon.GlobalBasis;
            Weapon.GlobalPosition += Hand.GlobalPosition - grip.GlobalPosition;
            Aim();
            if (_left == null) QueueFree();   // one-handed (old bodies): once is enough
        }
    }
}
