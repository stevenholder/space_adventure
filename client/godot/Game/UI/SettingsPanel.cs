// Settings (docs/GDD.md "Display and settings"): display mode, UI scale
// and look sensitivity, kept in user://sa.cfg [settings] and applied at
// boot. The UI is laid out for 1920×1080 and the window scales it
// (project.godot stretch canvas_items/expand); UI scale multiplies on top.
using System;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class Settings
    {
        private const string ConfigPath = "user://sa.cfg";

        /// <summary>0 windowed, 1 windowed fullscreen (borderless), 2 exclusive fullscreen.</summary>
        public int DisplayMode = 1;
        public float UiScale = 1.0f;
        public float MouseSensitivity = 1.0f;

        public static readonly string[] ModeNames = { "WINDOWED", "WINDOWED FULLSCREEN", "FULLSCREEN" };

        public void Load()
        {
            try
            {
                var cf = new ConfigFile();
                if (cf.Load(ConfigPath) != Error.Ok || !cf.HasSection("settings")) return;
                DisplayMode = Math.Clamp(cf.GetValue("settings", "display_mode", 1).AsInt32(), 0, 2);
                UiScale = Mathf.Clamp((float)cf.GetValue("settings", "ui_scale", 1.0).AsDouble(), 0.75f, 1.5f);
                MouseSensitivity = Mathf.Clamp((float)cf.GetValue("settings", "mouse_sensitivity", 1.0).AsDouble(), 0.25f, 3f);
            }
            catch (Exception) { }
        }

        public void Save()
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath); // keep [panels] and [hotbar]
            cf.SetValue("settings", "display_mode", DisplayMode);
            cf.SetValue("settings", "ui_scale", (double)UiScale);
            cf.SetValue("settings", "mouse_sensitivity", (double)MouseSensitivity);
            cf.Save(ConfigPath);
        }

        /// <summary>Puts the window in the chosen mode and scales the canvas. Safe to call every change.</summary>
        public void Apply(Window window)
        {
            window.Mode = DisplayMode switch
            {
                0 => Window.ModeEnum.Windowed,
                2 => Window.ModeEnum.ExclusiveFullscreen,
                _ => Window.ModeEnum.Fullscreen, // borderless at the desktop size
            };
            window.ContentScaleFactor = UiScale;
        }
    }

    /// <summary>The Esc menu's SETTINGS page.</summary>
    public sealed class SettingsView : ModalView
    {
        private readonly Settings _settings;
        private readonly Action _apply;

        public SettingsView(Control root, Settings settings, Action apply)
            : base(root, "Settings", 420, 0.22f)
        {
            _settings = settings;
            _apply = apply;
        }

        protected override void Fill(VBoxContainer body)
        {
            Line(body, "display", Styles.Dust, 12);
            var modes = Styles.Row(6);
            for (int i = 0; i < Settings.ModeNames.Length; i++)
            {
                int mode = i;
                modes.AddChild(Styles.Button(Settings.ModeNames[i], _settings.DisplayMode == i, () => { _settings.DisplayMode = mode; Changed(); }));
            }
            body.AddChild(modes);
            body.AddChild(Styles.Gap(8));

            Slider(body, "ui scale", _settings.UiScale, 0.75f, 1.5f, 0.05f, v => _settings.UiScale = v, v => $"{v * 100:0}%");
            Slider(body, "mouse sensitivity", _settings.MouseSensitivity, 0.25f, 3f, 0.05f, v => _settings.MouseSensitivity = v, v => $"{v:0.00}×");

            body.AddChild(Styles.Gap(6));
            Line(body, "laid out for 1920×1080; the window scales it  ·  Esc returns", Styles.Dust, 11);
        }

        private void Slider(VBoxContainer body, string name, float value, float min, float max, float step, Action<float> set, Func<float, string> show)
        {
            var row = Styles.Row(8);
            row.AddChild(Styles.Grow(Styles.Display_(name, 12, Styles.Dust)));
            var val = Styles.Display_(show(value), 12, Styles.Amber);
            val.CustomMinimumSize = new Vector2(56, 0);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(200, 20) };
            slider.ValueChanged += v => { set((float)v); val.Text = show((float)v); _apply(); };
            slider.DragEnded += _ => _settings.Save();
            row.AddChild(slider);
            row.AddChild(val);
            body.AddChild(row);
        }

        private void Changed() { _settings.Save(); _apply(); Rebuild(); }
    }
}
