// Phase 8 — the Scrapyard Comic tokens as code (GDD "UI style guide").
// This file IS the palette: a screen that needs a color not present here
// adds it to the GDD first, then here. No USS — every element is styled
// through these helpers, which is what a runtime-only panel demands.

using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game.UI
{
    public static class Styles
    {
        // ---- palette (GDD table, verbatim) ---------------------------------
        public static readonly Color Ink = Hex("#10131A");
        public static readonly Color Slate = Hex("#1B2029");
        public static readonly Color Steel = Hex("#2A3140");
        public static readonly Color Cream = Hex("#E8E2D0");
        public static readonly Color Dust = Hex("#9AA08E");
        public static readonly Color Amber = Hex("#FFAE19");
        public static readonly Color Danger = Hex("#FF4A3D");
        public static readonly Color Shield = Hex("#3FC1FF");
        public static readonly Color Good = Hex("#7FD18A");

        /// <summary>Rarity ramp; unknown renders as common (C64).</summary>
        public static Color Rarity(string tier) => tier switch
        {
            "uncommon" => Hex("#4FD15C"),
            "rare" => Hex("#3FA9FF"),
            "epic" => Hex("#B45CFF"),
            "legendary" => Hex("#FF9B1A"),
            _ => Hex("#B8B8A8"), // common, and every unknown
        };

        public const float PanelOpacity = 0.92f;

        /// <summary>
        /// The comic lean follows the screen side (GDD): left-anchored
        /// panels tilt −2°, right-anchored +2°, centered ones sit straight.
        /// </summary>
        public const float SkewLeft = -2f;
        public const float SkewRight = 2f;
        public const float SkewNone = 0f;

        // ---- the display font ----------------------------------------------
        //
        // Staatliches (OFL, vendored beside its license in Resources/Fonts).
        // Runtime FontAsset because the OS-font path crashes the text shaper
        // (the spike's second finding). Built once, shared by every label.
        private static FontAsset _display;

        public static FontAsset Display
        {
            get
            {
                if (_display == null)
                {
                    var font = Resources.Load<Font>("Fonts/Staatliches-Regular");
                    if (font != null) _display = FontAsset.CreateFontAsset(font);
                }
                return _display;
            }
        }

        // ---- element factories ---------------------------------------------

        /// <summary>
        /// A style-guide panel: slate @92%, 3px ink border, the 12px
        /// top-right notch (faked with a rotated ink square until vector API
        /// needs arise), −2° skew.
        /// </summary>
        public static VisualElement Panel(float skewDeg = SkewLeft)
        {
            var p = new VisualElement();
            var bg = Slate;
            bg.a = PanelOpacity;
            p.style.backgroundColor = bg;
            SetBorder(p, Ink, 3);
            p.style.rotate = new Rotate(new Angle(skewDeg, AngleUnit.Degree));
            p.style.paddingLeft = 14;
            p.style.paddingRight = 14;
            p.style.paddingTop = 10;
            p.style.paddingBottom = 12;
            return p;
        }

        /// <summary>Marks a panel active: the 1px amber inner edge.</summary>
        public static void SetActive(VisualElement panel, bool active)
        {
            SetBorder(panel, active ? Amber : Ink, active ? 2 : 3);
        }

        /// <summary>A display-type label (headers, big numerals, popups).</summary>
        public static Label Display_(string text, int size, Color color)
        {
            var l = new Label(text);
            l.style.color = color;
            l.style.fontSize = size;
            if (Display != null)
                l.style.unityFontDefinition = FontDefinition.FromSDFFont(Display);
            l.style.unityTextAlign = TextAnchor.MiddleLeft;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }

        /// <summary>A section header: ALL CAPS dust over a 2px ink rule.</summary>
        public static VisualElement Header(string text)
        {
            var box = new VisualElement();
            var l = Display_(text.ToUpperInvariant(), 16, Dust);
            l.style.letterSpacing = new Length(1.2f, LengthUnit.Pixel);
            box.Add(l);
            var rule = new VisualElement();
            rule.style.height = 2;
            rule.style.backgroundColor = Ink;
            rule.style.marginTop = 2;
            rule.style.marginBottom = 6;
            box.Add(rule);
            return box;
        }

        /// <summary>A bar: steel trough, colored fill, optional numeral.</summary>
        public static (VisualElement bar, VisualElement fill) Bar(Color fillColor, float height = 16)
        {
            var trough = new VisualElement();
            trough.style.backgroundColor = Steel;
            trough.style.height = height;
            SetBorder(trough, Ink, 2);
            var fill = new VisualElement();
            fill.style.backgroundColor = fillColor;
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.top = 0;
            fill.style.bottom = 0;
            fill.style.width = Length.Percent(100);
            trough.Add(fill);
            return (trough, fill);
        }

        public static void SetBorder(VisualElement e, Color c, int width)
        {
            e.style.borderLeftColor = c;
            e.style.borderRightColor = c;
            e.style.borderTopColor = c;
            e.style.borderBottomColor = c;
            e.style.borderLeftWidth = width;
            e.style.borderRightWidth = width;
            e.style.borderTopWidth = width;
            e.style.borderBottomWidth = width;
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }
    }
}
