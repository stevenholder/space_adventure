// The asset pipeline: glTF models, loaded at runtime, keyed by the id the
// SERVER already sends.
//
// Every entity def and NPC archetype in server/data carries an `asset` field
// -- "char.player", "npc.grunt", "weapon.pulse" -- and those strings are
// exactly the ids in art/manifest.json. The key was already crossing the wire;
// this file is what reads it. There is no second registry to keep in step with
// the server, because there is no second registry.
//
// WHY RUNTIME LOADING AND NOT AN EDITOR IMPORT. client/CONVENTIONS.md rule 1
// keeps models out of the project: a .glb under res:// would be imported as a
// scene with a sidecar per file, and `make godot-gate` refuses it. So the .glb
// files stay in art/, this reads them straight from there (a dev run) or from
// the art/ directory staged beside the executable (an export), and everything
// below is code. GltfDocument is synchronous and a 400-triangle model parses
// in a millisecond or two, so `Attach` calls back before it returns; the
// callback shape is kept so a caller never has to know.
//
// ONE MATERIAL FOR EVERY OPAQUE SURFACE. Colour travels in the vertex stream
// (art/tools/import_pack.mjs bakes glTF material colours into COLOR_0), and
// every opaque surface is overridden with the project's one vertex-colour
// material so a model and the terrain it stands on shade identically. The
// importer's own material is kept only for alphaMode BLEND (the ship's
// canopy), adjusted to draw both sides unlit.
//
// MODEL FLIP. glTF faces −Z; the Sim faces +Z. Every model is parented under a
// node carrying Frame.ModelFlip, here and nowhere else.
//
// NODE NAMES. Godot's node-name validator rewrites `.` (and `:@/"%`), so the
// contract names in art/README.md -- `hand.r`, `seat.pilot`, `arm.l` -- are not
// findable verbatim. FindNode compares a normalised key (alphanumerics only,
// case-folded), which survives whatever the importer chose.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using Newtonsoft.Json;

namespace SpaceAdventure.Game
{
    /// <summary>One row of art/manifest.json.</summary>
    internal class ManifestAsset
    {
        public string id { get; set; }
        public string file { get; set; }
        public string rig { get; set; }
        /// <summary>Surface names per mesh, in surface order (import_pack records them before the bake).</summary>
        public Dictionary<string, string[]> surfaces { get; set; }
        /// <summary>"mesh/surface" entries of the WEARER this piece covers; the client hides them.</summary>
        public string[] covers { get; set; }
        /// <summary>Real materials (roughness, metallic): drawn as imported, not with the shared vertex-colour one.</summary>
        public bool pbr { get; set; }
        /// <summary>A worn piece with parts on the arms: the first-person arms wear it too.</summary>
        public bool arms { get; set; }
    }

    internal class Manifest
    {
        public int version { get; set; }
        public ManifestAsset[] assets { get; set; }
        public ManifestPalettes palettes { get; set; }
    }

    /// <summary>One palette row: a skin tone (`tone`, `factor`) or a suit colour (`color`).</summary>
    internal class ManifestSwatch
    {
        public string id { get; set; }
        public string name { get; set; }
        public string tone { get; set; }
        public string factor { get; set; }
        public string color { get; set; }
    }

    internal class ManifestPalettes
    {
        public ManifestSwatch[] skin { get; set; }
        public ManifestSwatch[] suit { get; set; }
    }

    /// <summary>A palette entry: `Tone` is what the swatch shows, `Albedo` what the body's surface gets.</summary>
    public sealed class Swatch
    {
        public string Id, Name;
        /// <summary>The swatch colour (a skin's tone, a suit's colour).</summary>
        public Color Tone;
        /// <summary>skin: the factor the baked albedo is multiplied by; suit: the colour the surface is set to.</summary>
        public Color Albedo;
    }

