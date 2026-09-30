// The character panel (C) and the backpack (B), Phase 11.7.
//
// WoW's paper doll, drawn in the Scrapyard Comic style: slots around a live
// model of the character, the stats the game actually computes beneath it,
// and a 5x4 backpack of everything not worn. One widget, ItemSlot, does the
// work in both: icon, rarity frame, stack count, drag source and target,
// right-click to equip/unequip, and a tooltip built from the item def.
//
// Nothing here changes state on its own. A drop or a right-click sends
// `equip`; the result's `equipped` map lands in Character and the open
// panels rebuild from it (Boot). Icons are art/icons/<item id>.png,
// rendered at build by art/tools/gen_icons.py; an item with no model
// draws a rarity tile with its initials.

using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    /// <summary>Item icons from art/icons, cached; null when there is none.</summary>
    public sealed class Icons
    {
        private readonly string _dir;
        private readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        public Icons(string artRoot) { _dir = Path.Combine(artRoot, "icons"); }

        public Texture2D Get(string itemId)
        {
            if (_cache.TryGetValue(itemId, out Texture2D t)) return t;
            Texture2D tex = null;
            string file = Path.Combine(_dir, itemId + ".png");
            if (File.Exists(file))
            {
                var img = new Image();
                if (img.LoadPngFromBuffer(File.ReadAllBytes(file)) == Error.Ok) tex = ImageTexture.CreateFromImage(img);
            }
            _cache[itemId] = tex;
            return tex;
        }
    }

    /// <summary>
    /// One square: a worn slot (SlotName set) or a backpack cell. Empty
    /// slots show their name in dust; filled ones the icon, rarity frame
    /// and stack count. Drag between any two; right-click equips or
    /// unequips. The tooltip is the item def.
    /// </summary>
    public partial class ItemSlot : Control
    {
        public const int Cell = 60;

        public string SlotName = "";       // "" for a backpack cell
        /// <summary>A display-only slot (shop stock): tooltip yes, drag and right-click no.</summary>
        public bool Static;
        public string Item = "";
        public int Qty;
        public Defs Defs = Defs.Empty;
        public Icons Icons;
        /// <summary>(item, fromSlot or "") dropped here; the receiver decides.</summary>
        public System.Action<string, string> OnDrop;
        /// <summary>Right-click.</summary>
        public System.Action OnAlt;

        private Panel _box;
        private TextureRect _icon;
        private Label _initials;
        private Label _count;
        private Label _empty;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(Cell, Cell);
            MouseFilter = MouseFilterEnum.Stop;
            _box = Styles.Box(Styles.Steel, Styles.Ink, 2);
            _box.MouseFilter = MouseFilterEnum.Ignore;
            _box.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_box);
            _icon = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
            _icon.SetAnchorsPreset(LayoutPreset.FullRect);
            _icon.OffsetLeft = 4; _icon.OffsetTop = 4; _icon.OffsetRight = -4; _icon.OffsetBottom = -4;
            AddChild(_icon);
            _initials = Styles.Display_("", 22, Styles.Cream);
            _initials.HorizontalAlignment = HorizontalAlignment.Center;
            _initials.VerticalAlignment = VerticalAlignment.Center;
            _initials.SetAnchorsPreset(LayoutPreset.FullRect);
            _initials.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_initials);
            _empty = Styles.Display_("", 10, Styles.Dust);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.SetAnchorsPreset(LayoutPreset.FullRect);
            _empty.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_empty);
            _count = Styles.Display_("", 13, Styles.Cream);
            _count.HorizontalAlignment = HorizontalAlignment.Right;
            _count.SetAnchorsPreset(LayoutPreset.BottomRight);
            _count.GrowHorizontal = GrowDirection.Begin; _count.GrowVertical = GrowDirection.Begin;
            _count.OffsetRight = -4; _count.OffsetBottom = -2;
            _count.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_count);
            Refresh();
        }

        public void Set(string item, int qty)
        {
            Item = item ?? "";
            Qty = qty;
            if (IsNodeReady()) Refresh();
        }

        private void Refresh()
        {
            bool filled = !string.IsNullOrEmpty(Item);
            _empty.Text = filled ? "" : EmptyLabel(SlotName);
            Texture2D tex = filled && Icons != null ? Icons.Get(Item) : null;
            _icon.Texture = tex;
            _icon.Visible = tex != null;
            _initials.Text = filled && tex == null ? InitialsOf(Defs.ItemName(Item)) : "";
            _count.Text = filled && Qty > 1 ? Qty.ToString() : "";
            Color rarity = filled ? Styles.Rarity(Defs.ItemRarity(Item)) : Styles.Ink;
            _box.AddThemeStyleboxOverride("panel", FrameStyle(filled ? Styles.Steel : new Color(Styles.Steel, 0.55f), rarity, filled ? 3 : 2));
            TooltipText = filled ? " " : ""; // non-empty: Godot asks _MakeCustomTooltip
        }

        private static string EmptyLabel(string slot) => slot switch
        {
            "accessory1" => "ACC", "accessory2" => "ACC", "primary" => "WEAPON", "secondary" => "SIDEARM", _ => slot.ToUpperInvariant(),
        };

        private static StyleBoxFlat FrameStyle(Color bg, Color border, int width)
        {
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border };
            sb.SetBorderWidthAll(width);
            return sb;
        }

        private static string InitialsOf(string name)
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
            return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
        }

        // ---- input: right-click, drag out, drop in ------------------------

        public override void _GuiInput(InputEvent e)
        {
            if (!Static && e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right && !string.IsNullOrEmpty(Item))
            {
                OnAlt?.Invoke();
                AcceptEvent();
            }
        }

        public override Variant _GetDragData(Vector2 at)
        {
            if (Static || string.IsNullOrEmpty(Item)) return default;
            var preview = new ItemSlot { Item = Item, Qty = Qty, Defs = Defs, Icons = Icons, Modulate = new Color(1, 1, 1, 0.85f) };
            SetDragPreview(preview);
            return new Godot.Collections.Dictionary { { "item", Item }, { "from", SlotName } };
        }

        public override bool _CanDropData(Vector2 at, Variant data)
        {
            if (Static || data.VariantType != Variant.Type.Dictionary) return false;
            var d = data.AsGodotDictionary();
            string item = d["item"].AsString();
            if (SlotName == "") return true; // the bag takes anything back
            return Defs.SlotAccepts(Defs.SlotOf(item), SlotName);
        }

        public override void _DropData(Vector2 at, Variant data)
        {
            var d = data.AsGodotDictionary();
            OnDrop?.Invoke(d["item"].AsString(), d["from"].AsString());
        }

        // ---- tooltip -------------------------------------------------------

        public override Control _MakeCustomTooltip(string forText)
        {
            ItemDef def = Defs.Item(Item);
            var panel = Styles.Panel(Styles.SkewNone);
            Styles.SetPadding(panel, 10, 8, 10, 8);
            VBoxContainer body = Styles.Body(panel);
            var name = Styles.Display_(Defs.ItemName(Item), 15, Styles.Rarity(Defs.ItemRarity(Item)));
            body.AddChild(name);
            string tier = string.IsNullOrEmpty(Defs.ItemRarity(Item)) ? "common" : Defs.ItemRarity(Item);
            string kind = def?.Kind ?? "";
            body.AddChild(Styles.Display_($"{tier}  ·  {kind}" + (string.IsNullOrEmpty(def?.Slot) ? "" : $"  ·  {def.Slot}"), 11, Styles.Dust));
            if (def?.Weapon != null)
            {
                body.AddChild(Styles.Display_($"{def.Weapon.Damage} damage  ·  {60.0 / Math.Max(0.01, def.Weapon.FireInterval):0} rpm  ·  {def.Weapon.Magazine} rounds", 12, Styles.Cream));
                body.AddChild(Styles.Display_($"{def.Weapon.MaxRange:0} m range", 12, Styles.Cream));
            }
            if (def?.Armor != null) body.AddChild(Styles.Display_($"{def.Armor.Value} armor", 12, Styles.Cream));
            if (Qty > 1) body.AddChild(Styles.Display_($"×{Qty}" + (def != null && def.StackMax > 1 ? $" of {def.StackMax}" : ""), 12, Styles.Dust));
            if (!string.IsNullOrEmpty(def?.Desc))
            {
                var desc = Styles.Display_(def.Desc, 12, Styles.Dust);
                desc.AutowrapMode = TextServer.AutowrapMode.Word;
                desc.CustomMinimumSize = new Vector2(240, 0);
                body.AddChild(desc);
            }
            if (!Static) body.AddChild(Styles.Display_(SlotName == "" ? "right-click: equip  ·  drag to a slot" : "right-click: unequip  ·  drag to the bag", 10, Styles.Dust));
            return panel;
        }
    }

    /// <summary>
    /// The paper doll: the character's own model in a viewport of its own,
    /// turning slowly, lit for the panel. OwnWorld3D keeps the planet's
    /// sun and sky out of it.
    /// </summary>
    public sealed class Doll
    {
        public readonly SubViewportContainer View;
        private readonly Node3D _turn;
        private readonly AssetRegistry _assets;
        private bool _attached;

        public Doll(AssetRegistry assets, int width, int height)
        {
            _assets = assets;
            View = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(width, height), MouseFilter = Control.MouseFilterEnum.Ignore };
            var vp = new SubViewport { OwnWorld3D = true, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Size = new Vector2I(width, height) };
            View.AddChild(vp);
            var cam = new Camera3D { Fov = 36f, Near = 0.05f, Far = 20f, Position = new Vector3(0f, 1.0f, 4.0f) };
            cam.Basis = Basis.LookingAt(new Vector3(0f, 0.95f, 0f) - cam.Position, Vector3.Up);
            vp.AddChild(cam);
            cam.Current = true;
            var key = new DirectionalLight3D { LightEnergy = 1.3f, ShadowEnabled = false };
            key.Basis = Basis.LookingAt(new Vector3(-0.5f, -0.7f, -0.6f), Vector3.Up);
            vp.AddChild(key);
            var fill = new DirectionalLight3D { LightEnergy = 0.5f, ShadowEnabled = false };
            fill.Basis = Basis.LookingAt(new Vector3(0.7f, -0.2f, 0.4f), Vector3.Up);
            vp.AddChild(fill);
            vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0, 0, 0, 0),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.35f, 0.36f, 0.42f), AmbientLightEnergy = 1f,
            } });
            _turn = new Node3D();
            vp.AddChild(_turn);
        }

        /// <summary>Attach once the registry has the model; safe to call every open.</summary>
        public void Ensure()
        {
            if (_attached) return;
            _attached = true;
            _assets.Attach("char.player", _turn, null);
        }

        public void Tick(double dt) => _turn.RotateY((float)(dt * 0.6));
    }

    /// <summary>C: the character panel — slots around the doll, stats beneath.</summary>
    public sealed class CharacterView : ModalView
    {
        private readonly Character _character;
        private readonly SkillSheet _skills;
        private readonly Icons _icons;
        private Doll _doll; // rebuilt with the body, freed with it: no orphaned viewport at quit
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        private static readonly string[] LeftSlots = { "head", "chest", "legs", "hands", "feet", "back" };
        private static readonly string[] RightSlots = { "accessory1", "accessory2", "primary", "mod", "secondary", "tool", "gadget" }; // Phase 13: MOD under WEAPON

        public CharacterView(Control root, Character character, SkillSheet skills, Icons icons, AssetRegistry assets,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Character", 560, 0.05f, 0.30f) // left by default; the backpack sits right
        {
            _character = character;
            _skills = skills;
            _icons = icons;
            _assets = assets;
            _nextSeq = nextSeq;
            _send = send;
        }

        private readonly AssetRegistry _assets;

        public void Tick(double dt) { if (Open) _doll?.Tick(dt); }

        private ItemSlot Slot(string name)
        {
            var s = new ItemSlot { SlotName = name, Defs = _character.Defs, Icons = _icons };
            string worn = _character.Worn(name);
            s.Item = worn; s.Qty = string.IsNullOrEmpty(worn) ? 0 : 1;
            s.OnDrop = (item, from) =>
            {
                if (from == name) return;
                _send(_character.EquipCmd(_nextSeq(), name, item));
            };
            s.OnAlt = () => _send(_character.UnequipCmd(_nextSeq(), name));
            return s;
        }

        private VBoxContainer SlotColumn(IEnumerable<string> names)
        {
            var col = Styles.Column(6);
            foreach (string n in names)
                if (Known(n)) col.AddChild(Slot(n));
            return col;
        }

        private bool Known(string slot) => _character.Defs.EquipSlots == null || _character.Defs.EquipSlots.Count == 0
            ? slot == "primary" : _character.Defs.EquipSlots.Contains(slot);

        private static string NiceSlot(string n) => n switch
        {
            "accessory1" => "accessory", "accessory2" => "accessory", "primary" => "weapon", "secondary" => "sidearm", _ => n,
        };

        protected override void Fill(VBoxContainer body)
        {
            _doll = new Doll(_assets, 220, 270);
            _doll.Ensure();

            var top = Styles.Row(14);
            top.AddChild(SlotColumn(LeftSlots));
            var centre = Styles.Column(4);
            centre.AddChild(_doll.View);
            var nameLab = Styles.Display_(System.Environment.MachineName ?? "you", 14, Styles.Cream);
            nameLab.HorizontalAlignment = HorizontalAlignment.Center;
            centre.AddChild(nameLab);
            top.AddChild(centre);
            top.AddChild(SlotColumn(RightSlots));
            body.AddChild(top);
            body.AddChild(Styles.Gap(6));
            body.AddChild(Styles.Header("Stats"));

            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 18);
            grid.AddThemeConstantOverride("v_separation", 2);
            void Stat(string k, string v, Color c)
            {
                grid.AddChild(Styles.Display_(k, 12, Styles.Dust));
                var val = Styles.Display_(v, 13, c);
                val.HorizontalAlignment = HorizontalAlignment.Right;
                val.CustomMinimumSize = new Vector2(80, 0);
                grid.AddChild(val);
            }
            Defs defs = _character.Defs;
            double Bonus(string id) => _skills.EfficacyBonus(defs, id);
            int armor = 0;
            foreach (var kv in _character.Equipped) armor += defs.Item(kv.Value)?.Armor?.Value ?? 0;
            ItemDef weapon = defs.Item(_character.Primary);
            // Phase 13: the worn mod's deltas add onto the table, as the server's sim.ApplyMod does.
            ModDef mod = defs.Item(_character.Worn("mod"))?.Mod;
            int baseDmg = (weapon?.Weapon?.Damage ?? 0) + (mod?.Damage ?? 0);
            int mag = (weapon?.Weapon?.Magazine ?? 0) + (mod?.Magazine ?? 0);
            double range = (weapon?.Weapon?.MaxRange ?? 0) + (mod?.MaxRange ?? 0);
            double dmg = baseDmg * (1 + Bonus("marksmanship"));
            Stat("health", $"{_character.Health} / 100", Styles.Danger);
            Stat("armor", armor.ToString(), Styles.Shield);
            Stat("damage", weapon?.Weapon != null ? $"{dmg:0.#}" : "—", mod?.Damage > 0 ? Styles.Good : Styles.Cream);
            Stat("fire rate", weapon?.Weapon != null ? $"{60.0 / Math.Max(0.01, weapon.Weapon.FireInterval):0} rpm" : "—", Styles.Cream);
            Stat("magazine", weapon?.Weapon != null ? mag.ToString() : "—", mod?.Magazine > 0 ? Styles.Good : Styles.Cream);
            Stat("range", weapon?.Weapon != null ? $"{range:0} m" : "—", mod?.MaxRange > 0 ? Styles.Good : Styles.Cream);
            Stat("sprint", $"{Sim.Rules.SprintSpeed * (1 + Bonus("athletics")):0.0} m/s", Styles.Cream);
            Stat("rover", $"+{Bonus("driving") * 100:0.#}%", Styles.Cream);
            Stat("ship", $"+{Bonus("piloting") * 100:0.#}%", Styles.Cream);
            Stat("loot rolls", $"+{Bonus("scavenging") * 100:0.#}%", Styles.Good);
            Stat("buy prices", $"−{Bonus("commerce") * 100:0.#}%", Styles.Amber);
            Stat("discovery", $"+{Bonus("recon") * 100:0.#}%", Styles.Cream);
            Stat("credits", _character.Credits < 0 ? "—" : _character.Credits.ToString(), Styles.Amber);
            Stat("bag", $"{_character.UsedSlots} / {Character.InventorySlots}", Styles.Dust);
            body.AddChild(grid);

            body.AddChild(Styles.Gap(6));
            body.AddChild(Styles.Header("Skills"));
            var sk = new GridContainer { Columns = 5 };
            sk.AddThemeConstantOverride("h_separation", 16);
            if (defs.Skills != null)
                foreach (var s in defs.Skills)
                {
                    var cell = Styles.Row(4);
                    cell.AddChild(Styles.Display_(s.Name, 11, s.Reserved ? Styles.Dust : Styles.Cream));
                    cell.AddChild(Styles.Display_(_skills.Level(s.Id).ToString(), 13, Styles.Amber));
                    sk.AddChild(cell);
                }
            body.AddChild(sk);
            body.AddChild(Styles.Gap(4));
            Line(body, "C closes  ·  right-click a slot to unequip  ·  drag from the backpack (B)", Styles.Dust, 11);
        }
    }

    /// <summary>B: the backpack — a 5x4 grid of what you carry and do not wear.</summary>
    public sealed class BackpackView : ModalView
    {
        private ItemSlot _firstCell;
        /// <summary>The first bag cell itself, for the rig to start a drag from.</summary>
        public Control FirstCell => _firstCell != null && GodotObject.IsInstanceValid(_firstCell) ? _firstCell : null;
        /// <summary>Screen centre of the first bag cell, for the rig's hotbar drag proof.</summary>
        public Vector2 FirstCellCentre => _firstCell != null && GodotObject.IsInstanceValid(_firstCell)
            ? _firstCell.GlobalPosition + _firstCell.Size / 2f : Vector2.Zero;
        private readonly Character _character;
        private readonly Icons _icons;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public BackpackView(Control root, Character character, Icons icons, Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Backpack", 400, 0.18f, 0.80f) // right by default, beside the character
        {
            _character = character;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
        }

        /// <summary>The slot an item goes to on a right-click: its own, or the first free accessory slot.</summary>
        private string TargetSlot(string item)
        {
            string slot = _character.Defs.SlotOf(item);
            if (slot != "accessory") return slot;
            if (string.IsNullOrEmpty(_character.Worn("accessory1"))) return "accessory1";
            if (string.IsNullOrEmpty(_character.Worn("accessory2"))) return "accessory2";
            return "accessory1";
        }

        protected override void Fill(VBoxContainer body)
        {
            var head = Styles.Row(8);
            head.AddChild(Styles.Grow(Styles.Display_($"{_character.UsedSlots} / {Character.InventorySlots} slots", 12, Styles.Dust)));
            head.AddChild(Styles.Display_(_character.Credits < 0 ? "— cr" : $"{_character.Credits} cr", 14, Styles.Amber));
            body.AddChild(head);
            body.AddChild(Styles.Gap(4));

            var grid = new GridContainer { Columns = 5 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            var shown = new List<(string item, int qty)>();
            if (_character.Inventory != null)
                foreach (var st in _character.Inventory)
                {
                    int qty = st.qty;
                    if (!string.IsNullOrEmpty(_character.SlotHolding(st.item))) qty -= 1; // the worn one is on the doll
                    if (qty > 0) shown.Add((st.item, qty));
                }
            for (int i = 0; i < Character.InventorySlots; i++)
            {
                var cell = new ItemSlot { Defs = _character.Defs, Icons = _icons };
                if (i < shown.Count) { cell.Item = shown[i].item; cell.Qty = shown[i].qty; }
                if (i == 0) _firstCell = cell; // the rig's drag proof grabs it
                string item = cell.Item;
                cell.OnAlt = () =>
                {
                    // Phase 13: a consumable is used, everything else equipped.
                    if (_character.Defs.Item(item)?.Consumable != null) { _send(Character.UseCmd(_nextSeq(), item)); return; }
                    string slot = TargetSlot(item);
                    if (!string.IsNullOrEmpty(slot)) _send(_character.EquipCmd(_nextSeq(), slot, item));
                };
                cell.OnDrop = (dropped, from) =>
                {
                    if (!string.IsNullOrEmpty(from)) _send(_character.UnequipCmd(_nextSeq(), from));
                };
                grid.AddChild(cell);
            }
            body.AddChild(grid);
            body.AddChild(Styles.Gap(4));
            Line(body, "B closes  ·  right-click equips or uses  ·  drag onto the character (C) or the bar", Styles.Dust, 11);
        }
    }
}
