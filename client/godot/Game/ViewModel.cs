// The first-person view: what you see of yourself.
//
// Two instances of the same body, on purpose.
//
// THE FIRST-PERSON ARMS are a second `char.player` hung under the camera with
// its eye (0, 1.70, 0 in body space) on the camera and its forward down the
// camera's −Z. Only its `arms` mesh is drawn (body and head hidden), on the
// rig layer, lit by the rig light alone (Boot), casting no shadow. It plays
// the `fp_*` clips from art/tools/bpy/body.py. A hand weapon hangs in its
// own `hand.r`; a gun is weapon-first (Phase 27): posed by code under the
// camera (PoseWeapon) with the hands put on its `grip` and `fore` by HandIk.
// Either is the same model, grip and muzzle other players see. Worn armor
// that covers the arms (sleeves, gloves) is dressed on it too, so what you
// wear is what you see. Sway, bob and recoil move the arms
// about the EYE; the camera itself never kicks (GDD "First-person body").
// A wall closer than the rifle lowers it (fp_lower) instead of clipping it.
//
// THE BODY is the opposite: a real object at the player's feet on the normal
// layer, so looking down shows your torso and legs where they are and other
// players see exactly the same model. Its capped `torso`, its `legs` and the
// armor worn on chest/back/legs/feet draw with the NEAR-CUT material (real
// depth and sun, dissolved within NearCut of the eye) and cast nothing;
// shadows-only twins of them, plus the shadows-only head, hair, arms,
// `chest`, gloves and held weapon, cast the whole-figure ground shadow
// (GDD "First-person body, in the world").

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
        // Camera-space target for a gun's rear sight at the hip (+Y up, +X right).
        private static readonly Vector3 HoldSight = new Vector3(0.12f, -0.12f, 0f);
        // A gun's hip depth for that sight (where the arms held it before Phase 27).
        private const float HoldDepthRifle = 0.27f, HoldDepthPistol = 0.36f;
        // Aimed: eye to rear sight -- a shouldered long gun, a pistol at arm's length.
        private const float SightDistanceRifle = 0.20f, SightDistancePistol = 0.42f;
        // Hip fire: the bore converges on the crosshair this far out.
        private const float Converge = 15f;
        // Lowered (a wall ahead): the hip pose turned down and left about its
        // rear sight, which is lifted a little: the hip sight already sits
        // near the bottom edge, and turned about the grip (under the frame)
        // or dropped, the gun left the view.
        private const float LowerPitchDeg = 15f, LowerYawDeg = 25f, LowerLift = 0.035f;
        private const float ReloadTipDeg = 12f;   // the gun cants while the left hand is off it
        private static readonly Vector3 UnarmedHand = new Vector3(0.20f, -0.20f, 0f);
        private static readonly Vector3 UnarmedHandVanguard = new Vector3(0.20f, -0.17f, 0f);

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
                // Never frustum-culled: a skinned mesh is culled by bounds
                // that do not follow the pose, and arms posed up into the view
                // from shoulders under it vanished whole on some bodies (the
                // Colonist F unarmed, 2026-11-14) -- "the arms disappear".
                mi.ExtraCullMargin = 16384f;
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

        /// <summary>
        /// The near cut (GDD "First-person body, in the world"): what the
        /// local body draws with. The first-person shading WITHOUT the depth
        /// squeeze -- real depth, so a crate hides your shins, and it
        /// receives the sun's shadows -- plus a per-pixel discard of anything
        /// within NearCut of the eye, dithered over NearCutBand by a screen
        /// hash so a collar or a pauldron dissolves instead of slicing at the
        /// near plane. Wearers cast no shadow (the discard would punch holes
        /// in it); the shadows-only parts and the 3PM pieces cast the figure.
        /// </summary>
        public const float NearCut = 0.30f, NearCutBand = 0.05f;

        private const string NearCutCode = @"
uniform float NearCut = 0.30;
uniform float NearCutBand = 0.05;
float near_hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
void near_cut(vec3 v, vec2 frag) {
    float d = length(v);
    float t = clamp((d - NearCut) / NearCutBand, 0.0, 1.0);
    if (t < near_hash(frag)) discard;
}
";

        /// <summary>
        /// The torso's cap is a stand-in for the chest above it -- and that
        /// chest is a shadows-only caster sitting right on top of it, so
        /// from any sun the cap was black. On up-facing torso fragments
        /// (model-space normal, the cap) the shadow term is floored at
        /// CapShadowFloor; everything else, and a world shadow's darkening
        /// down to the floor, is plain Lambert.
        /// </summary>
        public const float CapShadowFloor = 0.65f;

        private const string CapLightCode = @"
uniform float CapShadowFloor = 0.65;
varying float v_cap;
void vertex() { v_cap = smoothstep(0.6, 0.9, NORMAL.y); }
void light() {
    float a = mix(ATTENUATION, max(ATTENUATION, CapShadowFloor), v_cap);
    DIFFUSE_LIGHT += clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * a * LIGHT_COLOR / PI;
}
";

        private const string PbrFragment = @"
