// The launcher (docs/GDD.md "Launcher (Phase 15)"): the small window the
// executable opens before the game. It owns the update, says whether the
// server is up and hands over on PLAY.
//
// Three parts. `Launcher` is the pure state machine of the GDD's six-state
// table -- events in, line text and PLAY-enabled out, no engine types, so
// -selftest walks it. `Login` is the account column's model (Phase 16,
// "Account column in the launcher"), pure the same way. `LauncherView`
// draws both at 560×360, 1:1 pixels.

using System;
using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class Launcher
    {
        public enum State { Checking, Updating, Restarting, UpToDate, DevBuild, Offline }

        public State Now { get; private set; } = State.Checking;
        public string Version { get; private set; } = "";
        public int Percent { get; private set; }
        /// <summary>Why the check failed (the log's, not the line's).</summary>
        public string Reason { get; private set; } = "";

        public void NotInstalled() => Now = State.DevBuild;

        public void Found(string version)
        {
            Version = Bare(version);
            Percent = 0;
            Now = State.Updating;
        }

        public void Progress(int percent)
        {
            if (Now == State.Updating) Percent = Math.Clamp(percent, 0, 100);
        }

        public void Downloaded()
        {
            if (Now == State.Updating) { Percent = 100; Now = State.Restarting; }
        }

        public void Current(string version)
        {
            Version = Bare(version);
            Now = State.UpToDate;
        }

        /// <summary>A check that threw or ran out of time, or a download that failed mid-way.</summary>
        public void Failed(string reason)
        {
            Reason = reason ?? "";
            Now = State.Offline;
        }

        /// <summary>The update line, verbatim from the GDD table.</summary>
        public string Line => Now switch
        {
            State.Checking => "CHECKING FOR UPDATES…",
            State.Updating => $"UPDATING TO v{Version} … {Percent} %",
            State.Restarting => "RESTARTING…",
            State.UpToDate => $"UP TO DATE · v{Version}",
            State.DevBuild => "DEV BUILD · not installed, no update check",
            _ => "UPDATE CHECK FAILED · playing the installed build",
        };

        /// <summary>The bar's fill, 0–1, or −1 when there is no bar.</summary>
        public float Fraction => Now switch
        {
            State.Updating => Percent / 100f,
            State.Restarting => 1f,
            _ => -1f,
        };

        public bool PlayEnabled => Now is State.UpToDate or State.DevBuild or State.Offline;

        private static string Bare(string v) => (v ?? "").TrimStart('v', 'V');

        /// <summary>
        /// The site behind a game URL: `wss://host/ws` → `https://host`,
        /// `ws://host:port/ws` → `http://host:port`. Null if it does not parse.
        /// </summary>
        public static string SiteUrl(string gameUrl)
        {
            if (!Uri.TryCreate(gameUrl, UriKind.Absolute, out Uri u)) return null;
            string scheme = u.Scheme switch { "wss" => "https", "ws" => "http", _ => u.Scheme };
            return $"{scheme}://{u.Authority}";
        }

        /// <summary>The server line; null online = unreachable.</summary>
        public static string ServerLine(int? online) =>
            online is int n ? $"SERVER · ONLINE · {n} PLAYING" : "SERVER · UNREACHABLE";
    }

    /// <summary>
    /// The account column's model (docs/GDD.md "Account column in the
    /// launcher"): four states, the line text, and the sign-in half of
    /// PLAY's two gates. No engine types, so -selftest walks it.
    /// </summary>
    public sealed class Login
    {
        public enum State { SignedOut, Busy, SignedIn, Failed }

        public State Now { get; private set; } = State.SignedOut;
        /// <summary>The signed-in email, "" otherwise.</summary>
        public string Email { get; private set; } = "";
        /// <summary>The Failed reason line, "" otherwise.</summary>
        public string Reason { get; private set; } = "";
        /// <summary>Busy: true = creating an account, false = signing in.</summary>
        public bool Creating { get; private set; }

        /// <summary>A remembered session and email: signed in with no request.</summary>
        public void Remembered(string email) => Ok(email);

        public void Submit(bool creating)
        {
            Creating = creating;
            Reason = "";
            Now = State.Busy;
        }

        public void Ok(string email)
        {
            Email = email ?? "";
            Reason = "";
            Now = State.SignedIn;
        }

        /// <summary>A sign-in or sign-up answered with this HTTP status (0 = no answer).</summary>
        public void Fail(int status) => Failed(status switch
        {
            401 => "WRONG EMAIL OR PASSWORD",
            409 => "EMAIL ALREADY REGISTERED",
            400 => "PASSWORD TOO SHORT",
            429 => "TOO MANY TRIES · wait a moment",
            _ => "SITE UNREACHABLE",
        });

        /// <summary>The remembered session was refused (a 401 on PLAY).</summary>
        public void Refused() => Failed("SIGNED OUT · sign in again");

        public void SignOut()
        {
            Email = "";
            Reason = "";
            Now = State.SignedOut;
        }

        private void Failed(string reason)
        {
            Email = "";
            Reason = reason;
            Now = State.Failed;
        }

        public bool PlayAllowed => Now == State.SignedIn;

        /// <summary>The column's status line, verbatim from the GDD table.</summary>
        public string Line => Now switch
        {
            State.Busy => Creating ? "CREATING ACCOUNT…" : "SIGNING IN…",
            State.SignedIn => "SIGNED IN · " + Shorten(Email),
            State.Failed => Reason,
            _ => "Sign in to play",
        };

        /// <summary>An email cut to at most `max` characters, "…" at the end when cut.</summary>
        public static string Shorten(string email, int max = 26)
        {
            email ??= "";
            if (email.Length <= max) return email;
            return max <= 1 ? "…" : email.Substring(0, max - 1) + "…";
        }
    }

    /// <summary>The launcher window's contents, built in code under a root.</summary>
    public sealed class LauncherView
    {
        public const int Width = 560, Height = 360;

        private readonly Control _root;
        private readonly Label _build, _line, _server;
        private readonly Control _bar;
        private readonly ColorRect _fill;
        private readonly Button _play;
        private readonly LineEdit _email, _password;
        private readonly Button _signIn, _create, _signOut;
        private readonly Control _form;
        private readonly Label _loginLine;
        private bool _loginAllowed; // PLAY's second gate: dark until SetLogin says signed in
        private bool _updateAllowed;
        private Login.State? _lastLogin;

        public LauncherView(Control parent, Action onPlay, Action onQuit,
                            Action<string, string> onSignIn,
                            Action<string, string> onCreate,
                            Action onSignOut)
        {
            _root = new ColorRect { Name = "launcher", Color = Styles.Ink, MouseFilter = Control.MouseFilterEnum.Stop };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            parent.AddChild(_root);

            var margin = new MarginContainer();
            margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            foreach (string side in new[] { "left", "right", "top", "bottom" })
                margin.AddThemeConstantOverride($"margin_{side}", 18);
            _root.AddChild(margin);
            var page = Styles.Column(10);
            margin.AddChild(page);

            // Header: the name, the build label right-aligned.
            var header = Styles.Row(8);
            var title = Styles.Display_("SPACE ADVENTURE", 28, Styles.Amber);
            header.AddChild(Styles.Grow(title));
            _build = Styles.Display_("", 12, Styles.Dust);
            _build.HorizontalAlignment = HorizontalAlignment.Right;
            header.AddChild(_build);
            page.AddChild(header);
            var rule = Styles.Rule();
            rule.Color = Styles.Steel; // an ink rule vanishes on the ink page
            page.AddChild(rule);

            var columns = Styles.Row(16);
            columns.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            page.AddChild(columns);

            // The account column (Phase 16). The frame is a Panel, not a
            // container, so its contents never widen it: the status column
            // stays exactly where Phase 15 put it.
            var account = Styles.Box(Styles.Slate, Styles.Steel, 2);
            account.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            account.SizeFlagsStretchRatio = 0.4f;
            account.MouseFilter = Control.MouseFilterEnum.Pass;
            account.ClipContents = true;
            columns.AddChild(account);
            var pad = new MarginContainer();
            pad.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            foreach (string side in new[] { "left", "right", "top", "bottom" })
                pad.AddThemeConstantOverride($"margin_{side}", 10);
            account.AddChild(pad);
            var col = Styles.Column(6);
            pad.AddChild(col);
            col.AddChild(Styles.Display_("ACCOUNT", 15, Styles.Cream));

            var form = Styles.Column(6);
            _form = form;
            col.AddChild(form);
            _email = Field("EMAIL", false);
            _password = Field("PASSWORD", true);
            form.AddChild(_email);
            form.AddChild(_password);
            void SignIn() => onSignIn?.Invoke(_email.Text.Trim(), _password.Text);
            _email.TextSubmitted += _ => { if (!_signIn.Disabled) SignIn(); };
            _password.TextSubmitted += _ => { if (!_signIn.Disabled) SignIn(); };
            var buttons = Styles.Row(6);
            _signIn = Styles.Button("SIGN IN", false, SignIn);
            _signIn.CustomMinimumSize = new Vector2(0, 26);
            _signIn.AddThemeColorOverride("font_disabled_color", Styles.Dust);
            var dark = new StyleBoxFlat { BgColor = Styles.Steel, BorderColor = Styles.Ink, ContentMarginLeft = 8, ContentMarginRight = 8 };
            dark.SetBorderWidthAll(2);
            _signIn.AddThemeStyleboxOverride("disabled", dark);
            buttons.AddChild(_signIn);
            _create = Secondary("CREATE ACCOUNT", () => onCreate?.Invoke(_email.Text.Trim(), _password.Text));
            buttons.AddChild(Styles.Grow(_create));
            form.AddChild(buttons);

            _loginLine = Styles.Display_("", 13, Styles.Dust);
            _loginLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _loginLine.VerticalAlignment = VerticalAlignment.Top;
            col.AddChild(_loginLine);

            var outRow = Styles.Row(0);
            _signOut = Secondary("SIGN OUT", () => onSignOut?.Invoke());
            outRow.AddChild(_signOut);
            col.AddChild(outRow);
            _signOut.Visible = false;

            // The status column.
            var status = Styles.Column(8);
            status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            status.SizeFlagsStretchRatio = 0.6f;
            columns.AddChild(status);

            _line = Styles.Display_("", 15, Styles.Cream);
            _line.AutowrapMode = TextServer.AutowrapMode.Word;
            _line.CustomMinimumSize = new Vector2(0, 40);
            _line.VerticalAlignment = VerticalAlignment.Bottom;
            status.AddChild(_line);
            var (bar, fill) = Styles.Bar(Styles.Amber, 14);
            _bar = bar;
            _fill = fill;
            status.AddChild(_bar);
            status.AddChild(Styles.Gap(4));
            _server = Styles.Display_("", 13, Styles.Dust);
            status.AddChild(_server);
            status.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

            _play = Styles.Button("PLAY", false, onPlay);
            _play.AddThemeFontSizeOverride("font_size", 22);
            _play.CustomMinimumSize = new Vector2(0, 48);
            _play.AddThemeColorOverride("font_disabled_color", Styles.Dust);
            var off = new StyleBoxFlat { BgColor = Styles.Steel, BorderColor = Styles.Ink };
            off.SetBorderWidthAll(2);
            _play.AddThemeStyleboxOverride("disabled", off);
            status.AddChild(_play);

            var quitRow = Styles.Row(0);
            quitRow.AddChild(Styles.Grow(new Control { MouseFilter = Control.MouseFilterEnum.Ignore }));
            var quit = Styles.Button("QUIT", true, onQuit);
            quit.CustomMinimumSize = new Vector2(72, 24);
            quitRow.AddChild(quit);
            status.AddChild(quitRow);
        }

        public bool Visible => _root.Visible;

        public void Show(bool on) => _root.Visible = on;

        public void Set(Launcher model, string buildLabel)
        {
            if (_build.Text != buildLabel) _build.Text = buildLabel;
            string line = model.Line;
            if (_line.Text != line) _line.Text = line;
            float f = model.Fraction;
            _bar.Modulate = f < 0 ? Colors.Transparent : Colors.White; // keeps its room either way
            Styles.SetFill(_fill, Math.Max(f, 0f));
            _updateAllowed = model.PlayEnabled;
            _play.Disabled = !(_updateAllowed && _loginAllowed);
        }

        /// <summary>Draws the account column per the GDD table; PLAY's sign-in gate.</summary>
        public void SetLogin(Login model)
        {
            Login.State st = model.Now;
            bool signedIn = st == Login.State.SignedIn;
            bool busy = st == Login.State.Busy;

            _form.Visible = !signedIn;
            _signOut.Visible = signedIn;
            _email.Editable = !busy;
            _password.Editable = !busy;
            _signIn.Disabled = busy;
            _create.Disabled = busy;
            // Cleared on entering Failed or SignedIn, not on every redraw:
            // a retype after a failure must survive the next SetLogin.
            if (st != _lastLogin && (st is Login.State.Failed or Login.State.SignedIn))
                _password.Text = "";
            _lastLogin = st;

            string line = model.Line;
            if (_loginLine.Text != line) _loginLine.Text = line;
            _loginLine.AddThemeColorOverride("font_color", st == Login.State.Failed ? Styles.Danger : Styles.Dust);

            _loginAllowed = model.PlayAllowed;
            _play.Disabled = !(_updateAllowed && _loginAllowed);
        }

        private static LineEdit Field(string placeholder, bool secret)
        {
            var f = new LineEdit { PlaceholderText = placeholder, Secret = secret, CustomMinimumSize = new Vector2(0, 28) };
            f.AddThemeFontSizeOverride("font_size", 14);
            return f;
        }

        /// <summary>A small steel button beside the amber primary (CREATE ACCOUNT, SIGN OUT).</summary>
        private static Button Secondary(string text, Action onPressed)
        {
            var b = Styles.Button(text, false, onPressed);
            b.AddThemeFontSizeOverride("font_size", 11);
            b.CustomMinimumSize = new Vector2(0, 26);
            foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                b.AddThemeColorOverride(state, Styles.Cream);
            b.AddThemeColorOverride("font_disabled_color", Styles.Dust);
            foreach (var (state, tint) in new[] { ("normal", 1f), ("hover", 1.25f), ("pressed", 0.85f), ("focus", 1f), ("disabled", 0.8f) })
            {
                var sb = new StyleBoxFlat
                {
                    BgColor = new Color(Styles.Steel.R * tint, Styles.Steel.G * tint, Styles.Steel.B * tint),
                    BorderColor = Styles.Ink,
                    ContentMarginLeft = 6, ContentMarginRight = 6,
                    ContentMarginTop = 2, ContentMarginBottom = 2,
                };
                sb.SetBorderWidthAll(2);
                b.AddThemeStyleboxOverride(state, sb);
            }
            return b;
        }

        public void SetServer(string line)
        {
            if (_server.Text == line) return;
            _server.Text = line;
            _server.AddThemeColorOverride("font_color", line.Contains("ONLINE") ? Styles.Good : Styles.Danger);
        }
    }
}
