// Phase 8 task 4 — the permanent HUD cluster in Scrapyard Comic (GDD "UI
// style guide": health bottom-left with shield above, ammo/credits
// bottom-right, the compass strip top-center), plus what the IMGUI HUD used
// to carry: crosshair, event log, F3 debug overlay, and health bars over the
// wounded. IMGUI is fully retired.

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
        private readonly Label _banner;
        private readonly List<(Label label, double bearing)> _markers = new();
        private readonly Label[] _log;
        private readonly VisualElement _logBox;
        private readonly Label _debug;
        private readonly VisualElement _healthBarLayer;
        private readonly Dictionary<uint, (VisualElement box, VisualElement fill)> _healthBars = new();
        private readonly List<uint> _staleBars = new();

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

            // ---- mission/party banner, below the flight line -----------------
            _banner = Styles.Display_("", 15, Styles.Amber);
            _banner.style.position = Position.Absolute;
            _banner.style.top = 76;
            _banner.style.left = Length.Percent(50);
            _banner.style.translate = new Translate(Length.Percent(-50), 0);
            _banner.style.display = DisplayStyle.None;
            root.Add(_banner);

            // ---- crosshair ---------------------------------------------------
            foreach (var (w, h) in new[] { (2f, 12f), (12f, 2f) })
            {
                var bar = new VisualElement();
                bar.style.position = Position.Absolute;
                bar.style.left = Length.Percent(50);
                bar.style.top = Length.Percent(50);
                bar.style.marginLeft = -w / 2;
                bar.style.marginTop = -h / 2;
                bar.style.width = w;
                bar.style.height = h;
                bar.style.backgroundColor = Styles.Cream;
                bar.pickingMode = PickingMode.Ignore;
                root.Add(bar);
            }

            // ---- health bars over the wounded (screen-projected) -------------
            _healthBarLayer = new VisualElement();
            _healthBarLayer.style.position = Position.Absolute;
            _healthBarLayer.style.left = 0;
            _healthBarLayer.style.top = 0;
            _healthBarLayer.style.right = 0;
            _healthBarLayer.style.bottom = 0;
            _healthBarLayer.pickingMode = PickingMode.Ignore;
            root.Add(_healthBarLayer);

            // ---- event log, top-left -----------------------------------------
            _logBox = new VisualElement();
            _logBox.style.position = Position.Absolute;
            _logBox.style.left = 16;
            _logBox.style.top = 160;
            _logBox.pickingMode = PickingMode.Ignore;
            _log = new Label[6];
            for (int i = 0; i < _log.Length; i++)
            {
                _log[i] = Styles.Display_("", 12, Styles.Dust);
                _logBox.Add(_log[i]);
            }
            root.Add(_logBox);

            // ---- F3 debug overlay --------------------------------------------
            var dbg = Styles.Panel(Styles.SkewNone);
            dbg.style.position = Position.Absolute;
            dbg.style.left = 16;
            dbg.style.top = 16;
            dbg.style.display = DisplayStyle.None;
            _debug = Styles.Display_("", 12, Styles.Cream);
            _debug.style.whiteSpace = WhiteSpace.Pre;
            dbg.Add(_debug);
            _debugPanel = dbg;
            root.Add(dbg);
        }

        private readonly VisualElement _debugPanel;

        /// <summary>The last few event lines; extra slots go blank.</summary>
        public void SetLog(IReadOnlyList<string> lines)
        {
            for (int i = 0; i < _log.Length; i++)
                _log[i].text = i < lines.Count ? lines[i] : "";
        }

        /// <summary>F3 diagnostics; null hides the panel.</summary>
        public void SetDebug(string text)
        {
            _debugPanel.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            _debug.text = text ?? "";
        }

        /// <summary>
        /// Screen-projected health bars above the wounded, pooled by entity id.
        /// Same placement math the IMGUI version used; UI Toolkit is y-down
        /// from the top, so the projected y flips.
        /// </summary>
        public void UpdateHealthBars(Camera cam, EntityViews views, bool visible)
        {
            _staleBars.Clear();
            foreach (var id in _healthBars.Keys) _staleBars.Add(id);

            if (cam != null && views != null && visible)
            {
                foreach (EntityView v in views.All)
                {
                    if (!v.ShowHealthBar || v.Root == null) continue;
                    Vector3 world = v.Root.transform.position;
                    Vector3 above = world + world.normalized * 2.05f;
                    Vector3 screen = cam.WorldToScreenPoint(above);
                    if (screen.z <= 0f) continue;

                    float width = Mathf.Clamp(900f / screen.z, 26f, 90f);
                    const float height = 5f;

                    if (!_healthBars.TryGetValue(v.Id, out var bar))
                    {
                        var box = new VisualElement();
                        box.style.position = Position.Absolute;
                        box.style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
                        box.pickingMode = PickingMode.Ignore;
                        var fill = new VisualElement();
                        fill.style.position = Position.Absolute;
                        fill.style.left = 1;
                        fill.style.top = 1;
                        fill.style.bottom = 1;
                        box.Add(fill);
                        _healthBarLayer.Add(box);
                        bar = (box, fill);
                        _healthBars[v.Id] = bar;
                    }
                    _staleBars.Remove(v.Id);

                    bar.box.style.left = screen.x - width * 0.5f;
                    bar.box.style.top = Screen.height - screen.y - 1;
                    bar.box.style.width = width + 2;
                    bar.box.style.height = height + 2;
                    float frac = v.HealthFraction;
                    bar.fill.style.width = width * frac;
                    bar.fill.style.backgroundColor = Color.Lerp(
                        new Color(0.85f, 0.15f, 0.12f), new Color(0.30f, 0.85f, 0.30f), frac);
                }
            }

            foreach (uint id in _staleBars)
            {
                _healthBarLayer.Remove(_healthBars[id].box);
                _healthBars.Remove(id);
            }
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

        /// <summary>Mission/party banner under the compass; null hides it.</summary>
        public void SetBanner(string text, bool priority)
        {
            _banner.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            _banner.text = text ?? "";
            _banner.style.color = priority ? Styles.Amber : Styles.Good;
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
