// Chat (GDD "Chat (Phase 20)"): one world channel, text only. ChatLog is the
// pure model -- the last 8 lines, each living 10 s -- and ChatView draws it
// bottom-left above the vitals, with the input line under it while open.
// Speaker in Amber display type, text in Cream; a line fades over its last
// 2 s; the panel is hidden when there is nothing to show.

using System;
using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class ChatLog
    {
        public const int MaxLines = 8;
        public const double LineSeconds = 10.0;
        public const double FadeSeconds = 2.0;

        public readonly struct Line
        {
            public readonly string Name, Text;
            public readonly double At;
            public Line(string name, string text, double at) { Name = name; Text = text; At = at; }
        }

        private readonly List<Line> _lines = new List<Line>();

        /// <summary>Oldest first.</summary>
        public IReadOnlyList<Line> Lines => _lines;

        public bool Visible => _lines.Count > 0;

        /// <summary>The input line is up: game keys are swallowed.</summary>
        public bool Open;

        public void Push(string name, string text, double now)
        {
            _lines.Add(new Line(name ?? "", text ?? "", now));
            while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }

        /// <summary>Drops every line older than LineSeconds.</summary>
        public void Expire(double now) => _lines.RemoveAll(l => now - l.At > LineSeconds);

        /// <summary>1 until the last FadeSeconds of a line's life, then down to 0.</summary>
        public static float Alpha(Line l, double now)
        {
            double left = LineSeconds - (now - l.At);
            return (float)Math.Clamp(left / FadeSeconds, 0.0, 1.0);
        }
    }

    public sealed class ChatView
    {
        private const float Width = 420f;

        private readonly PanelContainer _panel;
        private readonly VBoxContainer _log;
        private readonly LineEdit _input;
        private readonly Action<string> _onSend;
        private readonly List<(HBoxContainer row, Label name, Label text)> _rows = new List<(HBoxContainer, Label, Label)>();

        /// <summary>Fired when the line closes itself (Enter or Escape).</summary>
        public Action Closed;

        public ChatView(Control parent, Action<string> onSend)
        {
            _onSend = onSend;
            _panel = Styles.Panel(Styles.SkewLeft);
            _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
            Styles.SetPadding(_panel, 12, 8, 12, 8);
            // Above the vitals panel (bottom-left, 24 px in), clear of the
            // hotbar, which sits bottom-centre.
            Styles.Pin(_panel, Control.LayoutPreset.BottomLeft, 24, 112, Width);
            var body = Styles.Body(_panel);
            body.AddThemeConstantOverride("separation", 2);
            _log = Styles.Column(2);
            _log.MouseFilter = Control.MouseFilterEnum.Ignore;
            body.AddChild(_log);
            for (int i = 0; i < ChatLog.MaxLines; i++)
            {
                var row = Styles.Row(8);
                row.MouseFilter = Control.MouseFilterEnum.Ignore;
                var name = Styles.Display_("", 16, Styles.Amber);
                var text = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                text.AddThemeFontSizeOverride("font_size", 15);
                text.AddThemeColorOverride("font_color", Styles.Cream);
                row.AddChild(name);
                row.AddChild(text);
                row.Visible = false;
                _log.AddChild(row);
                _rows.Add((row, name, text));
            }

            _input = new LineEdit { PlaceholderText = "say something…", MaxLength = 200, Visible = false };
            var sb = new StyleBoxFlat { BgColor = Styles.Ink, BorderColor = Styles.Amber, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 };
            sb.SetBorderWidthAll(2);
            _input.AddThemeStyleboxOverride("normal", sb);
            _input.AddThemeStyleboxOverride("focus", sb);
            _input.AddThemeColorOverride("font_color", Styles.Cream);
            _input.AddThemeColorOverride("font_placeholder_color", Styles.Dust);
            _input.AddThemeColorOverride("caret_color", Styles.Amber);
            _input.AddThemeFontSizeOverride("font_size", 15);
            _input.TextSubmitted += OnSubmitted;
            _input.GuiInput += OnGuiInput;
            body.AddChild(_input);

            _panel.Visible = false;
            parent.AddChild(_panel);
        }

        public bool IsOpen => _input.Visible;

        public void SetOpen(bool open)
        {
            _input.Visible = open;
            _input.Clear();
            if (open) _input.GrabFocus();
            else _input.ReleaseFocus();
            _panel.Visible = open || _rows[0].row.Visible;
        }

        /// <summary>Draws the model: newest at the bottom, each row fading out.</summary>
        public void Set(ChatLog model, double now)
        {
            var lines = model.Lines;
            for (int i = 0; i < _rows.Count; i++)
            {
                var (row, name, text) = _rows[i];
                if (i < lines.Count)
                {
                    var l = lines[i];
                    if (name.Text != l.Name) name.Text = l.Name;
                    if (text.Text != l.Text) text.Text = l.Text;
                    row.Modulate = new Color(1, 1, 1, ChatLog.Alpha(l, now));
                    row.Visible = true;
                }
                else row.Visible = false;
            }
            if (model.Open != _input.Visible) SetOpen(model.Open);
            _panel.Visible = model.Visible || model.Open;
        }

        private void OnSubmitted(string text)
        {
            text = text.Trim();
            if (text.Length > 0) _onSend(text);
            Close();
        }

        private void OnGuiInput(InputEvent e)
        {
            if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
            {
                _input.AcceptEvent();
                Close();
            }
        }

        private void Close()
        {
            SetOpen(false);
            Closed?.Invoke();
        }
    }
}
