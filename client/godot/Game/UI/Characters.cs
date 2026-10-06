// The character select (docs/GDD.md "Character select", Phase 16): the
// game-canvas screen PLAY opens before any world exists.
//
// Three parts, the launcher's shape. `Characters` is the pure model -- the
// four states, the create form, the body-id table and the server's name rule
// -- with no engine types, so -selftest walks it. `CharactersView` draws it at
// the 1920×1080 UI base: an opaque list column on the left, a transparent
// right side the stage shows through. `CharacterStage` is that stage: the
// body on a dark floor under one key light, idling and turning, drawn by its
// own camera in the MAIN viewport (gl_compat draws SubViewports unlit).

using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace SpaceAdventure.Game.UI
{
    /// <summary>One row of `GET /api/characters`.</summary>
    public sealed class CharacterRow
    {
        public string Token, Name, Body, Hair;
        public long Credits, LastSeenMs;
    }

    /// <summary>The character select's model: events in, what to draw out.</summary>
    public sealed class Characters
    {
        public enum State { Loading, List, Create, Failed }

        public const int MaxCharacters = 5;

        private readonly List<CharacterRow> _rows = new List<CharacterRow>();

        public State Now { get; private set; } = State.Loading;
        public IReadOnlyList<CharacterRow> Rows => _rows;
        /// <summary>The selected row, −1 for none.</summary>
        public int Selected { get; private set; } = -1;
        /// <summary>The Failed line, or the create form's server reason; "" otherwise.</summary>
        public string Reason { get; private set; } = "";

        // The create form.
        public string Name { get; private set; } = "";
        public bool Female { get; private set; }
        public bool Vanguard { get; private set; }
        public string Hair { get; private set; } = DefaultHair;

        /// <summary>The list arrived: one or more pre-selects the first, none opens the form.</summary>
        public void Loaded(IReadOnlyList<CharacterRow> rows)
        {
            _rows.Clear();
            if (rows != null)
                foreach (CharacterRow r in rows)
                    if (r != null) _rows.Add(r);
            Reason = "";
            if (_rows.Count == 0)
            {
                Selected = -1;
                NewCharacter();
                return;
            }
            Selected = 0;
            Now = State.List;
        }

        public void Fail(string reason)
        {
            Reason = reason ?? "";
            Now = State.Failed;
        }

        public void Select(int i)
        {
            if (i >= 0 && i < _rows.Count) Selected = i;
        }

        public void NewCharacter()
        {
            Name = "";
            Female = false;
            Vanguard = false;
            Hair = DefaultHair;
            Reason = "";
            Now = State.Create;
        }

        public void SetName(string s) => Name = s ?? "";
        public void SetFemale(bool f) => Female = f;
        public void SetVanguard(bool v) => Vanguard = v;
        /// <summary>Any id; an unknown one shows as itself and the server answers `bad hair`.</summary>
        public void SetHair(string id) => Hair = string.IsNullOrEmpty(id) ? DefaultHair : id;
        public void NextHair() => Hair = HairStyles[(HairIndex(Hair) + 1) % HairStyles.Length].id;
        public void PrevHair() => Hair = HairStyles[(HairIndex(Hair) + HairStyles.Length - 1) % HairStyles.Length].id;

        /// <summary>Back to the list; with no rows PLAY simply stays dark.</summary>
        public void Cancel()
        {
            Reason = "";
            Now = State.List;
        }

        public void Created(CharacterRow row)
        {
            if (row != null)
            {
                _rows.Add(row);
                Selected = _rows.Count - 1;
            }
            Reason = "";
            Now = State.List;
        }

        /// <summary>The POST answered `status` with `body`: the form stays, the reason under it.</summary>
        public void CreateFailed(int status, string body)
        {
            string b = (body ?? "").ToLowerInvariant();
            Reason = status switch
            {
                409 when b.Contains("name taken") => "NAME TAKEN",
                409 when b.Contains("character limit") => "CHARACTER LIMIT",
                400 when b.Contains("bad name") => "BAD NAME",
                400 when b.Contains("bad body") => "BAD BODY",
                400 when b.Contains("bad hair") => "BAD HAIR",
                401 => "SIGNED OUT",
                _ => "COULD NOT CREATE",
            };
            Now = State.Create;
        }

        // ---- the body table (GDD "Bodies") ---------------------------------

        public static string BodyId(bool female, bool vanguard) =>
            (vanguard ? "char.ubc" : "char.player") + (female ? ".f" : "");

        /// <summary>A body id back to its toggles; unknown → (false, false).</summary>
        public static (bool female, bool vanguard) ParseBody(string id) => id switch
        {
            "char.player" => (false, false),
            "char.player.f" => (true, false),
            "char.ubc" => (false, true),
            "char.ubc.f" => (true, true),
            _ => (false, false),
        };

        // ---- hair (GDD "Faces and hair") -----------------------------------

        /// <summary>The HAIR row's order. Every style is built for every body (hair.py BODIES).</summary>
        public static readonly (string id, string label)[] HairStyles =
        {
            ("hair.none", "NONE"),
            ("hair.buzzed", "BUZZED"),
            ("hair.buzzed_female", "BUZZED F"),
            ("hair.simple_parted", "PARTED"),
            ("hair.long", "LONG"),
            ("hair.buns", "BUNS"),
            ("hair.beard", "BEARD"),
        };

        /// <summary>A new character's hair: the first real style, so the stage looks finished.</summary>
        public static readonly string DefaultHair = HairStyles[1].id;

        /// <summary>The style's index; an unknown id cycles as if it were NONE.</summary>
        public static int HairIndex(string id)
        {
            for (int i = 0; i < HairStyles.Length; i++)
                if (HairStyles[i].id == id) return i;
            return 0;
        }

        /// <summary>"LONG" for hair.long; an unknown id as itself.</summary>
        public static string HairLabel(string id)
        {
            foreach (var (hid, label) in HairStyles)
                if (hid == id) return label;
            return id ?? "";
        }

        public static string ModelName(bool vanguard) => vanguard ? "VANGUARD" : "COLONIST";

        /// <summary>"COLONIST · M" for a row's body.</summary>
        public static string BodyLine(string id)
        {
            var (female, vanguard) = ParseBody(id);
            return ModelName(vanguard) + " · " + (female ? "F" : "M");
        }

        /// <summary>
        /// The server's rule (web.go nameOK): 3–16 runes of letters, digits,
        /// space, `-`, `'`; no leading, trailing or double space. Also at most
        /// 24 UTF-8 bytes, SanitizeName's cap, so a name that passes here is
        /// never silently cut there.
        /// </summary>
        public static bool NameOk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Trim() != s || s.Contains("  ")) return false;
            if (Encoding.UTF8.GetByteCount(s) > 24) return false;
            int n = 0;
            foreach (Rune r in s.EnumerateRunes())
            {
                n++;
                if (!Rune.IsLetter(r) && !Rune.IsDigit(r) && r.Value != ' ' && r.Value != '-' && r.Value != '\'')
                    return false;
            }
            return n >= 3 && n <= 16;
        }

        public bool CanCreate => Now == State.Create && NameOk(Name);
        public bool CanPlay => Now == State.List && Selected >= 0 && Selected < _rows.Count;
        public bool CanNew => _rows.Count < MaxCharacters;

        /// <summary>The body the stage shows, or null for an empty stage.</summary>
        public string StageBody => Now switch
        {
            State.Create => BodyId(Female, Vanguard),
            State.List when Selected >= 0 && Selected < _rows.Count => _rows[Selected].Body,
            _ => null,
        };

        /// <summary>The hair the stage shows with StageBody, or null.</summary>
        public string StageHair => Now switch
        {
            State.Create => Hair,
            State.List when Selected >= 0 && Selected < _rows.Count => _rows[Selected].Hair,
            _ => null,
        };

        /// <summary>"just now", "N min ago", "N h ago", "N d ago"; "never" for no time.</summary>
        public static string Relative(long lastSeenMs, long nowMs)
        {
            if (lastSeenMs <= 0) return "never";
            long s = Math.Max(0, (nowMs - lastSeenMs) / 1000);
            if (s < 60) return "just now";
            if (s < 3600) return $"{s / 60} min ago";
            if (s < 86400) return $"{s / 3600} h ago";
            return $"{s / 86400} d ago";
        }
    }

    /// <summary>The character select's controls, on the game canvas.</summary>
    public sealed class CharactersView
    {
        /// <summary>The list column's share of the screen; the stage has the rest.</summary>
        public const float ListShare = 0.4f;
        /// <summary>Horizontal drag across the stage side, in pixels; Boot turns the stage body with it.</summary>
        public Action<float> OnStageDrag { get; set; }

        private readonly Control _root;
        private readonly Action<int> _onSelect;
        private readonly Control _loading, _list, _create, _failed;
        private readonly VBoxContainer _rowsBox;
        private readonly Button _new, _play, _signOut;
        private readonly LineEdit _name;
        private readonly Button _male, _female, _colonist, _vanguard, _createBtn;
        private readonly Label _createReason, _failReason, _hair;
        private readonly Action _onCreate;
        private Characters _model;

        private readonly List<(PanelContainer box, Label last, StyleBoxFlat frame)> _rowViews = new();
        private string _rowsKey;
        private Characters.State? _lastState;

        public CharactersView(Control parent,
            Action onPlay, Action onNew, Action onCreate, Action onCancel, Action onSignOut, Action onRetry,
            Action<int> onSelect, Action<string> onName, Action<bool> onFemale, Action<bool> onVanguard,
            Action onHairPrev, Action onHairNext)
        {
            _onSelect = onSelect;
            _onCreate = onCreate;

            // Full-rect and transparent; the right side passes the mouse on.
            _root = new Control { Name = "characters", MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            parent.AddChild(_root);

            // The list column: opaque slate, a 3 px ink edge on the stage side.
            var column = new ColorRect { Name = "list", Color = Styles.Slate, MouseFilter = Control.MouseFilterEnum.Stop };
            column.AnchorLeft = 0; column.AnchorTop = 0; column.AnchorBottom = 1; column.AnchorRight = ListShare;
            column.OffsetLeft = column.OffsetTop = column.OffsetRight = column.OffsetBottom = 0;
            _root.AddChild(column);
            var edge = new ColorRect { Color = Styles.Ink, MouseFilter = Control.MouseFilterEnum.Ignore };
            edge.AnchorLeft = 1; edge.AnchorRight = 1; edge.AnchorTop = 0; edge.AnchorBottom = 1;
            edge.OffsetLeft = -4; edge.OffsetRight = 0;
            column.AddChild(edge);

            // The stage side: transparent, catches a left-button drag and
            // reports its horizontal travel (the stage turns the body).
            var stage = new Control { Name = "stage", MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Drag };
            stage.AnchorLeft = ListShare; stage.AnchorRight = 1; stage.AnchorTop = 0; stage.AnchorBottom = 1;
            stage.OffsetLeft = stage.OffsetTop = stage.OffsetRight = stage.OffsetBottom = 0;
            stage.GuiInput += e =>
            {
                if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0) OnStageDrag?.Invoke(m.Relative.X);
            };
            _root.AddChild(stage);

            var margin = new MarginContainer();
            margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            margin.AddThemeConstantOverride("margin_left", 72);
            margin.AddThemeConstantOverride("margin_right", 64);
            margin.AddThemeConstantOverride("margin_top", 72);
            margin.AddThemeConstantOverride("margin_bottom", 64);
            column.AddChild(margin);
            var page = Styles.Column(14);
            margin.AddChild(page);

            var title = Styles.Display_("CHARACTERS", 64, Styles.Amber);
            Styles.LetterSpacing(title, 2);
            page.AddChild(title);
            var rule = Styles.Rule();
            rule.Color = Styles.Steel;
            rule.CustomMinimumSize = new Vector2(0, 3);
            page.AddChild(rule);
            page.AddChild(Styles.Gap(10));

            // ---- Loading
            var loading = new CenterContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
            loading.AddChild(Styles.Display_("LOADING CHARACTERS…", 30, Styles.Dust));
            _loading = loading;
            page.AddChild(loading);

            // ---- List
            var list = Styles.Column(14);
            list.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _list = list;
            page.AddChild(list);
            _rowsBox = Styles.Column(12);
            list.AddChild(_rowsBox);
            list.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
            _new = Secondary("NEW CHARACTER", 20, 52, onNew);
            list.AddChild(_new);
            _play = Primary("PLAY", 34, 84, onPlay);
            list.AddChild(_play);
            var outRow = Styles.Row(0);
            _signOut = Secondary("SIGN OUT", 16, 40, onSignOut);
            _signOut.CustomMinimumSize = new Vector2(180, 40);
            outRow.AddChild(_signOut);
            list.AddChild(Styles.Gap(4));
            list.AddChild(outRow);

            // ---- Create
            var create = Styles.Column(12);
            create.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _create = create;
            page.AddChild(create);
            create.AddChild(FieldLabel("NAME"));
            _name = new LineEdit { PlaceholderText = "3–16 letters, digits, space - '", MaxLength = 16, CustomMinimumSize = new Vector2(0, 60) };
            _name.AddThemeFontSizeOverride("font_size", 26);
            if (Styles.Display != null) _name.AddThemeFontOverride("font", Styles.Display);
            _name.AddThemeColorOverride("font_color", Styles.Cream);
            _name.AddThemeColorOverride("font_placeholder_color", new Color(Styles.Dust, 0.6f));
            _name.AddThemeColorOverride("caret_color", Styles.Amber);
            foreach (var (state, border) in new[] { ("normal", Styles.Ink), ("focus", Styles.Amber), ("read_only", Styles.Ink) })
            {
                var sb = new StyleBoxFlat { BgColor = Styles.Ink, BorderColor = border, ContentMarginLeft = 14, ContentMarginRight = 14 };
                sb.SetBorderWidthAll(state == "focus" ? 3 : 2);
                if (state == "normal") sb.BorderColor = Styles.Steel;
                _name.AddThemeStyleboxOverride(state, sb);
            }
            _name.TextChanged += s => onName?.Invoke(s);
            _name.TextSubmitted += _ => { if (_model != null && _model.CanCreate) _onCreate?.Invoke(); };
            create.AddChild(_name);
            create.AddChild(Styles.Gap(8));

            create.AddChild(FieldLabel("GENDER"));
            var genders = Styles.Row(10);
            _male = Toggle("M", () => onFemale?.Invoke(false));
            _female = Toggle("F", () => onFemale?.Invoke(true));
            _male.CustomMinimumSize = _female.CustomMinimumSize = new Vector2(120, 56);
            genders.AddChild(_male);
            genders.AddChild(_female);
            create.AddChild(genders);
            create.AddChild(Styles.Gap(8));

            create.AddChild(FieldLabel("MODEL"));
            var models = Styles.Row(10);
            _colonist = Toggle(Characters.ModelName(false), () => onVanguard?.Invoke(false));
            _vanguard = Toggle(Characters.ModelName(true), () => onVanguard?.Invoke(true));
            models.AddChild(Styles.Grow(_colonist));
            models.AddChild(Styles.Grow(_vanguard));
            _colonist.CustomMinimumSize = _vanguard.CustomMinimumSize = new Vector2(0, 56);
            create.AddChild(models);
            create.AddChild(Styles.Gap(8));

            // HAIR: ◀  LONG  ▶ -- the toggles' buttons, the style between them.
            create.AddChild(FieldLabel("HAIR"));
            var hairs = Styles.Row(10);
            Button prev = Toggle("◀", () => onHairPrev?.Invoke());
            Button next = Toggle("▶", () => onHairNext?.Invoke());
            prev.CustomMinimumSize = next.CustomMinimumSize = new Vector2(72, 56);
            Face(prev, false);
            Face(next, false);
            _hair = Styles.Display_("", 26, Styles.Cream);
            _hair.HorizontalAlignment = HorizontalAlignment.Center;
            _hair.VerticalAlignment = VerticalAlignment.Center;
            _hair.CustomMinimumSize = new Vector2(0, 56);
            hairs.AddChild(prev);
            hairs.AddChild(Styles.Grow(_hair));
            hairs.AddChild(next);
            create.AddChild(hairs);

            create.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
            _createBtn = Primary("CREATE", 34, 84, onCreate);
            create.AddChild(_createBtn);
            var cancelRow = Styles.Row(0);
            var cancel = Secondary("CANCEL", 16, 40, onCancel);
            cancel.CustomMinimumSize = new Vector2(180, 40);
            cancelRow.AddChild(cancel);
            create.AddChild(Styles.Gap(4));
            create.AddChild(cancelRow);
            _createReason = Styles.Display_("", 22, Styles.Danger);
            _createReason.CustomMinimumSize = new Vector2(0, 32);
            create.AddChild(_createReason);

            // ---- Failed
            var failed = new CenterContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
            var failCol = Styles.Column(24);
            failed.AddChild(failCol);
            _failReason = Styles.Display_("", 28, Styles.Danger);
            _failReason.HorizontalAlignment = HorizontalAlignment.Center;
            failCol.AddChild(_failReason);
            var retry = Primary("RETRY", 26, 64, onRetry);
            retry.CustomMinimumSize = new Vector2(260, 64);
            retry.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            failCol.AddChild(retry);
            _failed = failed;
            page.AddChild(failed);
        }

        public bool Visible => _root.Visible;

        public void Show(bool on) => _root.Visible = on;

        /// <summary>Draws the model; touches only what changed, so it can run every frame.</summary>
        public void Set(Characters model)
        {
            _model = model;
            Characters.State st = model.Now;
            bool entered = st != _lastState;
            _lastState = st;

            Vis(_loading, st == Characters.State.Loading);
            Vis(_list, st == Characters.State.List);
            Vis(_create, st == Characters.State.Create);
            Vis(_failed, st == Characters.State.Failed);

            switch (st)
            {
                case Characters.State.List:
                    SetRows(model);
                    Dis(_new, !model.CanNew);
                    Dis(_play, !model.CanPlay);
                    break;
                case Characters.State.Create:
                    if (_name.Text != model.Name)
                    {
                        int caret = _name.CaretColumn;
                        _name.Text = model.Name;
                        _name.CaretColumn = Math.Min(caret, model.Name.Length);
                    }
                    if (entered) _name.CallDeferred(Control.MethodName.GrabFocus);
                    Face(_male, !model.Female);
                    Face(_female, model.Female);
                    Face(_colonist, !model.Vanguard);
                    Face(_vanguard, model.Vanguard);
                    Txt(_hair, Characters.HairLabel(model.Hair));
                    Dis(_createBtn, !model.CanCreate);
                    Txt(_createReason, model.Reason);
                    break;
                case Characters.State.Failed:
                    Txt(_failReason, model.Reason);
                    break;
            }
        }

        // ---- the rows ----------------------------------------------------

        private void SetRows(Characters model)
        {
            IReadOnlyList<CharacterRow> rows = model.Rows;
            var key = new StringBuilder();
            foreach (CharacterRow r in rows) key.Append(r.Token).Append('|').Append(r.Name).Append('|').Append(r.Body).Append('|').Append(r.Credits).Append('\n');
            string k = key.ToString();
            if (k != _rowsKey)
            {
                _rowsKey = k;
                foreach (var v in _rowViews) v.box.QueueFree();
                _rowViews.Clear();
                for (int i = 0; i < rows.Count; i++) _rowViews.Add(MakeRow(rows[i], i));
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < _rowViews.Count; i++)
            {
                var (box, last, frame) = _rowViews[i];
                bool sel = i == model.Selected;
                Color border = sel ? Styles.Amber : Styles.Ink;
                int width = sel ? 4 : 2;
                if (frame.BorderColor != border || frame.BorderWidthTop != width)
                {
                    frame.BorderColor = border;
                    frame.SetBorderWidthAll(width);
                    frame.BgColor = sel ? Styles.Steel.Lightened(0.06f) : Styles.Steel;
                }
                Txt(last, "last played " + Characters.Relative(rows[i].LastSeenMs, now));
            }
        }

        private (PanelContainer, Label, StyleBoxFlat) MakeRow(CharacterRow row, int index)
        {
            var frame = new StyleBoxFlat
            {
                BgColor = Styles.Steel, BorderColor = Styles.Ink,
                ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 12, ContentMarginBottom = 14,
            };
            frame.SetBorderWidthAll(2);
            var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            box.AddThemeStyleboxOverride("panel", frame);
            box.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    _onSelect?.Invoke(index);
            };

            var line = Styles.Row(16);
            line.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(line);
            var text = Styles.Column(2);
            text.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(Styles.Grow(text));
            var name = Styles.Display_(row.Name ?? "", 36, Styles.Cream);
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            text.AddChild(name);
            text.AddChild(Styles.Display_(Characters.BodyLine(row.Body), 20, Styles.Dust));
            var last = Styles.Display_("", 16, Styles.Dust);
            text.AddChild(last);

            var credits = Styles.Display_($"{row.Credits:N0} CR", 26, Styles.Amber);
            credits.HorizontalAlignment = HorizontalAlignment.Right;
            credits.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            line.AddChild(credits);

            _rowsBox.AddChild(box);
            return (box, last, frame);
        }

        // ---- small helpers -----------------------------------------------

        private static void Vis(Control c, bool on) { if (c.Visible != on) c.Visible = on; }
        private static void Dis(BaseButton b, bool off) { if (b.Disabled != off) b.Disabled = off; }
        private static void Txt(Label l, string s) { s ??= ""; if (l.Text != s) l.Text = s; }

        private static Label FieldLabel(string text)
        {
            var l = Styles.Display_(text, 20, Styles.Dust);
            Styles.LetterSpacing(l, 2);
            return l;
        }

        private static StyleBoxFlat Flat(Color bg, Color border, int width)
        {
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 4, ContentMarginBottom = 4 };
            sb.SetBorderWidthAll(width);
            return sb;
        }

        /// <summary>The amber primary (PLAY, CREATE, RETRY), steel and dust when dark.</summary>
        private static Button Primary(string text, int size, int height, Action onPressed)
        {
            var b = Styles.Button(text, false, onPressed);
            b.AddThemeFontSizeOverride("font_size", size);
            b.CustomMinimumSize = new Vector2(0, height);
            b.AddThemeColorOverride("font_disabled_color", Styles.Dust);
            b.AddThemeStyleboxOverride("disabled", Flat(Styles.Steel, Styles.Ink, 3));
            return b;
        }

        /// <summary>A steel button with cream text (NEW CHARACTER, SIGN OUT, CANCEL).</summary>
        private static Button Secondary(string text, int size, int height, Action onPressed)
        {
            var b = Styles.Button(text, false, onPressed);
            b.AddThemeFontSizeOverride("font_size", size);
            b.CustomMinimumSize = new Vector2(0, height);
            foreach (string s in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                b.AddThemeColorOverride(s, Styles.Cream);
            b.AddThemeColorOverride("font_disabled_color", new Color(Styles.Dust, 0.5f));
            foreach (var (state, tint) in new[] { ("normal", 1f), ("hover", 1.3f), ("pressed", 0.85f), ("focus", 1f) })
                b.AddThemeStyleboxOverride(state, Flat(new Color(Styles.Steel.R * tint, Styles.Steel.G * tint, Styles.Steel.B * tint), Styles.Ink, 2));
            b.AddThemeStyleboxOverride("disabled", Flat(Styles.Ink, Styles.Steel, 2));
            return b;
        }

        /// <summary>One half of a two-way toggle; Face() paints it on or off.</summary>
        private static Button Toggle(string text, Action onPressed)
        {
            var b = Styles.Button(text, false, onPressed);
            b.AddThemeFontSizeOverride("font_size", 24);
            b.FocusMode = Control.FocusModeEnum.None;
            b.SetMeta("on", -1);
            return b;
        }

        private static void Face(Button b, bool on)
        {
            int want = on ? 1 : 0;
            if ((int)b.GetMeta("on") == want) return;
            b.SetMeta("on", want);
            Color bg = on ? Styles.Amber : Styles.Steel;
            Color fg = on ? Styles.Ink : Styles.Cream;
            foreach (string s in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                b.AddThemeColorOverride(s, fg);
            foreach (var (state, tint) in new[] { ("normal", 1f), ("hover", on ? 1.05f : 1.3f), ("pressed", 0.85f), ("focus", 1f) })
                b.AddThemeStyleboxOverride(state, Flat(new Color(bg.R * tint, bg.G * tint, bg.B * tint), Styles.Ink, on ? 3 : 2));
        }
    }

    /// <summary>
    /// The select's 3D preview, in the main viewport, far below where the
    /// world will be: its own camera, a dark floor disc, a key spot and a
    /// faint fill, and the body on a turntable.
    /// </summary>
    public sealed class CharacterStage
    {
        /// <summary>Far below any world, so nothing of one ever shares the frame.</summary>
        private static readonly Vector3 Origin = new Vector3(0, -5000f, 0);
        private const float DragRadPerPx = 0.012f;   // a full turn is ~520 px of drag
        private const float EyeDistance = 6.2f, EyeHeight = 1.75f, LookHeight = 0.95f;
        private const float FovDeg = 30f;

        private readonly AssetRegistry _assets;
        private readonly Node3D _root, _turntable;
        private readonly Camera3D _camera;
        private Node3D _body;
        private string _bodyId, _hairId;
        private int _generation;
        // The hair, worn exactly as a body in the world wears it (EntityViews.Dress).
        private readonly Dictionary<string, string> _worn = new(), _wornDrawn = new();
        private readonly Dictionary<string, Node3D> _wornNodes = new();

        public CharacterStage(Node parent, AssetRegistry assets)
        {
            _assets = assets;
            _root = new Node3D { Name = "CharacterStage", Position = Origin };
            parent.AddChild(_root);

            // The camera's own environment: a plain dark backdrop and a low
            // ambient, whatever the world's WorldEnvironment says.
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = Color.FromHtml("#0B0E14"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.30f, 0.32f, 0.40f),
                AmbientLightEnergy = 0.6f,
            };
            _camera = new Camera3D { Name = "StageEye", Fov = FovDeg, Near = 0.05f, Far = 60f, Environment = env };
            _root.AddChild(_camera);
            // Aimed in local space: LookAt wants the node in the tree.
            _camera.Transform = new Transform3D(Basis.Identity, new Vector3(0, EyeHeight, EyeDistance))
                .LookingAt(new Vector3(0, LookHeight, 0), Vector3.Up);

            // The key light is a spot, not a DirectionalLight3D: under
            // gl_compatibility a second directional (the world's sun is the
            // first) lights nothing. Overhead, front-left, warm.
            var key = new SpotLight3D
            {
                Name = "Key", LightEnergy = 3.5f, SpotRange = 14f, SpotAngle = 24f, SpotAttenuation = 0.6f,
                LightColor = new Color(1f, 0.93f, 0.82f), ShadowEnabled = true,
                // Against acne on the suit's smooth curves: a little more bias
                // than the default, and the world's Sun is off while the stage
                // shows (Boot) -- its cascades were the stripes.
                ShadowBias = 0.06f, ShadowNormalBias = 2.5f,
            };
            key.Transform = new Transform3D(Basis.Identity, new Vector3(-2.2f, 5.2f, 3.0f))
                .LookingAt(new Vector3(0, 0.9f, 0), Vector3.Up);
            _root.AddChild(key);
            // A faint cool fill from the other side, and a rim from behind.
            var fill = new OmniLight3D { Name = "Fill", LightEnergy = 0.5f, OmniRange = 8f, LightColor = new Color(0.55f, 0.7f, 1f) };
            _root.AddChild(fill);
            fill.Position = new Vector3(2.6f, 1.6f, 2.4f);
            var rim = new OmniLight3D { Name = "Rim", LightEnergy = 0.8f, OmniRange = 6f, LightColor = Styles.Amber };
            _root.AddChild(rim);
            rim.Position = new Vector3(0.6f, 2.2f, -2.2f);

            // The floor: a dark steel disc with an amber ring set into it.
            var floorMat = new StandardMaterial3D { AlbedoColor = Color.FromHtml("#141922"), Roughness = 0.95f };
            var floor = new MeshInstance3D
            {
                Name = "Floor",
                Mesh = new CylinderMesh { TopRadius = 1.05f, BottomRadius = 1.12f, Height = 0.12f, RadialSegments = 48 },
                MaterialOverride = floorMat,
                Position = new Vector3(0, -0.06f, 0),
            };
            _root.AddChild(floor);
            var ringMat = new StandardMaterial3D { AlbedoColor = Styles.Amber, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            var ring = new MeshInstance3D
            {
                Name = "Ring",
                Mesh = new TorusMesh { InnerRadius = 1.02f, OuterRadius = 1.06f, Rings = 64, RingSegments = 6 },
                MaterialOverride = ringMat,
                Position = new Vector3(0, 0.005f, 0),
                Scale = new Vector3(1f, 0.15f, 1f),
            };
            _root.AddChild(ring);

            _turntable = new Node3D { Name = "Turntable" };
            _root.AddChild(_turntable);
            _root.Visible = false;
        }

        /// <summary>
        /// Shows the stage with `bodyId` on it (null = empty) wearing `hairId`
        /// (null or hair.none = bald) and takes the camera.
        /// </summary>
        public void Show(string bodyId, string hairId)
        {
            _root.Visible = true;
            if (!_camera.Current) _camera.Current = true;
            if (hairId != _hairId)
            {
                _hairId = hairId;
                _worn["hair"] = hairId ?? "";
                Dress();   // the body stays; only the piece is swapped
            }
            if (bodyId == _bodyId) return;   // an unknown or failed id is tried once, not every frame
            _bodyId = bodyId;
            _body?.QueueFree();
            _body = null;
            _wornDrawn.Clear();   // the pieces went with the body
            _wornNodes.Clear();
            _turntable.Rotation = Vector3.Zero;   // a new body faces the camera
            if (string.IsNullOrEmpty(bodyId)) return;
            int gen = ++_generation;
            _assets.Attach(bodyId, _turntable, holder =>
            {
                if (gen != _generation) { holder.QueueFree(); return; }
                _body = holder;
                Idle(holder);
                Dress();
            });
        }

        /// <summary>The `hair` slot on the body: `hair.<style>@<body>` when built, the bare piece otherwise.</summary>
        private void Dress()
        {
            if (_body != null) EntityViews.Dress(_assets, a => a, _body, _worn, _wornDrawn, _wornNodes);
        }

        private static void Idle(Node model)
        {
            foreach (AnimationPlayer ap in AssetRegistry.Descendants<AnimationPlayer>(model))
            {
                if (!ap.HasAnimation("idle")) continue;
                ap.Deterministic = true;   // see CharacterAnim: untracked bones go to rest
                ap.GetAnimation("idle").LoopMode = Animation.LoopModeEnum.Linear;
                ap.Play("idle");
                return;
            }
        }

        /// <summary>Drag across the stage turns the body: dx in pixels, left drag turns it left.</summary>
        public void Drag(float dx) => _turntable.RotateY(-dx * DragRadPerPx);

        /// <summary>The camera kept so the body sits mid-stage at any aspect. The body only turns when dragged.</summary>
        public void Frame(double dt)
        {
            if (!_root.Visible) return;
            Viewport vp = _camera.GetViewport();
            if (vp == null) return;
            Vector2 size = vp.GetVisibleRect().Size;
            if (size.Y <= 0) return;
            // Shift the image so the body stands at the centre of the right
            // (1 − ListShare) of the screen: half of that share right of centre.
            float visibleW = 2f * EyeDistance * Mathf.Tan(Mathf.DegToRad(FovDeg / 2f)) * (size.X / size.Y);
            float shift = (CharactersView.ListShare / 2f) * visibleW;
            _camera.HOffset = -shift;
        }

        public void Free()
        {
            _generation++;
            if (GodotObject.IsInstanceValid(_root)) _root.QueueFree();
            _body = null;
            _bodyId = _hairId = null;
            _wornDrawn.Clear();
            _wornNodes.Clear();
        }
    }
}
