// Phase 8 task 5 — combat feedback (GDD "UI style guide", "Combat
// feedback"): damage numbers at the hit's world point rising 0.8 m over
// 0.6 s, amber for kills; a 120 ms four-tick hit marker at the reticle
// for YOUR landed shots; a 500 ms danger arc at the screen edge in the
// attacker's direction when the hit is on YOU. The single loudest
// Borderlands signature, and the whole reason shots need to feel landed.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game.UI
{
    public sealed class CombatFeed
    {
        private const float NumberSeconds = 0.6f;
        private const float NumberRiseMeters = 0.8f;
        private const float MarkerSeconds = 0.12f;
        private const float IncomingSeconds = 0.5f;

        private sealed class Popup
        {
            public Label Label;
            public Vector3 WorldPos;
            public float BornAt;
        }

        private readonly VisualElement _root;
        private readonly List<Popup> _popups = new();
        private readonly VisualElement[] _markerTicks = new VisualElement[4];
        private readonly VisualElement[] _incoming = new VisualElement[8];
        private float _markerUntil;
        private bool _markerKill;
        private readonly float[] _incomingUntil = new float[8];

        public CombatFeed(VisualElement root)
        {
            _root = root;

            // The four hit-marker ticks around screen center, hidden until a
            // shot lands. Diagonal offsets; rotation gives the X shape.
            for (int i = 0; i < 4; i++)
            {
                var t = new VisualElement();
                t.style.position = Position.Absolute;
                t.style.width = 12;
                t.style.height = 3;
                t.style.backgroundColor = Styles.Cream;
                t.style.rotate = new Rotate(new Angle(i % 2 == 0 ? 45 : -45, AngleUnit.Degree));
                t.style.display = DisplayStyle.None;
                t.pickingMode = PickingMode.Ignore;
                _root.Add(t);
                _markerTicks[i] = t;
            }

            // Eight incoming-damage sectors hugging the screen edge.
            for (int i = 0; i < 8; i++)
            {
                var arc = new VisualElement();
                arc.style.position = Position.Absolute;
                arc.style.width = 90;
                arc.style.height = 8;
                arc.style.backgroundColor = Styles.Danger;
                arc.style.display = DisplayStyle.None;
                arc.pickingMode = PickingMode.Ignore;
                _root.Add(arc);
                _incoming[i] = arc;
            }
        }

        /// <summary>A landed hit somewhere in the world: pop the number.</summary>
        public void Damage(Vector3 worldPoint, int amount, bool kill)
        {
            var l = Styles.Display_(amount.ToString(), kill ? 26 : 18,
                kill ? Styles.Amber : Styles.Cream);
            l.style.position = Position.Absolute;
            l.pickingMode = PickingMode.Ignore;
            // Volley legibility: stagger siblings horizontally a touch.
            l.style.marginLeft = (_popups.Count % 3 - 1) * 14;
            _root.Add(l);
            _popups.Add(new Popup { Label = l, WorldPos = worldPoint, BornAt = Time.time });
        }

        /// <summary>Your shot landed (kill turns the marker amber).</summary>
        public void HitMarker(bool kill)
        {
            _markerUntil = Time.time + MarkerSeconds;
            _markerKill = kill;
        }

        /// <summary>You were hit from `bearingRad` (egocentric, + = right).</summary>
        public void Incoming(double bearingRad)
        {
            if (double.IsNaN(bearingRad)) return;
            // Eight sectors: 0 = ahead, going clockwise (right first).
            int sector = (int)System.Math.Round(bearingRad / (System.Math.PI / 4));
            sector = ((sector % 8) + 8) % 8;
            _incomingUntil[sector] = Time.time + IncomingSeconds;
        }

        /// <summary>Per-frame: project, drift, fade, expire.</summary>
        public void Tick(Camera cam)
        {
            float now = Time.time;

            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                var p = _popups[i];
                float age = now - p.BornAt;
                if (age > NumberSeconds)
                {
                    _root.Remove(p.Label);
                    _popups.RemoveAt(i);
                    continue;
                }
                float k = age / NumberSeconds;
                Vector3 world = p.WorldPos + p.WorldPos.normalized * (NumberRiseMeters * k);
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z < 0) { p.Label.style.display = DisplayStyle.None; continue; }
                p.Label.style.display = DisplayStyle.Flex;
                p.Label.style.left = sp.x;
                p.Label.style.top = Screen.height - sp.y;
                p.Label.style.opacity = 1f - k * k;
            }

            // Hit marker: four ticks in an X, gap in the middle.
            bool marker = now < _markerUntil;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                var t = _markerTicks[i];
                t.style.display = marker ? DisplayStyle.Flex : DisplayStyle.None;
                if (!marker) continue;
                t.style.backgroundColor = _markerKill ? Styles.Amber : Styles.Cream;
                float dx = (i % 2 == 0 ? 1 : -1) * 11;
                float dy = (i < 2 ? -1 : 1) * 11;
                t.style.left = cx + dx - 6 + (i >= 2 ? 0 : 0);
                t.style.top = cy + dy - 1.5f;
            }

            // Incoming arcs: sector 0 top-center, clockwise.
            for (int i = 0; i < 8; i++)
            {
                var arc = _incoming[i];
                bool on = now < _incomingUntil[i];
                arc.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                if (!on) continue;
                double ang = i * System.Math.PI / 4; // 0 ahead, + clockwise
                float px = cx + (float)System.Math.Sin(ang) * (cx - 70);
                float py = cy - (float)System.Math.Cos(ang) * (cy - 40);
                arc.style.left = px - 45;
                arc.style.top = py - 4;
                arc.style.rotate = new Rotate(new Angle((float)(ang * Mathf.Rad2Deg), AngleUnit.Degree));
            }
        }
    }
}
