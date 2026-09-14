// Phase 11 UI: the skills panel (K) and the XP feed. Ten rows — name,
// level, progress bar to next, the multiplier the level buys — reserved
// rows greyed, synergy arrows drawn under their source. The feed drips
// awards at the right edge and shouts LEVEL UP across the middle.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    public sealed class SkillsView : ModalView
    {
        private readonly SkillSheet _sheet;
        private readonly Character _character;

        public SkillsView(VisualElement root, SkillSheet sheet, Character character)
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

        protected override void Fill(VisualElement body)
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

                var row = new VisualElement();
                row.style.marginBottom = 1;

                // One line: name, what the level buys (dust), the level. Ten
                // rows have to fit a 720p frame with the arrows under them.
                string tip = reserved ? "Phase 12 — the artisan loop"
                    : EfficacyText(sk.Efficacy?.Kind ?? "", _sheet.EfficacyBonus(defs, sk.Id));
                var head = new VisualElement();
                head.style.flexDirection = FlexDirection.Row;
                head.style.alignItems = Align.Center;
                head.Add(Styles.Display_(sk.Name, 14, ink));
                var t = Styles.Display_(tip, 11, Styles.Dust);
                t.style.marginLeft = 10;
                t.style.flexGrow = 1;
                head.Add(t);
                head.Add(Styles.Display_($"LV {level}", 14, reserved ? Styles.Dust : Styles.Amber));
                row.Add(head);

                var (bar, fill) = Styles.Bar(reserved ? Styles.Steel : Styles.Shield, 8);
                float frac = level >= SkillCurve.MaxLevel ? 1f
                    : next > floor ? Mathf.Clamp01((float)(xp - floor) / (next - floor)) : 0f;
                fill.style.width = Length.Percent(frac * 100f);
                if (!reserved)
                {
                    var num = Styles.Display_(level >= SkillCurve.MaxLevel ? "99" : $"{xp - floor} / {next - floor}", 9, Styles.Cream);
                    num.style.position = Position.Absolute;
                    num.style.right = 4;
                    num.style.top = -4;
                    bar.Add(num);
                }
                row.Add(bar);

                // Synergy arrows: a bar and a chevron in Good under the
                // SOURCE row, pointing at the partner it feeds.
                if (defs.Synergies != null)
                {
                    foreach (var sy in defs.Synergies)
                    {
                        if (sy.Source != sk.Id) continue;
                        var arrow = new VisualElement();
                        arrow.style.flexDirection = FlexDirection.Row;
                        arrow.style.alignItems = Align.Center;
                        arrow.style.marginLeft = 10;
                        arrow.style.marginTop = 0;
                        var shaft = new VisualElement();
                        shaft.style.width = 16;
                        shaft.style.height = 2;
                        shaft.style.backgroundColor = Styles.Good;
                        arrow.Add(shaft);
                        var tipEl = new VisualElement();
                        tipEl.style.width = 6;
                        tipEl.style.height = 6;
                        tipEl.style.marginLeft = -4;
                        tipEl.style.borderTopWidth = 2;
                        tipEl.style.borderRightWidth = 2;
                        tipEl.style.borderTopColor = Styles.Good;
                        tipEl.style.borderRightColor = Styles.Good;
                        tipEl.style.rotate = new Rotate(new Angle(45, AngleUnit.Degree));
                        arrow.Add(tipEl);
                        var text = Styles.Display_(
                            $"{NameOf(sy.Target)}  {SynergyText(sy, _sheet.SynergyBonus(sy))}", 11, Styles.Good);
                        text.style.marginLeft = 8;
                        arrow.Add(text);
                        row.Add(arrow);
                    }
                }
                body.Add(row);
            }

            Line(body, $"{_sheet.Discovered.Count} POIs discovered  ·  K closes", Styles.Dust, 12)
                .style.marginTop = 4;
        }
    }

    /// <summary>XP drip at the right edge; LEVEL UP banner across the middle.</summary>
    public sealed class SkillsFeed
    {
        private const float DripSeconds = 2.2f;
        private const float BannerSeconds = 3.0f;
        private const int DripSlots = 5;

        private readonly VisualElement _column;
        private readonly Label[] _drips = new Label[DripSlots];
        private readonly VisualElement _bannerBox;
        private readonly Label _banner;
        private readonly Label _bannerSub;

        public SkillsFeed(VisualElement root)
        {
            _column = new VisualElement();
            _column.style.position = Position.Absolute;
            _column.style.right = 28;
            _column.style.bottom = 120; // above the ammo/credits box
            _column.style.alignItems = Align.FlexEnd;
            _column.pickingMode = PickingMode.Ignore;
            for (int i = 0; i < DripSlots; i++)
            {
                var l = Styles.Display_("", 14, Styles.Shield);
                l.style.display = DisplayStyle.None;
                _column.Add(l);
                _drips[i] = l;
            }
            root.Add(_column);

            _bannerBox = Styles.Panel(Styles.SkewRight);
            _bannerBox.style.position = Position.Absolute;
            _bannerBox.style.top = Length.Percent(26);
            _bannerBox.style.left = Length.Percent(50);
            _bannerBox.style.translate = new Translate(Length.Percent(-50), 0);
            _bannerBox.style.paddingTop = 6;
            _bannerBox.style.paddingBottom = 8;
            _bannerBox.style.alignItems = Align.Center;
            _bannerBox.style.display = DisplayStyle.None;
            _bannerBox.pickingMode = PickingMode.Ignore;
            _banner = Styles.Display_("LEVEL UP", 28, Styles.Amber);
            _banner.style.letterSpacing = new Length(2f, LengthUnit.Pixel);
            _bannerSub = Styles.Display_("", 15, Styles.Cream);
            _bannerBox.Add(_banner);
            _bannerBox.Add(_bannerSub);
            root.Add(_bannerBox);
        }

        public void Tick(SkillSheet sheet, Defs defs, float now)
        {
            while (sheet.Drips.Count > 0 && now - sheet.Drips[0].At > DripSeconds) sheet.Drips.RemoveAt(0);
            int start = Mathf.Max(0, sheet.Drips.Count - DripSlots);
            for (int i = 0; i < DripSlots; i++)
            {
                int idx = start + i;
                var l = _drips[i];
                if (idx >= sheet.Drips.Count) { l.style.display = DisplayStyle.None; continue; }
                var d = sheet.Drips[idx];
                string name = defs.Skills?.Find(s => s.Id == d.Skill)?.Name ?? d.Skill;
                l.text = $"+{d.XP} {name}";
                l.style.display = DisplayStyle.Flex;
                l.style.opacity = 1f - Mathf.Clamp01((now - d.At) / DripSeconds);
            }

            bool shout = now - sheet.LevelUpAt < BannerSeconds;
            _bannerBox.style.display = shout ? DisplayStyle.Flex : DisplayStyle.None;
            if (shout)
            {
                string name = defs.Skills?.Find(s => s.Id == sheet.LevelUpSkill)?.Name ?? sheet.LevelUpSkill;
                _bannerSub.text = $"{name.ToUpperInvariant()}  {sheet.LevelUpLevel}";
            }
        }
    }
}
