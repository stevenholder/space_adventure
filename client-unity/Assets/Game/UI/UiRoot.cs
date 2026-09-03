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

            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = "runtime-panel-settings";
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.scale = 1f;
            // An EMPTY theme, created at runtime: a PanelSettings with a null
            // themeStyleSheet never attaches its panel (the spike's first run
            // proved it — probe pixel came back sky), while an empty one
            // attaches fine and simply provides no default styles, which is
            // the deal we wanted anyway: every element styles itself.
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

            // No label in the spike: the first run's log showed the text-
            // shaping job (ATGTextJobSystem.ShapeText) throwing on an OS-font
            // FontDefinition and taking the whole panel down with it. Text
            // arrives with task 3's vendored font as a real FontAsset; the
            // spike proves the PANEL.

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

            Root.Remove(probe);
        }
    }
}
