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
// So: SnapshotTimeline (Core/Timeline.cs, engine-free so SimDump can verify
// it headless) estimates the server's tick from arrivals, and views here
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

        /// <summary>Set once the model loads, null for anything without clips.</summary>
        public CharacterAnim Anim;

        /// <summary>The loaded body, once it arrives; null while it is boxes.</summary>
        public GameObject Model;

        /// <summary>What is currently in this body's hand, and which item it is.</summary>
        public GameObject Held;
        public string HeldItem = "";

        /// <summary>When this body was first seen dead, for the death fade.</summary>
        public float DiedAt = -1f;

        /// <summary>Occupancy from the snapshot; nonzero = seated, not drawn.</summary>
        public uint ParentId;

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
        /// The most health this entity has ever been seen with.
        ///
        /// Entities spawn and respawn at full, so the high-water mark IS the
        /// maximum — no need to parse `defs` for a per-archetype table, and no
        /// second source of truth to drift from the server's. An entity whose
        /// max genuinely rises later simply gets a new mark.
        /// </summary>
        public ushort MaxHealth;

        /// <summary>Fraction remaining, or 1 when nothing has been seen yet.</summary>
        public float HealthFraction => MaxHealth == 0 ? 1f : Mathf.Clamp01((float)Health / MaxHealth);

        /// <summary>Health bars are for the wounded; a full bar is noise.</summary>
        public bool ShowHealthBar => !Dead && MaxHealth > 0 && Health > 0 && Health < MaxHealth;
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
        private readonly AssetRegistry _assets;

        /// <summary>
        /// The server's tables, which carry the `asset` id for every entity.
        /// Set when the `defs` message lands — entities can spawn before that,
        /// and those simply keep their box model.
        /// </summary>
        public Defs Defs { get; set; } = Defs.Empty;

        public EntityViews(Transform parent, Material material, AssetRegistry assets)
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
        /// view transforms rather than from the snapshot, so the map shows
        /// what is on screen rather than a second opinion about it.
        /// </summary>
        public IEnumerable<MapMarker> Markers()
        {
            foreach (EntityView v in _views.Values)
            {
                if (v.Root == null || !v.Root.activeSelf) continue;
                yield return new MapMarker(v.Root.transform.position, v.Type, MapLabel(v));
            }
        }

        /// <summary>
        /// What to write beside a marker. Only things worth walking to get a
        /// name: labelling thirty identical grunts turns the map into a wall
        /// of text and hides the one thing you opened it to find.
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
                Object.Destroy(v.Root);
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
        /// Puts the equipped weapon in a body's right hand.
        ///
        /// `equipped` has been arriving and being stored since Phase 2 with
        /// nothing reading it -- Models.cs said the rifle was "held in the
        /// viewmodel and, later, in a remote player's hands", and this is that
        /// later. Until now an NPC shot at you with empty hands.
        ///
        /// Which model to hold is the SERVER's answer again: items.json gives
        /// weapon.pulse an `asset`, exactly as entity defs do, so there is no
        /// client-side table mapping items to art.
        ///
        /// Called from both the equip event and the model load because either
        /// can arrive first, and it needs both.
        /// </summary>
        private void Equip(EntityView view)
        {
            if (view.Model == null) return;
            if (view.HeldItem == view.EquippedItem) return;

            if (view.Held != null) { Object.Destroy(view.Held); view.Held = null; }
            view.HeldItem = view.EquippedItem;
            if (string.IsNullOrEmpty(view.EquippedItem)) return;

            string asset = Defs.ItemAsset(view.EquippedItem);
            if (string.IsNullOrEmpty(asset)) return;

            Transform hand = FindDeep(view.Model.transform, "hand.r");
            if (hand == null) return;

            _assets.Attach(asset, hand, weapon =>
            {
                view.Held = weapon;

                // Undo the body's scale. `hand.r` lives inside the model, under
                // the wrapper that fits the character to 1.8 m -- about 2.3x
                // for these -- and a child inherits that. Left alone, a 0.9 m
                // rifle is carried as a 2 m one. The mount's POSITION should
                // scale with the body (the hand is where the hand is); only
                // the weapon's own size should not.
                float s = hand.lossyScale.x;
                if (s > 1e-4f) weapon.transform.localScale = Vector3.one / s;

                // Line the weapon's `grip` node up with the hand rather than
                // its origin: art/README.md puts `grip` "where the character's
                // right hand holds it", and the model's origin is its centre.
                //
                // Done in WORLD space on purpose. `grip` is a grandchild of the
                // holder (glTFast puts a scene root in between) and the holder
                // has just been counter-scaled, so its local offset is in
                // neither the hand's units nor the weapon's. A world-space
                // delta needs to know none of that.
                Transform grip = FindDeep(weapon.transform, "grip");
                if (grip != null)
                    weapon.transform.position += hand.position - grip.position;
            });
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.gameObject.name == name) return t;
            return null;
        }

        /// <summary>
        /// Places every entity at the interpolated pose, skipping
        /// <paramref name="selfId"/> — the local body is predicted, never
        /// interpolated, or it would lag its own input by interp_delay.
        /// </summary>
        public void Render(SnapshotTimeline timeline, uint selfId)
        {
            foreach (var kv in timeline.Interpolate(Time.time))
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
                // the hull, and a seated pose is post-M2 art. The composed
                // transform stays authoritative data; we just draw nothing.
                view.ParentId = kv.Value.ParentId;
                if (kv.Value.ParentId != 0)
                {
                    view.Root.SetActive(false);
                    continue;
                }

                view.Health = kv.Value.Health;
                if (kv.Value.Health > view.MaxHealth) view.MaxHealth = kv.Value.Health;
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
                // Speed as DRAWN, smoothed. A snapshot arrives every few
                // frames and the pose between them is interpolated, so the
                // raw per-frame delta is spiky enough to flicker a body
                // between idle and walk while it moves steadily.
                Vector3 drawn = view.Root.transform.position;
                if (view.HasLastDrawn && Time.deltaTime > 1e-5f)
                {
                    float instant = Vector3.Distance(drawn, view.LastDrawn) / Time.deltaTime;
                    view.Speed = Mathf.Lerp(view.Speed, instant, 0.25f);
                }
                view.LastDrawn = drawn;
                view.HasLastDrawn = true;
                view.Anim?.Drive(view.Speed, view.Dead);

                // Dying is allowed to take as long as the death clip, and not
                // one second longer.
                //
                // The first version of this kept any body with a clip up
                // indefinitely, which is not "playing the animation out", it
                // is leaving corpses standing in the camp forever. The server
                // does not despawn a dead NPC promptly -- it never had to,
                // because the client used to switch the body off the instant
                // it died, which looked like a despawn.
                if (kv.Value.Dead && view.DiedAt < 0f) view.DiedAt = Time.time;
                if (!kv.Value.Dead) view.DiedAt = -1f;

                float linger = view.Anim?.DeathLength ?? 0f;
                bool stillDying = view.DiedAt >= 0f && Time.time - view.DiedAt < linger;
                view.Root.SetActive(!kv.Value.Dead || stillDying);
            }
        }

        private EntityView Create(uint id)
        {
            _pendingTypes.TryGetValue(id, out ushort type);
            _pendingLabels.TryGetValue(id, out string label);

            var root = new GameObject($"entity-{id}");
            root.transform.SetParent(_parent, false);

            // Box models, built once per archetype and shared. Their origin is
            // between the feet and their proportions come from the hitbox the
            // server resolves against, so nothing here needs an offset and
            // what you shoot at is what the server tests.
            //
            // This is now the FALLBACK, not the art. It goes up immediately and
            // stays up until the real model finishes loading, which is what
            // keeps a slow or missing .glb from ever being an error: worst
            // case, an entity is boxes — the same rule art/README.md set for
            // the retired client.
            GameObject box = BoxMesh.Attach(root.transform, "model", MeshFor(type, label), _material, 0);

            var view = new EntityView { Id = id, Type = type, Label = label ?? "", Root = root };

            _assets.Attach(AssetFor(type, label), root.transform, model =>
            {
                if (box != null) Object.Destroy(box);
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
        private static Mesh MeshFor(ushort type, string def) => type switch
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
        /// tables. `asset` has been on the wire since Phase 2 — items.json and
        /// npcs.json both carry it — so there is no client-side table here to
        /// drift out of step with the server's.
        ///
        /// NPCs go through the archetype rather than the entity def because one
        /// entity def serves every NPC on the wire: `npc` alone cannot tell a
        /// quartermaster from a camp gunner, and the entity def's asset is the
        /// shopkeeper's. It falls back to that def only when the archetype is
        /// unknown.
        ///
        /// An empty id means "no model for this yet" — Loot and Projectile have
        /// no entity def at all — and AssetRegistry.Attach ignores it, leaving
        /// the box model up.
        /// </summary>
        private string AssetFor(ushort type, string def) => type switch
        {
            EntityType.Player => Defs.EntityAsset("player"),
            EntityType.Target => Defs.EntityAsset("target"),
            EntityType.Npc => Fallback(Defs.NpcAsset(def), Defs.EntityAsset("npc")),
            EntityType.Vehicle => Defs.EntityAsset("vehicle"),

            // Loot is the one id named here rather than by the server, and it
            // is not an exception being smuggled in. The server has an
            // entity_def for everything it has something to SAY about --
            // hitbox, max health, damageable -- and loot has none of those: it
            // is not shot at and it has no hitbox, so items.json gives it no
            // def and therefore no asset field to read. What a dropped crate
            // looks like is a client-side question about client-side art.
            EntityType.Loot => "prop.loot.crate",

            _ => "",
        };

        private static string Fallback(string first, string second) =>
            string.IsNullOrEmpty(first) ? second : first;
    }
}
