// The hotbar (Phase 13, docs/GDD.md "The hotbar"): twenty slots on ten
// keys and a Shift row, each holding a REFERENCE — a consumable item id, a
// worn item id with an ability, or one of the two built-in actions the game
// already had on E and R. Client state only: user://sa.cfg [hotbar].
using System;
using System.Collections.Generic;
using Godot;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    /// <summary>What a hotbar slot points at.</summary>
    public struct HotbarRef
    {
        public string Kind; // "item" | "ability" | "action" | ""
        public string Id;
        public bool Empty => string.IsNullOrEmpty(Kind);
        public override string ToString() => Empty ? "" : Kind + ":" + Id;
        public static HotbarRef Parse(string s)
        {
            int i = s?.IndexOf(':') ?? -1;
            if (i <= 0) return default;
            return new HotbarRef { Kind = s.Substring(0, i), Id = s.Substring(i + 1) };
        }
        public static HotbarRef Action(string id) => new HotbarRef { Kind = "action", Id = id };
    }

    /// <summary>The bar's contents and key table, saved between sessions.</summary>
    public sealed class Hotbar
    {
        public const int Row = 10;
        public const int Slots = 20;
        public static readonly Key[] Keys = { Key.Key1, Key.Key2, Key.Key3, Key.Key4, Key.Key5, Key.Q, Key.E, Key.R, Key.T, Key.F };
        private static readonly string[] Labels = { "1", "2", "3", "4", "5", "Q", "E", "R", "T", "F" };
        private const string ConfigPath = "user://sa.cfg";

        public readonly HotbarRef[] Refs = new HotbarRef[Slots];

        public Hotbar() { Defaults(); }

        /// <summary>A fresh profile: interact on E, reload on R, nothing else — the game the player already knew.</summary>
        public void Defaults()
        {
            Array.Clear(Refs, 0, Slots);
            Refs[6] = HotbarRef.Action("interact");
            Refs[7] = HotbarRef.Action("reload");
        }

        public static string Label(int slot) => (slot >= Row ? "⇧" : "") + Labels[slot % Row];

        /// <summary>The key label of the slot holding `action`, "E" style; "" when unbound.</summary>
        public string KeyFor(string action)
        {
            for (int i = 0; i < Slots; i++)
                if (Refs[i].Kind == "action" && Refs[i].Id == action) return Label(i);
            return "";
        }

        public void Load()
        {
            try
            {
                var cf = new ConfigFile();
                if (cf.Load(ConfigPath) != Error.Ok || !cf.HasSection("hotbar")) return;
                for (int i = 0; i < Slots; i++)
                    Refs[i] = HotbarRef.Parse(cf.GetValue("hotbar", $"slot{i}", "").AsString());
            }
            catch (Exception) { Defaults(); }
        }

        public void Save()
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath); // keep [panels]
            for (int i = 0; i < Slots; i++) cf.SetValue("hotbar", $"slot{i}", Refs[i].ToString());
            cf.Save(ConfigPath);
        }

        /// <summary>Slot index for a key press, or -1.</summary>
        public static int SlotFor(Key k, bool shift)
        {
            int i = Array.IndexOf(Keys, k);
            return i < 0 ? -1 : (shift ? i + Row : i);
        }
    }

    /// <summary>One cell: drop target, drag source, icon, key label, count, cooldown sweep.</summary>
    public partial class HotbarCell : Control
    {
        public const int CellPx = 56;
        public int Index;
        public Hotbar Bar;
        public Character Character;
        public Icons Icons;
        public System.Action Changed;

        private PanelContainer _frame;
        private ItemSlot _slot;
        private Label _glyph, _key, _count;
        private ColorRect _sweep, _dim;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(CellPx, CellPx);
            MouseFilter = MouseFilterEnum.Stop;
            _frame = Styles.Panel(Styles.SkewNone);
            _frame.SetAnchorsPreset(LayoutPreset.FullRect);
            _frame.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_frame);
            _glyph = Styles.Display_("", 18, Styles.Cream);
            _glyph.SetAnchorsPreset(LayoutPreset.FullRect);
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            _glyph.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_glyph);
            _dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            _dim.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_dim);
            _sweep = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            _sweep.AnchorLeft = 0; _sweep.AnchorRight = 1; _sweep.AnchorBottom = 1; _sweep.AnchorTop = 0;
            AddChild(_sweep);
            _key = Styles.Display_("", 10, Styles.Amber);
            _key.Position = new Vector2(4, 2);
            _key.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_key);
            _count = Styles.Display_("", 11, Styles.Cream);
            _count.SetAnchorsPreset(LayoutPreset.BottomRight);
            _count.OffsetLeft = -28; _count.OffsetTop = -16; _count.OffsetRight = -4; _count.OffsetBottom = -1;
            _count.HorizontalAlignment = HorizontalAlignment.Right;
            _count.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_count);
        }

        private string _shownItem = "";

        /// <summary>
        /// Redraws from the bar's reference, the bag and a cooldown fraction
        /// (0..1 left, or -1). An ItemSlot builds its icon once in _Ready, so
        /// the cell keeps one per item and swaps it when the reference
        /// changes — the count label is the cell's own.
        /// </summary>
        public void Refresh(HotbarRef r, string label, float cooldownLeft)
        {
            _key.Text = label;
            _glyph.Text = "";
            _dim.Visible = false;
            int count = -1;
            bool greyed = false;
            string want = r.Kind == "item" || r.Kind == "ability" ? r.Id : "";
            if (want != _shownItem)
            {
                _slot?.QueueFree();
                _slot = null;
                _shownItem = want;
                if (want != "")
                {
                    _slot = new ItemSlot { Defs = Character.Defs, Icons = Icons, Static = true, Item = want, Qty = 1, MouseFilter = MouseFilterEnum.Ignore };
                    _slot.Position = new Vector2((CellPx - ItemSlot.Cell) / 2f, (CellPx - ItemSlot.Cell) / 2f);
                    AddChild(_slot);
                    MoveChild(_slot, 1); // over the frame, under the dim/sweep/labels
                }
            }
            switch (r.Kind)
            {
                case "item":
                    count = Character.Count(r.Id);
                    greyed = count <= 0;
                    break;
                case "ability":
                    greyed = Character.SlotHolding(r.Id) == "";
                    break;
                case "action":
                    _glyph.Text = r.Id == "interact" ? "✋" : r.Id == "reload" ? "⟳" : r.Id;
                    break;
            }
            _count.Text = count >= 0 ? count.ToString() : "";
            _dim.Visible = greyed;
            _sweep.Visible = cooldownLeft > 0f;
            if (_sweep.Visible) _sweep.AnchorTop = 1f - Mathf.Clamp(cooldownLeft, 0f, 1f);
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                Bar.Refs[Index] = default;
                Changed?.Invoke();
                AcceptEvent();
            }
        }

        public override Variant _GetDragData(Vector2 at)
        {
            if (Bar.Refs[Index].Empty) return default;
            var r = Bar.Refs[Index];
            var preview = new Label { Text = r.Kind == "action" ? r.Id : Character.Defs.ItemName(r.Id), Modulate = new Color(1, 1, 1, 0.85f) };
            SetDragPreview(preview);
            return new Godot.Collections.Dictionary { { "hotbar", Index } };
        }

        public override bool _CanDropData(Vector2 at, Variant data)
        {
            if (data.VariantType != Variant.Type.Dictionary) return false;
            var d = data.AsGodotDictionary();
            if (d.ContainsKey("hotbar")) return true;
            if (!d.ContainsKey("item")) return false;
            return Character.Defs.Item(d["item"].AsString())?.Usable ?? false;
        }

        public override void _DropData(Vector2 at, Variant data)
        {
            var d = data.AsGodotDictionary();
            if (d.ContainsKey("hotbar"))
            {
                int from = d["hotbar"].AsInt32();
                if (from == Index) return;
                // A bar is not a bag: the dropped-on reference is replaced, not swapped.
                Bar.Refs[Index] = Bar.Refs[from];
                Bar.Refs[from] = default;
            }
            else
            {
                string item = d["item"].AsString();
                ItemDef def = Character.Defs.Item(item);
                Bar.Refs[Index] = new HotbarRef { Kind = def?.Consumable != null ? "item" : "ability", Id = item };
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Ten cells across the bottom centre; the Shift row while Shift is held.</summary>
    public sealed class HotbarView
    {
        private readonly Hotbar _bar;
        private readonly Character _character;
        private readonly HotbarCell[] _cells = new HotbarCell[Hotbar.Row];
        private readonly Dictionary<string, (double start, double end)> _cooldowns = new Dictionary<string, (double, double)>();

        /// <summary>Held Shift shows and fires the second row; the rig can force it.</summary>
        public bool Shift;

        public HotbarView(Control root, Hotbar bar, Character character, Icons icons)
        {
            _bar = bar;
            _character = character;
            var row = Styles.Row(4);
            row.MouseFilter = Control.MouseFilterEnum.Ignore;
            for (int i = 0; i < Hotbar.Row; i++)
            {
                var cell = new HotbarCell { Index = i, Bar = bar, Character = character, Icons = icons, Changed = () => { bar.Save(); Refresh(0); } };
                _cells[i] = cell;
                row.AddChild(cell);
            }
            Styles.Pin(row, Control.LayoutPreset.CenterBottom, 0, 24);
            root.AddChild(row);
        }

        /// <summary>Screen centre of a visible cell, for the rig's drag proof.</summary>
        public Vector2 CellCentre(int i) => _cells[i % Hotbar.Row].GlobalPosition + _cells[i % Hotbar.Row].Size / 2f;

        /// <summary>A use result: draw the sweep for exactly the server's seconds.</summary>
        public void SetCooldown(string item, double seconds, double now)
        {
            if (seconds > 0) _cooldowns[item] = (now, now + seconds);
        }

        public bool Cooling(string item, double now) => _cooldowns.TryGetValue(item, out var c) && now < c.end;

        public void Refresh(double now)
        {
            int baseIdx = Shift ? Hotbar.Row : 0;
            for (int i = 0; i < Hotbar.Row; i++)
            {
                int slot = baseIdx + i;
                var r = _bar.Refs[slot];
                _cells[i].Index = slot;
                float left = -1f;
                if (!r.Empty && r.Kind != "action" && _cooldowns.TryGetValue(r.Id, out var c) && now < c.end)
                    left = (float)((c.end - now) / Math.Max(1e-6, c.end - c.start));
                _cells[i].Refresh(r, Hotbar.Label(slot), left);
            }
        }
    }
}
