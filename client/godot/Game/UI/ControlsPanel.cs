// Key binds: one table of game actions and the PHYSICAL key each reads
// (InputState is physical throughout, so WASD is WASD on any layout), kept
// in user://sa.cfg [keys]. The Esc menu's CONTROLS page lists them and
// rebinds on a click + a key press. Enter (chat), Esc (menu), F3 (debug)
// and the mouse buttons (fire / aim) are fixed and not in the table.
using System;
using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public static class Binds
    {
        private const string ConfigPath = "user://sa.cfg";

        /// <summary>Every rebindable action, in the order the page lists them. The last ten are the hotbar slots.</summary>
        public static readonly (string Id, string Label, Key Default)[] Table =
        {
            ("forward", "move forward", Key.W),
            ("back", "move back", Key.S),
            ("left", "strafe left", Key.A),
            ("right", "strafe right", Key.D),
            ("sprint", "sprint", Key.Shift),
            ("jump", "jump", Key.Space),
            ("interact", "interact / hold", Key.F),
            ("reload", "reload", Key.R),
            ("swap", "swap gun / melee", Key.Tab),
            ("map", "map", Key.M),
            ("bags", "backpack", Key.B),
            ("sheet", "character", Key.C),
            ("journal", "journal", Key.J),
            ("party", "party", Key.P),
            ("skills", "skills", Key.K),
            ("hotbar1", "hotbar 1", Key.Key1),
            ("hotbar2", "hotbar 2", Key.Key2),
            ("hotbar3", "hotbar 3", Key.Key3),
            ("hotbar4", "hotbar 4", Key.Key4),
            ("hotbar5", "hotbar 5", Key.Key5),
            ("hotbar6", "hotbar 6", Key.Q),
            ("hotbar7", "hotbar 7", Key.E),
            ("hotbar8", "hotbar 8", Key.T),
            ("hotbar9", "hotbar 9", Key.Z),
            ("hotbar10", "hotbar 10", Key.X),
        };

        private static readonly Dictionary<string, Key> Keys = Defaults();

        /// <summary>The ten hotbar keys in slot order (Hotbar.SlotFor indexes this).</summary>
        public static Key[] Hotbar { get; private set; } = HotbarKeys();

        private static Dictionary<string, Key> Defaults()
        {
            var d = new Dictionary<string, Key>();
            foreach (var (id, _, def) in Table) d[id] = def;
            return d;
        }

        private static Key[] HotbarKeys()
        {
            var ks = new Key[Game.UI.Hotbar.Row];
            for (int i = 0; i < ks.Length; i++) ks[i] = Keys[$"hotbar{i + 1}"];
            return ks;
        }

        public static Key Of(string id) => Keys[id];

        /// <summary>Binds <paramref name="id"/> to <paramref name="k"/>; whatever else held <paramref name="k"/> takes the vacated key (a swap, never two actions on one key).</summary>
        public static void Set(string id, Key k)
        {
            Key old = Keys[id];
            foreach (var (other, _, _) in Table)
                if (other != id && Keys[other] == k) Keys[other] = old;
            Keys[id] = k;
            Hotbar = HotbarKeys();
        }

        public static void Reset()
        {
            foreach (var (id, _, def) in Table) Keys[id] = def;
            Hotbar = HotbarKeys();
        }

        /// <summary>What the key prints on this keyboard ("W", "Shift", "Space"); the physical key as a fallback.</summary>
        public static string Name(Key k)
        {
            Key shown = Key.None;
            try { shown = DisplayServer.KeyboardGetKeycodeFromPhysical(k); } catch (Exception) { }
            if (shown == Key.None) shown = k;
            string s = OS.GetKeycodeString(shown);
            return string.IsNullOrEmpty(s) ? k.ToString() : s;
        }

        public static void Load()
        {
            try
            {
                var cf = new ConfigFile();
                if (cf.Load(ConfigPath) != Error.Ok || !cf.HasSection("keys")) return;
                foreach (var (id, _, def) in Table)
                {
                    var k = (Key)cf.GetValue("keys", id, (long)def).AsInt64();
                    if (Enum.IsDefined(typeof(Key), k) && k != Key.None && k != Key.Escape) Keys[id] = k;
                }
                Hotbar = HotbarKeys();
            }
            catch (Exception) { Reset(); }
        }

        public static void Save()
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath); // keep [settings], [panels], [hotbar]
            foreach (var (id, _, _) in Table) cf.SetValue("keys", id, (long)Keys[id]);
            cf.Save(ConfigPath);
        }
    }

    /// <summary>The Esc menu's CONTROLS page: action, key; click the key, press a new one.</summary>
    public sealed class ControlsView : ModalView
    {
        /// <summary>The action waiting for a key press, or null.</summary>
        public string Listening { get; private set; }

        public ControlsView(Control root) : base(root, "Controls", 420, 0.10f) { }

        /// <summary>Boot._Input hands every key here while a bind is listening. Esc cancels; anything else binds.</summary>
        public void Capture(Key physical)
        {
            if (Listening == null) return;
            if (physical != Key.Escape)
            {
                Binds.Set(Listening, physical);
                Binds.Save();
            }
            Listening = null;
            Rebuild();
        }

        /// <summary>What a click on a key button does (the rig calls it directly).</summary>
        public void Listen(string id) { Listening = id; Rebuild(); }

        public override void Show(bool on) { Listening = null; base.Show(on); }

        protected override void Fill(VBoxContainer body)
        {
            Line(body, Listening == null ? "click a key to change it" : "press the new key  ·  Esc cancels", Styles.Dust, 12);
            body.AddChild(Styles.Gap(4));
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 4);
            foreach (var (id, label, _) in Binds.Table)
            {
                grid.AddChild(Styles.Grow(Styles.Display_(label, 12, Styles.Dust)));
                bool waiting = Listening == id;
                var b = Styles.Button(waiting ? "..." : Binds.Name(Binds.Of(id)), waiting, () => Listen(id));
                b.FocusMode = Control.FocusModeEnum.None; // the pressed key must reach _Input, not this button
                b.CustomMinimumSize = new Vector2(96, 0);
                grid.AddChild(b);
            }
            body.AddChild(grid);
            body.AddChild(Styles.Gap(8));
            body.AddChild(Styles.Button("RESET DEFAULTS", true, () => { Binds.Reset(); Binds.Save(); Rebuild(); }));
            body.AddChild(Styles.Gap(6));
            Line(body, "fixed: Enter chat  ·  Esc menu  ·  F3 debug  ·  mouse fire / aim", Styles.Dust, 11);
        }
    }
}