uniform vec4 albedo : source_color = vec4(1.0);
uniform float roughness = 0.6;
uniform float metallic = 0.0;
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float normal_scale = 0.0;
uniform float alpha_cut = -1.0;
void fragment() {
    near_cut(VERTEX, FRAGCOORD.xy);
    vec4 tex = texture(albedo_tex, UV);
    if (albedo.a * tex.a < alpha_cut) discard;
    ALBEDO = albedo.rgb * tex.rgb;
    NORMAL_MAP = texture(normal_tex, UV).rgb;
    NORMAL_MAP_DEPTH = normal_scale;
    ROUGHNESS = roughness;
    METALLIC = metallic;
}";

        private const string VcFragment = @"
void fragment() {
    near_cut(VERTEX, FRAGCOORD.xy);
    ALBEDO = COLOR.rgb;
    ROUGHNESS = 1.0;
    METALLIC = 0.0;
}";

        /// <summary>Variant index: bit 0 pbr, bit 1 cull disabled, bit 2 the cap light.</summary>
        private static Shader NearCutShader(int v) => new Shader
        {
            Code = "shader_type spatial;\nrender_mode " + ((v & 2) == 0 ? "cull_back" : "cull_disabled") +
                   ((v & 1) == 0 ? ", specular_disabled" : "") + ";\n" + NearCutCode +
                   ((v & 4) != 0 ? CapLightCode : "") + ((v & 1) != 0 ? PbrFragment : VcFragment),
        };

        private static readonly Shader[] NearCutShaders =
        {
            NearCutShader(0), NearCutShader(1), NearCutShader(2), NearCutShader(3),
            NearCutShader(4), NearCutShader(5), NearCutShader(6), NearCutShader(7),
        };

        /// <summary>
        /// -selftest: every near-cut variant parses (a shader that fails to
        /// compile lists no uniforms) and carries NearCut/NearCutBand.
        /// </summary>
        public static bool NearCutShadersParse() => Array.TrueForAll(NearCutShaders, sh =>
        {
            var names = new HashSet<string>();
            foreach (Godot.Collections.Dictionary u in sh.GetShaderUniformList()) names.Add((string)u["name"]);
            return names.Contains("NearCut") && names.Contains("NearCutBand");
        });

        private static readonly Dictionary<(Material, bool), Material> NearCopies = new Dictionary<(Material, bool), Material>();
        private static readonly Material NoSource = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        /// <summary>
        /// The near-cut twin of `source`, cached per (source, cap): the
        /// vertex-colour variant for the shared shading, a pbr copy (albedo
        /// and its texture, normal map, roughness, metallic) for real
        /// materials, culling as the source does. `cap` adds the cap light
        /// (the torso). Null for anything that is not a BaseMaterial3D.
        /// </summary>
        public static Material NearCutFor(Material source, bool cap = false)
        {
            source ??= NoSource;
            if (NearCopies.TryGetValue((source, cap), out Material m)) return m;
            if (source is not BaseMaterial3D bm) return null;
            bool pbr = !bm.VertexColorUseAsAlbedo;
            int v = (pbr ? 1 : 0) | (bm.CullMode == BaseMaterial3D.CullModeEnum.Back ? 0 : 2) | (cap ? 4 : 0);
            var sm = new ShaderMaterial { Shader = NearCutShaders[v] };
            if (pbr)
            {
                sm.SetShaderParameter("albedo", bm.AlbedoColor);
                sm.SetShaderParameter("roughness", bm.Roughness);
                sm.SetShaderParameter("metallic", bm.Metallic);
                if (bm.AlbedoTexture != null) sm.SetShaderParameter("albedo_tex", bm.AlbedoTexture);
                if (bm.NormalEnabled && bm.NormalTexture != null)
                {
                    sm.SetShaderParameter("normal_tex", bm.NormalTexture);
                    sm.SetShaderParameter("normal_scale", bm.NormalScale);
                }
                // A blended surface (an armor decal) becomes a cut-out: the
                // near cut is an opaque pass, and a blended quad drawn
                // opaque was a cream slab over the plate.
                if (bm.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor)
                    sm.SetShaderParameter("alpha_cut", bm.AlphaScissorThreshold);
                else if (bm.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                    sm.SetShaderParameter("alpha_cut", 0.5f);
            }
            NearCopies[(source, cap)] = sm;
            return sm;
        }

        /// <summary>
        /// Every surface under `root` draws near-cut and casts no shadow
        /// (the local body's drawn parts and the pieces worn on them). Each
        /// mesh gets a shadows-only twin on the same skeleton with its own
        /// material: that twin is the 3PM's share of the whole-figure ground
        /// shadow, never punched by the discard. `cap`: the torso.
        /// </summary>
        public static void NearCutOverride(Node root, bool cap = false)
        {
            foreach (MeshInstance3D mi in new List<MeshInstance3D>(AssetRegistry.Descendants<MeshInstance3D>(root)))
            {
                if (mi.HasMeta("sa_shadow_twin")) continue;
                if (mi.GetParent() is Node parent)
                {
                    var twin = (MeshInstance3D)mi.Duplicate();
                    twin.Name = mi.Name + "_shadow";
                    twin.SetMeta("sa_shadow_twin", true);
                    twin.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                    parent.AddChild(twin);
                }
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (mi.Mesh == null) continue;
                for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                {
                    Material nc = NearCutFor(mi.GetActiveMaterial(i), cap);
                    if (nc != null) mi.SetSurfaceOverrideMaterial(i, nc);
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
        private HandIk _fpIk;                  // the arms onto a gun (guns only; Phase 27)
        private Vector3 _sightL, _frontL, _gripL; // the held gun's sight, front post, grip in its own frame
        private Node3D _bodyModel, _held;
        private CharacterAnim _bodyAnim;
        private string _heldAsset = "", _fpHeldAsset = "";

        private readonly Dictionary<string, string> _worn = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _wornDrawn = new Dictionary<string, string>();
        private readonly Dictionary<string, Node3D> _wornNodes = new Dictionary<string, Node3D>();
        private readonly Dictionary<string, string> _fpWornDrawn = new Dictionary<string, string>();
        private readonly Dictionary<string, Node3D> _fpWornNodes = new Dictionary<string, Node3D>();

        private Vector2 _sway;
        private float _bobPhase, _kick, _moving;
        private Vector3 _ads;                  // camera-space shift that frames the rifle (see Tick)
        private bool _lowered, _aiming;
        private float _adsW;   // 0 hip .. 1 aimed: how far the gun is on the sight line
        private float _lowW;   // 0 hip .. 1 lowered
        private float _leftW = 1f;   // the left hand's IK weight: 0 while a reload has it
        private float _reachHip, _reachAim;   // the gun pulled in (m, toward the eye) at hip / aimed until both arms reach it
        private const float ReachSlack = 0.02f;   // ...with this much bend left in the straighter arm
        private double _reloadFrom, _reloadUntil;

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

            AttachFp("char.player");

            // ---- body: a real object in the world, seen when you look down ----
            _body = new Node3D { Name = "LocalBody" };
            worldParent.AddChild(_body);
            BoxMesh.Attach(_body, "model", Models.PlayerLocal(), material, 0);
            // The head casts but does not draw. Without it the shadow on the
            // ground in front of you is headless.
            MeshInstance3D head = BoxMesh.Attach(_body, "head-shadow", Models.PlayerHead(), material, 0);
            head.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;

            AttachBody("char.player");
        }

        /// <summary>The body id both instances wear ("char.player" until SetBody).</summary>
        public string BodyId { get; private set; } = "char.player";

        /// <summary>
        /// Phase 16: the chosen character's body (GDD "Characters" Bodies) on
        /// both the first-person arms and the local body. The old instances
        /// go, armor and the held weapon are re-hung on the new ones. An id
        /// with no .glb falls back to char.player.
        /// </summary>
        public void SetBody(string bodyId)
        {
            if (string.IsNullOrEmpty(bodyId)) bodyId = "char.player";
            if (!_assets.Has(bodyId))
            {
                GD.Print($"viewmodel: body {bodyId} not in the manifest, wearing char.player");
                bodyId = "char.player";
            }
            if (bodyId == BodyId) return;
            BodyId = bodyId;

            if (_fpModel != null) { _fpModel.QueueFree(); _fpModel = null; }
            _fpAnim = null; _fpIk = null;
            // A gun hangs under the arms holder, not the model: it goes by hand.
            if (_fpHeld != null && GodotObject.IsInstanceValid(_fpHeld)) _fpHeld.QueueFree();
            _fpHeld = null; _fpMuzzle = null; _fpGrip = null; _fpHeldAsset = "";
            _fpWornDrawn.Clear(); _fpWornNodes.Clear();

            if (_bodyModel != null) { _bodyModel.QueueFree(); _bodyModel = null; }
            _bodyAnim = null;
            _held = null;
            _wornDrawn.Clear(); _wornNodes.Clear();

            AttachFp(bodyId);
            AttachBody(bodyId);
        }

        private void AttachFp(string bodyId)
        {
            _assets.Attach(bodyId, _fp, model =>
            {
                _fpModel = model;
                foreach (MeshInstance3D mi in AssetRegistry.Descendants<MeshInstance3D>(model))
                    if (mi.Name == "chest" || mi.Name == "torso" || mi.Name == "legs" || mi.Name == "head") mi.Visible = false;
                AssetRegistry.SetLayers(model, _layers);
                FpOverride(model);
                foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(model))
                    g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                _fpAnim = FirstPersonAnim.For(model);
                if (_fpAnim != null) _fpAnim.Class = _cls;
                foreach (Skeleton3D sk in AssetRegistry.Descendants<Skeleton3D>(model))
                {
                    _fpIk = new HandIk { Name = "HandIk", Active = false };
                    sk.AddChild(_fpIk);
                    break;
                }
                DressFp();
                HoldFp(_heldAsset);
            });
        }

        private void AttachBody(string bodyId)
        {
            _assets.Attach(bodyId, _body, model =>
            {
                _bodyModel = model;
                // The 1PM (GDD "First-person body, in the world"): the
                // capped torso and the legs draw near-cut, in the world,
                // casting nothing; head, arms and the chest above the cap
                // are shadows-only, so the ground shadow is the whole figure.
                foreach (string part in new[] { "head", "arms", "chest" })
                {
                    Node3D n = AssetRegistry.FindNode(model, part);
                    if (n != null)
                        foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(n))
                            g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                }
                if (AssetRegistry.FindNode(model, "torso") is Node3D torso) NearCutOverride(torso, cap: true);
                if (AssetRegistry.FindNode(model, "legs") is Node3D legs) NearCutOverride(legs);
                _bodyAnim = CharacterAnim.For(model);
                if (_bodyAnim != null) _bodyAnim.Class = _cls;
                EntityViews.Dress(_assets, a => a, _bodyModel, _worn, _wornDrawn, _wornNodes, local: true);
                string want = _heldAsset; _heldAsset = "\u0000"; Hold(want, _cls);
                foreach (Node3D n in AssetRegistry.Descendants<Node3D>(_body))
                    if (n.Name == "model" || n.Name == "head-shadow") n.Visible = false;
            });
        }

        /// <summary>The local body's model is attached (Wear lands on it now).</summary>
        public bool BodyReady => _bodyModel != null;

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
                    mi.ExtraCullMargin = 16384f;   // as FpOverride: never culled
                }
            // A worn piece has its own skeleton (BoneMirror copies the clip's
            // pose into it): it repeats the arms' IK, or a vambrace would stay
            // where the clip put the forearm while the arm is on the gun.
            foreach (Node3D piece in _fpWornNodes.Values)
                foreach (Skeleton3D sk in AssetRegistry.Descendants<Skeleton3D>(piece))
                {
                    if (_fpIk != null && sk.GetNodeOrNull("HandIk") == null) sk.AddChild(new HandIk { Name = "HandIk", Source = _fpIk });
                    break;
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
                weapon.AddChild(new AlignToForearm { Weapon = weapon, Hand = hand, Model = _bodyModel, Blade = cls.StartsWith("_melee") });
            });
        }

        private void HoldFp(string asset)
        {
            if (_fpModel == null || asset == _fpHeldAsset) return;
            _fpHeldAsset = asset;
            if (_fpHeld != null) { _fpHeld.QueueFree(); _fpHeld = null; _fpMuzzle = null; _fpGrip = null; }
            if (_fpIk != null) { _fpIk.Active = false; _fpIk.Weapon = null; }
            if (_fpAnim != null) _fpAnim.Armed = asset != "";
            if (asset == "") return;
            Node3D hand = AssetRegistry.FindNode(_fpModel, "hand.r");
            if (hand == null) return;
            // A gun is weapon-first (Phase 27): it hangs under the arms holder,
            // placed by Tick, and HandIk puts the hands on it. A hand weapon
            // stays on the fist, swung by its clips.
            bool blade = _cls.StartsWith("_melee");
            _assets.Attach(asset, blade ? hand : _fp, weapon =>
            {
                _fpHeld = weapon;
                AssetRegistry.SetLayers(weapon, _layers);
                FpOverride(weapon);
                foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(weapon))
                    g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                _fpMuzzle = AssetRegistry.FindNode(weapon, "muzzle");
                _fpGrip = AssetRegistry.FindNode(weapon, "grip");
                if (blade)
                {
                    float sc = hand.GlobalBasis.Scale.X;
                    if (sc > 1e-4f) weapon.Scale = Vector3.One / sc;
                    weapon.AddChild(new AlignToForearm { Weapon = weapon, Hand = hand, Model = _fpModel, Blade = true });
                    return;
                }
                // The gun's own frame (bore +Z, top +Y after the holder's flip).
                Transform3D inv = weapon.GlobalTransform.AffineInverse();
                Vector3 L(Node3D n) => inv * n.GlobalPosition;
                Node3D sight = AssetRegistry.FindNode(weapon, "sight"), front = AssetRegistry.FindNode(weapon, "front");
                _gripL = _fpGrip != null ? L(_fpGrip) : Vector3.Zero;
                _sightL = sight != null ? L(sight) : _gripL + Vector3.Up * SightAboveGrip;
                _frontL = front != null ? L(front) : _fpMuzzle != null ? L(_fpMuzzle) + (_sightL.Y - L(_fpMuzzle).Y) * Vector3.Up : _sightL + Vector3.Back;
                AnimationPlayer ap = null;
                foreach (AnimationPlayer p in AssetRegistry.Descendants<AnimationPlayer>(_fpModel)) { ap = p; break; }
                Animation clip = ap != null ? ap.GetAnimation(CharacterAnim.Clip(ap, "fp_idle", _cls)) : null;
                if (_fpIk != null)
                {
                    // Long guns share the rifle's grip and fore-end (human.py FP_GUN).
                    _fpIk.Active = _fpIk.Reference(_fpModel, clip, weapon, _cls == "_pistol" ? "pistol" : "rifle");
                    _fpIk.WeightR = 1f; _fpIk.WeightL = _leftW;
                }
                GD.Print($"viewmodel: {asset} weapon-first on {BodyId}, hand ik {(_fpIk?.Active == true ? (_fpIk.Held ? "on, hold empties" : "on, palm mounts") : "OFF")}");
                PoseWeapon();
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

        /// <summary>A melee swing: `clip` (attack / attack_spin) on the arms and on the body.</summary>
        public void Swing(string clip)
        {
            _fpAnim?.Swing("fp_" + clip, Clock.Now);
            _bodyAnim?.Attack(Clock.Now, clip);
        }

        /// <summary>R was pressed with a gun in hand: the reload motion, stretched to the weapon's time.</summary>
        public void Reload(double seconds)
        {
            // The left hand leaves the gun for the clip's magazine and comes back as it ends.
            if (seconds > 0) { _reloadFrom = Clock.Now; _reloadUntil = Clock.Now + seconds; }
            _fpAnim?.Reload(seconds, Clock.Now);
        }

        /// <summary>
        /// The first-person frame rule, measured (rig `uiShot` prints it):
        /// the shift the framing applies to the arms (`lift`; a clip authored
        /// at view height needs none) and where each shoulder and elbow sits
        /// in camera space (x right, y up, z ahead), `IN` when it is inside
        /// the frame. An arm must leave the frame before it ends: a shoulder
        /// in frame is a cut-off arm.
        /// </summary>
        public IEnumerable<(string clip, float at)> FpSheet() => _fpAnim?.Sheet() ?? Array.Empty<(string, float)>();
        public void FpPose(string clip, float at)
        {
            // A frozen reload frame shows the left hand where the clip has it.
            bool reload = clip.StartsWith("fp_reload");
            _reloadUntil = reload ? double.MaxValue : 0;
            _leftW = reload ? 0f : 1f;
            _fpAnim?.Pose(clip, at);
        }

        public const string MotionHeader = "gapR,gapL,sx,sy,sz,bx,by,bz,hrx,hry,hrz,hlx,hly,hlz";

        /// <summary>
        /// One frame of the first-person hold, for the rig's `-uiMotion`:
        /// metres from each hand mount to its socket on the weapon (`grip`,
        /// `fore`; -1 without one), then in camera space (x right, y up, z
        /// ahead) the sight, the bore direction and both hand mounts.
        /// </summary>
        public string MotionSample()
        {
            Transform3D inv = _eye.GlobalTransform.AffineInverse();
            Vector3 Cam(Vector3 w) { Vector3 c = inv * w; return new Vector3(c.X, c.Y, -c.Z); }
            // Each hand's node that HandIk puts on its socket: the hold empty
            // when the body has them (its origin IS the socket's), else the palm mount.
            bool ik = _fpIk != null && _fpIk.Active;
            Node3D hr = ik && _fpIk.MountOf(0) is Node3D mr && IsInstanceValidNode(mr) ? mr : _fpModel != null ? AssetRegistry.FindNode(_fpModel, "hand.r") : null;
            Node3D hl = ik && _fpIk.MountOf(1) is Node3D ml && IsInstanceValidNode(ml) ? ml : _fpModel != null ? AssetRegistry.FindNode(_fpModel, "hand.l") : null;
            bool held = _fpHeld != null && IsInstanceValidNode(_fpHeld);
            Node3D grip = held ? AssetRegistry.FindNode(_fpHeld, "grip") : null;
            Node3D fore = held ? AssetRegistry.FindNode(_fpHeld, "fore") : null;
            Node3D sight = held ? AssetRegistry.FindNode(_fpHeld, "sight") ?? grip : null;
            Node3D muzzle = held ? AssetRegistry.FindNode(_fpHeld, "muzzle") : null;
            float gapR = hr != null && grip != null ? hr.GlobalPosition.DistanceTo(grip.GlobalPosition) : -1f;
            float gapL = hl != null && fore != null ? hl.GlobalPosition.DistanceTo(fore.GlobalPosition) : -1f;
            Vector3 s = sight != null ? Cam(sight.GlobalPosition) : Vector3.Zero;
            Vector3 b = grip != null && muzzle != null
                ? (Cam(muzzle.GlobalPosition) - Cam(grip.GlobalPosition)).Normalized() : Vector3.Zero;
            Vector3 r = hr != null ? Cam(hr.GlobalPosition) : Vector3.Zero;
            Vector3 l = hl != null ? Cam(hl.GlobalPosition) : Vector3.Zero;
            return FormattableString.Invariant(
                $"{gapR:F4},{gapL:F4},{s.X:F4},{s.Y:F4},{s.Z:F4},{b.X:F4},{b.Y:F4},{b.Z:F4},{r.X:F4},{r.Y:F4},{r.Z:F4},{l.X:F4},{l.Y:F4},{l.Z:F4}");
        }

        public string FpAudit()
        {
            var sb = new System.Text.StringBuilder($"{BodyId} held={_heldAsset} lift=({_ads.X:F3},{_ads.Y:F3},{_ads.Z:F3})");
            if (_fpIk != null && _fpIk.Active) sb.Append($" wrist=({_fpIk.WristBend(0):F0},{_fpIk.WristBend(1):F0})");
            if (_fpModel == null) return sb.ToString();
            float ty = Mathf.Tan(Mathf.DegToRad(_eye.Fov / 2f));
            Vector2 vp = _eye.GetViewport().GetVisibleRect().Size;
            float tx = ty * vp.X / Mathf.Max(1f, vp.Y);
            foreach (Skeleton3D sk in AssetRegistry.Descendants<Skeleton3D>(_fpModel))
            {
                foreach (string bone in new[] { "upperarm_r", "upperarm_l", "lowerarm_r", "lowerarm_l" })
                {
                    int i = sk.FindBone(bone);
                    if (i < 0) continue;
                    // On a gun the arms are HandIk's, not the clip's (Skeleton3D restores the clip after its modifiers).
                    Transform3D pose = _fpIk != null && _fpIk.Active && _fpIk.Solved.TryGetValue(i, out Transform3D solved) ? solved : sk.GetBoneGlobalPose(i);
                    Vector3 c = _eye.GlobalTransform.AffineInverse() * (sk.GlobalTransform * pose.Origin);
                    float z = -c.Z;
                    bool inFrame = z > _eye.Near && Mathf.Abs(c.X) < tx * z && Mathf.Abs(c.Y) < ty * z;
                    sb.Append($" {bone}=({c.X:F2},{c.Y:F2},{z:F2}){(inFrame ? "IN" : "")}");
                }
                break;
            }
            foreach (string mount in new[] { "hand.r", "hand.l" })
                if (AssetRegistry.FindNode(_fpModel, mount) is Node3D m)
                {
                    Vector3 c = _eye.GlobalTransform.AffineInverse() * m.GlobalPosition;
                    sb.Append($" {mount}=({c.X:F2},{c.Y:F2},{-c.Z:F2})");
                }
            return sb.ToString();
        }

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

        private static Basis SwayBasis(Vector2 swayDeg, float pitchDeg) => Basis.FromEuler(new Vector3(
            Mathf.DegToRad(-swayDeg.Y + pitchDeg), Mathf.DegToRad(-swayDeg.X), 0f));

        private void PoseFp(Vector2 swayDeg, Vector3 bob, float kick)
        {
            Basis r = SwayBasis(swayDeg, kick / KickMetres * KickPitchDeg);
            _fp.Transform = FpHolder(r, bob + _ads + new Vector3(0f, 0f, kick));
        }

        /// <summary>An orthonormal frame with +Z along `fwd` and +Y as near `up` as it can be.</summary>
        private static Basis Aligned(Vector3 fwd, Vector3 up)
        {
            Vector3 z = fwd.Normalized(), x = up.Cross(z).Normalized();
            return new Basis(x, z.Cross(x), z);
        }

        /// <summary>The gun in camera space (x right, y up, −z ahead) with its `local` point at `at` and its line of sight along `los`, top up.</summary>
        private Transform3D GunAt(Vector3 at, Vector3 local, Vector3 los)
        {
            Basis b = Aligned(los, Vector3.Up) * Aligned(_frontL - _sightL, Vector3.Up).Inverse();
            return new Transform3D(b, at - b * local);
        }

        private static float Ease(float w) => w * w * (3f - 2f * w);

        /// <summary>
        /// Weapon-first (Phase 27): the gun's whole transform, by code, every
        /// frame -- hip (rear sight at HoldSight, the bore converged on the
        /// crosshair), aimed (the line of sight down the view, rear sight
        /// SightDistance ahead), lowered (the hip pose turned down and left
        /// about its rear sight), eased between; then the reload cant, the recoil
        /// kick, sway and bob. No clip moves it. HandIk then puts the hands on it.
        /// </summary>
        private void PoseWeapon(Vector2 sway = default, Vector3 bob = default)
        {
            if (_fpHeld == null || !GodotObject.IsInstanceValid(_fpHeld) || !_fpHeld.IsInsideTree() || _cls.StartsWith("_melee")) return;
            bool pistol = _cls == "_pistol";
            float kick = _kick;
            var sightHip = new Vector3(HoldSight.X, HoldSight.Y, -(pistol ? HoldDepthPistol : HoldDepthRifle));
            Transform3D hip = GunAt(sightHip, _sightL, new Vector3(0f, 0f, -Converge) - sightHip);
            // Hip recoil: back and climbing about the eye.
            hip = new Transform3D(SwayBasis(Vector2.Zero, kick / KickMetres * KickPitchDeg), new Vector3(0f, 0f, kick)) * hip;
            // Aimed (was AlignToForearm.Aim): nudged up and back by the kick.
            Vector3 fwd = Vector3.Forward, up = Vector3.Up;
            Transform3D aim = GunAt(fwd * ((pistol ? SightDistancePistol : SightDistanceRifle) - kick) - up * 0.012f,
                _sightL, fwd + up * kick * 1.2f);
            Vector3 rear = hip * _sightL;
            Basis lower = new Basis(Vector3.Up, Mathf.DegToRad(LowerYawDeg)) * new Basis(Vector3.Right, -Mathf.DegToRad(LowerPitchDeg));
            Transform3D low = new Transform3D(lower, rear - lower * rear + up * LowerLift) * hip;
            Transform3D pose = hip.InterpolateWith(aim, Ease(_adsW)).InterpolateWith(low, Ease(_lowW));
            // The cant rises and falls over the whole reload (a frozen sheet frame: all of it).
            double now = Clock.Now, len = _reloadUntil - _reloadFrom;
            float tip = _reloadUntil == double.MaxValue ? ReloadTipDeg
                : now < _reloadUntil && len > 0 ? ReloadTipDeg * Mathf.Sin(Mathf.Pi * (float)((now - _reloadFrom) / len)) : 0f;
            if (tip > 0.01f)
            {
                Vector3 g = pose * _gripL, axis = (pose.Basis * (_frontL - _sightL)).Normalized();
                var cant = new Basis(axis, Mathf.DegToRad(tip));
                pose = new Transform3D(cant, g - cant * g) * pose;
            }
            pose = new Transform3D(SwayBasis(sway, 0f), bob + new Vector3(0f, 0f, Mathf.Lerp(_reachHip, _reachAim, Ease(_adsW)))) * pose;
            _fpHeld.GlobalTransform = _eye.GlobalTransform * pose;
            if (_fpIk != null) { _fpIk.WeightR = 1f; _fpIk.WeightL = Ease(_leftW); }
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
            // Eased: a bob dropped to nothing in the frame you stop was a jump (-uiMotion `stop`).
            _moving = Mathf.Lerp(_moving, Mathf.Clamp(speed / 4.5f, 0f, 1f), 1f - Mathf.Exp(-6f * dt));
            float moving = _moving;
            _bobPhase += dt * BobSpeed * moving;
            var bob = new Vector3(Mathf.Cos(_bobPhase), -Mathf.Abs(Mathf.Sin(_bobPhase)), 0f) * BobAmount * moving * scale;
            _kick = Mathf.Lerp(_kick, 0f, 1f - Mathf.Exp(-18f * dt));

            // Framing, empty hands only: the right hand to the lower right of
            // the view, the last centimetres the clip leaves (a shift over
            // ~5 cm means the clip is authored off the view: fix the clip,
            // never lift the arms to it; `FpAudit` prints it). A hand weapon
            // rides its clips unframed; a gun is placed by code (PoseWeapon)
            // and the hands go to it (HandIk), so neither needs a trim.
            Vector3 want = Vector3.Zero;
            Node3D hand = _fpModel != null ? AssetRegistry.FindNode(_fpModel, "hand.r") : null;
            if (!Armed && hand != null && IsInstanceValidNode(hand))
            {
                Vector3 un = _eye.GlobalTransform.AffineInverse() * hand.GlobalPosition - _ads;
                // The Vanguard's fists are chest-high in its clip (human.py
                // `fists`) and its big hands hang under the wrist: a higher
                // target keeps them in the frame, with the shoulders still under it.
                want = (BodyId.StartsWith("char.ubc") ? UnarmedHandVanguard : UnarmedHand) - un;
                want.Z = 0f;
            }
            _ads = _ads.Lerp(want, 1f - Mathf.Exp(-14f * dt));
            _adsW = Mathf.MoveToward(_adsW, ads ? 1f : 0f, dt / 0.18f);   // 0.18 s in or out
            _lowW = Mathf.MoveToward(_lowW, _lowered && Armed ? 1f : 0f, dt / 0.25f);
            bool reloading = Clock.Now < _reloadUntil - 0.2;   // back on the gun as the reload ends
            _leftW = Mathf.MoveToward(_leftW, reloading ? 0f : 1f, dt / (reloading ? 0.15f : 0.2f));
            // Last frame's reach (HandIk.Short) steers it: in when an arm is
            // short, back out (never past the pose) when there is slack. One
            // value per pose, blended as the pose is, so aiming in and out
            // carries the pull with it instead of chasing it a frame late.
            float shortBy = (_fpIk != null && _fpIk.Active ? _fpIk.Short + ReachSlack : -1f) * (1f - Mathf.Exp(-30f * dt));
            float a = Ease(_adsW);
            _reachHip = Mathf.Clamp(_reachHip + shortBy * (1f - a), 0f, 0.15f);
            _reachAim = Mathf.Clamp(_reachAim + shortBy * a, 0f, 0.15f);

            _eye.Fov = Mathf.Lerp(_eye.Fov, ads ? AdsFov : BaseFov, 1f - Mathf.Exp(-12f * dt));

            bool gun = Armed && !_cls.StartsWith("_melee");
            PoseFp(_sway, bob, gun ? 0f : _kick);   // a gun takes the kick itself
            if (gun) PoseWeapon(_sway, bob);
            _fpAnim?.Drive(speed, ads, _lowered, Clock.Now);

            // Same speed the arms bob on, so your legs and your gun cannot
            // disagree about whether you are moving.
            _bodyAnim?.Drive(speed, false);

            if (_fpMuzzle != null && IsInstanceValidNode(_fpMuzzle))
            {
                _muzzle.GlobalPosition = _fpMuzzle.GlobalPosition;
                _muzzle.GlobalBasis = _fpMuzzle.GlobalBasis.Orthonormalized();
            }
            PlaceOrbit();
        }

        /// <summary>
        /// The rig's `-uiFpOrbit`: (yaw, pitch in degrees, distance in m) of a
        /// camera circling the held weapon's grip (the right hand mount when
        /// nothing is held), in the view's frame -- yaw 0 from behind the
        /// eye, 90 from the right, 180 from the front; pitch above 0 from
        /// above, -90 from below. It draws only the first-person layer
        /// (arms and gun) and becomes the current camera, so a shot shows
        /// how the hands close on the weapon. Null: off.
        /// </summary>
        public Vector3? Orbit;
        public bool OrbitFore;   // circle the weapon's `fore` (the support hand) instead of its grip
        private Camera3D _orbitCam;

        private void PlaceOrbit()
        {
            if (Orbit is not Vector3 o) return;
            if (_orbitCam == null)
            {
                _orbitCam = new Camera3D { Name = "FpOrbit", Fov = 40f, Near = 0.01f, CullMask = _layers };
                _eye.AddChild(_orbitCam);
                // Its own rig-layer light, above and right of it as Boot's is of
                // the eye: seen from the side, the eye's light leaves it black.
                _orbitCam.AddChild(new OmniLight3D
                {
                    // Layers too: a camera's cull mask culls lights by their own layers.
                    Name = "FpOrbitLight", LightEnergy = 2.5f, OmniRange = 3f, LightCullMask = _layers, Layers = _layers, ShadowEnabled = false,
                    Position = new Vector3(0.35f, 0.25f, 0.1f),
                });
            }
            Node3D fore = OrbitFore && _fpHeld != null && IsInstanceValidNode(_fpHeld) ? AssetRegistry.FindNode(_fpHeld, "fore") : null;
            Node3D pivotN = fore != null ? fore : _fpGrip != null && IsInstanceValidNode(_fpGrip) ? _fpGrip
                : _fpModel != null ? AssetRegistry.FindNode(_fpModel, "hand.r") : null;
            Basis view = _eye.GlobalBasis.Orthonormalized();
            Vector3 pivot = pivotN != null ? pivotN.GlobalPosition : _eye.GlobalPosition - view.Z * 0.3f;
            Vector3 off = view * (new Basis(Vector3.Up, Mathf.DegToRad(o.X)) * new Basis(Vector3.Right, -Mathf.DegToRad(o.Y)) * new Vector3(0f, 0f, o.Z));
            Vector3 up = Mathf.Abs(o.Y) > 80f ? -view.Z : view.Y;   // straight above or below: the view's forward is up
            _orbitCam.LookAtFromPosition(pivot + off, pivot, up);
            _orbitCam.Current = true;
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
            CharacterAnim.Mix(_player);
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

        public void Swing(string name, double now)
        {
            string clip = CharacterAnim.Clip(_player, name, Class);
            if (!Armed || !_player.HasAnimation(clip)) return;
            _player.Play(clip, 0.04);
            _player.Seek(0, true);
            _oneShotUntil = now + _player.GetAnimation(clip).Length;
            _current = clip;
        }

        /// <summary>
        /// Every first-person clip this hold can show, with the moments worth
        /// a look (fractions of the clip): the rig's `-uiFpSheet`.
        /// </summary>
        public IEnumerable<(string clip, float at)> Sheet()
        {
            if (!Armed) { foreach (float t in new[] { 0f, 0.25f }) yield return ("fp_unarmed", t); yield break; }
            var frames = new (string name, float[] at)[]
            {
                ("fp_idle", new[] { 0f }), ("fp_walk", new[] { 0.25f, 0.75f }), ("fp_sprint", new[] { 0.25f, 0.75f }),
                ("fp_ads", new[] { 0f }), ("fp_lower", new[] { 0f }), ("fp_fire", new[] { 0.4f }),
                ("fp_reload", new[] { 0.3f, 0.5f, 0.7f }),
                ("fp_attack", new[] { 0.2f, 0.4f, 0.6f, 0.8f }), ("fp_attack_spin", new[] { 0.2f, 0.4f, 0.6f, 0.8f }),
            };
            foreach (var (name, at) in frames)
                // A one-shot the class has no clip of its own for (a blade's
                // fire, a gun's swing) is never played: skip it.
                if (_player.HasAnimation(name + Class) || !CharacterAnim.OneShot(name) && _player.HasAnimation(name))
                    foreach (float t in at) yield return (CharacterAnim.Clip(_player, name, Class), t);
        }

        /// <summary>Freeze the arms on one moment of one clip (the rig's sheet).</summary>
        public void Pose(string clip, float at)
        {
            _player.Play(clip, 0);
            _player.Seek(at * _player.GetAnimation(clip).Length, true);
            _player.Pause();
            _oneShotUntil = double.MaxValue;
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
