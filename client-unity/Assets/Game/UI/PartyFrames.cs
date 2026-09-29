// Phase 10: party frames — the left-edge strip that says who is with you
// and how alive they are. Visible only while in a party; fed every frame
// from the same snapshots everything else renders from (no culling, so a
// member's health is always current wherever they stand).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game.UI
{
    public sealed class PartyFrames
    {
        private readonly VisualElement _panel;
        private readonly List<(VisualElement row, Label name, VisualElement fill, Label num)> _rows =
            new List<(VisualElement, Label, VisualElement, Label)>();

        public PartyFrames(VisualElement root)
        {
            _panel = Styles.Panel(Styles.SkewLeft);
            _panel.style.position = Position.Absolute;
            _panel.style.left = 24;
            _panel.style.top = Length.Percent(32);
            _panel.style.width = 170;
            _panel.style.paddingTop = 8;
            _panel.style.paddingBottom = 8;
            _panel.style.display = DisplayStyle.None;
            _panel.pickingMode = PickingMode.Ignore;

            var header = Styles.Header("Party");
            _panel.Add(header);
            root.Add(_panel);
        }

        /// <summary>
        /// One row per member: name over a slim health bar. `health` is
        /// (current, max, known) — a member whose entity has not reached us
        /// yet renders dimmed rather than dead.
        /// </summary>
        public void Update(IReadOnlyList<(string name, int health, int max, bool known, bool self)> members)
        {
            if (members.Count == 0)
            {
                _panel.style.display = DisplayStyle.None;
                return;
            }
            _panel.style.display = DisplayStyle.Flex;

            while (_rows.Count < members.Count)
            {
                var row = new VisualElement();
                row.style.marginBottom = 6;
                var name = Styles.Display_("", 13, Styles.Cream);
                row.Add(name);
                var (trough, fill) = Styles.Bar(Styles.Danger, 9);
                var num = Styles.Display_("", 10, Styles.Cream);
                num.style.position = Position.Absolute;
                num.style.right = 4;
                num.style.top = -3;
                trough.Add(num);
                row.Add(trough);
                _panel.Add(row);
                _rows.Add((row, name, fill, num));
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                var (row, name, fill, num) = _rows[i];
                if (i >= members.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                var m = members[i];
                name.text = m.self ? $"{m.name} (you)" : m.name;
                name.style.color = m.self ? Styles.Amber : Styles.Cream;
                if (!m.known)
                {
                    fill.style.width = Length.Percent(100);
                    fill.style.backgroundColor = Styles.Steel;
                    num.text = "—";
                    continue;
                }
                float frac = m.max > 0 ? Mathf.Clamp01((float)m.health / m.max) : 0f;
                fill.style.width = Length.Percent(frac * 100f);
                // Same ramp as the over-head bars: red empty, green full.
                fill.style.backgroundColor = Color.Lerp(
                    new Color(0.85f, 0.15f, 0.12f), new Color(0.30f, 0.85f, 0.30f), frac);
                num.text = m.health.ToString();
            }
        }
    }
}
