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
    }

    internal class Manifest
    {
        public int version { get; set; }
        public ManifestAsset[] assets { get; set; }
    }

    /// <summary>
    /// Loads art/*.glb by manifest id and instantiates them. One parse per id
    /// however many entities want it: a GltfState generates any number of
    /// scenes.
    /// </summary>
    public sealed class AssetRegistry
    {
        private readonly Dictionary<string, string> _paths = new Dictionary<string, string>();
        private readonly Dictionary<string, GltfState> _loaded = new Dictionary<string, GltfState>();
        private readonly Material _material;
        private readonly string _root;

        public AssetRegistry(Material material)
        {
            _material = material;
            _root = ResolveArtRoot();
            LoadManifest();
        }

        /// <summary>
        /// Where art/ is: -artDir, then SA_ART_DIR, then the repo's art/ when
        /// running from source, then the art/ staged beside the executable.
        /// </summary>
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
                var flip = new Node3D { Name = "flip", Basis = Frame.ModelFlip };
                Node scene = Generate(state);
                if (scene == null) { holder.QueueFree(); return; }
                flip.AddChild(scene);
                holder.AddChild(flip);
                parent.AddChild(holder);
                ApplyMaterials(holder);
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
        private void ApplyMaterials(Node root)
        {
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

        /// <summary>
        /// Drops a loaded model into the space its fallback box mesh already
        /// occupies, and onto its render layers.
        ///
        /// The rig around a model is tuned to the model. The first-person
        /// rifle is the case that forced it: the box rifle points +Z with its
        /// origin at the grip, and the hands, the muzzle marker and the rest
        /// pose are all measured against that. The imported one points −Z with
        /// a centred origin, because that is what art/README.md asks every
        /// weapon for. So the model is fitted to the BOUNDS of the box it
        /// replaces: same volume, same centre. `yaw` (radians) is the one thing
        /// a bounding box cannot recover — a box does not know which end is
        /// the barrel. Scale is uniform, from the volume ratio.
        /// </summary>
        public void AttachFitted(string assetId, Node3D parent, ArrayMesh fallback,
                                 float yaw, uint layers, Action<Node3D> onAttached)
        {
            Attach(assetId, parent, holder =>
            {
                SetLayers(holder, layers);
                holder.Basis = new Basis(Vector3.Up, yaw);

                if (!LocalBounds(holder, parent, out Aabb loaded) || loaded.Size.LengthSquared() < 1e-12f)
                {
                    onAttached?.Invoke(holder);
                    return;
                }

                // Same VOLUME, uniform scale: a model whose proportions differ
                // from the box (the Kenney rifle is five times as tall) lands
                // at a believable size rather than stretched to one axis.
                Aabb target = fallback.GetAabb();
                float from = loaded.Volume;
                float to = target.Volume;
                Vector3 loadedCentre = loaded.GetCenter();
                if (from > 1e-9f && to > 1e-9f)
                {
                    float k = Mathf.Pow(to / from, 1f / 3f);
                    // A fitted model is a REPLACEMENT for its fallback, so k
                    // should land near 1. A wild ratio means the bounds were
                    // measured wrong; refuse it rather than fill the screen.
                    if (k < 0.2f || k > 5f)
                    {
                        GD.PushWarning($"asset {assetId}: fit ratio {k:F2} is out of range " +
                                       $"(model {loaded.Size} vs fallback {target.Size}); leaving it unscaled");
                    }
                    else
                    {
                        holder.Scale = new Vector3(k, k, k);
                        loadedCentre *= k;
                    }
                }
                holder.Position += target.GetCenter() - loadedCentre;
                GD.Print($"fit {assetId}: {loaded.Size} into {target.Size}, scale {holder.Scale.X:F2}");
                onAttached?.Invoke(holder);
            });
        }

        /// <summary>Bounds of every mesh under `node`, in `space`'s local frame.</summary>
        private static bool LocalBounds(Node3D node, Node3D space, out Aabb bounds)
        {
            bounds = default;
            bool any = false;
            Transform3D toSpace = space.GlobalTransform.AffineInverse();
            foreach (MeshInstance3D mi in Descendants<MeshInstance3D>(node))
            {
                if (mi.Mesh == null) continue;
                Aabb local = mi.GetAabb();
                Transform3D t = toSpace * mi.GlobalTransform;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = t * local.GetEndpoint(i);
                    if (!any) { bounds = new Aabb(p, Vector3.Zero); any = true; }
                    else bounds = bounds.Expand(p);
                }
            }
            return any;
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
                    Name = imi.Name,
                    Transform = imi.Transform,
                    Mesh = imi.Mesh?.GetMesh(),
                    Skin = imi.Skin,
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
            if (manifest?.assets == null) return;
            foreach (ManifestAsset a in manifest.assets)
            {
                if (string.IsNullOrEmpty(a.id) || string.IsNullOrEmpty(a.file)) continue;
                _paths[a.id] = Path.Combine(_root, a.file);
            }
            GD.Print($"art manifest: {_paths.Count} assets");
        }
    }
}
