// The permanent HUD cluster in Scrapyard Comic (GDD "UI style guide":
// health bottom-left with shield above, ammo/credits bottom-right, the
// compass strip top-center), plus crosshair, event log, F3 debug overlay,
// and health bars over the wounded.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class HudView
    {
        private readonly Control _root;
        private readonly ColorRect _healthFill;
        private readonly ColorRect _shieldFill;
        private readonly Label _healthNum;
        private readonly Label _ammoMag;
        private readonly Label _ammoReserve;
        private readonly Label _credits;
        private readonly Panel _compass;
        private const float CompassWidth = 440f;
        private readonly Label _flight;
        private readonly Label _banner;
        private readonly List<Label> _markers = new List<Label>();
        private readonly Label[] _log;
        private readonly Label _debug;
        private readonly PanelContainer _debugPanel;
        private readonly Control _healthBarLayer;
        private readonly ColorRect _death;
        private readonly Label _deathLine;
        private readonly Dictionary<uint, (Panel box, ColorRect fill)> _healthBars = new();
        private readonly List<uint> _staleBars = new();

        public HudView(Control root)
        {
            _root = root;

            // ---- bottom-left: health + shield --------------------------------
            var vitals = Styles.Panel(Styles.SkewLeft);
            vitals.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.Pin(vitals, Control.LayoutPreset.BottomLeft, 24, 24, 240);
            var vbody = Styles.Body(vitals);
            vbody.AddThemeConstantOverride("separation", 4);
            var (shieldBar, shieldFill) = Styles.Bar(Styles.Shield, 8);
            _shieldFill = shieldFill;
            vbody.AddChild(shieldBar);
            var (healthBar, healthFill) = Styles.Bar(Styles.Danger, 20);
            _healthFill = healthFill;
            _healthNum = Styles.Display_("100", 16, Styles.Cream);
            _healthNum.Position = new Vector2(8, -2);
            healthBar.AddChild(_healthNum);
            vbody.AddChild(healthBar);
            root.AddChild(vitals);

            // ---- bottom-right: ammo + credits --------------------------------
            var supply = Styles.Panel(Styles.SkewRight);
            supply.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.Pin(supply, Control.LayoutPreset.BottomRight, 24, 24);
            var sbody = Styles.Body(supply);
            _credits = Styles.Display_("0 CR", 16, Styles.Amber);
            _credits.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            sbody.AddChild(_credits);
            var ammoRow = Styles.Row(6);
            ammoRow.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            _ammoMag = Styles.Display_("--", 30, Styles.Cream);
            _ammoMag.VerticalAlignment = VerticalAlignment.Bottom;
            _ammoReserve = Styles.Display_("/ --", 16, Styles.Dust);
            _ammoReserve.VerticalAlignment = VerticalAlignment.Bottom;
            ammoRow.AddChild(_ammoMag);
            ammoRow.AddChild(_ammoReserve);
            sbody.AddChild(ammoRow);
            root.AddChild(supply);

            // ---- top-center: the compass strip -------------------------------
            var bg = Styles.Slate; bg.A = Styles.PanelOpacity;
            _compass = Styles.Box(bg, Styles.Ink, 3);
            _compass.CustomMinimumSize = new Vector2(CompassWidth, 30);
            _compass.ClipContents = true;
            Styles.Pin(_compass, Control.LayoutPreset.CenterTop, 0, 16, CompassWidth);
            // Center tick: where you look.
            _compass.AddChild(new ColorRect
            {
                Color = Styles.Amber, Position = new Vector2(CompassWidth / 2 - 1, 0),
                Size = new Vector2(2, 30), MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            root.AddChild(_compass);

            // ---- flight readout, under the compass while seated --------------
            _flight = Styles.Display_("", 14, Styles.Cream);
            Styles.Pin(_flight, Control.LayoutPreset.CenterTop, 0, 52);
            _flight.Visible = false;
            root.AddChild(_flight);

            // ---- mission/party banner, below the flight line -----------------
            _banner = Styles.Display_("", 15, Styles.Amber);
            Styles.Pin(_banner, Control.LayoutPreset.CenterTop, 0, 76);
            _banner.Visible = false;
            root.AddChild(_banner);

            // ---- crosshair ---------------------------------------------------
            foreach (var (w, h) in new[] { (2f, 12f), (12f, 2f) })
            {
                var bar = new ColorRect { Color = Styles.Cream, MouseFilter = Control.MouseFilterEnum.Ignore };
                bar.AnchorLeft = bar.AnchorRight = 0.5f;
                bar.AnchorTop = bar.AnchorBottom = 0.5f;
                bar.OffsetLeft = -w / 2; bar.OffsetRight = w / 2;
                bar.OffsetTop = -h / 2; bar.OffsetBottom = h / 2;
                root.AddChild(bar);
            }

            // ---- Phase 12 channel bar, under the crosshair -------------------
            _channel = Styles.Progress(0f, Styles.Amber, "", 160f);
            Styles.Pin(_channel, Control.LayoutPreset.Center, 0, 34);
            _channel.Visible = false;
            root.AddChild(_channel);

            // ---- health bars over the wounded (screen-projected) -------------
            _healthBarLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _healthBarLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(_healthBarLayer);

            // ---- event log, top-left -----------------------------------------
            var logBox = Styles.Column(0);
            logBox.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.Pin(logBox, Control.LayoutPreset.TopLeft, 16, 160);
            _log = new Label[6];
            for (int i = 0; i < _log.Length; i++)
            {
                _log[i] = Styles.Display_("", 12, Styles.Dust);
                logBox.AddChild(_log[i]);
            }
            root.AddChild(logBox);

            // ---- death overlay: the whole screen, over everything ------------
            _death = new ColorRect { Color = new Color(0.30f, 0.02f, 0.02f, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
            _death.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            var deathBox = Styles.Column(6);
            deathBox.MouseFilter = Control.MouseFilterEnum.Ignore;
            deathBox.SetAnchorsPreset(Control.LayoutPreset.Center);
            deathBox.GrowHorizontal = Control.GrowDirection.Both;
            deathBox.GrowVertical = Control.GrowDirection.Both;
            var deathTitle = Styles.Display_("YOU DIED", 48, Styles.Cream);
            deathTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _deathLine = Styles.Display_("", 20, Styles.Amber);
            _deathLine.HorizontalAlignment = HorizontalAlignment.Center;
            deathBox.AddChild(deathTitle);
            deathBox.AddChild(_deathLine);
            _death.AddChild(deathBox);
            root.AddChild(_death);

            // ---- F3 debug overlay --------------------------------------------
            _debugPanel = Styles.Panel(Styles.SkewNone);
            _debugPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.Pin(_debugPanel, Control.LayoutPreset.TopLeft, 16, 16);
            _debugPanel.Visible = false;
            _debug = Styles.Display_("", 12, Styles.Cream);
            Styles.Body(_debugPanel).AddChild(_debug);
            root.AddChild(_debugPanel);
        }

        /// <summary>The last few event lines; extra slots go blank.</summary>
        public void SetLog(IReadOnlyList<string> lines)
        {
            for (int i = 0; i < _log.Length; i++)
                _log[i].Text = i < lines.Count ? lines[i] : "";
        }

        /// <summary>F3 diagnostics; null hides the panel.</summary>
        public void SetDebug(string text)
        {
            _debugPanel.Visible = !string.IsNullOrEmpty(text);
            _debug.Text = text ?? "";
        }

        private Control _channel;

        /// <summary>
        /// Phase 12: the gather channel. frac in [0,1] fills the bar with the
        /// verb over it; a negative frac hides it.
        /// </summary>
        public void SetChannel(float frac, string label)
        {
            if (_channel == null) return;
            _channel.Visible = frac >= 0f;
            if (frac < 0f) return;
            foreach (Node n in _channel.GetChildren())
            {
                if (n is ColorRect fill) Styles.SetFill(fill, Mathf.Clamp(frac, 0f, 1f));
                if (n is Label l) l.Text = label;
            }
        }

        /// <summary>
        /// Screen-projected health bars above the wounded, pooled by entity id.
        /// </summary>
        public void UpdateHealthBars(Camera3D cam, EntityViews views, bool visible)
        {
            _staleBars.Clear();
            foreach (var id in _healthBars.Keys) _staleBars.Add(id);

            if (cam != null && views != null && visible)
            {
                foreach (EntityView v in views.All)
                {
                    if (!v.ShowHealthBar || v.Root == null) continue;
                    Vector3 world = v.Root.GlobalPosition;
                    Vector3 above = world + world.Normalized() * 2.05f;
                    if (cam.IsPositionBehind(above)) continue;
                    Vector2 screen = cam.UnprojectPosition(above);
                    float depth = cam.GlobalPosition.DistanceTo(above);

                    // Sized by eye on a 1280x720 shot: 900/26/90 and 5 px tall read as a
                    // hairline at 9 m and vanished at 13 m.
                    float width = Mathf.Clamp(1400f / depth, 40f, 120f);
                    const float height = 8f;

                    if (!_healthBars.TryGetValue(v.Id, out var bar))
                    {
                        var box = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
                        // A hairline border: a black box on the space sky was invisible, so a
                        // low bar read as a floating sliver of fill.
                        var style = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.65f), BorderColor = new Color(1f, 1f, 1f, 0.35f) };
                        style.SetBorderWidthAll(1);
                        box.AddThemeStyleboxOverride("panel", style);
                        var fill = new ColorRect { Position = new Vector2(1, 1), MouseFilter = Control.MouseFilterEnum.Ignore };
                        box.AddChild(fill);
                        _healthBarLayer.AddChild(box);
                        bar = (box, fill);
                        _healthBars[v.Id] = bar;
                    }
                    _staleBars.Remove(v.Id);

                    bar.box.Position = new Vector2(screen.X - width * 0.5f, screen.Y - 1);
                    bar.box.Size = new Vector2(width + 2, height + 2);
                    float frac = v.HealthFraction;
                    bar.fill.Size = new Vector2(width * frac, height);
                    bar.fill.Color = new Color(0.85f, 0.15f, 0.12f).Lerp(new Color(0.30f, 0.85f, 0.30f), frac);
                }
            }

            foreach (uint id in _staleBars)
            {
                _healthBars[id].box.QueueFree();
                _healthBars.Remove(id);
            }
        }

        /// <summary>The death screen; negative hides it, otherwise seconds to respawn.</summary>
        public void SetDeath(double respawnIn)
        {
            _death.Visible = respawnIn >= 0;
            if (respawnIn >= 0) _deathLine.Text = respawnIn > 0.05 ? $"RESPAWNING IN {System.Math.Ceiling(respawnIn):0}" : "RESPAWNING…";
        }

        public void SetVitals(int health, int maxHealth)
        {
            float f = maxHealth > 0 ? Mathf.Clamp((float)health / maxHealth, 0f, 1f) : 0f;
            Styles.SetFill(_healthFill, f);
            _healthNum.Text = health.ToString();
            // No shield stat exists yet; the bar reads full as a placeholder
            // and becomes real when a shield item does.
            Styles.SetFill(_shieldFill, 1f);
        }

        public void SetAmmo(int mag, int reserve, bool armed)
        {
            _ammoMag.Text = armed ? mag.ToString() : "--";
            _ammoReserve.Text = armed ? $"/ {reserve}" : "";
        }

        /// <summary>Mission/party banner under the compass; null hides it.</summary>
        public void SetBanner(string text, bool priority)
        {
            _banner.Visible = !string.IsNullOrEmpty(text);
            _banner.Text = text ?? "";
            _banner.AddThemeColorOverride("font_color", priority ? Styles.Amber : Styles.Good);
        }

        /// <summary>Flight readout; null hides it (on foot).</summary>
        public void SetFlight(string line)
        {
            _flight.Visible = !string.IsNullOrEmpty(line);
            _flight.Text = line ?? "";
        }

        public void SetCredits(long credits) =>
            _credits.Text = credits < 0 ? "— CR" : $"{credits} CR"; // −1 = not fetched yet

        /// <summary>
        /// Rebuilds the marker set (cheap at our marker count) and lays each
        /// out by bearing: the strip spans ±90°, markers past that clamp to
        /// the edges so "behind you" still shows AT an edge.
        /// </summary>
        public void SetMarkers(IEnumerable<(string name, double bearingRad)> markers)
        {
            foreach (Label l in _markers) l.QueueFree();
            _markers.Clear();
            foreach (var (name, bearing) in markers)
            {
                if (double.IsNaN(bearing)) continue;
                double half = System.Math.PI / 2;
                double x = System.Math.Clamp(bearing / half, -1, 1); // −1..1 across the strip
                var l = Styles.Display_(name, 12, System.Math.Abs(bearing) > half ? Styles.Dust : Styles.Cream);
                l.Position = new Vector2((float)(CompassWidth * (0.5 + x * 0.48)) - 14, 4);
                _compass.AddChild(l);
                _markers.Add(l);
            }
        }
    }
}
