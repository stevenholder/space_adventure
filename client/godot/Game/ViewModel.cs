// The first-person view: what you see of yourself.
//
// Two instances of the same body, on purpose.
//
// THE FIRST-PERSON ARMS are a second `char.player` hung under the camera with
// its eye (0, 1.70, 0 in body space) on the camera and its forward down the
// camera's −Z. Only its `arms` mesh is drawn (body and head hidden), on the
// rig layer, lit by the rig light alone (Boot), casting no shadow. It plays
// the `fp_*` clips from art/tools/bpy/body.py and holds the real weapon in
// its own `hand.r` -- the same model, the same grip and muzzle other players
// see. Worn armor that covers the arms (sleeves, gloves) is dressed on it
// too, so what you wear is what you see. Sway, bob and recoil move the arms
// about the EYE; the camera itself never kicks (GDD "First-person body").
// A wall closer than the rifle lowers it (fp_lower) instead of clipping it.
//
// THE BODY is the opposite: a real object at the player's feet on the normal
// layer, so looking down shows your chest and legs where they are and other
// players see exactly the same model. Its head, its arms, the armor on them
// and its held weapon are shadows-only: the eye is inside the head and the
// arms it should see are the first-person ones -- but the ground shadow
// keeps all of them, so it holds the gun the way everyone else's does.

