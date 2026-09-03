// Phase 8 — the UI Toolkit root, built ENTIRELY at runtime: PanelSettings
// from ScriptableObject.CreateInstance, no UXML, no USS, no theme asset —
// C47's no-assets rule applied to the interface (ROADMAP Phase 8 task 1).
//
// A null theme means NO default styles: every element styles itself from
// Ui.Styles tokens, and every Label must carry an explicit font — which is
// the discipline the style guide wants anyway.
//
// SPIKE SELF-CHECK: because nobody can eyeball a headless box, Verify()
// draws a known amber rect, waits a frame, samples the screen pixel under
// it and prints "ui: toolkit ready" or "ui: toolkit MISSING" to the player
// log. The packaged-player run is the proof, not the Editor.

using System.Collections;
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

            Root = doc.rootVisualElement;
            Root.style.position = Position.Absolute;
            Root.style.left = 0;
            Root.style.top = 0;
            Root.style.right = 0;
            Root.style.bottom = 0;
            Root.pickingMode = PickingMode.Ignore;
        }

        /// <summary>
        /// The spike's proof: draw a known rect, sample the pixel, log the
        /// verdict. Runs once from Boot; removed from the flow once panels
        /// are ported (the panels themselves become the evidence).
        /// </summary>
        public IEnumerator Verify(MonoBehaviour host)
        {
            var probe = new VisualElement();
            probe.style.position = Position.Absolute;
            probe.style.left = 20;
            probe.style.top = 20;
            probe.style.width = 40;
            probe.style.height = 40;
            probe.style.backgroundColor = new Color(1f, 0.682f, 0.098f); // amber
            Root.Add(probe);

            // Text through the VENDORED FontAsset only — the OS-font path
            // crashed the text shaper and took the panel with it (run 1).
            Label label = null;
            if (Styles.Display != null)
            {
                label = Styles.Display_("SCRAPYARD", 20, Color.white);
                label.style.position = Position.Absolute;
                label.style.left = 70;
                label.style.top = 24;
                Root.Add(label);
            }

            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            // ReadPixels rather than ScreenCapture: the ScreenCapture module
            // is not linked in this player configuration, and one pixel is
            // all the spike needs.
            var tex = new Texture2D(1, 1, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(40, Screen.height - 40, 1, 1), 0, 0);
            tex.Apply();
            Color got = tex.GetPixel(0, 0);
            Object.Destroy(tex);
            // Hue test, not brightness: the project renders in linear color
            // space, so sRGB amber (1, .68, .1) reads back around
            // (.45, .31, .04). What identifies the probe is the ratio.
            bool amber = got.r > 0.25f && got.r > got.g && got.g > got.b * 3f;
            Debug.Log(amber
                ? $"ui: toolkit ready (probe pixel {got})"
                : $"ui: toolkit MISSING (probe pixel {got}) — styled-IMGUI fallback per ROADMAP");
            if (label != null)
            {
                // Text proof: the shaper laid it out (nonzero width) and the
                // panel survived (the amber check above still passed).
                Debug.Log(label.resolvedStyle.width > 1f
                    ? $"ui: text ready (label width {label.resolvedStyle.width:F0}px)"
                    : "ui: text MISSING (label never laid out)");
                Root.Remove(label);
            }
            else
            {
                Debug.Log("ui: text MISSING (vendored font did not load)");
            }

            Root.Remove(probe);
        }
    }
}
