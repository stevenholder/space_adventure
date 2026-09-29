// The Scrapyard Comic tokens as code (GDD "UI style guide"), and the small
// Control toolkit every screen builds from. This file IS the palette: a
// screen that needs a colour not present here adds it to the GDD first, then
// here. No theme resource, no .tres — every element is styled through these
// helpers, which is what a code-only interface demands (C91).
//
// Layout vocabulary, since Godot's differs from a CSS-style box:
//   - Panel(skew) is a PanelContainer with a StyleBoxFlat; its one child is a
//     VBoxContainer (Body) that stacks whatever a view adds.
//   - Pin(...) anchors a Control to a corner or edge of its parent with pixel
//     offsets; the Control then grows from that edge as its content grows.
//   - Anything positioned freely inside a free Control uses Position/Size.

using System;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public static class Styles
    {
        // ---- palette (GDD table, verbatim) ---------------------------------
        public static readonly Color Ink = Color.FromHtml("#10131A");
        public static readonly Color Slate = Color.FromHtml("#1B2029");
        public static readonly Color Steel = Color.FromHtml("#2A3140");
        public static readonly Color Cream = Color.FromHtml("#E8E2D0");
        public static readonly Color Dust = Color.FromHtml("#9AA08E");
        public static readonly Color Amber = Color.FromHtml("#FFAE19");
        public static readonly Color Danger = Color.FromHtml("#FF4A3D");
        public static readonly Color Shield = Color.FromHtml("#3FC1FF");
        public static readonly Color Good = Color.FromHtml("#7FD18A");

        /// <summary>Rarity ramp; unknown renders as common (C64).</summary>
        public static Color Rarity(string tier) => tier switch
        {
            "uncommon" => Color.FromHtml("#4FD15C"),
            "rare" => Color.FromHtml("#3FA9FF"),
            "epic" => Color.FromHtml("#B45CFF"),
            "legendary" => Color.FromHtml("#FF9B1A"),
            _ => Color.FromHtml("#B8B8A8"), // common, and every unknown
        };

        public const float PanelOpacity = 0.92f;

        /// <summary>
        /// The comic lean follows the screen side (GDD): left-anchored
        /// panels lean −2°, right-anchored +2°, centered ones sit straight.
        /// A real parallelogram skew here, which is what the style guide
        /// describes; the Unity build faked it with a rotation.
        /// </summary>
        public const float SkewLeft = -2f;
        public const float SkewRight = 2f;
        public const float SkewNone = 0f;

        // ---- the display font ----------------------------------------------
        //
        // Staatliches (OFL, vendored beside its license in fonts/). Loaded
        // once, shared by every label; null falls back to the default font.
        private static FontFile _display;
        private static bool _displayTried;

        public static FontFile Display
        {
            get
            {
                if (!_displayTried)
                {
                    _displayTried = true;
                    _display = ResourceLoader.Exists("res://fonts/Staatliches-Regular.ttf")
                        ? GD.Load<FontFile>("res://fonts/Staatliches-Regular.ttf") : null;
                }
                return _display;
            }
        }

        // ---- element factories ---------------------------------------------

        /// <summary>
        /// A style-guide panel: slate @92%, 3px ink border, the comic lean.
        /// Add content to Body(panel).
        /// </summary>
        public static PanelContainer Panel(float skewDeg = SkewLeft)
        {
            var bg = Slate;
            bg.A = PanelOpacity;
            var sb = new StyleBoxFlat
            {
                BgColor = bg,
                BorderColor = Ink,
                ContentMarginLeft = 14, ContentMarginRight = 14,
                ContentMarginTop = 10, ContentMarginBottom = 12,
                Skew = new Vector2(Mathf.Tan(Mathf.DegToRad(skewDeg)), 0f),
            };
            sb.SetBorderWidthAll(3);
            var p = new PanelContainer();
            p.AddThemeStyleboxOverride("panel", sb);
            p.AddChild(new VBoxContainer { Name = "body" });
            return p;
        }

        /// <summary>The stacking body of a Panel.</summary>
        public static VBoxContainer Body(PanelContainer panel) => panel.GetNode<VBoxContainer>("body");

        public static void SetPadding(PanelContainer panel, float left, float top, float right, float bottom)
        {
            var sb = (StyleBoxFlat)panel.GetThemeStylebox("panel");
            sb.ContentMarginLeft = left; sb.ContentMarginTop = top;
            sb.ContentMarginRight = right; sb.ContentMarginBottom = bottom;
        }

        /// <summary>Marks a panel active: the amber inner edge.</summary>
        public static void SetActive(PanelContainer panel, bool active)
        {
            var sb = (StyleBoxFlat)panel.GetThemeStylebox("panel");
            sb.BorderColor = active ? Amber : Ink;
            sb.SetBorderWidthAll(active ? 2 : 3);
        }

        /// <summary>A display-type label (headers, big numerals, popups).</summary>
        public static Label Display_(string text, int size, Color color)
        {
            var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (Display != null) l.AddThemeFontOverride("font", Display);
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", color);
            l.VerticalAlignment = VerticalAlignment.Center;
            return l;
        }

        /// <summary>Letter-spaced variant of the display font, in pixels.</summary>
        public static void LetterSpacing(Label l, int px)
        {
            var v = new FontVariation { BaseFont = Display ?? ThemeDB.FallbackFont };
            v.SetSpacing(TextServer.SpacingType.Glyph, px);
            l.AddThemeFontOverride("font", v);
        }

        /// <summary>A section header: ALL CAPS dust over a 2px ink rule.</summary>
        public static VBoxContainer Header(string text)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 2);
            var l = Display_(text.ToUpperInvariant(), 16, Dust);
            LetterSpacing(l, 1);
            box.AddChild(l);
            var rule = Rule();
            box.AddChild(rule);
            box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
            return box;
        }

        /// <summary>A 2px ink rule.</summary>
        public static ColorRect Rule() => new ColorRect
        {
            Color = Ink,
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        /// <summary>
        /// A bar: steel trough, coloured fill. The fill's AnchorRight is the
        /// fraction; children of the trough are positioned freely.
        /// </summary>
        public static (Panel bar, ColorRect fill) Bar(Color fillColor, float height = 16)
        {
            var sb = new StyleBoxFlat { BgColor = Steel, BorderColor = Ink };
            sb.SetBorderWidthAll(2);
            var trough = new Panel
            {
                CustomMinimumSize = new Vector2(0, height),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ClipContents = false,
            };
            trough.AddThemeStyleboxOverride("panel", sb);
            var fill = new ColorRect { Color = fillColor, MouseFilter = Control.MouseFilterEnum.Ignore };
            fill.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            fill.OffsetLeft = 0; fill.OffsetTop = 0; fill.OffsetRight = 0; fill.OffsetBottom = 0;
            trough.AddChild(fill);
            return (trough, fill);
        }

        public static void SetFill(ColorRect fill, float fraction) =>
            fill.AnchorRight = Mathf.Clamp(fraction, 0f, 1f);

        /// <summary>Sets a Panel's or PanelContainer's border.</summary>
        public static void SetBorder(Control e, Color c, int width)
        {
            if (e.GetThemeStylebox("panel") is StyleBoxFlat sb)
            {
                sb.BorderColor = c;
                sb.SetBorderWidthAll(width);
            }
        }

        /// <summary>A flat box with a border, for cards and rings.</summary>
        public static Panel Box(Color bg, Color border, int borderWidth, float cornerRadius = 0f)
        {
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border, DrawCenter = bg.A > 0f };
            sb.SetBorderWidthAll(borderWidth);
            if (cornerRadius > 0f) sb.SetCornerRadiusAll((int)cornerRadius);
            var p = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            p.AddThemeStyleboxOverride("panel", sb);
            return p;
        }

        /// <summary>A style-guide button: amber (or danger) face, ink text and border.</summary>
        public static Button Button(string text, bool danger, Action onPressed)
        {
            var b = new Button { Text = text };
            if (Display != null) b.AddThemeFontOverride("font", Display);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.AddThemeColorOverride("font_color", Ink);
            b.AddThemeColorOverride("font_hover_color", Ink);
            b.AddThemeColorOverride("font_pressed_color", Ink);
            b.AddThemeColorOverride("font_focus_color", Ink);
            Color face = danger ? Danger : Amber;
            foreach (var (state, tint) in new[] { ("normal", 1f), ("hover", 1.08f), ("pressed", 0.85f), ("focus", 1f) })
            {
                var sb = new StyleBoxFlat
                {
                    BgColor = new Color(face.R * tint, face.G * tint, face.B * tint),
                    BorderColor = Ink,
                    ContentMarginLeft = 8, ContentMarginRight = 8,
                    ContentMarginTop = 2, ContentMarginBottom = 2,
                };
                sb.SetBorderWidthAll(2);
                b.AddThemeStyleboxOverride(state, sb);
            }
            b.Pressed += onPressed;
            return b;
        }

        /// <summary>A horizontal row with vertically centred children.</summary>
        /// <summary>
        /// The card every list uses now (Phase 11.7 task 7): a steel panel
        /// with a 2 px ink frame and a colour band on the left, a leading
        /// control (an ItemSlot, a Tile), a title over a dust subline, and
        /// trailing controls (prices, buttons) on the right.
        /// </summary>
        public static PanelContainer Card(Color band, Control leading, string title, Color titleColor, string sub, params Control[] trailing)
        {
            var sb = new StyleBoxFlat { BgColor = Steel, BorderColor = Ink, ContentMarginLeft = 0, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
            sb.SetBorderWidthAll(2);
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", sb);
            var row = Row(10);
            card.AddChild(row);
            row.AddChild(new ColorRect { Color = band, CustomMinimumSize = new Vector2(5, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
            if (leading != null) { leading.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; row.AddChild(leading); }
            var text = Column(0);
            text.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            text.AddChild(Display_(title, 14, titleColor));
            if (!string.IsNullOrEmpty(sub))
            {
                var subLab = Display_(sub, 11, Dust);
                subLab.AutowrapMode = TextServer.AutowrapMode.Word;
                text.AddChild(subLab);
            }
            row.AddChild(Grow(text));
            foreach (Control t in trailing)
            {
                if (t == null) continue;
                t.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                row.AddChild(t);
            }
            return card;
        }

        /// <summary>A 40 px glyph tile: a letter or two on a coloured square, the icon for things without models.</summary>
        public static Control Tile(string glyph, Color color, int size = 40)
        {
            var box = Box(new Color(color, 0.22f), color, 2);
            box.CustomMinimumSize = new Vector2(size, size);
            var l = Display_(glyph, size >= 40 ? 16 : 13, color);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.VerticalAlignment = VerticalAlignment.Center;
            l.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            l.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(l);
            return box;
        }

        /// <summary>A labelled progress bar: fill fraction, the text inside.</summary>
        public static Control Progress(float frac, Color color, string text, float width = 140f)
        {
            var (bar, fill) = Bar(color, 12);
            bar.CustomMinimumSize = new Vector2(width, 12);
            SetFill(fill, frac);
            var l = Display_(text, 9, Cream);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            l.MouseFilter = Control.MouseFilterEnum.Ignore;
            bar.AddChild(l);
            return bar;
        }

        public static HBoxContainer Row(int separation = 6)
        {
            var r = new HBoxContainer();
            r.AddThemeConstantOverride("separation", separation);
            return r;
        }

        /// <summary>A vertical stack.</summary>
        public static VBoxContainer Column(int separation = 4)
        {
            var c = new VBoxContainer();
            c.AddThemeConstantOverride("separation", separation);
            return c;
        }

        /// <summary>A fixed-height spacer inside a column.</summary>
        public static Control Gap(float px) => new Control { CustomMinimumSize = new Vector2(0, px), MouseFilter = Control.MouseFilterEnum.Ignore };

        /// <summary>Makes a child take the spare width in a row.</summary>
        public static T Grow<T>(T c) where T : Control
        {
            c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            return c;
        }

        // ---- placement -----------------------------------------------------

        /// <summary>
        /// Anchors a Control to a corner or edge of its parent. `x`/`y` are
        /// the pixel offsets from that edge (positive = inward); `width` fixes
        /// the width, 0 lets content decide. The Control grows away from the
        /// anchored edge as its minimum size grows, so a bottom-anchored
        /// panel keeps its bottom margin however tall its content gets.
        /// </summary>
        public static void Pin(Control c, Control.LayoutPreset preset, float x, float y, float width = 0f)
        {
            c.SetAnchorsPreset(preset);
            float w = width > 0 ? width : 0;
            switch (preset)
            {
                case Control.LayoutPreset.TopLeft:
                    c.OffsetLeft = x; c.OffsetTop = y; c.OffsetRight = x + w; c.OffsetBottom = y;
                    c.GrowHorizontal = Control.GrowDirection.End; c.GrowVertical = Control.GrowDirection.End;
                    break;
                case Control.LayoutPreset.TopRight:
                    c.OffsetRight = -x; c.OffsetLeft = -x - w; c.OffsetTop = y; c.OffsetBottom = y;
                    c.GrowHorizontal = Control.GrowDirection.Begin; c.GrowVertical = Control.GrowDirection.End;
                    break;
                case Control.LayoutPreset.BottomLeft:
                    c.OffsetLeft = x; c.OffsetRight = x + w; c.OffsetBottom = -y; c.OffsetTop = -y;
                    c.GrowHorizontal = Control.GrowDirection.End; c.GrowVertical = Control.GrowDirection.Begin;
                    break;
                case Control.LayoutPreset.BottomRight:
                    c.OffsetRight = -x; c.OffsetLeft = -x - w; c.OffsetBottom = -y; c.OffsetTop = -y;
                    c.GrowHorizontal = Control.GrowDirection.Begin; c.GrowVertical = Control.GrowDirection.Begin;
                    break;
                case Control.LayoutPreset.CenterTop:
                    c.OffsetLeft = -w / 2; c.OffsetRight = w / 2; c.OffsetTop = y; c.OffsetBottom = y;
                    c.GrowHorizontal = Control.GrowDirection.Both; c.GrowVertical = Control.GrowDirection.End;
                    break;
                case Control.LayoutPreset.CenterBottom:
                    c.OffsetLeft = -w / 2; c.OffsetRight = w / 2; c.OffsetBottom = -y; c.OffsetTop = -y;
                    c.GrowHorizontal = Control.GrowDirection.Both; c.GrowVertical = Control.GrowDirection.Begin;
                    break;
                case Control.LayoutPreset.CenterLeft:
                    c.OffsetLeft = x; c.OffsetRight = x + w; c.OffsetTop = y; c.OffsetBottom = y;
                    c.GrowHorizontal = Control.GrowDirection.End; c.GrowVertical = Control.GrowDirection.Both;
                    break;
                default:
                    c.OffsetLeft = -w / 2; c.OffsetRight = w / 2; c.OffsetTop = y; c.OffsetBottom = y;
                    c.GrowHorizontal = Control.GrowDirection.Both; c.GrowVertical = Control.GrowDirection.Both;
                    break;
            }
        }

        /// <summary>
        /// Anchors a Control at a fractional point of its parent (0.5, 0.18
        /// = horizontally centred, 18% down), growing from there.
        /// </summary>
        public static void PinAt(Control c, float ax, float ay, float width = 0f,
            Control.GrowDirection growH = Control.GrowDirection.Both,
            Control.GrowDirection growV = Control.GrowDirection.End)
        {
            c.AnchorLeft = ax; c.AnchorRight = ax; c.AnchorTop = ay; c.AnchorBottom = ay;
            float w = width > 0 ? width : 0;
            c.OffsetLeft = growH == Control.GrowDirection.Both ? -w / 2 : growH == Control.GrowDirection.Begin ? -w : 0;
            c.OffsetRight = c.OffsetLeft + w;
            c.OffsetTop = 0; c.OffsetBottom = 0;
            c.GrowHorizontal = growH;
            c.GrowVertical = growV;
        }
    }
}