using System;
using System.Collections.Generic;
using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class ViewModel
    {
        private const float SwayDegrees = 0.9f;   // how far the arms lag a fast turn
        private const float SwaySmoothing = 12f;
        private const float BobSpeed = 9f;
        private const float BobAmount = 0.010f;

        public const float BaseFov = 60f;
        public const float AdsFov = 45f;
        private const float AdsSway = 0.3f;       // sway and bob while aiming
        public const float LowerAt = 1.0f;        // obstruction closer than this lowers the rifle
        public const float RaiseAt = 1.25f;       // and it comes up again past this
        public const float Reach = 1.3f;          // how far ahead Blocked looks
        private const float KickMetres = 0.025f;  // recoil: the arms come back...
        private const float KickPitchDeg = 2.0f;  // ...and the muzzle climbs
        private const float SightAboveGrip = 0.095f; // rear-sight notch over the grip node (gen_weapon.py)
        // Camera-space targets for the rear sight (+Y up, +X right; depth is
        // left where the arms put it).
        private static readonly Vector3 HoldSight = new Vector3(0.12f, -0.12f, 0f);
        private static readonly Vector3 AdsSight = new Vector3(0f, -0.01f, 0f);
        private static readonly Vector3 UnarmedHand = new Vector3(0.20f, -0.20f, 0f);

        /// <summary>
        /// The shared vertex-colour shading, with the depth squeezed into the
        /// nearest 2 % of the range: the first-person arms and rifle always
        /// draw in front of the world -- your own chest included, which sits
        /// nearer the eye than the rifle when you look down -- but still sort
        /// correctly against each other. The one-camera way to do what a
        /// second, depth-cleared camera would (a SubViewport overlay renders
        /// unlit under gl_compatibility). Godot 4.7 uses REVERSED depth even in
        /// the compatibility renderer: +1 is near (the −1-near form made the
        /// whole set vanish behind the far plane).
        /// </summary>
        public static readonly ShaderMaterial FpMaterial = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode specular_disabled;
void vertex() {
    POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION.z = (1.0 - (1.0 - POSITION.z / POSITION.w) * 0.02) * POSITION.w;
}
void fragment() {
    ALBEDO = COLOR.rgb;
    ROUGHNESS = 1.0;
    METALLIC = 0.0;
}",
            },
        };

        /// <summary>
        /// The same depth squeeze for a real (pbr) material: albedo (and its
        /// texture), normal map, roughness and metallic copied off the
        /// imported material.
        /// </summary>
        private static readonly Shader FpPbrShader = new Shader
        {
            Code = @"shader_type spatial;
uniform vec4 albedo : source_color = vec4(1.0);
uniform float roughness = 0.6;
uniform float metallic = 0.0;
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float normal_scale = 0.0;
void vertex() {
    POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POSITION.z = (1.0 - (1.0 - POSITION.z / POSITION.w) * 0.02) * POSITION.w;
}
void fragment() {
    ALBEDO = albedo.rgb * texture(albedo_tex, UV).rgb;
    NORMAL_MAP = texture(normal_tex, UV).rgb;
    NORMAL_MAP_DEPTH = normal_scale;
    ROUGHNESS = roughness;
    METALLIC = metallic;
}",
        };

        private static readonly Dictionary<Material, Material> FpCopies = new Dictionary<Material, Material>();

        /// <summary>
        /// Every surface under `root` draws with its first-person twin: the
        /// vertex-colour FpMaterial for baked models, a per-material copy of
        /// the depth-squeezed shader for pbr ones.
        /// </summary>
        public static void FpOverride(Node root)
        {
            foreach (MeshInstance3D mi in AssetRegistry.Descendants<MeshInstance3D>(root))
            {
                if (mi.Mesh == null) continue;
                for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                {
                    Material src = mi.GetActiveMaterial(i);
                    Material fp = FpMaterial;
                    if (src is BaseMaterial3D bm && !bm.VertexColorUseAsAlbedo)
                    {
                        if (!FpCopies.TryGetValue(src, out fp))
                        {
                            var sm = new ShaderMaterial { Shader = FpPbrShader };
                            sm.SetShaderParameter("albedo", bm.AlbedoColor);
                            sm.SetShaderParameter("roughness", bm.Roughness);
                            sm.SetShaderParameter("metallic", bm.Metallic);
                            if (bm.AlbedoTexture != null) sm.SetShaderParameter("albedo_tex", bm.AlbedoTexture);
                            if (bm.NormalEnabled && bm.NormalTexture != null)
                            {
                                sm.SetShaderParameter("normal_tex", bm.NormalTexture);
                                sm.SetShaderParameter("normal_scale", bm.NormalScale);
                            }
                            FpCopies[src] = fp = sm;
                        }
                    }
                    mi.SetSurfaceOverrideMaterial(i, fp);
                }
            }
        }

        private readonly Camera3D _eye;
        private readonly Node3D _fp;           // first-person arms holder, under the camera
        private readonly Node3D _muzzle;       // proxy, copied from the held weapon's muzzle
        private readonly Node3D _body;         // the real body, in the world
        private readonly uint _layers;
        private readonly AssetRegistry _assets;

        private Node3D _fpModel, _fpHeld, _fpMuzzle, _fpGrip;
        private FirstPersonAnim _fpAnim;
        private Node3D _bodyModel, _held;
        private CharacterAnim _bodyAnim;
        private string _heldAsset = "", _fpHeldAsset = "";

        private readonly Dictionary<string, string> _worn = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _wornDrawn = new Dictionary<string, string>();
        private readonly Dictionary<string, Node3D> _wornNodes = new Dictionary<string, Node3D>();
        private readonly Dictionary<string, string> _fpWornDrawn = new Dictionary<string, string>();
        private readonly Dictionary<string, Node3D> _fpWornNodes = new Dictionary<string, Node3D>();

        private Vector2 _sway;
        private float _bobPhase, _kick;
        private Vector3 _ads;                  // camera-space shift that frames the rifle (see Tick)
        private bool _lowered, _aiming;
        private float _adsW;   // 0 hip .. 1 aimed: how far the gun is on the sight line

        public ViewModel(Camera3D eye, Material material, uint layers, Node worldParent, AssetRegistry assets)
        {
            _eye = eye;
            _layers = layers;
            _assets = assets;

            // ---- first-person arms, under the camera ----
            _fp = new Node3D { Name = "FpArms" };
            eye.AddChild(_fp);
            PoseFp(Vector2.Zero, Vector3.Zero, 0f);
            _muzzle = new Node3D { Name = "muzzle", Position = new Vector3(0.1f, -0.15f, -0.6f) };
            eye.AddChild(_muzzle);

            assets.Attach("char.player", _fp, model =>
            {
                _fpModel = model;
                foreach (MeshInstance3D mi in AssetRegistry.Descendants<MeshInstance3D>(model))
                    if (mi.Name == "body" || mi.Name == "head") mi.Visible = false;
                AssetRegistry.SetLayers(model, _layers);
                FpOverride(model);
                foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(model))
                    g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                _fpAnim = FirstPersonAnim.For(model);
                if (_fpAnim != null) _fpAnim.Class = _cls;
                DressFp();
                HoldFp(_heldAsset);
            });

            // ---- body: a real object in the world, seen when you look down ----
            _body = new Node3D { Name = "LocalBody" };
            worldParent.AddChild(_body);
            BoxMesh.Attach(_body, "model", Models.PlayerLocal(), material, 0);
            // The head casts but does not draw. Without it the shadow on the
            // ground in front of you is headless.
            MeshInstance3D head = BoxMesh.Attach(_body, "head-shadow", Models.PlayerHead(), material, 0);
            head.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;

            assets.Attach("char.player", _body, model =>
            {
                _bodyModel = model;
                foreach (string part in new[] { "head", "arms" })
                {
                    Node3D n = AssetRegistry.FindNode(model, part);
                    if (n != null)
                        foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(n))
                            g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                }
                _bodyAnim = CharacterAnim.For(model);
                if (_bodyAnim != null) _bodyAnim.Class = _cls;
                EntityViews.Dress(_assets, a => a, _bodyModel, _worn, _wornDrawn, _wornNodes, local: true);
                string want = _heldAsset; _heldAsset = "\u0000"; Hold(want);
                foreach (Node3D n in AssetRegistry.Descendants<Node3D>(_body))
                    if (n.Name == "model" || n.Name == "head-shadow") n.Visible = false;
            });
        }

        /// <summary>Armor on our own body, and the arm pieces of it on the first-person arms. Takes the ASSET id.</summary>
        public void Wear(string slot, string asset)
        {
            _worn[slot] = asset ?? "";
            EntityViews.Dress(_assets, a => a, _bodyModel, _worn, _wornDrawn, _wornNodes, local: true);
            DressFp();
        }

        private void DressFp()
        {
            EntityViews.Dress(_assets, a => a, _fpModel, _worn, _fpWornDrawn, _fpWornNodes,
                keep: a => EntityViews.CoversArms(_assets, a), layers: _layers, material: FpMaterial);
            // The first-person arms wear only what is ON the forearms and
            // hands. Armor parts are separate meshes named "<bone>__<part>"
            // (art/tools/bpy/armor.py); a suit's chest plate and pauldrons sit
            // right under the camera and, drawn always-in-front, covered the
            // bottom of the view.
            foreach (Node3D piece in _fpWornNodes.Values)
                foreach (MeshInstance3D mi in AssetRegistry.Descendants<MeshInstance3D>(piece))
                {
                    string n = mi.Name.ToString();
                    mi.Visible = n.StartsWith("lowerarm") || n.StartsWith("hand");
                }
        }

        /// <summary>The barrel tip, in world space. Shots are DRAWN from here.</summary>
        public Vector3 MuzzlePosition => _muzzle.GlobalPosition;
        public Node3D Muzzle => _muzzle;
        public uint Layers => _layers;

        /// <summary>Something in hand: the fire gate.</summary>
        public bool Armed => _heldAsset != "" && _heldAsset != "\u0000";
        private string _cls = "";

        /// <summary>
        /// The equipped weapon, by ASSET id ("" for empty hands): in the
        /// first-person hand, drawn, and in the body's hand, shadows-only,
        /// with both instances on their armed clips.
        /// </summary>
        public void Hold(string asset, string cls = "")
        {
            asset ??= "";
            if (asset == _heldAsset) return;
            _heldAsset = asset;
            _cls = cls;
            if (_bodyAnim != null) _bodyAnim.Class = cls;
            if (_fpAnim != null) _fpAnim.Class = cls;
            HoldFp(asset);
            if (_bodyModel == null) return;
            if (_held != null) { _held.QueueFree(); _held = null; }
            if (_bodyAnim != null) _bodyAnim.Armed = asset != "";
            if (asset == "") return;
            Node3D hand = AssetRegistry.FindNode(_bodyModel, "hand.r");
            if (hand == null) return;
            _assets.Attach(asset, hand, weapon =>
            {
                _held = weapon;
                foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(weapon))
                    g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                float sc = hand.GlobalBasis.Scale.X;
                if (sc > 1e-4f) weapon.Scale = Vector3.One / sc;
                weapon.AddChild(new AlignToForearm { Weapon = weapon, Hand = hand, Model = _bodyModel });
            });
        }

        private void HoldFp(string asset)
        {
            if (_fpModel == null || asset == _fpHeldAsset) return;
            _fpHeldAsset = asset;
            if (_fpHeld != null) { _fpHeld.QueueFree(); _fpHeld = null; _fpMuzzle = null; _fpGrip = null; }
            if (_fpAnim != null) _fpAnim.Armed = asset != "";
            if (asset == "") return;
            Node3D hand = AssetRegistry.FindNode(_fpModel, "hand.r");
            if (hand == null) return;
            _assets.Attach(asset, hand, weapon =>
            {
                _fpHeld = weapon;
                AssetRegistry.SetLayers(weapon, _layers);
                FpOverride(weapon);
                foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(weapon))
                    g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                float sc = hand.GlobalBasis.Scale.X;
                if (sc > 1e-4f) weapon.Scale = Vector3.One / sc;
                weapon.AddChild(new AlignToForearm
                {
                    Weapon = weapon, Hand = hand, Model = _fpModel,
                    // Aiming: the barrel down the line of sight, so the front
                    // post sits on the crosshair whatever the arms manage.
                    Ads = () => (_eye.GlobalTransform, _adsW, _kick),
                    SightDistance = _cls == "_pistol" ? 0.42f : 0.20f,
                });
                _fpMuzzle = AssetRegistry.FindNode(weapon, "muzzle");
                _fpGrip = AssetRegistry.FindNode(weapon, "grip");
            });
        }

        /// <summary>The trigger went down: kick the arms, play the recoil clip.</summary>
        public void Fire()
        {
            _kick = KickMetres;
            // The recoil clip is built on the hip hold; aimed, the gun rides
            // the sight line and only the kick moves it (and the arms follow).
            if (!_aiming) _fpAnim?.Fire(Clock.Now);
        }

        /// <summary>R was pressed with a gun in hand: the reload motion, stretched to the weapon's time.</summary>
        public void Reload(double seconds) => _fpAnim?.Reload(seconds, Clock.Now);

        /// <summary>The first-person arms and weapon (hidden while seated).</summary>
        public bool ArmsVisible
        {
            get => _fp.Visible;
            set => _fp.Visible = value;
        }

        /// <summary>
        /// The local body, hidden while seated: the on-foot predictor is not
        /// stepped in a vehicle, so the body would stand where you boarded
        /// while the rover drives off. Same rule other clients apply to any
        /// seated row (GDD "Seats and occupancy").
        /// </summary>
        public bool BodyVisible
        {
            get => _body.Visible;
            set => _body.Visible = value;
        }

        /// <summary>
        /// The arms holder's rest: the body's eye (0, 1.7, 0) on the camera
        /// and its forward down −Z. `Attach` already turned the glTF model to
        /// +Z (one ModelFlip); the camera looks down −Z, so the holder flips
        /// it back. Rotations pivot about the EYE, not the feet 1.7 m below.
        /// </summary>
        public static Transform3D FpHolder(Basis r, Vector3 offset) =>
            new Transform3D(r * Frame.ModelFlip, r * new Vector3(0f, -FpsController.EyeHeight, 0f) + offset);

        private void PoseFp(Vector2 swayDeg, Vector3 bob, float kick)
        {
            float kickPitch = kick / KickMetres * KickPitchDeg;
            Basis r = Basis.FromEuler(new Vector3(
                Mathf.DegToRad(-swayDeg.Y + kickPitch), Mathf.DegToRad(-swayDeg.X), 0f));
            _fp.Transform = FpHolder(r, bob + _ads + new Vector3(0f, 0f, kick));
        }

        /// <summary>
        /// Once a frame. `speed` is the body's actual speed from the
        /// simulation, not the input: bobbing on input alone keeps bobbing
        /// while you walk into a wall. `aheadClear` is the distance to the
        /// nearest obstruction along the view (Blocked).
        /// </summary>
        public void Tick(Vector2 lookDelta, float speed, float dt, bool aiming, float aheadClear)
        {
            if (_lowered) _lowered = aheadClear < RaiseAt;
            else _lowered = aheadClear < LowerAt;
            bool ads = aiming && Armed && !_lowered;
            _aiming = ads;

            float scale = ads ? AdsSway : 1f;
            _sway = _sway.Lerp(-lookDelta * SwayDegrees * scale, 1f - Mathf.Exp(-SwaySmoothing * dt));
            float moving = Mathf.Clamp(speed / 4.5f, 0f, 1f);
            _bobPhase += dt * BobSpeed * moving;
            var bob = new Vector3(Mathf.Cos(_bobPhase), -Mathf.Abs(Mathf.Sin(_bobPhase)), 0f) * BobAmount * moving * scale;
            _kick = Mathf.Lerp(_kick, 0f, 1f - Mathf.Exp(-18f * dt));

            // Framing: the shoulders are 0.25 m under the eye, so no arm pose
            // alone puts a rifle where a first-person view wants it. Every
            // frame the arms holder is shifted (camera space, without the
            // shift already applied) so the rear sight lands on a target:
            // low and right for the hold, dead centre 30 cm out for ADS.
            // Empty hands get a fixed lift instead. Measured off the weapon,
            // so any rifle with a `grip` frames itself.
            Vector3 want = Vector3.Zero;
            Node3D hand = _fpModel != null ? AssetRegistry.FindNode(_fpModel, "hand.r") : null;
            if (!Armed && hand != null && IsInstanceValidNode(hand))
            {
                // Empty hands: the right hand to the lower right of the view.
                Vector3 un = _eye.GlobalTransform.AffineInverse() * hand.GlobalPosition - _ads;
                want = UnarmedHand - un;
                want.Z = 0f;
            }
            else if (Armed && _fpGrip != null && IsInstanceValidNode(_fpGrip) && _adsW > 0.5f)
            {
                // Aimed: the gun sits on the sight line by itself, so pull
                // the arms to it -- the right hand onto the grip.
                Vector3 gripCam = _eye.GlobalTransform.AffineInverse() * _fpGrip.GlobalPosition;
                Vector3 handCam = _eye.GlobalTransform.AffineInverse() * hand.GlobalPosition;
                want = _ads + (gripCam - handCam);
            }
            else if (Armed && _fpGrip != null && IsInstanceValidNode(_fpGrip))
            {
                Node3D sightNode = AssetRegistry.FindNode(_fpHeld, "sight");
                Vector3 sightAt = sightNode != null ? sightNode.GlobalPosition
                    : _fpGrip.GlobalPosition + _fpGrip.GlobalBasis.Y.Normalized() * SightAboveGrip;
                Vector3 sight = _eye.GlobalTransform.AffineInverse() * sightAt;
                Vector3 un = sight - _ads;
                want = (ads ? AdsSight : HoldSight) - un;
                // Sideways and up only. Pushing the sight further out drags the
                // whole body forward and its shoulders into the frame; a real
                // rear sight sits 15-20 cm from the eye anyway.
                want.Z = 0f;
            }
            _ads = _ads.Lerp(want, 1f - Mathf.Exp(-14f * dt));
            _adsW = Mathf.MoveToward(_adsW, ads ? 1f : 0f, dt / 0.18f);   // 0.18 s in or out

            _eye.Fov = Mathf.Lerp(_eye.Fov, ads ? AdsFov : BaseFov, 1f - Mathf.Exp(-12f * dt));

            PoseFp(_sway, bob, _kick);
            _fpAnim?.Drive(speed, ads, _lowered, Clock.Now);

            // Same speed the arms bob on, so your legs and your gun cannot
            // disagree about whether you are moving.
            _bodyAnim?.Drive(speed, false);

            if (_fpMuzzle != null && IsInstanceValidNode(_fpMuzzle))
            {
                _muzzle.GlobalPosition = _fpMuzzle.GlobalPosition;
                _muzzle.GlobalBasis = _fpMuzzle.GlobalBasis.Orthonormalized();
            }
        }

        private static bool IsInstanceValidNode(Node n) => GodotObject.IsInstanceValid(n) && n.IsInsideTree();

        /// <summary>
        /// Stands the body at the player's feet, facing where the body faces —
        /// which is NOT where the camera looks. Pitch belongs to the head; a
        /// body that pitches with the view lies on its back when you look up.
        /// </summary>
        public void Place(Vec3 feet, Vec3 facing) => EntityViews.Place(_body, feet, facing);

        /// <summary>
        /// Distance along `fwd` from `eye` to the nearest collider or the
        /// ground, up to `reach`; +∞ when clear. Boxes by a slab test in
        /// their own frame (+5 cm), spheres by ray-sphere, terrain sampled at
        /// the reach point. Game-side only: the shared Sim colliders are the
        /// C40 reference and stay untouched.
        /// </summary>
        public static float Blocked(Vector3 eye, Vector3 fwd, float reach, Collider[] colliders, TerrainField terrain)
        {
            const float margin = 0.05f;
            float best = float.PositiveInfinity;
            foreach (Collider c in colliders ?? Array.Empty<Collider>())
            {
                Vector3 centre = Frame.ToGodot(c.Center);
                if ((centre - eye).Length() > reach + (float)Math.Max(c.Half.X, Math.Max(c.Half.Y, c.Half.Z)) + 1f) continue;
                float t;
                if (c.Kind == ColliderKind.Sphere)
                {
                    float r = (float)c.Half.X + margin;
                    Vector3 oc = eye - centre;
                    float b = oc.Dot(fwd), cc = oc.Dot(oc) - r * r, disc = b * b - cc;
                    if (disc < 0) continue;
                    t = -b - Mathf.Sqrt(disc);
                    if (t < 0) t = cc < 0 ? 0 : float.PositiveInfinity;
                }
                else
                {
                    var q = new Quaternion((float)c.Rot.X, (float)c.Rot.Y, (float)c.Rot.Z, (float)c.Rot.W);
                    Basis inv = new Basis(q).Inverse();
                    Vector3 o = inv * (eye - centre), d = inv * fwd;
                    Vector3 h = new Vector3((float)c.Half.X, (float)c.Half.Y, (float)c.Half.Z) + Vector3.One * margin;
                    float tmin = 0f, tmax = reach;
                    bool hit = true;
                    for (int i = 0; i < 3 && hit; i++)
                    {
                        if (Mathf.Abs(d[i]) < 1e-6f) { if (Mathf.Abs(o[i]) > h[i]) hit = false; continue; }
                        float t1 = (-h[i] - o[i]) / d[i], t2 = (h[i] - o[i]) / d[i];
                        if (t1 > t2) (t1, t2) = (t2, t1);
                        tmin = Mathf.Max(tmin, t1); tmax = Mathf.Min(tmax, t2);
                        if (tmin > tmax) hit = false;
                    }
                    if (!hit) continue;
                    t = tmin;
                }
                if (t <= reach) best = Mathf.Min(best, t);
            }
            if (terrain != null)
            {
                Vector3 p = eye + fwd * reach;
                if (terrain.SampleRadius(Frame.ToSim(p.Normalized())) > p.Length()) best = Mathf.Min(best, reach);
            }
            return best;
        }
    }

    /// <summary>
    /// The first-person clip picker: `fp_*` clips on the arms instance.
    /// Gaits and poses cross-fade; fire and reload are one-shots that hold
    /// the arms until they end.
    /// </summary>
    public sealed class FirstPersonAnim
    {
        private readonly AnimationPlayer _player;
        private string _current;
        private double _oneShotUntil;
        public bool Armed;
        /// <summary>Hold family suffix, as CharacterAnim.Class.</summary>
        public string Class = "";

        private FirstPersonAnim(AnimationPlayer player)
        {
            _player = player;
            _player.Deterministic = true;   // untracked bones to rest; see CharacterAnim
            foreach (string name in _player.GetAnimationList())
                _player.GetAnimation(name).LoopMode =
                    CharacterAnim.OneShot(name) ? Animation.LoopModeEnum.None : Animation.LoopModeEnum.Linear;
        }

        public static FirstPersonAnim For(Node model)
        {
            foreach (AnimationPlayer ap in AssetRegistry.Descendants<AnimationPlayer>(model))
                return ap.HasAnimation("fp_idle") ? new FirstPersonAnim(ap) : null;
            return null;
        }

        /// <summary>Unarmed beats lowered beats aiming beats the gait.</summary>
        public static string Pick(float speed, bool armed, bool aiming, bool lowered) =>
            !armed ? "fp_unarmed"
            : lowered ? "fp_lower"
            : aiming ? "fp_ads"
            : speed > CharacterAnim.SprintAt ? "fp_sprint"
            : speed > CharacterAnim.WalkAt ? "fp_walk"
            : "fp_idle";

        public void Drive(float speed, bool aiming, bool lowered, double now)
        {
            if (now < _oneShotUntil) return;
            string want = CharacterAnim.Clip(_player, Pick(speed, Armed, aiming, lowered), Class);
            if (want == _current || !_player.HasAnimation(want)) return;
            _player.Play(want, aiming ? 0.10 : 0.15);
            _current = want;
        }

        public void Fire(double now)
        {
            string clip = CharacterAnim.Clip(_player, "fp_fire", Class);
            if (!Armed || !_player.HasAnimation(clip) || now < _oneShotUntil && _current.StartsWith("fp_reload")) return;
            _player.Play(clip, 0.02);
            _player.Seek(0, true);
            _oneShotUntil = now + _player.GetAnimation(clip).Length;
            _current = clip;
        }

        public void Reload(double seconds, double now)
        {
            string clip = CharacterAnim.Clip(_player, "fp_reload", Class);
            if (!Armed || !_player.HasAnimation(clip) || seconds <= 0) return;
            double len = _player.GetAnimation(clip).Length;
            _player.Play(clip, 0.1, (float)(len / seconds));
            _oneShotUntil = now + seconds;
            _current = clip;
        }
    }
}
