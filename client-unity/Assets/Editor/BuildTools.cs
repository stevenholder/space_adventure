// Scene and player build, driven from the command line.
//
// CONVENTIONS.md forbids agent-authored .unity and .prefab files because
// Unity stores them as GUID-keyed YAML: unreviewable in a diff, unmergeable on
// conflict, and "verify" means opening the Editor. The boot scene is the one
// permitted exception (C47), and this generates it rather than committing a
// hand-written one — the scene's CONTENT is then this file, which reviews like
// code, and regenerating it is a command rather than an Editor session.
//
// The scene is deliberately empty. Boot.cs builds the whole hierarchy at
// runtime from a [RuntimeInitializeOnLoadMethod], so the scene exists only to
// give a packaged player something to load (C45).
//
//   Unity -batchmode -quit -executeMethod SpaceAdventure.EditorTools.BuildTools.GenerateBootScene
//   Unity -batchmode -quit -executeMethod SpaceAdventure.EditorTools.BuildTools.BuildStandalone

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpaceAdventure.EditorTools
{
    public static class BuildTools
    {
        private const string SceneDir = "Assets/Scenes";
        private const string ScenePath = SceneDir + "/Boot.unity";

        [MenuItem("Space Adventure/Generate Boot Scene")]
        public static void GenerateBootScene()
        {
            if (!Directory.Exists(SceneDir)) Directory.CreateDirectory(SceneDir);

            // No camera, no light, nothing: Boot.cs creates all of it. A scene
            // that ships objects would give the runtime two sources of truth
            // for the hierarchy, and the one in the YAML is the one nobody
            // reviews.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved) throw new Exception($"could not save {ScenePath}");

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"boot scene written to {ScenePath} and registered in build settings");
        }

        /// <summary>
        /// Shaders the runtime asks for by NAME. Unity strips any shader no
        /// asset references, and this project references none: every material
        /// is built in C# via Shader.Find, which the build has no way to see.
        /// The result is a player that builds cleanly and dies on its first
        /// frame with "Value cannot be null. Parameter name: shader" — it did,
        /// before this existed. Registering them here is what keeps them in.
        /// </summary>
        private static readonly string[] RequiredShaders =
        {
            "Standard",                              // entities, tinted per instance
            "Sprites/Default",                       // tracers and impact markers
            "SpaceAdventure/TerrainVertexColor",     // the planet's vertex colours
            "Skybox/Cubemap",                        // the generated starfield
        };

        /// <summary>
        /// The layer the first-person rig renders on. A second camera draws
        /// only this layer, on top, so the weapon never clips into a wall the
        /// player is standing against — the standard fix, and the reason the
        /// rig needs a layer of its own at all.
        /// </summary>
        public const string ViewModelLayer = "ViewModel";

        /// <summary>Layer index used when the name is not registered yet.</summary>
        public const int ViewModelLayerIndex = 8; // first user layer

        /// <summary>
        /// Names layer 8 "ViewModel" if it is unnamed. Runs on every domain
        /// reload, so a fresh clone needs no manual setup step — the
        /// alternative is a layer that exists only as a magic number and shows
        /// up blank in the Inspector.
        /// </summary>
        [InitializeOnLoadMethod]
        [MenuItem("Space Adventure/Ensure Layers")]
        public static void EnsureLayers()
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            if (layers == null || layers.arraySize <= ViewModelLayerIndex) return;

            SerializedProperty slot = layers.GetArrayElementAtIndex(ViewModelLayerIndex);
            if (slot.stringValue == ViewModelLayer) return;
            if (!string.IsNullOrEmpty(slot.stringValue))
            {
                Debug.LogWarning($"layer {ViewModelLayerIndex} is '{slot.stringValue}', not '{ViewModelLayer}' — " +
                                 "the first-person rig will fall back to the index");
                return;
            }
            slot.stringValue = ViewModelLayer;
            tagManager.ApplyModifiedProperties();
            Debug.Log($"layer {ViewModelLayerIndex} named '{ViewModelLayer}'");
        }

        [MenuItem("Space Adventure/Ensure Shaders")]
        public static void EnsureShaders()
        {
            var settings = AssetDatabase
                .LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")
                .OfType<UnityEngine.Rendering.GraphicsSettings>()
                .FirstOrDefault();
            if (settings == null) throw new Exception("GraphicsSettings.asset not loadable");

            var so = new SerializedObject(settings);
            SerializedProperty list = so.FindProperty("m_AlwaysIncludedShaders");

            foreach (string name in RequiredShaders)
            {
                Shader shader = Shader.Find(name);
                if (shader == null) throw new Exception($"shader not found in the Editor either: {name}");

                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) { present = true; break; }
                }
                if (present) continue;

                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                Debug.Log($"always-included shader added: {name}");
            }

            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Space Adventure/Build Standalone")]
        public static void BuildStandalone()
        {
            EnsureShaders();
            EnsureLayers();
            if (!File.Exists(ScenePath)) GenerateBootScene();

            // Honour -buildOutput from the command line so CI and a local run
            // can put the player somewhere different without editing this.
            string[] argv = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(argv, "-buildOutput");
            string outDir = at >= 0 && at + 1 < argv.Length ? argv[at + 1] : "Build";

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string exe = target == BuildTarget.StandaloneWindows64
                ? "SpaceAdventure.exe"
                : "SpaceAdventure";

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outDir, exe),
                target = target,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"build {summary.result}: {summary.totalSize} bytes, " +
                      $"{summary.totalErrors} errors, output {summary.outputPath}");
            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                // Batch mode ignores a thrown exception's exit code unless the
                // build itself failed, so make the failure explicit.
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Fails if any scene beyond the boot scene has crept into the build
        /// list — the same rule `make unity-gate` enforces on disk, checked
        /// where it actually matters for a shipped player.
        /// </summary>
        [MenuItem("Space Adventure/Check Build Scenes")]
        public static void CheckBuildScenes()
        {
            string[] extra = EditorBuildSettings.scenes
                .Select(s => s.path)
                .Where(p => p != ScenePath)
                .ToArray();
            if (extra.Length > 0)
            {
                Debug.LogError($"unexpected scenes in the build: {string.Join(", ", extra)}");
                EditorApplication.Exit(1);
            }
            Debug.Log("build scenes: boot only");
        }
    }
}
