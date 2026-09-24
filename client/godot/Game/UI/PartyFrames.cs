// Party frames — the left-edge strip that says who is with you and how
// alive they are. Visible only while in a party; fed every frame from the
// same snapshots everything else renders from.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class PartyFrames
    {
        private readonly PanelContainer _panel;
        private readonly VBoxContainer _body;
        private readonly List<(Control row, Label name, ColorRect fill, Label num)> _rows = new();

        public PartyFrames(Control root)
        {
            _panel = Styles.Panel(Styles.SkewLeft);
            _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.PinAt(_panel, 0f, 0.32f, 170, Control.GrowDirection.End);
            _panel.OffsetLeft = 24; _panel.OffsetRight = 24 + 170;
            Styles.SetPadding(_panel, 14, 8, 14, 8);
            _panel.Visible = false;
            _body = Styles.Body(_panel);
            _body.AddChild(Styles.Header("Party"));
            root.AddChild(_panel);
        }

        /// <summary>
        /// One row per member: name over a slim health bar. A member whose
        /// entity has not reached us yet renders dimmed rather than dead.
        /// </summary>
        public void Update(IReadOnlyList<(string name, int health, int max, bool known, bool self)> members)
        {
            _panel.Visible = members.Count > 0;
            if (members.Count == 0) return;

            while (_rows.Count < members.Count)
            {
                var row = Styles.Column(2);
                var name = Styles.Display_("", 13, Styles.Cream);
                row.AddChild(name);
                var (trough, fill) = Styles.Bar(Styles.Danger, 9);
                var num = Styles.Display_("", 10, Styles.Cream);
                num.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                num.GrowHorizontal = Control.GrowDirection.Begin;
                num.OffsetRight = -4; num.OffsetTop = -3;
                trough.AddChild(num);
                row.AddChild(trough);
                row.AddChild(Styles.Gap(4));
                _body.AddChild(row);
                _rows.Add((row, name, fill, num));
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                var (row, name, fill, num) = _rows[i];
                row.Visible = i < members.Count;
                if (!row.Visible) continue;
                var m = members[i];
                name.Text = m.self ? $"{m.name} (you)" : m.name;
                name.AddThemeColorOverride("font_color", m.self ? Styles.Amber : Styles.Cream);
                if (!m.known)
                {
                    Styles.SetFill(fill, 1f);
                    fill.Color = Styles.Steel;
                    num.Text = "—";
                    continue;
                }
                float frac = m.max > 0 ? Mathf.Clamp((float)m.health / m.max, 0f, 1f) : 0f;
                Styles.SetFill(fill, frac);
                // Same ramp as the over-head bars: red empty, green full.
                fill.Color = new Color(0.85f, 0.15f, 0.12f).Lerp(new Color(0.30f, 0.85f, 0.30f), frac);
                num.Text = m.health.ToString();
            }
        }
    }
}
