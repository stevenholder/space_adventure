// Phase 8 — the UI Toolkit root, built ENTIRELY at runtime: PanelSettings
// from ScriptableObject.CreateInstance, no UXML, no USS, no theme asset —
// C47's no-assets rule applied to the interface (ROADMAP Phase 8 task 1).
//
// A null theme means NO default styles: every element styles itself from
// Ui.Styles tokens, and every Label must carry an explicit font — which is
// the discipline the style guide wants anyway.
//
// The spike's Verify() pixel probe is gone: it earned its keep proving the
// runtime-only panel worked at all, and retired when the real panels became
// the evidence (every -uiShot screenshot shows them). The "ui: toolkit
// attached" log line remains so a packaged run still leaves a trace.

using UnityEngine;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game.UI
{
    public sealed class UiRoot
    {
        public VisualElement Root { get; private set; }

        private readonly GameObject _go;

        public UiRoot()
        {
            _go = new GameObject("UiRoot");
            Object.DontDestroyOnLoad(_go);
            var doc = _go.AddComponent<UIDocument>();

            // Prefer the BUILD-GENERATED PanelSettings (BuildTools makes it
            // for the build and deletes it after): it carries the ICU data
            // the Advanced Text Generator needs, which a runtime-created one
            // cannot (Unity's warning names this exact constraint). The
            // runtime-created fallback covers in-Editor play, where ICU is
            // available anyway. Either way, the theme is an EMPTY runtime
            // ThemeStyleSheet: a null theme never attaches the panel (spike
            // run 1 — probe pixel came back sky), an empty one attaches and
            // provides no default styles, so every element styles itself.
            var settings = Resources.Load<PanelSettings>("ui/panel-settings");
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.name = "runtime-panel-settings";
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.scale = 1f;
            }
            if (settings.themeStyleSheet == null)
                settings.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>();
            doc.panelSettings = settings;
            doc.sortingOrder = 100;
            Debug.Log($"ui: toolkit attached ({(settings.name == "runtime-panel-settings" ? "runtime" : "build")} panel settings)");

            Root = doc.rootVisualElement;
            Root.style.position = Position.Absolute;
            Root.style.left = 0;
            Root.style.top = 0;
            Root.style.right = 0;
            Root.style.bottom = 0;
            Root.pickingMode = PickingMode.Ignore;
        }

    }
}