    /// <summary>
    /// Phase 21 (GDD "Skin and suit colours"): the manifest's `palettes`.
    /// A `worn` frame in slot `skin` or `suit` carries one of these ids --
    /// not an item, not an asset -- and the body's `skin`/`suit` surfaces
    /// are tinted with it. Filled by the AssetRegistry's manifest load.
    /// </summary>
    public static class Palette
    {
        public const string DefaultSkin = "skin.01", DefaultSuit = "suit.slate";
        public static readonly List<Swatch> Skin = new List<Swatch>();
        public static readonly List<Swatch> Suit = new List<Swatch>();
        private static readonly HashSet<string> Warned = new HashSet<string>();

        /// <summary>The two worn slots that tint rather than hang a piece.</summary>
        public static bool IsTintSlot(string slot) => slot == "skin" || slot == "suit";

        /// <summary>A palette id is its own "asset" on the wire (hair's precedent).</summary>
        public static bool IsPaletteId(string id) =>
            id != null && (id.StartsWith("skin.", StringComparison.Ordinal) || id.StartsWith("suit.", StringComparison.Ordinal));

        public static List<Swatch> For(string slot) => slot == "skin" ? Skin : slot == "suit" ? Suit : null;

        /// <summary>The entry, or null (an unknown id warns once).</summary>
        public static Swatch Find(string slot, string id)
        {
            List<Swatch> list = For(slot);
            if (list == null || string.IsNullOrEmpty(id)) return null;
            foreach (Swatch s in list) if (s.Id == id) return s;
            if (Warned.Add(slot + "=" + id)) GD.PushWarning($"palette: unknown {slot} id {id}, left as baked");
            return null;
        }

        /// <summary>The entry's index in its palette, 0 for an unknown id (it cycles from the default).</summary>
        public static int IndexOf(string slot, string id)
        {
            List<Swatch> list = For(slot);
            if (list != null) for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
            return 0;
        }

        internal static void Load(ManifestPalettes p)
        {
            Skin.Clear();
            Suit.Clear();
            if (p == null) return;
            foreach (ManifestSwatch m in p.skin ?? Array.Empty<ManifestSwatch>())
                if (!string.IsNullOrEmpty(m.id) && Color.HtmlIsValid(m.tone ?? "") && Color.HtmlIsValid(m.factor ?? ""))
                    Skin.Add(new Swatch { Id = m.id, Name = m.name ?? m.id, Tone = Color.FromHtml(m.tone), Albedo = Color.FromHtml(m.factor) });
            foreach (ManifestSwatch m in p.suit ?? Array.Empty<ManifestSwatch>())
                if (!string.IsNullOrEmpty(m.id) && Color.HtmlIsValid(m.color ?? ""))
                    Suit.Add(new Swatch { Id = m.id, Name = m.name ?? m.id, Tone = Color.FromHtml(m.color), Albedo = Color.FromHtml(m.color) });
        }
    }

    /// <summary>
    /// Loads art/*.glb by manifest id and instantiates them. One parse per id
    /// however many entities want it: a GltfState generates any number of
    /// scenes.
    /// </summary>
    public sealed class AssetRegistry
    {
        private readonly Dictionary<string, string> _paths = new Dictionary<string, string>();
        private readonly Dictionary<string, ManifestAsset> _rows = new Dictionary<string, ManifestAsset>();

        /// <summary>The surface names of one of an asset's meshes, in surface order, or null.</summary>
        public string[] Surfaces(string assetId, string mesh) =>
            _rows.TryGetValue(assetId ?? "", out var r) && r.surfaces != null && r.surfaces.TryGetValue(mesh, out var names) ? names : null;

        /// <summary>The wearer surfaces a piece covers ("mesh/surface"), or an empty list.</summary>
        public bool IsPbr(string assetId) => _rows.TryGetValue(assetId ?? "", out var r) && r.pbr;
        public bool OnArms(string assetId) => _rows.TryGetValue(assetId ?? "", out var r) && r.arms;

        public string[] Covers(string assetId) =>
            _rows.TryGetValue(assetId ?? "", out var r) && r.covers != null ? r.covers : System.Array.Empty<string>();
        private readonly Dictionary<string, GltfState> _loaded = new Dictionary<string, GltfState>();
        private readonly Material _material;
        private readonly string _root;

        public AssetRegistry(Material material)
        {
            _material = material;
            _root = ResolveArtRoot();
            Root = _root;
            LoadManifest();
        }

