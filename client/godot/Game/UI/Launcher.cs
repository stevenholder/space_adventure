// The launcher (docs/GDD.md "Launcher (Phase 15)"): the small window the
// executable opens before the game. It owns the update, says whether the
// server is up and hands over on PLAY.
//
// Two halves. `Launcher` is the pure state machine of the GDD's six-state
// table -- events in, line text and PLAY-enabled out, no engine types, so
// -selftest walks it. `LauncherView` draws it at 560×360, 1:1 pixels.

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

    /// <summary>The launcher window's contents, built in code under a root.</summary>
    public sealed class LauncherView
    {
        public const int Width = 560, Height = 360;

        private readonly Control _root;
        private readonly Label _build, _line, _server;
        private readonly Control _bar;
        private readonly ColorRect _fill;
        private readonly Button _play;

        public LauncherView(Control parent, Action onPlay, Action onQuit)
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

            // The account column: reserved for the login, the frame only.
            var account = Styles.Box(Styles.Slate, Styles.Steel, 2);
            account.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            account.SizeFlagsStretchRatio = 0.4f;
            columns.AddChild(account);

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
            _play.Disabled = !model.PlayEnabled;
        }

        public void SetServer(string line)
        {
            if (_server.Text == line) return;
            _server.Text = line;
            _server.AddThemeColorOverride("font_color", line.Contains("ONLINE") ? Styles.Good : Styles.Danger);
        }
    }
}
