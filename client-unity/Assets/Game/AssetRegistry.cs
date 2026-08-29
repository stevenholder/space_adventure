// The asset pipeline: glTF models, loaded at runtime, keyed by the id the
// SERVER already sends.
//
// Every entity def and NPC archetype in server/data carries an `asset` field
// -- "char.player", "npc.grunt", "weapon.pulse" -- and those strings are
// exactly the ids in art/manifest.json. The key was already crossing the wire;
// this file is what finally reads it. There is no second registry to keep in
// step with the server, because there is no second registry.
//
// WHY RUNTIME LOADING AND NOT AN EDITOR IMPORT. CONVENTIONS.md rule 1 forbids
// any agent authoring a .prefab or a .asset under Assets/, and `make
// unity-gate` enforces it. A model dragged into a scene, an AnimatorController,
// a material asset -- all of it is off the table. So the .glb files stay in
// art/, `make art-sync` copies them into StreamingAssets (which Unity packages
// verbatim, with no importer involved), and everything below is code.
//
// WHY ONE MATERIAL FOR EVERY MODEL. Boot builds a single material from
// SpaceAdventure/TerrainVertexColor and colour travels in the vertex stream.
// glTFast would otherwise generate its own materials from the glTF's PBR
// blocks, against shaders that this project references nowhere -- and Unity
// strips any shader no asset references, so they would come back MAGENTA in a
// packaged build and be perfectly fine in the Editor. `SingleMaterial` below
// hands glTFast our one material for everything and that whole failure mode
// stops existing. It also means the import pipeline must bake glTF material
// colours down into COLOR_0, which art/tools/import_pack.mjs does.
//
// Loading is async and spawning is not, so `Attach` returns immediately and
// parents the model when it arrives. The caller keeps its box model up in the
// meantime and drops it on the callback: a missing or slow asset is never an
// error, it is just boxes for a moment -- the same rule art/README.md set for
// the retired client.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;

namespace SpaceAdventure.Game
{
    /// <summary>
    /// Hands glTFast the project's one vertex-colour material for every glTF
    /// material, so no glTFast shader is ever referenced and none can be
    /// stripped out of the build. See the file header.
    /// </summary>
    internal sealed class SingleMaterial : IMaterialGenerator
    {
        private readonly Material _material;
        public SingleMaterial(Material material) { _material = material; }

        public Material GetDefaultMaterial(bool pointsSupport = false) => _material;

        public Material GenerateMaterial(
            GLTFast.Schema.MaterialBase gltfMaterial,
            IGltfReadable gltf,
            bool pointsSupport = false) => _material;

        public void SetLogger(ICodeLogger logger) { }
    }

    /// <summary>One row of art/manifest.json.</summary>
    [Serializable]
    internal class ManifestAsset
    {
        public string id;
        public string file;
        public string rig;
    }

    [Serializable]
    internal class Manifest
    {
        public int version;
        public ManifestAsset[] assets;
    }

    /// <summary>
    /// Loads art/*.glb by manifest id and instantiates them. One load per id
    /// however many entities want it: glTFast can instantiate the same
    /// <see cref="GltfImport"/> any number of times.
    /// </summary>
    public sealed class AssetRegistry
    {
        private readonly Dictionary<string, string> _paths = new Dictionary<string, string>();
        private readonly Dictionary<string, Task<GltfImport>> _loads =
            new Dictionary<string, Task<GltfImport>>();
        private readonly IMaterialGenerator _materials;
        private readonly string _root;

        /// <summary>
        /// Node names must survive the import: `grip`, `muzzle`, `hand.r` and
        /// `seat.pilot` are a contract the client mounts things by.
        /// OriginalUnique rather than Original because glTFast needs it to
        /// resolve animation targets.
        ///
        /// AnimationMethod.Legacy, not Mecanim, and not by preference: Mecanim
        /// needs an AnimatorController, which is an asset, which rule 1 says
        /// nobody here may author. Legacy puts real AnimationClips on a plain
        /// Animation component with no asset anywhere.
        /// </summary>
        private static readonly ImportSettings Settings = new ImportSettings
        {
            NodeNameMethod = NameImportMethod.OriginalUnique,
            AnimationMethod = AnimationMethod.Legacy,
            GenerateMipMaps = false,
        };