        /// <summary>
        /// Where art/ is: -artDir, then SA_ART_DIR, then the repo's art/ when
        /// running from source, then the art/ staged beside the executable.
        /// </summary>
        /// <summary>The art/ directory in use (icons live beside the models).</summary>
        public string Root { get; private set; }

        private static string ResolveArtRoot()
        {
            string[] argv = OS.GetCmdlineUserArgs();
            for (int i = 0; i < argv.Length - 1; i++)
                if (argv[i] == "-artDir") return argv[i + 1];
            string env = System.Environment.GetEnvironmentVariable("SA_ART_DIR");
            if (!string.IsNullOrEmpty(env)) return env;
            if (OS.HasFeature("editor"))
                return Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "art"));
            return Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "art");
        }

        /// <summary>
        /// Hangs a skinned piece (armor) on a body: the piece keeps its own
        /// Skeleton3D (the same armature the body was built on) and a
        /// BoneMirror copies the wearer's bone poses into it every frame, so
        /// it follows every clip the wearer plays. Returns the holder to free
        /// when the piece comes off, or null when there is nothing to hang.
        /// </summary>
        public Node3D AttachSkinned(string assetId, Skeleton3D wearer)
        {
            if (!Has(assetId) || wearer == null) return null;
            try
            {
                GltfState state = LoadOnce(assetId);
                if (state == null) return null;
                Node scene = Generate(state);
                if (scene == null) return null;
                Skeleton3D own = null;
                foreach (Skeleton3D sk in Descendants<Skeleton3D>(scene)) { own = sk; break; }
                if (own == null || own.GetBoneCount() != wearer.GetBoneCount())
                {
                    scene.QueueFree();
                    GD.PushWarning($"asset {assetId}: no skeleton, or not the wearer's ({own?.GetBoneCount()} vs {wearer.GetBoneCount()} bones)");
                    return null;
                }
                var holder = new Node3D { Name = assetId.Replace('.', '_') };
                holder.AddChild(scene);
                holder.AddChild(new BoneMirror { Source = wearer, Target = own });
                // Beside the wearer's skeleton, under the same fit/flip parents.
                wearer.GetParent().AddChild(holder);
                ApplyMaterials(holder, IsPbr(assetId));
                return holder;
            }
            catch (Exception e)
            {
                GD.PushWarning($"asset {assetId} failed to wear: {e.Message}");
                return null;
            }
        }

        /// <summary>True when an id has a .glb behind it at all.</summary>
        public bool Has(string assetId) =>
            !string.IsNullOrEmpty(assetId) && _paths.ContainsKey(assetId);

        /// <summary>
        /// Parents a fresh instance of <paramref name="assetId"/> under
        /// <paramref name="parent"/> and calls <paramref name="onAttached"/>
        /// with the holder node. Silently does nothing when the id is unknown,
        /// the file is missing or the load fails — the caller's fallback model
        /// simply stays up.
        /// </summary>
        public void Attach(string assetId, Node3D parent, Action<Node3D> onAttached)
        {
            if (!Has(assetId) || parent == null) return;
            Node3D holder = null;
            try
            {
                GltfState state = LoadOnce(assetId);
                if (state == null) return;

                holder = new Node3D { Name = assetId.Replace('.', '_') };
                holder.SetMeta("asset", assetId); // so a dresser can look the wearer's surfaces up
                var flip = new Node3D { Name = "flip", Basis = Frame.ModelFlip };
                Node scene = Generate(state);
                if (scene == null) { holder.QueueFree(); return; }
                flip.AddChild(scene);
                holder.AddChild(flip);
                parent.AddChild(holder);
                ApplyMaterials(holder, IsPbr(assetId));
                onAttached?.Invoke(holder);
            }
            catch (Exception e)
            {
                holder?.QueueFree();
                GD.PushWarning($"asset {assetId} failed to load: {e.Message}");
            }
        }

        /// <summary>
        /// Every opaque surface gets the shared vertex-colour material; a
        /// BLEND surface keeps the importer's, drawn both-sided and unlit
        /// (the canopy glass — a lit, culled pane read as a black slab).
        /// </summary>
        private void ApplyMaterials(Node root, bool pbr = false)
        {
            if (pbr) return;   // imported materials as they are (art recipe `pbr`)
            foreach (MeshInstance3D mi in Descendants<MeshInstance3D>(root))
            {
                if (mi.Mesh == null) continue;
                for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                {
                    if (mi.GetActiveMaterial(i) is BaseMaterial3D m &&
                        m.Transparency == BaseMaterial3D.TransparencyEnum.Alpha)
                    {
                        m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                        m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                        continue;
                    }
                    mi.SetSurfaceOverrideMaterial(i, _material);
                }
            }
        }

        /// <summary>True when a manifest surface name is `surface`. Every body names its skin `skin` (the Vanguard's build renames the pack's material).</summary>
        public static bool SurfaceIs(string name, string surface) => name == surface;

        /// <summary>
        /// Phase 21: a `skin` / `suit` worn frame on a body. `id` "" puts the
        /// body back as baked; an unknown id is a no-op (one warning).
        /// Returns the number of surfaces touched.
        /// </summary>
        public int TintSlot(Node3D model, string slot, string id)
        {
            if (!Palette.IsTintSlot(slot)) return 0;
            if (string.IsNullOrEmpty(id)) return Tint(model, slot, null, false);
            Swatch s = Palette.Find(slot, id);
            if (s == null) return 0;
            // The defaults ARE the bodies as baked (C178): nothing to tint.
            if (id == (slot == "skin" ? Palette.DefaultSkin : Palette.DefaultSuit)) return Tint(model, slot, null, false);
            return Tint(model, slot, s.Albedo, multiply: slot == "skin");
        }

        /// <summary>
        /// Tints every surface of the BODY whose manifest name is `surface`
        /// (the `surfaces` table: mesh name → surface names in surface order,
        /// the same lookup Cover uses to hide what armor covers; the pieces
        /// hung under the model have their own mesh names and never match).
        /// `albedo` multiplies the untinted material's albedo (`multiply`,
        /// a skin factor) or replaces it (a suit colour); null restores the
        /// untinted material. The untinted one is remembered per surface
        /// (`sa_tint_src_i`) the first time, so re-tinting never compounds;
        /// the tinted one is a COPY (cached per source and colour), never the
        /// source changed in place: materials are shared between instances.
        /// A StandardMaterial3D gets `AlbedoColor`; a ShaderMaterial (the
        /// first-person and near-cut twins ViewModel made at attach) gets its
        /// `albedo` uniform. A surface Cover has hidden stays hidden: its
        /// remembered base (`sa_base_i`) is what is tinted.
        /// </summary>
        public int Tint(Node3D model, string surface, Color? albedo, bool multiply)
        {
            if (model == null) return 0;
            string wearer = model.HasMeta("asset") ? (string)model.GetMeta("asset") : null;
            if (wearer == null) return 0;
            int touched = 0;
            foreach (MeshInstance3D mi in Descendants<MeshInstance3D>(model))
            {
                string[] names = Surfaces(wearer, mi.Name);
                if (names == null || mi.Mesh == null) continue;
                for (int i = 0; i < mi.Mesh.GetSurfaceCount() && i < names.Length; i++)
                {
                    if (!SurfaceIs(names[i], surface)) continue;
                    string srcKey = "sa_tint_src_" + i, baseKey = "sa_base_" + i;
                    if (!mi.HasMeta(srcKey))
                    {
                        Material untinted = mi.HasMeta(baseKey) ? MetaMaterial(mi, baseKey)
                            : mi.GetSurfaceOverrideMaterial(i) ?? mi.Mesh.SurfaceGetMaterial(i);
                        mi.SetMeta(srcKey, untinted != null ? (Variant)untinted : false);
                    }
                    Material src = MetaMaterial(mi, srcKey);
                    if (src == null) continue;
                    Material want = albedo is Color c ? Tinted(src, c, multiply) : src;
                    if (want == null) continue;
                    if (mi.HasMeta(baseKey))
                    {
                        // Cover's remembered base: hidden (the override is not
                        // the base) stays hidden and uncovers to the tint.
                        bool hidden = mi.GetSurfaceOverrideMaterial(i) != MetaMaterial(mi, baseKey);
                        mi.SetMeta(baseKey, want);
                        if (!hidden) mi.SetSurfaceOverrideMaterial(i, want);
                    }
                    else mi.SetSurfaceOverrideMaterial(i, want);
                    touched++;
                }
            }
            return touched;
        }

        private static Material MetaMaterial(GodotObject o, string key)
        {
            Variant v = o.GetMeta(key);
            return v.VariantType == Variant.Type.Object ? v.As<Material>() : null;
        }

        private static readonly Dictionary<(Material, Color, bool), Material> TintCopies = new Dictionary<(Material, Color, bool), Material>();

        /// <summary>A tinted copy of `src`, cached; null when `src` has no albedo to tint.</summary>
        public static Material Tinted(Material src, Color albedo, bool multiply)
        {
            if (TintCopies.TryGetValue((src, albedo, multiply), out Material m)) return m;
            // skin: the bake times the factor. suit: the colour itself -- the
            // suit's albedo is a texture (fabric, seams) under a white factor,
            // so the factor is the colour over the texture's mean (in linear,
            // where the shader multiplies): the surface averages to the
            // colour and keeps its detail.
            Color Mix(Color was, Texture2D tex) => multiply
                ? new Color(was.R * albedo.R, was.G * albedo.G, was.B * albedo.B, was.A)
                : SetTo(albedo, tex, was.A);
            switch (src)
            {
                case BaseMaterial3D bm:
                {
                    var dup = (BaseMaterial3D)bm.Duplicate();
                    dup.AlbedoColor = Mix(bm.AlbedoColor, bm.AlbedoTexture);
                    if (!multiply) dup.VertexColorUseAsAlbedo = false;   // the colour, not the bake's
                    m = dup;
                    break;
                }
                case ShaderMaterial sm when sm.Shader != null && HasUniform(sm.Shader, "albedo"):
                {
                    var dup = (ShaderMaterial)sm.Duplicate();
                    Variant v = sm.GetShaderParameter("albedo");
                    Color was = v.VariantType == Variant.Type.Color ? v.AsColor() : Colors.White;
                    Variant t = sm.GetShaderParameter("albedo_tex");
                    dup.SetShaderParameter("albedo", Mix(was, t.VariantType == Variant.Type.Object ? t.As<Texture2D>() : null));
                    m = dup;
                    break;
                }
                default:
                    return null;   // the vertex-colour shaders carry no albedo: nothing to tint
            }
            TintCopies[(src, albedo, multiply)] = m;
            return m;
        }

        /// <summary>The albedo factor that makes `tex` average to `color` (sRGB in, sRGB out).</summary>
        private static Color SetTo(Color color, Texture2D tex, float alpha)
        {
            Color? mean = MeanLinear(tex);
            if (mean is not Color mu) return new Color(color.R, color.G, color.B, alpha);
            Color lin = color.SrgbToLinear();
            var f = new Color(lin.R / Mathf.Max(mu.R, 1e-3f), lin.G / Mathf.Max(mu.G, 1e-3f), lin.B / Mathf.Max(mu.B, 1e-3f)).LinearToSrgb();
            return new Color(f.R, f.G, f.B, alpha);
        }

        private static readonly Dictionary<Texture2D, Color?> Means = new Dictionary<Texture2D, Color?>();

        /// <summary>
        /// A texture's mean colour in linear space, or null (no texture, or
        /// no image to read -- the headless renderer keeps none). Read once
        /// per texture, from a 32×32 reduction.
        /// </summary>
        public static Color? MeanLinear(Texture2D tex)
        {
            if (tex == null) return null;
            if (Means.TryGetValue(tex, out Color? known)) return known;
            Color? mean = null;
            try
            {
                Image img = tex.GetImage();
                if (img != null && !img.IsEmpty())
                {
                    img = (Image)img.Duplicate();
                    if (img.IsCompressed()) img.Decompress();
                    img.Resize(32, 32, Image.Interpolation.Bilinear);
                    float r = 0, g = 0, b = 0;
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            Color c = img.GetPixel(x, y).SrgbToLinear();
                            r += c.R; g += c.G; b += c.B;
                        }
                    mean = new Color(r / 1024f, g / 1024f, b / 1024f);
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"tint: texture unreadable ({e.Message}), suit colour set flat");
            }
            Means[tex] = mean;
            return mean;
        }

        private static bool HasUniform(Shader shader, string name)
        {
            foreach (Godot.Collections.Dictionary u in shader.GetShaderUniformList())
                if ((string)u["name"] == name) return true;
            return false;
        }

        /// <summary>Every surface under `root` draws with `material` (first-person set).</summary>
        public static void OverrideMaterial(Node root, Material material)
        {
            foreach (MeshInstance3D mi in Descendants<MeshInstance3D>(root))
                if (mi.Mesh != null)
                    for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                        mi.SetSurfaceOverrideMaterial(i, material);
        }

        public static void SetLayers(Node root, uint layers)
        {
            foreach (VisualInstance3D v in Descendants<VisualInstance3D>(root)) v.Layers = layers;
        }

        /// <summary>
        /// Hands back the loaded model's first mesh, for callers that draw
        /// geometry themselves — the rock scatter instances one mesh four
        /// hundred times and wants no nodes at all. First, not merged: a
        /// multi-mesh model asked for this way logs rather than picking quietly.
        /// </summary>
        public void Mesh(string assetId, Action<Mesh> onReady)
        {
            if (!Has(assetId)) return;
            try
            {
                GltfState state = LoadOnce(assetId);
                if (state == null) return;
                Node scene = Generate(state);
                var meshes = new List<Mesh>();
                foreach (MeshInstance3D mi in Descendants<MeshInstance3D>(scene))
                    if (mi.Mesh != null) meshes.Add(mi.Mesh);
                scene.Free();
                if (meshes.Count == 0)
                {
                    GD.PushWarning($"asset {assetId} has no mesh to instance");
                    return;
                }
                if (meshes.Count > 1)
                    GD.PushWarning($"asset {assetId} has {meshes.Count} meshes; instancing only the first");
                onReady?.Invoke(meshes[0]);
            }
            catch (Exception e)
            {
                GD.PushWarning($"asset {assetId} failed to load: {e.Message}");
            }
        }

        /// <summary>
        /// Finds a contract node (`hand.r`, `grip`, `seat.pilot`) under `root`
        /// whatever the importer did to its punctuation.
        /// </summary>
        public static Node3D FindNode(Node root, string name)
        {
            string want = Key(name);
            foreach (Node3D n in Descendants<Node3D>(root))
                if (Key(n.Name) == want) return n;
            return null;
        }

        private static string Key(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static IEnumerable<T> Descendants<T>(Node root) where T : Node
        {
            if (root == null) yield break;
            if (root is T t) yield return t;
            foreach (Node child in root.GetChildren())
                foreach (T d in Descendants<T>(child)) yield return d;
        }

        /// <summary>
        /// `-dumpNodes <id>`: the node tree and the clip list of one asset,
        /// so the name-sanitisation rule and the animation import are pinned
        /// by a log line rather than guessed.
        /// </summary>
        public string Describe(string assetId)
        {
            GltfState state = Has(assetId) ? LoadOnce(assetId) : null;
            if (state == null) return $"{assetId}: not loadable";
            Node scene = Generate(state);
            var sb = new StringBuilder();
            void Walk(Node n, int depth)
            {
                sb.Append(' ', depth * 2).Append(n.Name).Append(" (").Append(n.GetType().Name).Append(")\n");
                if (n is AnimationPlayer ap)
                    foreach (string a in ap.GetAnimationList())
                        sb.Append(' ', depth * 2 + 2).Append("clip ").Append(a).Append(' ')
                          .Append(ap.GetAnimation(a).Length.ToString("F2")).Append("s\n");
                foreach (Node c in n.GetChildren()) Walk(c, depth + 1);
            }
            Walk(scene, 0);
            scene.Free();
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// A fresh scene from a parsed state. GenerateScene hands back
        /// ImporterMeshInstance3D nodes — the import pipeline's placeholder,
        /// which draws nothing — so each is swapped for a MeshInstance3D with
        /// the same name at the same spot in the tree, which keeps every
        /// AnimationPlayer track path valid.
        /// </summary>
        private static Node Generate(GltfState state)
        {
            Node scene = new GltfDocument().GenerateScene(state);
            if (scene == null) return null;
            var importers = new List<ImporterMeshInstance3D>(Descendants<ImporterMeshInstance3D>(scene));
            foreach (ImporterMeshInstance3D imi in importers)
            {
                var mi = new MeshInstance3D
                {
                    // No automatic LOD: Godot simplifies imported meshes and
                    // swaps the simplified one in when an object is small on
                    // screen. On a model made of separate boxes (the rifle) that
                    // fused them into long stray triangles. Our budgets are set
                    // per asset, so LOD0 is always the one to draw.
                    LodBias = 1000f,
                    Name = imi.Name,
                    Transform = imi.Transform,
                    Mesh = imi.Mesh?.GetMesh(),
                    Skin = imi.Skin,
                    Skeleton = imi.SkeletonPath, // a skinned mesh (char.player) deforms on its Skeleton3D
                    Visible = imi.Visible,
                };
                Node parent = imi.GetParent();
                int at = imi.GetIndex();
                // Children (a hand node under an arm mesh) move across intact.
                foreach (Node child in imi.GetChildren())
                {
                    child.Owner = null; // the importer's owner bookkeeping means nothing at runtime
                    imi.RemoveChild(child);
                    mi.AddChild(child);
                }
                parent.RemoveChild(imi);
                parent.AddChild(mi);
                parent.MoveChild(mi, at);
                imi.Free();
            }
            return scene;
        }

        private GltfState LoadOnce(string assetId)
        {
            if (_loaded.TryGetValue(assetId, out GltfState existing)) return existing;
            GltfState state = LoadFile(_paths[assetId]);
            _loaded[assetId] = state; // null is cached too: one warning, not one per spawn
            return state;
        }

        private static GltfState LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                GD.PushWarning($"asset file missing: {path}");
                return null;
            }
            var state = new GltfState();
            Error err = new GltfDocument().AppendFromFile(path, state);
            if (err != Error.Ok)
            {
                GD.PushWarning($"asset failed to parse ({err}): {path}");
                return null;
            }
            return state;
        }

        private void LoadManifest()
        {
            string file = Path.Combine(_root, "manifest.json");
            if (!File.Exists(file))
            {
                GD.PushWarning($"no art manifest at {file} — every entity falls back to its box model.");
                return;
            }
            Manifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                GD.PushWarning($"art manifest unreadable: {e.Message}");
                return;
            }
            Palette.Load(manifest?.palettes);
            if (manifest?.assets == null) return;
            foreach (ManifestAsset a in manifest.assets)
            {
                if (string.IsNullOrEmpty(a.id) || string.IsNullOrEmpty(a.file)) continue;
                _paths[a.id] = Path.Combine(_root, a.file);
                _rows[a.id] = a;
            }
            GD.Print($"art manifest: {_paths.Count} assets, {Palette.Skin.Count} skin tones, {Palette.Suit.Count} suit colours");
        }
    }

    /// <summary>
    /// Copies bone poses from one Skeleton3D to another of the same layout,
    /// every frame: how a worn piece follows the body it is on.
    /// </summary>
    public partial class BoneMirror : Node
    {
        public Skeleton3D Source;
        public Skeleton3D Target;

        public override void _Process(double delta)
        {
            if (Source == null || Target == null || !IsInstanceValid(Source)) return;
            int n = Math.Min(Source.GetBoneCount(), Target.GetBoneCount());
            for (int i = 0; i < n; i++)
            {
                Target.SetBonePosePosition(i, Source.GetBonePosePosition(i));
                Target.SetBonePoseRotation(i, Source.GetBonePoseRotation(i));
                Target.SetBonePoseScale(i, Source.GetBonePoseScale(i));
            }
        }
    }
}
