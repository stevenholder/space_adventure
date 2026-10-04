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
            _empty = Styles.Display_("", 12, Styles.Dust);
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
            "accessory1" => "ACC", "accessory2" => "ACC", "primary" => "WEAPON", "melee" => "MELEE", _ => slot.ToUpperInvariant(),
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
            body.AddChild(Styles.Display_($"{tier}  ·  {kind}" + (string.IsNullOrEmpty(def?.Slot) ? "" : $"  ·  {def.Slot}"), 12, Styles.Dust));
            if (def?.Weapon != null)
            {
                body.AddChild(Styles.Display_($"{def.Weapon.Damage} damage  ·  {60.0 / Math.Max(0.01, def.Weapon.FireInterval):0} rpm  ·  {def.Weapon.Magazine} rounds", 12, Styles.Cream));
                body.AddChild(Styles.Display_($"{def.Weapon.MaxRange:0} m range", 12, Styles.Cream));
            }
            if (def?.Melee is MeleeDef m)
            {
                body.AddChild(Styles.Display_($"{m.Damage} damage  ·  {m.Interval:0.0#} s swing  ·  {(m.Hands >= 2 ? "two" : "one")}-handed", 12, Styles.Cream));
                body.AddChild(Styles.Display_(m.Arc >= 360 ? $"hits all around, {m.Range:0.#} m" : $"{m.Arc:0}° arc ahead, {m.Range:0.#} m  ·  Tab swaps", 12, Styles.Cream));
            }
            if (def?.Consumable?.Throw is ThrowDef th)
                body.AddChild(Styles.Display_($"thrown: {th.Damage} damage, {th.Radius:0.#} m blast", 12, Styles.Cream));
            if (def?.Armor != null) body.AddChild(Styles.Display_($"{def.Armor.Value} armor", 12, Styles.Cream));
            if (Qty > 1) body.AddChild(Styles.Display_($"×{Qty}" + (def != null && def.StackMax > 1 ? $" of {def.StackMax}" : ""), 12, Styles.Dust));
            if (!string.IsNullOrEmpty(def?.Desc))
            {
                var desc = Styles.Display_(def.Desc, 12, Styles.Dust);
                desc.AutowrapMode = TextServer.AutowrapMode.Word;
                desc.CustomMinimumSize = new Vector2(240, 0);
                body.AddChild(desc);
            }
            if (!Static) body.AddChild(Styles.Display_(SlotName == "" ? "right-click: equip  ·  drag to a slot" : "right-click: unequip  ·  drag to the bag", 12, Styles.Dust));
            return panel;
        }
    }

    /// <summary>
    /// A row that explains itself on hover: a title and lines of text in the
    /// item tooltip's panel. The character sheet's stats and skills use it.
    /// </summary>
    public partial class Tip : HBoxContainer
    {
        public string Title = "";
        public readonly List<(string Text, Color Color)> Lines = new List<(string, Color)>();

        public Tip() { MouseFilter = MouseFilterEnum.Pass; TooltipText = " "; } // non-empty: Godot asks _MakeCustomTooltip

        public Tip Add(string text, Color color) { Lines.Add((text, color)); return this; }

        public override Control _MakeCustomTooltip(string forText)
        {
            var panel = Styles.Panel(Styles.SkewNone);
            Styles.SetPadding(panel, 10, 8, 10, 8);
            VBoxContainer body = Styles.Body(panel);
            body.AddChild(Styles.Display_(Title, 15, Styles.Amber));
            foreach (var (text, color) in Lines)
            {
                var l = Styles.Display_(text, 12, color);
                l.AutowrapMode = TextServer.AutowrapMode.Word;
                l.CustomMinimumSize = new Vector2(300, 0);
                body.AddChild(l);
            }
            return panel;
        }
    }

    /// <summary>
    /// The paper doll: the character's own model in a viewport of its own,
    /// lit for the panel; drag it left/right to turn it. OwnWorld3D keeps
    /// the planet's sun and sky out of it.
    /// </summary>
    public sealed class Doll
    {
        public readonly SubViewportContainer View;
        private readonly Node3D _turn;
        private readonly AssetRegistry _assets;
        private bool _attached;
        // ponytail: static, there is one doll; survives the rebuild on every equip
        private static float _yaw;

        public Doll(AssetRegistry assets, int width, int height)
        {
            _assets = assets;
            View = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(width, height), MouseFilter = Control.MouseFilterEnum.Stop };
            View.GuiInput += e =>
            {
                if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0)
                    _turn.Rotation = new Vector3(0f, _yaw += m.Relative.X * 0.01f, 0f);
            };
            var vp = new SubViewport { OwnWorld3D = true, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Size = new Vector2I(width, height) };
            View.AddChild(vp);
            // A 1.8 m body filling ~90% of the height: 36° vertical at 3.1 m sees ~2 m.
            var cam = new Camera3D { Fov = 36f, Near = 0.05f, Far = 20f, Position = new Vector3(0f, 0.92f, 3.1f) };
            cam.Basis = Basis.LookingAt(new Vector3(0f, 0.92f, 0f) - cam.Position, Vector3.Up);
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
            _turn = new Node3D { Rotation = new Vector3(0f, _yaw, 0f) };
            vp.AddChild(_turn);
        }

        /// <summary>
        /// Attach once the registry has the model, standing in idle, wearing
        /// the body slots and holding the weapon by the world's own rules.
        /// Safe to call every open; the panel rebuilds the doll on an equip.
        /// </summary>
        public void Ensure(Character character, IEnumerable<string> bodySlots)
        {
            if (_attached) return;
            _attached = true;
            Defs defs = character.Defs;
            var worn = new Dictionary<string, string>();
            foreach (string slot in bodySlots) worn[slot] = character.Worn(slot);
            string inHand = character.Held;
            string weapon = defs.ItemAsset(inHand);
            _assets.Attach("char.player", _turn, model =>
            {
                CharacterAnim anim = CharacterAnim.For(model);
                if (anim != null) { anim.Class = defs.HoldSuffix(inHand); anim.Armed = weapon != ""; anim.Drive(0f, false); }
                EntityViews.Dress(_assets, defs.ItemAsset, model, worn, new Dictionary<string, string>(), new Dictionary<string, Node3D>());
                if (weapon != "") EntityViews.Hold(_assets, model, weapon, null, blade: defs.Item(inHand)?.Melee != null);
            });
        }

    }

    /// <summary>C: the character panel — slots around the doll, stats beneath.</summary>
    public sealed class CharacterView : ModalView
    {
        private readonly Character _character;
        private readonly SkillSheet _skills;
        private readonly Icons _icons;
        private Doll _doll; // rebuilt with the body, freed with it: no orphaned viewport at quit
        private readonly List<Tip> _tips = new List<Tip>();
        /// <summary>Screen centre of the stat or skill whose tooltip title starts with `title`, for the rig's -uiTip.</summary>
        public Vector2? TipCentre(string title)
        {
            Tip t = _tips.Find(x => GodotObject.IsInstanceValid(x) && x.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase));
            return t == null ? null : t.GlobalPosition + t.Size / 2f;
        }
        /// <summary>Screen centre of the doll, for the rig's -uiDollDrag.</summary>
        public Vector2 DollCentre => _doll != null && GodotObject.IsInstanceValid(_doll.View) ? _doll.View.GlobalPosition + _doll.View.Size / 2f : Vector2.Zero;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        private static readonly string[] LeftSlots = { "head", "chest", "legs", "hands", "feet", "back" };
        private static readonly string[] RightSlots = { "accessory1", "accessory2", "primary", "mod", "melee", "tool", "gadget" }; // Phase 13: MOD under WEAPON; MELEE the hand weapon Tab swaps to

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

        /// <summary>What a skill's efficacy does, in play (server/skillsengine.go and its callers). "!" = caveat.</summary>
        private static string SkillEffect(string id, string kind) => kind switch
        {
            "damage_mult" => "Multiplies the damage of every shot you land.",
            "sprint_mult" => "Multiplies your sprint speed. Walking is unchanged.",
            "drive_mult" => "Multiplies rover acceleration. Top speed and grip are unchanged.",
            "flight_mult" => "Multiplies ship thrust, normal and boosted. Top speed is unchanged.",
            "loot_extra_roll" => "The chance an enemy you kill drops its loot a second time.",
            "buy_discount" => "Cuts every shop price.",
            "discovery_range" => "!For now, points of interest are discovered within a fixed 84 m.",
            "gather_speed" => id == "salvaging" ? "Shortens the time to salvage a wreck (never below 1 s)."
                : "Shortens the time to drill iron and copper ore (never below 1 s).",
            "craft_extra" => "The chance a craft makes one extra item. Not for single items such as tools.",
            _ => "",
        };

        /// <summary>How each skill earns XP (server/data/skills.json `awards`, nodes.json, recipes.json).</summary>
        private static string SkillTraining(string id) => id switch
        {
            "marksmanship" => "landing shots. 2 XP per point of damage, 40 per kill, 400 for the Warlord.",
            "athletics" => "sprinting. 1 XP per 10 m.",
            "driving" => "driving a rover. 1 XP per 10 m.",
            "piloting" => "flying a ship. 1 XP per 10 m, and 50 for a landing after 3 s or more in the air.",
            "scavenging" => "picking items up off the ground. 10 XP each.",
            "commerce" => "trading at shops. 1 XP per 5 credits bought, sold or bought back.",
            "recon" => "reaching a point of interest for the first time (250 XP) and finishing scout missions (100 XP).",
            "mining" => "drilling ore.",
            "salvaging" => "salvaging wrecks.",
            "engineering" => "crafting at a bench. The recipe's XP for every item made.",
            _ => "using it.",
        };

        private static string NiceSlot(string n) => n switch
        {
            "accessory1" => "accessory", "accessory2" => "accessory", "primary" => "weapon", "melee" => "melee", _ => n,
        };

        protected override void Fill(VBoxContainer body)
        {
            _tips.Clear();
            _doll = new Doll(_assets, 300, 440);

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
            // In the tree first: a cached model attaches synchronously, and
            // holding a weapon reads the hand's global transform.
            _doll.Ensure(_character, LeftSlots); // LeftSlots: the slots drawn on the body
            body.AddChild(Styles.Gap(6));
            body.AddChild(Styles.Header("Stats"));

            // Every stat and skill explains itself on hover (Tip). The numbers
            // and rules are the server's (sim/combat.go, sim/player_death.go,
            // server/skillsengine.go); a line starting "!" is a caveat, drawn amber.
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 18);
            grid.AddThemeConstantOverride("v_separation", 2);
            static Tip Lines(Tip t, string[] lines)
            {
                foreach (string l in lines)
                    if (l.StartsWith("!")) t.Add(l.Substring(1), Styles.Amber); else t.Add(l, Styles.Cream);
                return t;
            }
            void Stat(string k, string v, Color c, params string[] lines)
            {
                var row = Lines(new Tip { Title = k.ToUpperInvariant() }, lines);
                var key = Styles.Display_(k, 12, Styles.Dust);
                key.CustomMinimumSize = new Vector2(84, 0);
                row.AddChild(key);
                var val = Styles.Display_(v, 13, c);
                val.HorizontalAlignment = HorizontalAlignment.Right;
                val.CustomMinimumSize = new Vector2(80, 0);
                row.AddChild(val);
                grid.AddChild(row); _tips.Add(row);
            }
            Defs defs = _character.Defs;
            double Bonus(string id) => _skills.EfficacyBonus(defs, id);
            string Pct(double b) => SkillsView.Pct(b);
            string Lv(string id) => $"{defs.Skills?.Find(s => s.Id == id)?.Name ?? id} {_skills.Level(id)}";
            double Synergy(string source, string target) =>
                defs.Synergies?.Find(s => s.Source == source && s.Target == target) is SynergyDef sy ? _skills.SynergyBonus(sy) : 0;

            int armor = 0;
            var armorLines = new List<string>();
            foreach (var kv in _character.Equipped) // every worn item, as the server's sim.ArmorOf sums them
                if (defs.Item(kv.Value)?.Armor is ArmorDef a)
                {
                    armor += a.Value;
                    armorLines.Add($"{defs.ItemName(kv.Value)}: {a.Value}");
                }
            if (armorLines.Count == 0) armorLines.Add("Nothing worn. Drag armor onto HEAD, CHEST, LEGS, HANDS, FEET or BACK.");
            armorLines.Insert(0, "The armor values of everything you wear, added up:");
            // sim.Mitigate: a hit × 100 / (100 + armor), rounded, never below 1.
            double armorCut = 1 - 100.0 / (100.0 + armor);
            armorLines.Add($"Every hit you take is cut by {Pct(armorCut)}: 100 / (100 + {armor}). A 20 damage hit lands as {Math.Max(1, Math.Round(20 * 100.0 / (100 + armor), MidpointRounding.AwayFromZero))}.");
            armorLines.Add("More armor always helps, but each point is worth a little less. A hit always does at least 1.");

            WeaponDef w = defs.Item(_character.Primary)?.Weapon;
            // Phase 13: the worn mod's deltas add onto the table, as the server's sim.ApplyMod does.
            ModDef mod = defs.Item(_character.Worn("mod"))?.Mod;
            string modName = mod != null ? defs.ItemName(_character.Worn("mod")) : "";
            int baseDmg = (w?.Damage ?? 0) + (mod?.Damage ?? 0);
            int mag = (w?.Magazine ?? 0) + (mod?.Magazine ?? 0);
            double range = (w?.MaxRange ?? 0) + (mod?.MaxRange ?? 0);
            double fStart = (w?.FalloffStart ?? 0) + (mod?.FalloffStart ?? 0), fEnd = (w?.FalloffEnd ?? 0) + (mod?.FalloffEnd ?? 0);
            double dmg = baseDmg * (1 + Bonus("marksmanship"));
            string falloff = w != null && fEnd > 0
                ? $"Full damage out to {fStart:0} m, then falls off evenly to {(w.FalloffMin * 100):0}% at {fEnd:0} m."
                : "Full damage at any distance in range.";
            const string noWeapon = "No weapon equipped. Drag one onto the WEAPON slot.";

            Stat("health", $"{_character.Health} / 100", Styles.Danger,
                "How much damage you can take. 100 at most.",
                "Armor cuts every hit you take (see ARMOR).",
                "Regenerates 8 per second after 8 s without taking damage.",
                "At 0 you are down for 5 s, then respawn at full health, protected for 3 s.",
                "!Dying drops the materials in your bag where you fell. Gear, credits, tools and ammo stay with you.");
            Stat("armor", armor > 0 ? $"{armor}  −{Pct(armorCut)}" : "0", Styles.Shield, armorLines.ToArray());
            Stat("damage", w != null ? $"{dmg:0.#}" : "—", mod?.Damage > 0 ? Styles.Good : Styles.Cream, w == null ? new[] { noWeapon } : new[]
            {
                "Damage per hit, at close range.",
                $"Weapon {w.Damage}" + (mod?.Damage != 0 && mod != null ? $" + {modName} {mod.Damage}" : "")
                    + $", × {Lv("marksmanship")} (+{Pct(Bonus("marksmanship"))}) = {dmg:0.#}.",
                falloff,
                "One hitbox per body: no headshots.",
            });
            Stat("fire rate", w != null ? $"{60.0 / Math.Max(0.01, w.FireInterval):0} rpm" : "—", Styles.Cream, w == null ? new[] { noWeapon } : new[]
            {
                $"Rounds per minute: one shot every {w.FireInterval:0.###} s.",
                "Shots faster than this are ignored. Mods do not change it.",
            });
            Stat("magazine", w != null ? mag.ToString() : "—", mod?.Magazine > 0 ? Styles.Good : Styles.Cream, w == null ? new[] { noWeapon } : new[]
            {
                $"Rounds before you must reload: weapon {w.Magazine}" + (mod?.Magazine > 0 ? $" + {modName} {mod.Magazine}" : "") + ".",
                $"Reloading (R) takes {(string.IsNullOrEmpty(w.AmmoItem) ? "ammo" : defs.ItemName(w.AmmoItem))} from your bag. With none, you cannot reload.",
                "Switching weapons refills the magazine.",
            });
            Stat("range", w != null ? $"{range:0} m" : "—", mod?.MaxRange > 0 ? Styles.Good : Styles.Cream, w == null ? new[] { noWeapon } : new[]
            {
                $"The farthest a shot can hit: weapon {w.MaxRange:0} m" + (mod?.MaxRange > 0 ? $" + {modName} {mod.MaxRange:0} m" : "") + ". Past it, shots never land.",
                falloff,
            });
            Stat("sprint", $"{Sim.Rules.SprintSpeed * (1 + Bonus("athletics")):0.0} m/s", Styles.Cream,
                $"Running speed: {Sim.Rules.SprintSpeed:0.0} m/s × {Lv("athletics")} (+{Pct(Bonus("athletics"))}).",
                "Walking speed does not change.");
            Stat("rover", $"+{Pct(Bonus("driving"))}", Styles.Cream,
                $"Rover acceleration bonus from {Lv("driving")}, forwards and in reverse.",
                "Top speed and grip do not change.");
            Stat("ship", $"+{Pct(Bonus("piloting"))}", Styles.Cream,
                $"Ship thrust bonus from {Lv("piloting")}, normal and boosted.",
                "Top speed does not change.");
            Stat("loot rolls", $"+{Pct(Bonus("scavenging"))}", Styles.Good,
                $"The chance that an enemy you kill drops its loot a second time, from {Lv("scavenging")}.",
                $"Inside a discovered point of interest, {Lv("recon")} adds +{Pct(Synergy("recon", "scavenging"))}.",
                "Only enemy kills. Ore and wrecks are not affected.");
            Stat("buy prices", $"−{Pct(Bonus("commerce"))}", Styles.Amber,
                $"Shop prices cut by {Lv("commerce")}.",
                $"Shops buy from you at half an item's value, +{Pct(Synergy("scavenging", "commerce"))} from {Lv("scavenging")}.");
            Stat("discovery", $"+{Pct(Bonus("recon"))}", Styles.Cream,
                $"Discovery range bonus from {Lv("recon")}.",
                "!Not active yet: you discover a point of interest within a fixed 84 m. Recon still earns XP for every first discovery.");
            Stat("credits", _character.Credits < 0 ? "—" : _character.Credits.ToString(), Styles.Amber,
                "Money. Spent at shops on items and buybacks.",
                "Earned by selling, missions and bounties. You keep it when you die.");
            Stat("bag", $"{_character.UsedSlots} / {Character.InventorySlots}", Styles.Dust,
                $"Bag slots in use, of {Character.InventorySlots} (B opens the bag).",
                "Each stack takes one slot, up to its stack size. Worn gear takes none.");
            body.AddChild(grid);

            body.AddChild(Styles.Gap(6));
            body.AddChild(Styles.Header("Skills"));
            var sk = new GridContainer { Columns = 5 };
            sk.AddThemeConstantOverride("h_separation", 16);
            if (defs.Skills != null)
                foreach (var s in defs.Skills)
                {
                    int level = _skills.Level(s.Id);
                    long xp = _skills.XP.TryGetValue(s.Id, out long x) ? x : 0;
                    var tip = new Tip { Title = $"{s.Name}  ·  level {level}" };
                    tip.Add(level >= SkillCurve.MaxLevel ? "Max level."
                        : $"{xp} XP. Level {level + 1} at {SkillCurve.PointsForLevel(level + 1)} ({SkillCurve.PointsForLevel(level + 1) - xp} to go).", Styles.Dust);
                    if (s.Efficacy != null)
                    {
                        tip.Add($"Now: {SkillsView.EfficacyText(s.Efficacy.Kind, Bonus(s.Id))}. Each level adds {Pct(s.Efficacy.PerLevel)}, up to {Pct((SkillCurve.MaxLevel - 1) * s.Efficacy.PerLevel)} at level {SkillCurve.MaxLevel}.", Styles.Cream);
                        Lines(tip, new[] { SkillEffect(s.Id, s.Efficacy.Kind) });
                    }
                    if (defs.Synergies != null)
                        foreach (var sy in defs.Synergies)
                        {
                            if (sy.Source != s.Id && sy.Target != s.Id) continue;
                            string other = defs.Skills.Find(o => o.Id == (sy.Source == s.Id ? sy.Target : sy.Source))?.Name ?? "";
                            string text = SkillsView.SynergyText(sy, _skills.SynergyBonus(sy));
                            tip.Add(sy.Source == s.Id ? $"Helps {other}: {text}." : $"Helped by {other}: {text}.", Styles.Good);
                        }
                    tip.Add($"Trained by: {SkillTraining(s.Id)}", Styles.Dust);
                    foreach (NodeDef n in defs.Nodes)
                        if (n.Skill == s.Id)
                            tip.Add($"{n.Name}: {n.XP} XP each, needs level {n.Level} and a {defs.ItemName(n.Tool)}.", Styles.Dust);
                    tip.AddThemeConstantOverride("separation", 4);
                    tip.AddChild(Styles.Display_(s.Name, 12, s.Reserved ? Styles.Dust : Styles.Cream));
                    tip.AddChild(Styles.Display_(level.ToString(), 13, Styles.Amber));
                    sk.AddChild(tip); _tips.Add(tip);
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

        public BackpackView(Control root, Character character, Icons icons, Func<ushort> nextSeq, Action<byte[]> send, Interaction interact = null)
            : base(root, "Backpack", 400, 0.18f, 0.80f) // right by default, beside the character
        {
            _character = character;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
            _interact = interact;
        }

        private readonly Interaction _interact;

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
                int qty = cell.Qty;
                cell.OnAlt = () =>
                {
                    // Phase 13, WoW's rule: with a shop open, right-click sells
                    // (one; Shift for the stack). Otherwise a consumable is
                    // used, everything else equipped.
                    if (_interact != null && _interact.ShopOpen)
                    {
                        if (_character.Defs.SellPrice(item) <= 0) { _interact.Notice = "no one buys that"; return; }
                        _send(_interact.SellCmd(_nextSeq(), item, Input.IsKeyPressed(Key.Shift) ? qty : 1));
                        return;
                    }
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
            Line(body, _interact != null && _interact.ShopOpen
                ? "right-click sells one  ·  shift right-click sells the stack  ·  B closes"
                : "B closes  ·  right-click equips or uses  ·  drag onto the character (C) or the bar", Styles.Dust, 11);
        }
    }
}
