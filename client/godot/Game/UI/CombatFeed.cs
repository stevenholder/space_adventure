// Combat feedback (GDD "UI style guide", "Combat feedback"): damage numbers
// at the hit's world point rising 0.8 m over 0.6 s, amber for kills; a
// 120 ms four-tick hit marker at the reticle for YOUR landed shots; a 500 ms
// danger arc at the screen edge in the attacker's direction when the hit is
// on YOU. The single loudest Borderlands signature, and the whole reason
// shots need to feel landed.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class CombatFeed
    {
        private const double NumberSeconds = 0.6;
        private const float NumberRiseMeters = 0.8f;
        private const double MarkerSeconds = 0.12;
        private const double IncomingSeconds = 0.5;

        private sealed class Popup
        {
            public Label Label;
            public Vector3 WorldPos;
            public double BornAt;
            public float Stagger;
        }

        private readonly Control _root;
        private readonly List<Popup> _popups = new();
        private readonly ColorRect[] _markerTicks = new ColorRect[4];
        private readonly ColorRect[] _incoming = new ColorRect[8];
        private double _markerUntil;
        private bool _markerKill;
        private readonly double[] _incomingUntil = new double[8];

        public CombatFeed(Control root)
        {
            _root = root;

            // The four hit-marker ticks around screen center, hidden until a
            // shot lands. Diagonal offsets; rotation gives the X shape.
            for (int i = 0; i < 4; i++)
            {
                var t = new ColorRect
                {
                    Size = new Vector2(12, 3), Color = Styles.Cream, PivotOffset = new Vector2(6, 1.5f),
                    Rotation = Mathf.DegToRad(i % 2 == 0 ? 45 : -45), Visible = false,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                _root.AddChild(t);
                _markerTicks[i] = t;
            }

            // Eight incoming-damage sectors hugging the screen edge.
            for (int i = 0; i < 8; i++)
            {
                var arc = new ColorRect
                {
                    Size = new Vector2(90, 8), Color = Styles.Danger, PivotOffset = new Vector2(45, 4),
                    Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                _root.AddChild(arc);
                _incoming[i] = arc;
            }
        }

        /// <summary>A landed hit somewhere in the world: pop the number.</summary>
        public void Damage(Vector3 worldPoint, int amount, bool kill)
        {
            var l = Styles.Display_(amount.ToString(), kill ? 26 : 18, kill ? Styles.Amber : Styles.Cream);
            _root.AddChild(l);
            // Volley legibility: stagger siblings horizontally a touch.
            _popups.Add(new Popup { Label = l, WorldPos = worldPoint, BornAt = Clock.Now, Stagger = (_popups.Count % 3 - 1) * 14 });
        }

        /// <summary>Your shot landed (kill turns the marker amber).</summary>
        public void HitMarker(bool kill)
        {
            _markerUntil = Clock.Now + MarkerSeconds;
            _markerKill = kill;
        }

        /// <summary>You were hit from `bearingRad` (egocentric, + = right).</summary>
        public void Incoming(double bearingRad)
        {
            if (double.IsNaN(bearingRad)) return;
            // Eight sectors: 0 = ahead, going clockwise (right first).
            int sector = (int)System.Math.Round(bearingRad / (System.Math.PI / 4));
            sector = ((sector % 8) + 8) % 8;
            _incomingUntil[sector] = Clock.Now + IncomingSeconds;
        }

        /// <summary>Per-frame: project, drift, fade, expire.</summary>
        public void Tick(Camera3D cam)
        {
            double now = Clock.Now;
            Vector2 screen = _root.Size;

            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                var p = _popups[i];
                double age = now - p.BornAt;
                if (age > NumberSeconds)
                {
                    p.Label.QueueFree();
                    _popups.RemoveAt(i);
                    continue;
                }
                float k = (float)(age / NumberSeconds);
                Vector3 world = p.WorldPos + p.WorldPos.Normalized() * (NumberRiseMeters * k);
                if (cam.IsPositionBehind(world)) { p.Label.Visible = false; continue; }
                Vector2 sp = cam.UnprojectPosition(world);
                p.Label.Visible = true;
                p.Label.Position = new Vector2(sp.X + p.Stagger, sp.Y);
                p.Label.Modulate = new Color(1, 1, 1, 1f - k * k);
            }

            // Hit marker: four ticks in an X, gap in the middle.
            bool marker = now < _markerUntil;
            float cx = screen.X * 0.5f, cy = screen.Y * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                var t = _markerTicks[i];
                t.Visible = marker;
                if (!marker) continue;
                t.Color = _markerKill ? Styles.Amber : Styles.Cream;
                float dx = (i % 2 == 0 ? 1 : -1) * 11;
                float dy = (i < 2 ? -1 : 1) * 11;
                t.Position = new Vector2(cx + dx - 6, cy + dy - 1.5f);
            }

            // Incoming arcs: sector 0 top-center, clockwise.
            for (int i = 0; i < 8; i++)
            {
                var arc = _incoming[i];
                bool on = now < _incomingUntil[i];
                arc.Visible = on;
                if (!on) continue;
                double ang = i * System.Math.PI / 4; // 0 ahead, + clockwise
                float px = cx + (float)System.Math.Sin(ang) * (cx - 70);
                float py = cy - (float)System.Math.Cos(ang) * (cy - 40);
                arc.Position = new Vector2(px - 45, py - 4);
                arc.Rotation = (float)ang;
            }
        }
    }
}
