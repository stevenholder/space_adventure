// Phase 8 task 4 — the permanent HUD cluster in Scrapyard Comic (GDD "UI
// style guide": health bottom-left with shield above, ammo/credits
// bottom-right, the compass strip top-center). This is the first ported
// screen; the IMGUI HUD stays alive underneath until every screen moves,
// then dies in one commit.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game.UI
{
    public sealed class HudView
    {
        private readonly VisualElement _healthFill;
        private readonly VisualElement _shieldFill;
        private readonly Label _healthNum;
        private readonly Label _ammoMag;
        private readonly Label _ammoReserve;
        private readonly Label _credits;
        private readonly VisualElement _compass;
        private readonly Label _flight;
        private readonly List<(Label label, double bearing)> _markers = new();

        public HudView(VisualElement root)
        {
            // ---- bottom-left: health + shield --------------------------------
            var vitals = Styles.Panel(Styles.SkewLeft);
            vitals.style.position = Position.Absolute;
            vitals.style.left = 24;
            vitals.style.bottom = 24;
            vitals.style.width = 240;

            var (shieldBar, shieldFill) = Styles.Bar(Styles.Shield, 8);
            _shieldFill = shieldFill;
            shieldBar.style.marginBottom = 4;
            vitals.Add(shieldBar);

            var (healthBar, healthFill) = Styles.Bar(Styles.Danger, 20);
            _healthFill = healthFill;
            _healthNum = Styles.Display_("100", 16, Styles.Cream);
            _healthNum.style.position = Position.Absolute;
            _healthNum.style.left = 8;
            _healthNum.style.top = -2;
            healthBar.Add(_healthNum);
            vitals.Add(healthBar);
            root.Add(vitals);

            // ---- bottom-right: ammo + credits --------------------------------
            var supply = Styles.Panel(Styles.SkewRight);
            supply.style.position = Position.Absolute;
            supply.style.right = 24;
            supply.style.bottom = 24;
            supply.style.alignItems = Align.FlexEnd;

            _credits = Styles.Display_("0 CR", 16, Styles.Amber);
            supply.Add(_credits);

            var ammoRow = new VisualElement();
            ammoRow.style.flexDirection = FlexDirection.Row;
            ammoRow.style.alignItems = Align.FlexEnd;
            _ammoMag = Styles.Display_("--", 30, Styles.Cream);
            _ammoReserve = Styles.Display_("/ --", 16, Styles.Dust);
            _ammoReserve.style.marginBottom = 3;
            _ammoReserve.style.marginLeft = 6;
            ammoRow.Add(_ammoMag);
            ammoRow.Add(_ammoReserve);
            supply.Add(ammoRow);
            root.Add(supply);

            // ---- top-center: the compass strip -------------------------------
            _compass = Styles.Panel(Styles.SkewNone);
            _compass.style.position = Position.Absolute;
            _compass.style.top = 16;
            _compass.style.left = Length.Percent(50);
            _compass.style.marginLeft = -220;
            _compass.style.width = 440;
            _compass.style.height = 30;
            _compass.style.paddingTop = 2;
            _compass.style.paddingBottom = 2;
            _compass.style.overflow = Overflow.Hidden;
            // Center tick: where you look.
            var tick = new VisualElement();
            tick.style.position = Position.Absolute;
            tick.style.left = Length.Percent(50);
            tick.style.top = 0;
            tick.style.bottom = 0;
            tick.style.width = 2;
            tick.style.backgroundColor = Styles.Amber;
            _compass.Add(tick);
            root.Add(_compass);

            // ---- flight readout, under the compass while seated --------------
            _flight = Styles.Display_("", 14, Styles.Cream);
            _flight.style.position = Position.Absolute;
            _flight.style.top = 52;
            _flight.style.left = Length.Percent(50);
            _flight.style.translate = new Translate(Length.Percent(-50), 0);
            _flight.style.display = DisplayStyle.None;
            root.Add(_flight);
        }

        public void SetVitals(int health, int maxHealth)
        {
            float f = maxHealth > 0 ? Mathf.Clamp01((float)health / maxHealth) : 0f;
            _healthFill.style.width = Length.Percent(f * 100f);
            _healthNum.text = health.ToString();
            // No shield stat exists yet; the bar reads full as a placeholder
            // and becomes real when a shield item does.
            _shieldFill.style.width = Length.Percent(100);
        }

        public void SetAmmo(int mag, int reserve, bool armed)
        {
            _ammoMag.text = armed ? mag.ToString() : "--";
            _ammoReserve.text = armed ? $"/ {reserve}" : "";
        }

        /// <summary>Flight readout; null hides it (on foot).</summary>
        public void SetFlight(string line)
        {
            _flight.style.display = string.IsNullOrEmpty(line) ? DisplayStyle.None : DisplayStyle.Flex;
            _flight.text = line ?? "";
        }

        public void SetCredits(long credits) =>
            _credits.text = credits < 0 ? "— CR" : $"{credits} CR"; // −1 = not fetched yet

        /// <summary>
        /// Rebuilds the marker set (cheap at our marker count) and lays each
        /// out by bearing: the strip spans ±90°, markers past that clamp to
        /// the edges so "behind you" still shows AT an edge.
        /// </summary>
        public void SetMarkers(IEnumerable<(string name, double bearingRad)> markers)
        {
            foreach (var (label, _) in _markers) _compass.Remove(label);
            _markers.Clear();
            foreach (var (name, bearing) in markers)
            {
                if (double.IsNaN(bearing)) continue;
                var l = Styles.Display_(name, 12, Styles.Cream);
                l.style.position = Position.Absolute;
                l.style.top = 4;
                double half = System.Math.PI / 2;
                double x = System.Math.Clamp(bearing / half, -1, 1); // −1..1 across the strip
                l.style.left = Length.Percent((float)(50 + x * 48));
                l.style.marginLeft = -14;
                l.style.color = System.Math.Abs(bearing) > half ? Styles.Dust : Styles.Cream;
                _compass.Add(l);
                _markers.Add((l, bearing));
            }
        }
    }
}