        public AssetRegistry(Material material)
        {
            _materials = new SingleMaterial(material);
            _root = Path.Combine(Application.streamingAssetsPath, "art");
            LoadManifest();
        }

        /// <summary>True when an id has a .glb behind it at all.</summary>
        public bool Has(string assetId) =>
            !string.IsNullOrEmpty(assetId) && _paths.ContainsKey(assetId);

        /// <summary>
        /// Parents a fresh instance of <paramref name="assetId"/> under
        /// <paramref name="parent"/> and calls <paramref name="onAttached"/>
        /// with it. Returns at once; the model lands a frame or more later.
        ///
        /// Silently does nothing when the id is unknown, the file is missing
        /// or the load fails — the caller's fallback model simply stays up.
        /// </summary>
        /// <remarks>
        /// `async void`, because the caller is a synchronous spawn path that
        /// has nothing to await on. That makes the try/catch mandatory rather
        /// than tidy: an exception escaping an async void is not caught by
        /// anything above it and takes the player down. Every failure here is
        /// a warning and a box model instead.
        /// </remarks>
        public async void Attach(string assetId, Transform parent, Action<GameObject> onAttached)
        {
            if (!Has(assetId)) return;

            GameObject holder = null;
            try
            {
                GltfImport import = await LoadOnce(assetId);
                if (import == null) return;

                // The entity may have despawned while the file was loading.
                if (parent == null) return;

                holder = new GameObject(assetId);
                holder.transform.SetParent(parent, false);

                bool ok = await import.InstantiateMainSceneAsync(holder.transform);
                if (!ok || parent == null)
                {
                    UnityEngine.Object.Destroy(holder);
                    return;
                }

                onAttached?.Invoke(holder);
            }
            catch (Exception e)
            {
                if (holder != null) UnityEngine.Object.Destroy(holder);
                Debug.LogWarning($"asset {assetId} failed to load: {e.Message}");
            }
        }

        /// <summary>
        /// Drops a loaded model into the space its fallback box mesh already
        /// occupies, and onto its layer.
        ///
        /// This exists because the rig around a model is tuned to the model.
        /// The first-person rifle is the case that forced it: the box rifle
        /// points +Z with its origin at the grip, and the hands, the muzzle
        /// marker and the rest pose are all measured against that. The
        /// imported one points -Z with a centred origin, because that is what
        /// art/README.md asks every weapon for. Hardcoding the offset between
        /// those two frames would be a number nobody could check, and it would
        /// be wrong again the next time the model changed.
        ///
        /// So the model is fitted to the BOUNDS of the box it replaces: same
        /// volume, same centre, so everything measured against the old one
        /// still lands. `yaw` is the one thing that cannot be recovered from a
        /// bounding box -- a box does not know which end is the barrel.
        ///
        /// Scale is uniform, from the longest axis, so a model with slightly
        /// different proportions is not stretched to match.
        /// </summary>
        public void AttachFitted(string assetId, Transform parent, Mesh fallback,
                                 float yaw, int layer, Action<GameObject> onAttached)
        {
            Attach(assetId, parent, go =>
            {
                SetLayer(go, layer);
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

                if (!LocalBounds(go, parent, out Bounds loaded) || loaded.size.sqrMagnitude < 1e-12f)
                {
                    onAttached?.Invoke(go);
                    return;
                }

                Bounds target = fallback.bounds;
                float from = Mathf.Max(loaded.size.x, Mathf.Max(loaded.size.y, loaded.size.z));
                float to = Mathf.Max(target.size.x, Mathf.Max(target.size.y, target.size.z));
                if (from > 1e-6f && to > 1e-6f)
                {
                    float k = to / from;
                    go.transform.localScale = new Vector3(k, k, k);
                    loaded.center *= k;
                }
                go.transform.localPosition += target.center - loaded.center;

                onAttached?.Invoke(go);
            });
        }

