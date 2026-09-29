// The skills panel (K) and the XP feed. Ten rows — name, level, progress
// bar to next, the multiplier the level buys — reserved rows greyed, synergy
// arrows drawn under their source. The feed drips awards at the right edge
// and shouts LEVEL UP across the middle.

using Godot;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    public sealed class SkillsView : ModalView
    {
        private readonly SkillSheet _sheet;
        private readonly Character _character;

        public SkillsView(Control root, SkillSheet sheet, Character character)
            : base(root, "Skills", 440)
        {
            _sheet = sheet;
            _character = character;
        }

        private static string Pct(double bonus) => $"{bonus * 100:0.#}%";

        /// <summary>What a level buys, in the GDD roster's words.</summary>
        private static string EfficacyText(string kind, double bonus) => kind switch
        {
            "damage_mult" => $"+{Pct(bonus)} weapon damage",
            "sprint_mult" => $"+{Pct(bonus)} sprint speed",
            "drive_mult" => $"+{Pct(bonus)} rover accel & grip",
            "flight_mult" => $"+{Pct(bonus)} ship handling",
            "loot_extra_roll" => $"+{Pct(bonus)} extra loot roll",
            "buy_discount" => $"−{Pct(bonus)} buy prices",
            "discovery_range" => $"+{Pct(bonus)} discovery range",
            _ => "",
        };

        private static string SynergyText(SynergyDef sy, double bonus) => sy.What switch
        {
            "loot_extra_roll" => $"+{Pct(bonus)} loot rolls" + (sy.Where == "poi" ? " in discovered POIs" : ""),
            "drive_grip" => $"+{Pct(bonus)} rover grip",
            "sell_bonus" => $"+{Pct(bonus)} sell prices",
            _ => $"+{Pct(bonus)} {sy.What}",
        };

        protected override void Fill(VBoxContainer body)
        {
            var defs = _character.Defs;
            if (defs.Skills == null || defs.Skills.Count == 0)
            {
                Line(body, "no roster yet", Styles.Dust);
                return;
            }
            if (!_sheet.Loaded) Line(body, "fetching the sheet…", Styles.Dust, 12);

            string NameOf(string id) => defs.Skills.Find(s => s.Id == id)?.Name ?? id;

            foreach (var sk in defs.Skills)
            {
                bool reserved = sk.Reserved;
                int level = reserved ? 1 : _sheet.Level(sk.Id);
                long xp = _sheet.XP.TryGetValue(sk.Id, out long v) ? v : 0;
                long floor = SkillCurve.PointsForLevel(level);
                long next = SkillCurve.PointsForLevel(level + 1);
                Color ink = reserved ? Styles.Dust : Styles.Cream;

                var row = Styles.Column(1);

                // One line: name, what the level buys (dust), the level. Ten
                // rows have to fit a 720p frame with the arrows under them.
                string tip = reserved ? "Phase 12 — the artisan loop"
                    : EfficacyText(sk.Efficacy?.Kind ?? "", _sheet.EfficacyBonus(defs, sk.Id));
                var head = Styles.Row(10);
                head.AddChild(Styles.Display_(sk.Name, 14, ink));
                head.AddChild(Styles.Grow(Styles.Display_(tip, 11, Styles.Dust)));
                head.AddChild(Styles.Display_($"LV {level}", 14, reserved ? Styles.Dust : Styles.Amber));
                row.AddChild(head);

                var (bar, fill) = Styles.Bar(reserved ? Styles.Steel : Styles.Shield, 8);
                float frac = level >= SkillCurve.MaxLevel ? 1f
                    : next > floor ? Mathf.Clamp((float)(xp - floor) / (next - floor), 0f, 1f) : 0f;
                Styles.SetFill(fill, frac);
                if (!reserved)
                {
                    var num = Styles.Display_(level >= SkillCurve.MaxLevel ? "99" : $"{xp - floor} / {next - floor}", 9, Styles.Cream);
                    num.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                    num.GrowHorizontal = Control.GrowDirection.Begin;
                    num.OffsetRight = -4; num.OffsetTop = -4;
                    bar.AddChild(num);
                }
                row.AddChild(bar);

                // Synergy arrows: a bar and a chevron in Good under the
                // SOURCE row, pointing at the partner it feeds.
                if (defs.Synergies != null)
                {
                    foreach (var sy in defs.Synergies)
                    {
                        if (sy.Source != sk.Id) continue;
                        var arrow = Styles.Row(0);
                        arrow.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0) });
                        arrow.AddChild(new ColorRect
                        {
                            Color = Styles.Good, CustomMinimumSize = new Vector2(16, 2),
                            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
                        });
                        var chevron = new StyleBoxFlat { DrawCenter = false, BorderColor = Styles.Good, BorderWidthTop = 2, BorderWidthRight = 2 };
                        var tipEl = new Panel
                        {
                            CustomMinimumSize = new Vector2(6, 6), PivotOffset = new Vector2(3, 3),
                            Rotation = Mathf.DegToRad(45), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                            MouseFilter = Control.MouseFilterEnum.Ignore,
                        };
                        tipEl.AddThemeStyleboxOverride("panel", chevron);
                        arrow.AddChild(tipEl);
                        arrow.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) });
                        arrow.AddChild(Styles.Display_(
                            $"{NameOf(sy.Target)}  {SynergyText(sy, _sheet.SynergyBonus(sy))}", 11, Styles.Good));
                        row.AddChild(arrow);
                    }
                }
                body.AddChild(row);
            }

            body.AddChild(Styles.Gap(4));
            Line(body, $"{_sheet.Discovered.Count} POIs discovered  ·  K closes", Styles.Dust, 12);
        }
    }

    /// <summary>XP drip at the right edge; LEVEL UP banner across the middle.</summary>
    public sealed class SkillsFeed
    {
        private const float DripSeconds = 2.2f;
        private const float BannerSeconds = 3.0f;
        private const int DripSlots = 5;

        private readonly Label[] _drips = new Label[DripSlots];
        private readonly PanelContainer _bannerBox;
        private readonly Label _bannerSub;

        public SkillsFeed(Control root)
        {
            var column = Styles.Column(2);
            column.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.Pin(column, Control.LayoutPreset.BottomRight, 28, 120); // above the ammo/credits box
            for (int i = 0; i < DripSlots; i++)
            {
                var l = Styles.Display_("", 14, Styles.Shield);
                l.HorizontalAlignment = HorizontalAlignment.Right;
                l.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
                l.Visible = false;
                column.AddChild(l);
                _drips[i] = l;
            }
            root.AddChild(column);

            _bannerBox = Styles.Panel(Styles.SkewRight);
            _bannerBox.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.PinAt(_bannerBox, 0.5f, 0.26f);
            Styles.SetPadding(_bannerBox, 14, 6, 14, 8);
            _bannerBox.Visible = false;
            var banner = Styles.Display_("LEVEL UP", 28, Styles.Amber);
            Styles.LetterSpacing(banner, 2);
            banner.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _bannerSub = Styles.Display_("", 15, Styles.Cream);
            _bannerSub.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            Styles.Body(_bannerBox).AddChild(banner);
            Styles.Body(_bannerBox).AddChild(_bannerSub);
            root.AddChild(_bannerBox);
        }

        public void Tick(SkillSheet sheet, Defs defs, float now)
        {
            while (sheet.Drips.Count > 0 && now - sheet.Drips[0].At > DripSeconds) sheet.Drips.RemoveAt(0);
            int start = Mathf.Max(0, sheet.Drips.Count - DripSlots);
            for (int i = 0; i < DripSlots; i++)
            {
                int idx = start + i;
                var l = _drips[i];
                if (idx >= sheet.Drips.Count) { l.Visible = false; continue; }
                var d = sheet.Drips[idx];
                string name = defs.Skills?.Find(s => s.Id == d.Skill)?.Name ?? d.Skill;
                l.Text = $"+{d.XP} {name}";
                l.Visible = true;
                l.Modulate = new Color(1, 1, 1, 1f - Mathf.Clamp((now - d.At) / DripSeconds, 0f, 1f));
            }

            bool shout = now - sheet.LevelUpAt < BannerSeconds;
            _bannerBox.Visible = shout;
            if (shout)
            {
                string name = defs.Skills?.Find(s => s.Id == sheet.LevelUpSkill)?.Name ?? sheet.LevelUpSkill;
                _bannerSub.Text = $"{name.ToUpperInvariant()}  {sheet.LevelUpLevel}";
            }
        }
    }
}