        /// <summary>Bounds of everything under <paramref name="go"/>, in
        /// <paramref name="space"/>'s local frame.</summary>
        private static bool LocalBounds(GameObject go, Transform space, out Bounds bounds)
        {
            bounds = default;
            var filters = go.GetComponentsInChildren<MeshFilter>();
            var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>();
            bool any = false;

            foreach (MeshFilter f in filters)
            {
                if (f.sharedMesh == null) continue;
                any = Encapsulate(f.sharedMesh.bounds, f.transform, space, ref bounds, any);
            }
            foreach (SkinnedMeshRenderer r in skinned)
            {
                if (r.sharedMesh == null) continue;
                any = Encapsulate(r.sharedMesh.bounds, r.transform, space, ref bounds, any);
            }
            return any;
        }

        private static bool Encapsulate(Bounds local, Transform from, Transform space,
                                        ref Bounds acc, bool any)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = space.InverseTransformPoint(from.TransformPoint(corner));
                if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                else acc.Encapsulate(p);
            }
            return any;
        }

        private static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
        }

        /// <summary>
        /// Hands back the loaded model's first Mesh, for callers that draw
        /// geometry themselves rather than mounting a GameObject -- the rock
        /// scatter instances one mesh four hundred times and wants no
        /// transforms at all.
        ///
        /// First, not merged: the props this is used for are single-mesh by
        /// contract (art/manifest.json describes each rock as one unit-sized
        /// variant). A multi-mesh model asked for this way would silently
        /// lose its other parts, so it logs rather than picking quietly.
        /// </summary>
        public async void Mesh(string assetId, Action<Mesh> onReady)
        {
            if (!Has(assetId)) return;
            try
            {
                GltfImport import = await LoadOnce(assetId);
                if (import == null) return;

                Mesh[] meshes = import.GetMeshes();
                if (meshes == null || meshes.Length == 0)
                {
                    Debug.LogWarning($"asset {assetId} has no mesh to instance");
                    return;
                }
                if (meshes.Length > 1)
                    Debug.LogWarning(
                        $"asset {assetId} has {meshes.Length} meshes; instancing only the first");

                onReady?.Invoke(meshes[0]);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"asset {assetId} failed to load: {e.Message}");
            }
        }

        /// <summary>
        /// One load per id, shared by every caller that asks for it while it
        /// is still in flight. Returns null when the file is missing or does
        /// not parse.
        /// </summary>
        private Task<GltfImport> LoadOnce(string assetId)
        {
            if (_loads.TryGetValue(assetId, out var existing)) return existing;

            Task<GltfImport> task = LoadFile(_paths[assetId]);
            _loads[assetId] = task;
            return task;
        }

        private async Task<GltfImport> LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"asset file missing (run `make art-sync`): {path}");
                return null;
            }

            var import = new GltfImport(materialGenerator: _materials);
            bool ok = await import.LoadFile(path, importSettings: Settings);
            if (!ok)
            {
                Debug.LogWarning($"asset failed to parse: {path}");
                return null;
            }
            return import;
        }

        /// <summary>
        /// Reads art/manifest.json out of StreamingAssets to learn id -> file.
        ///
        /// A plain file read is correct here and would not be on Android or in
        /// a browser, where StreamingAssets is inside the package and needs
        /// UnityWebRequest. This client is a native desktop build by decision
        /// (ROADMAP Phase 3.5 drops browser delivery), so the simple path is
        /// the right one.
        /// </summary>
        private void LoadManifest()
        {
            string file = Path.Combine(_root, "manifest.json");
            if (!File.Exists(file))
            {
                Debug.LogWarning($"no art manifest at {file} — run `make art-sync`. " +
                                 "Every entity falls back to its box model.");
                return;
            }

            Manifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"art manifest unreadable: {e.Message}");
                return;
            }
            if (manifest?.assets == null) return;

            foreach (ManifestAsset a in manifest.assets)
            {
                if (string.IsNullOrEmpty(a.id) || string.IsNullOrEmpty(a.file)) continue;
                _paths[a.id] = Path.Combine(_root, a.file);
            }
            Debug.Log($"art manifest: {_paths.Count} assets");
        }
    }
}
