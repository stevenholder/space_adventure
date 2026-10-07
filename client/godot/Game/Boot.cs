// The frame loop. Everything else is a subsystem; this is the file that makes
// them a game.
//
// Code-first, per client/CONVENTIONS.md: Boot.tscn is one Node carrying this
// script and nothing else. The whole hierarchy is built here at runtime, so
// the repo carries reviewable C# instead of scene files.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Godot;
using Velopack;
using Velopack.Sources;
using SpaceAdventure.Game.UI;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public partial class Boot : Node
    {
        // ---- config ---------------------------------------------------------

        /// <summary>
        /// User arguments are everything after `--` on the command line, so the
        /// flag names the Unity player took (-serverUrl, -token, -quitAfter …)
        /// work unchanged: `SpaceAdventure -- -serverUrl ws://…`.
        /// </summary>
        private static string Arg(string name)
        {
            string[] argv = OS.GetCmdlineUserArgs();
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == name) return argv[i + 1];
            }
            return null;
        }

        /// <summary>-rigWorn pieces waiting for the local body to attach.</summary>
        private readonly List<(string slot, string asset)> _rigWorn = new List<(string, string)>();

        private static bool Flag(string name) => Array.IndexOf(OS.GetCmdlineUserArgs(), name) >= 0;

        /// <summary>
        /// Server URL. C45 requires this to come from config rather than being
        /// compiled in, so -serverUrl or the env var wins over the default.
        /// </summary>
        /// <summary>What the HUD says while there is no world to look at.</summary>
        private string LinkBanner() => _net == null ? "" : _net.State switch
        {
            LinkState.Failed => $"CONNECTION FAILED: {_serverUrl} — {_net.LastError}",
            LinkState.Reconnecting => $"RECONNECTING to {_serverUrl}… ({_net.LastError})",
            LinkState.Joined => "JOINED — loading the world…",
            _ => $"CONNECTING to {_serverUrl}…",
        };

        private static string ResolveServerUrl()
        {
            string url = Arg("-serverUrl");
            if (url != null) return url;
            string env = System.Environment.GetEnvironmentVariable("SA_SERVER_URL");
            if (!string.IsNullOrEmpty(env)) return env;
            // From the editor (and every godot-cli flow, which passes -serverUrl)
            // the default is the local stack. A double-clicked export has no
            // server on its localhost -- the first Windows launch sat on an
            // empty HUD -- so it goes to the public door.
            return OS.HasFeature("editor") ? "ws://127.0.0.1:18080/ws" : PublicServerUrl;
        }

        /// <summary>The public door (docs/RUNBOOK.md): Cloudflare → NPM → Traefik.</summary>
        private const string PublicServerUrl = "wss://game.stevenholder.info/ws";
        private string _serverUrl;

        private const string ConfigPath = "user://sa.cfg";

        /// <summary>
        /// The character token on disk (written at PLAY so Reconnect keeps
        /// working); -token overrides (the rigs use it). Phase 16: never
        /// minted here -- a token comes from the account's character list,
        /// so a config without one is "".
        /// </summary>
        private static string ResolveToken()
        {
            string arg = Arg("-token");
            if (arg != null) return arg;
            return Identity("token");
        }

        private static void SaveToken(string token) => SaveIdentity("token", token);

        /// <summary>`[identity] session` / `email` / `token`; a missing file or key is "".</summary>
        private static string Identity(string key)
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath); // a missing file is an empty config
            return (string)cf.GetValue("identity", key, "");
        }

        private static void SaveIdentity(string key, string value)
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath);
            cf.SetValue("identity", key, value);
            cf.Save(ConfigPath);
        }

        /// <summary>Signed out (or the session died): session, email and token all go.</summary>
        private static void ClearIdentity()
        {
            var cf = new ConfigFile();
            if (cf.Load(ConfigPath) != Error.Ok) return;
            foreach (string key in new[] { "session", "email", "token" })
                if (cf.HasSectionKey("identity", key)) cf.EraseSectionKey("identity", key);
            cf.Save(ConfigPath);
        }

        // ---- state ----------------------------------------------------------

        private NetClient _net;
        private Predictor _predictor;
        private FpsController _fps;
        private SnapshotTimeline _timeline;
        private readonly InputState _input = new InputState();

        private Camera3D _camera;
        private OmniLight3D _rigLight;
        private DirectionalLight3D _sun;
        private GameMenuView _gameMenu;

        /// <summary>The viewmodel's render layer (bit 2): the overlay camera draws only this.</summary>
        private const uint VmLayer = 1u << 1;

        private ViewModel _viewModel;
        private CombatFx _fx;
        private Sfx _sfx;
        private float _stepDist;
        private int _stepN;

        /// <summary>
        /// weapon.pulse fire_interval, from the GDD weapon table. The server
        /// is the authority either way and simply drops anything early.
        /// </summary>
        private const double FireIntervalSeconds = 0.15;
        private double _nextFireAt;

        private AssetRegistry _assets;
        private EntityViews _views;
        private Structures _structures;
        private Rocks _rocks;
        private WorldEnvironment _environment;

        // Phase 4/5 — seat occupancy, from our own snapshot row (the mode byte
        // is a declaration; occupancy is what the server says). Seat 1 drives.
        private uint _seatVehicle;
        private ushort _seat;
        private readonly RoverPredictor _rover = new RoverPredictor();
        private readonly ShipPredictor _ship = new ShipPredictor();

        // Mouse delta accumulated ACROSS the frames within one tick while
        // piloting — per-frame deltas consumed per-tick would drop most of
        // the motion.
        private Vector2 _mouseAccum;

        /// <summary>k_rate (GDD flight input map): mouse px/s → rad/s.</summary>
        private const float KRate = 0.0022f;

        // GDD "Rover seats" seat_eye column, sim frame, indexed by seat.
        private static readonly Vec3[] RoverSeatEye =
        {
            default,
            new Vec3(-0.35, 1.55, +0.10), // driver
            new Vec3(+0.35, 1.55, -0.40), // passenger
        };

        // GDD "Seats and occupancy" ship seat_eye column, sim frame.
        private static readonly Vec3[] ShipSeatEye =
        {
            default,
            new Vec3(0.00, 2.21, +1.90), // pilot
            new Vec3(+0.35, 2.21, +0.75),
            new Vec3(-0.35, 2.21, +0.75),
        };

        /// <summary>The seated vehicle's EntityType, 0 when on foot.</summary>
        private ushort SeatKind =>
            _seat != 0 && _views.TryGet(_seatVehicle, out var v) ? v.Type : (ushort)0;

        private bool Piloting => _seat == 1 && SeatKind == EntityType.Ship;

        /// <summary>
        /// On foot, or riding a rover's passenger seat: the open buggy has a
        /// gunner, the driver's hands are on the wheel, a ship's crew is
        /// inside a hull. Mirrors server.go fireLocked, which drops the rest.
        /// </summary>
        /// <summary>The melee table of the weapon in hand, null for a gun or empty hands.</summary>
        private MeleeDef Swung => _character.Defs.Item(_character.Held)?.Melee;

        private bool CanShoot => _seat == 0 || (_seat >= 2 && SeatKind == EntityType.Vehicle);
        private Hud _hud;
        private Character _character;
        private Interaction _interact;

        // The interface layer.
        private UiRoot _ui;
        private HudView _hudView;
        private CombatFeed _combatFeed;
        private ShopView _shopView;
        private BenchView _benchView; // Phase 12
        private Settings _settings;
        private SettingsView _settingsView;
        private Hotbar _hotbar; // Phase 13
        private HotbarView _hotbarView;
        private readonly ChatLog _chat = new ChatLog();
        private ChatView _chatView;
        private readonly List<(string name, Vector3 pos, double until)> _scanPings = new List<(string, Vector3, double)>();
        // Phase 12 gather channel as drawn: the bar runs from start to end.
        private double _channelStart = -1, _channelEnd = -1;
        private string _channelLabel = "";
        private BackpackView _bagsView;
        private CharacterView _sheetView;
        private Icons _icons;
        private PromptView _promptView;
        private readonly MissionLog _missionLog = new MissionLog();
        private readonly PartyState _partyState = new PartyState();
        private JournalView _journalView;
        private PartyView _partyView;
        private PartyFrames _partyFrames;
        private readonly SkillSheet _skills = new SkillSheet();
        private SkillsView _skillsView;
        private SkillsFeed _skillsFeed;
        private MapView _map;
        private readonly List<(string, int, int, bool, bool)> _framesScratch = new List<(string, int, int, bool, bool)>();

        private bool ModalOpen =>
            (_shopView?.Open ?? false) || (_bagsView?.Open ?? false) ||
            (_sheetView?.Open ?? false) ||
            (_journalView?.Open ?? false) || (_partyView?.Open ?? false) ||
            (_skillsView?.Open ?? false) || (_gameMenu?.Open ?? false) ||
            (_benchView?.Open ?? false) || (_settingsView?.Open ?? false);

        private TerrainField _terrain;
        private Sim.Collider[] _colliders = Array.Empty<Sim.Collider>();
        private MeshInstance3D _planet;
        private double _noticeUntil;

        // One material for everything with geometry: terrain, bodies, props
        // and the viewmodel all carry their colour in the vertex stream.
        // Godot's standard material reads it; no custom shader needed.
        private StandardMaterial3D _material;

        private ushort _seq;

        /// <summary>
        /// cmd sequence, counted apart from the input seq. `cmd` correlates a
        /// request with its result; `input.seq` is what the shot rewind is
        /// computed from. Sharing one counter would make a shop purchase move
        /// where the next bullet lands.
        /// </summary>
        private ushort _cmdSeq;
        private ushort NextCmdSeq() => ++_cmdSeq;

        private double _tickAccumulator;
        private bool _worldBuilt;

        // Frame-budget evidence (C46): every 5 s, the window's average fps and
        // worst frame, in the log where `godot-cli run` can read them.
        private double _statWindowStart;
        private int _statFrames;
        private double _statWorstDt;

        private double _quitAfter = -1;
        private double _elapsed;

        /// <summary>Set once installed: the feed this session keeps watching.</summary>
        private UpdateManager _updates;
        /// <summary>An update downloaded mid-session, applied when the game closes.</summary>
        private VelopackAsset _updateReady;
        private double _nextUpdateCheck;
        private bool _updateChecking;

        /// <summary>
        /// A deploy publishes its release a few minutes after the server
        /// restarts; 12 checks an hour sits well under GitHub's 60/h anonymous
        /// limit. SA_UPDATE_EVERY (seconds) shortens it for a test.
        /// </summary>
        private static readonly double UpdateEvery =
            double.TryParse(System.Environment.GetEnvironmentVariable("SA_UPDATE_EVERY"), NumberStyles.Float, CultureInfo.InvariantCulture, out double e) ? e : 300;

        /// <summary>
        /// What build this is, top-right on the HUD and in a bug report:
        /// the release number when installed (`v1.0.57`), `dev` otherwise,
        /// plus the commit the SDK stamps into the assembly.
        /// </summary>
        private string BuildLabel => _updates != null ? $"v{_updates.CurrentVersion}{Sha}" : $"dev{Sha}";

        /// <summary>" · abc1234" from the commit the SDK stamps into the assembly, or "".</summary>
        private static readonly string Sha = ((System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
            typeof(Boot).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute)))?.InformationalVersion is { } info
            && info.IndexOf('+') is int plus and >= 0 && info.Length >= plus + 8 ? $" · {info.Substring(plus + 1, 7)}" : "";

        /// <summary>
        /// An installed client (Setup.exe / AppImage) updates itself from the
        /// GitHub releases; deploy.yml publishes the feed. Source runs,
        /// godot-cli flows and loose exports are not installed (DevBuild).
        /// The check gets 5 s; a timeout, a throw or a failed download is
        /// Offline and plays the current build: an unreachable GitHub must
        /// never keep anyone out of the game. Drives `_launch`; true when
        /// an update was downloaded and the process is restarting into it.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> UpdateStep()
        {
            try
            {
                // SA_UPDATE_SOURCE: a local dir or URL feed, for testing an update end to end.
                string feed = System.Environment.GetEnvironmentVariable("SA_UPDATE_SOURCE");
                var mgr = string.IsNullOrEmpty(feed)
                    ? new UpdateManager(new GithubSource(ReleasesRepo, null, true))
                    : new UpdateManager(feed);
                if (!mgr.IsInstalled)
                {
                    _launch.NotInstalled();
                    return false;
                }
                _updates = mgr;
                _nextUpdateCheck = Clock.Now + UpdateEvery;

                var check = mgr.CheckForUpdatesAsync();
                if (await System.Threading.Tasks.Task.WhenAny(check, System.Threading.Tasks.Task.Delay(CheckBudgetMs)) != check)
                {
                    _ = check.ContinueWith(t => _ = t.Exception, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException($"no answer in {CheckBudgetMs / 1000} s");
                }
                if (await check is not { } update)
                {
                    _launch.Current(mgr.CurrentVersion?.ToString());
                    return false;
                }
                GD.Print($"update: {mgr.CurrentVersion} -> {update.TargetFullRelease.Version}");
                _launch.Found(update.TargetFullRelease.Version.ToString());
                await mgr.DownloadUpdatesAsync(update, p => _launch.Progress(p));
                _launch.Downloaded();
                await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout); // let RESTARTING… draw
                // Not ApplyUpdatesAndRestart: that Environment.Exit()s under a
                // running engine. Hand the swap to Velopack and quit cleanly.
                mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, OS.GetCmdlineArgs());
                GetTree().Quit(0);
                return true;
            }
            catch (Exception e)
            {
                GD.Print($"update: skipped ({e.Message})");
                _launch.Failed(e.Message);
                return false;
            }
        }

        private const int CheckBudgetMs = 5000;

        /// <summary>The rig and -play: update (if installed), then straight in, no launcher.</summary>
        private async System.Threading.Tasks.Task UpdateThenConnect()
        {
            if (await UpdateStep()) return;
            Connect(RigToken());
        }

        /// <summary>
        /// The token a run that skips the login connects with: -token, else
        /// the one on disk, else (a rig with no config -- the kind stack
        /// seats any token) a fresh one, not saved.
        /// </summary>
        private static string RigToken()
        {
            string token = ResolveToken();
            return token != "" ? token : Guid.NewGuid().ToString("N");
        }

        private void Connect(string token) => _net.Connect(_serverUrl, System.Environment.MachineName ?? "player", token);

        // ---- the launcher (GDD "Launcher (Phase 15)") ------------------------

        private readonly Launcher _launch = new Launcher();
        private LauncherView _launcherView;
        private bool _launcherUp;
        private double _nextStats;
        private bool _statsBusy;
        private static readonly System.Net.Http.HttpClient StatsHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // Phase 16: the account column (GDD "Accounts, launcher login and characters").
        private readonly Login _login = new Login();
        private bool _playBusy;
        private ColorRect _curtain;
        /// <summary>Sign in, register, characters, logout; each call carries its own 5 s budget.</summary>
        private static readonly System.Net.Http.HttpClient AccountHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private const int AccountBudgetMs = 5000;
        private Window.ContentScaleModeEnum _gameScaleMode;
        private Window.ContentScaleAspectEnum _gameScaleAspect;
        private Vector2I _gameScaleSize;
        /// <summary>OpenLauncherWindow kept the game's stretch (a rig select never opened the launcher).</summary>
        private bool _launcherWindowSaved;

        /// <summary>A player's launch: everything not rigged and not `-play`; `-uiLauncher` photographs it.</summary>
        private bool LauncherMode => Arg("-uiChars") == null &&
            ((!Rigged && !Flag("-play")) || Arg("-uiLauncher") != null || Arg("-uiPlayAfter") != null || Arg("-uiPlayReal") != null);

        /// <summary>The 560×360 window at 1:1 pixels; the game's stretch is kept to restore on PLAY.</summary>
        private void OpenLauncherWindow()
        {
            Window w = GetWindow();
            _launcherWindowSaved = true;
            _gameScaleMode = w.ContentScaleMode;
            _gameScaleAspect = w.ContentScaleAspect;
            _gameScaleSize = w.ContentScaleSize;
            w.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
            w.ContentScaleFactor = 1f;
            w.Mode = Window.ModeEnum.Windowed;
            w.Borderless = false; // created as a transparent borderless dot (project.godot)
            w.Transparent = false;
            w.TransparentBg = false;
            w.Unresizable = true;
            w.Title = "Space Adventure";
            w.Size = new Vector2I(LauncherView.Width, LauncherView.Height);
            w.MoveToCenter();
            GD.Print($"window: launcher {w.Size.X}x{w.Size.Y} at {w.Position.X},{w.Position.Y}");
        }

        /// <summary>
        /// The game's window: the project's canvas size clamped to the screen,
        /// centred, resizable. The OS window opens at the LAUNCHER's size
        /// (project.godot window_*_override, so the launcher never flashes a
        /// 1920x1080 frame first); rig and -play runs grow it here at boot and
        /// PLAY grows it after the launcher. A window that is not the launcher
        /// size is left alone: a CLI --resolution, or a display mode already
        /// applied.
        /// </summary>
        private void OpenGameWindow()
        {
            Window w = GetWindow();
            w.Borderless = false; // created as a transparent borderless dot (project.godot)
            w.Transparent = false;
            w.TransparentBg = false;
            w.Unresizable = false;
            // The boot dot, or the launcher's window: grow to the canvas. A
            // CLI --resolution (rig shots) is bigger than both and is kept.
            if (w.Size.X <= LauncherView.Width)
            {
                Rect2I usable = DisplayServer.ScreenGetUsableRect(w.CurrentScreen);
                var size = new Vector2I(Math.Min(w.ContentScaleSize.X, usable.Size.X), Math.Min(w.ContentScaleSize.Y, usable.Size.Y));
                // Position first, for where the grown window will sit, then
                // grow: the window expands in place instead of growing from
                // the launcher's corner and jumping to the centre afterwards.
                w.Position = usable.Position + (usable.Size - size) / 2;
                w.Size = size;
            }
            else w.MoveToCenter();
            GD.Print($"window: game {w.Size.X}x{w.Size.Y} at {w.Position.X},{w.Position.Y}");
        }

        /// <summary>The launcher's UI, over everything; the HUD stays hidden until PLAY.</summary>
        private void BuildLauncher()
        {
            var layer = new CanvasLayer { Name = "Launcher", Layer = 120 };
            AddChild(layer);
            var root = new Control { Name = "root", MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(root);
            _launcherView = new LauncherView(root, Play, () => GetTree().Quit(0),
                (email, pw) => _ = SignIn(email, pw, false),
                (email, pw) => _ = SignIn(email, pw, true),
                SignOut);
            _launcherView.SetServer("SERVER · …");
            // The curtain: a black full-rect over everything while the window
            // changes shape between the launcher and the select. Without it the
            // launcher's canvas is seen rescaled into the half-grown window for
            // a frame or two, top-left, before the select appears.
            var curtainLayer = new CanvasLayer { Name = "Curtain", Layer = 200 };
            AddChild(curtainLayer);
            _curtain = new ColorRect { Name = "curtain", Color = Styles.Ink, Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
            _curtain.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            curtainLayer.AddChild(_curtain);
            string savedEmail = Identity("email");
            if (savedEmail != "" && Identity("session") != "") _login.Remembered(savedEmail);
            _launcherUp = true;
            _ui.Root.Visible = false;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }

        /// <summary>The launcher's frame: the model onto the view, the server poll.</summary>
        private void LauncherFrame()
        {
            _launcherView.Set(_launch, BuildLabel);
            _launcherView.SetLogin(_login);
            // Rig: -uiPlayAfter <s> presses PLAY, so the restore-and-connect
            // path runs headless (`world ready` under -quitAfter is the proof).
            string playAfter = Arg("-uiPlayAfter");
            if (playAfter != null && _elapsed >= double.Parse(playAfter, CultureInfo.InvariantCulture)) { Play(); return; }
            // Rig: -uiPlayReal <s> presses PLAY with no rig bypass -- the real
            // session, the real character select.
            string playReal = Arg("-uiPlayReal");
            if (playReal != null && _elapsed >= double.Parse(playReal, CultureInfo.InvariantCulture)) { Play(); if (!_launcherUp) return; }
            if (Arg("-uiLauncher") != null || _statsBusy || Clock.Now < _nextStats) return;
            _nextStats = Clock.Now + 10;
            _ = PollStats();
        }

        /// <summary>GET &lt;site&gt;/api/stats; any failure is UNREACHABLE. Never gates PLAY.</summary>
        private async System.Threading.Tasks.Task PollStats()
        {
            _statsBusy = true;
            int? online = null;
            try
            {
                string site = Launcher.SiteUrl(_serverUrl) ?? throw new FormatException(_serverUrl);
                string body = await StatsHttp.GetStringAsync(site + "/api/stats");
                online = (int?)Newtonsoft.Json.Linq.JObject.Parse(body)["online"];
            }
            catch (Exception e)
            {
                GD.Print($"launcher: stats unreachable ({e.Message})");
            }
            _statsBusy = false;
            if (_launcherUp) _launcherView.SetServer(Launcher.ServerLine(online));
        }

        /// <summary>A POST with the sa-client header (every mutating route wants it) and an optional bearer.</summary>
        private static HttpRequestMessage AccountRequest(HttpMethod method, string url, string json, string session)
        {
            var req = new HttpRequestMessage(method, url);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (method != HttpMethod.Get) req.Headers.Add("X-Requested-With", "sa-client");
            if (!string.IsNullOrEmpty(session)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
            return req;
        }

        /// <summary>
        /// SIGN IN / CREATE ACCOUNT: (register, then) game-login inside one
        /// 5 s budget; the session and email are kept, the password never --
        /// it lives in this call's arguments and is neither stored nor logged.
        /// </summary>
        private async System.Threading.Tasks.Task SignIn(string email, string password, bool creating)
        {
            if (_login.Now == Login.State.Busy) return;
            _login.Submit(creating);
            using var budget = new System.Threading.CancellationTokenSource(AccountBudgetMs);
            try
            {
                string site = Launcher.SiteUrl(_serverUrl) ?? throw new FormatException(_serverUrl);
                string body = new Newtonsoft.Json.Linq.JObject { ["email"] = email, ["password"] = password }.ToString(Newtonsoft.Json.Formatting.None);
                if (creating)
                {
                    using HttpRequestMessage reg = AccountRequest(HttpMethod.Post, site + "/api/register", body, null);
                    using HttpResponseMessage regResp = await AccountHttp.SendAsync(reg, budget.Token);
                    if (!regResp.IsSuccessStatusCode)
                    {
                        GD.Print($"launcher: register refused ({(int)regResp.StatusCode})");
                        _login.Fail((int)regResp.StatusCode);
                        return;
                    }
                }
                using HttpRequestMessage req = AccountRequest(HttpMethod.Post, site + "/api/game-login", body, null);
                using HttpResponseMessage resp = await AccountHttp.SendAsync(req, budget.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    GD.Print($"launcher: sign-in refused ({(int)resp.StatusCode})");
                    _login.Fail((int)resp.StatusCode);
                    return;
                }
                string text = await resp.Content.ReadAsStringAsync(budget.Token);
                string session = (string)Newtonsoft.Json.Linq.JObject.Parse(text)["session"];
                if (string.IsNullOrEmpty(session)) throw new FormatException("no session in the game-login reply");
                SaveIdentity("session", session);
                SaveIdentity("email", email);
                _login.Ok(email);
                GD.Print("launcher: signed in");
            }
            catch (Exception e)
            {
                GD.Print($"launcher: sign-in unreachable ({e.GetType().Name})");
                _login.Fail(0);
            }
        }

        /// <summary>SIGN OUT: end the session on the server (best effort), forget it here.</summary>
        private void SignOut()
        {
            string session = Identity("session");
            string site = Launcher.SiteUrl(_serverUrl);
            if (session != "" && site != null) _ = Logout(site, session);
            ClearIdentity();
            _login.SignOut();
        }

        private static async System.Threading.Tasks.Task Logout(string site, string session)
        {
            try
            {
                using var budget = new System.Threading.CancellationTokenSource(AccountBudgetMs);
                using HttpRequestMessage req = AccountRequest(HttpMethod.Post, site + "/api/logout", null, session);
                using HttpResponseMessage resp = await AccountHttp.SendAsync(req, budget.Token);
            }
            catch (Exception)
            {
                // Best effort: the session is forgotten here either way.
            }
        }

        /// <summary>
        /// PLAY: (signed in) the game window on the character select (GDD
        /// "Character select"); the world waits for a character. Rigs
        /// (-token, -uiPlayAfter) skip the login and the select.
        /// </summary>
        private void Play()
        {
            if (!_launcherUp || _playBusy || !_launch.PlayEnabled) return;
            if (Arg("-token") != null || Arg("-uiPlayAfter") != null) { EnterGame(RigToken()); return; }
            if (!_login.PlayAllowed) return;
            _ = PlayToSelect();
        }

        /// <summary>
        /// PLAY → select behind the curtain: the launcher hides and the curtain
        /// draws for one frame BEFORE the window changes shape, the select is
        /// built and given two frames to lay out and draw its stage, then the
        /// curtain lifts. Nothing in between is ever seen.
        /// </summary>
        private async System.Threading.Tasks.Task PlayToSelect()
        {
            _playBusy = true;
            _curtain.Visible = true;
            _launcherUp = false;
            _launcherView.Show(false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            OpenGame();
            OpenSelect(fake: false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _curtain.Visible = false;
            _playBusy = false;
        }

        /// <summary>
        /// The game window after the launcher: the game's canvas stretch back,
        /// the saved display mode and UI scale, the launcher hidden. No connect.
        /// </summary>
        private void OpenGame()
        {
            _launcherUp = false;
            _launcherView?.Show(false);
            Window w = GetWindow();
            if (_launcherWindowSaved)
            {
                w.ContentScaleMode = _gameScaleMode;
                w.ContentScaleAspect = _gameScaleAspect;
                w.ContentScaleSize = _gameScaleSize;
            }
            OpenGameWindow();
            ApplySettings();
        }

        private void EnterGame(string token)
        {
            OpenGame();
            _ui.Root.Visible = true;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Captured;
            Connect(token);
        }

        // ---- the character select (GDD "Character select", Phase 16) ----------

        private Characters _chars;
        private CharactersView _charsView;
        private CanvasLayer _charsLayer;
        private CharacterStage _stage;
        private string _stageBody, _stageHair;
        private bool _selectUp;
        private bool _createBusy;
        /// <summary>The session came from the select: a 1008 before the first snapshot returns there.</summary>
        private bool _selectBorn;
        private bool _gotSnapshot;

        /// <summary>The chosen character's body (row.Body), set at PLAY in the select.</summary>
        public string SelfBody { get; private set; } = "char.player";

        /// <summary>
        /// The select over the game canvas: its own layer above the HUD, the
        /// stage under Boot (main viewport, its own camera and light), the
        /// list loading. `fake` (-uiChars) leaves the model to the rig.
        /// </summary>
        private void OpenSelect(bool fake)
        {
            _ui.Root.Visible = false;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
            _chars = new Characters();
            _charsLayer = new CanvasLayer { Name = "Characters", Layer = 110 };
            AddChild(_charsLayer);
            var root = new Control { Name = "root", MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _charsLayer.AddChild(root);
            // Positional, in the contract's order: play, new, create, cancel,
            // sign out, retry, select, name, female, vanguard, hair prev, hair next,
            // then Phase 18's edit, save, delete, confirm delete, keep.
            _charsView = new CharactersView(root,
                PlaySelected,
                () => _chars.NewCharacter(),
                () => _ = CreateCharacter(),
                () => _chars.Cancel(),
                SignOutFromSelect,
                () => _ = LoadCharacters(),
                i => _chars.Select(i),
                n => _chars.SetName(n),
                f => _chars.SetFemale(f),
                v => _chars.SetVanguard(v),
                () => _chars.PrevHair(),
                () => _chars.NextHair(),
                i => _chars.Edit(i),
                () => _ = SaveCharacter(),
                () => _chars.AskDelete(),
                () => _ = DeleteCharacter(),
                () => _chars.KeepIt());
            _charsView.Show(true);
            _stage = new CharacterStage(this, _assets);
            _charsView.OnStageDrag = dx => _stage?.Drag(dx);
            _charsView.OnExit = () => GetTree().Quit(0);
            _sun.Visible = false; // the world is not built; the Sun's shadow cascades only striped the stage
            _stageBody = _stageHair = null;
            _selectUp = true;
            GD.Print("select: open");
            if (!fake) _ = LoadCharacters();
        }

        /// <summary>The select's frame: the model onto the view, the stage turning, its body and hair following the model.</summary>
        private void SelectFrame(double dt)
        {
            _charsView.Set(_chars);
            string body = _chars.StageBody ?? "";   // "" = the empty stage (Loading, Failed)
            string hair = _chars.StageHair ?? "";
            if (body != _stageBody || hair != _stageHair)
            {
                _stageBody = body;
                _stageHair = hair;
                _stage.Show(body == "" ? null : StageAsset(body), hair == "" ? null : hair);
            }
            _stage.Frame(dt);
            // Rig: -uiSelectPlay <s> presses PLAY in the select once a row is
            // selectable, so the select → world path runs headless.
            string selectPlay = Arg("-uiSelectPlay");
            if (selectPlay != null && _chars.CanPlay && _elapsed >= double.Parse(selectPlay, CultureInfo.InvariantCulture)) PlaySelected();
        }

        /// <summary>A body id the registry can draw: the `.f` asset falls back to its model's male body until it lands.</summary>
        private string StageAsset(string id)
        {
            if (_assets.Has(id)) return id;
            if (id.EndsWith(".f", StringComparison.Ordinal) && _assets.Has(id.Substring(0, id.Length - 2))) return id.Substring(0, id.Length - 2);
            return "char.player";
        }

        private void CloseSelect()
        {
            _selectUp = false;
            _sun.Visible = true;
            _stage?.Free();
            _stage = null;
            _charsView?.Show(false);
            _charsView = null;
            _charsLayer?.QueueFree();
            _charsLayer = null;
            _camera.Current = true;
        }

        /// <summary>The select gone, the launcher window back (a dead session, SIGN OUT).</summary>
        private void BackToLauncher()
        {
            if (_launcherView == null) { CloseSelect(); GetTree().Quit(0); return; } // a rig select has no launcher to return to
            _ = BackToLauncherCurtained();
        }

        private async System.Threading.Tasks.Task BackToLauncherCurtained()
        {
            _curtain.Visible = true;
            CloseSelect();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            OpenLauncherWindow();
            _launcherView.Show(true);
            _launcherUp = true;
            _ui.Root.Visible = false;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _curtain.Visible = false;
        }

        /// <summary>A 401 anywhere in the select: both keys cleared, `SIGNED OUT · sign in again`.</summary>
        private void SessionRefused()
        {
            GD.Print("select: session refused, signed out");
            ClearIdentity();
            _login.Refused();
            BackToLauncher();
        }

        private void SignOutFromSelect()
        {
            SignOut();
            BackToLauncher();
        }

        /// <summary>GET /api/characters (RETRY, a vanished character): a fresh model in Loading, then the rows.</summary>
        private async System.Threading.Tasks.Task LoadCharacters()
        {
            var chars = _chars = new Characters();
            try
            {
                string site = Launcher.SiteUrl(_serverUrl) ?? throw new FormatException(_serverUrl);
                using var budget = new System.Threading.CancellationTokenSource(AccountBudgetMs);
                using HttpRequestMessage req = AccountRequest(HttpMethod.Get, site + "/api/characters", null, Identity("session"));
                using HttpResponseMessage resp = await AccountHttp.SendAsync(req, budget.Token);
                if (!_selectUp || chars != _chars) return;
                if ((int)resp.StatusCode == 401) { SessionRefused(); return; }
                if (!resp.IsSuccessStatusCode)
                {
                    GD.Print($"select: characters failed ({(int)resp.StatusCode})");
                    chars.Fail(LoadFailed);
                    return;
                }
                var rows = new List<CharacterRow>();
                foreach (Newtonsoft.Json.Linq.JToken r in Newtonsoft.Json.Linq.JArray.Parse(await resp.Content.ReadAsStringAsync(budget.Token)))
                    rows.Add(ParseRow(r));
                if (!_selectUp || chars != _chars) return;
                chars.Loaded(rows);
                GD.Print($"select: {rows.Count} characters");
            }
            catch (Exception e)
            {
                GD.Print($"select: characters unreachable ({e.GetType().Name})");
                if (_selectUp && chars == _chars) chars.Fail(LoadFailed);
            }
        }

        private const string LoadFailed = "COULD NOT LOAD CHARACTERS · retry";

        private static CharacterRow ParseRow(Newtonsoft.Json.Linq.JToken r) => new CharacterRow
        {
            Token = (string)r["token"] ?? "",
            Name = (string)r["name"] ?? "",
            Body = string.IsNullOrEmpty((string)r["body"]) ? "char.player" : (string)r["body"],
            Hair = string.IsNullOrEmpty((string)r["hair"]) ? "hair.none" : (string)r["hair"],   // an old server sends none
            Credits = (long?)r["credits"] ?? 0,
            LastSeenMs = (long?)r["last_seen_ms"] ?? 0,
        };

        /// <summary>CREATE: POST /api/characters {name, body, hair}; 200 lists and selects it, 400/409 show the server's reason.</summary>
        private async System.Threading.Tasks.Task CreateCharacter()
        {
            if (_createBusy || !_chars.CanCreate) return;
            _createBusy = true;
            var chars = _chars;
            try
            {
                string site = Launcher.SiteUrl(_serverUrl) ?? throw new FormatException(_serverUrl);
                string json = new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = chars.Name,
                    ["body"] = Characters.BodyId(chars.Female, chars.Vanguard),
                    ["hair"] = chars.Hair,
                }.ToString(Newtonsoft.Json.Formatting.None);
                using var budget = new System.Threading.CancellationTokenSource(AccountBudgetMs);
                using HttpRequestMessage req = AccountRequest(HttpMethod.Post, site + "/api/characters", json, Identity("session"));
                using HttpResponseMessage resp = await AccountHttp.SendAsync(req, budget.Token);
                string text = await resp.Content.ReadAsStringAsync(budget.Token);
                if (!_selectUp || chars != _chars) return;
                if ((int)resp.StatusCode == 401) { SessionRefused(); return; }
                if (!resp.IsSuccessStatusCode)
                {
                    GD.Print($"select: create refused ({(int)resp.StatusCode} {text.Trim()})");
                    chars.CreateFailed((int)resp.StatusCode, text.Trim());
                    return;
                }
                CharacterRow row = ParseRow(Newtonsoft.Json.Linq.JObject.Parse(text));
                chars.Created(row);
                GD.Print($"select: created {row.Name} ({row.Body}, {row.Hair})");
            }
            catch (Exception e)
            {
                GD.Print($"select: create unreachable ({e.GetType().Name})");
                if (_selectUp && chars == _chars) chars.CreateFailed(0, "");
            }
            finally
            {
                _createBusy = false;
            }
        }

        /// <summary>
        /// SAVE in edit mode: PATCH /api/characters/&lt;token&gt; with both
        /// {name, hair} (renaming to its own name is allowed, so sending the
        /// unchanged one is harmless); 200 replaces the row, 400/404/409 show
        /// the server's reason under the form.
        /// </summary>
        private async System.Threading.Tasks.Task SaveCharacter()
        {
            if (_createBusy || !_chars.CanSave) return;
            _createBusy = true;
            var chars = _chars;
            CharacterRow editing = chars.Editing;
            try
            {
                string site = Launcher.SiteUrl(_serverUrl) ?? throw new FormatException(_serverUrl);
                string json = new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = chars.Name,
                    ["hair"] = chars.Hair,
                }.ToString(Newtonsoft.Json.Formatting.None);
                using var budget = new System.Threading.CancellationTokenSource(AccountBudgetMs);
                using HttpRequestMessage req = AccountRequest(HttpMethod.Patch, site + "/api/characters/" + Uri.EscapeDataString(editing.Token), json, Identity("session"));
                using HttpResponseMessage resp = await AccountHttp.SendAsync(req, budget.Token);
                string text = await resp.Content.ReadAsStringAsync(budget.Token);
                if (!_selectUp || chars != _chars || chars.Editing != editing) return;
                if ((int)resp.StatusCode == 401) { SessionRefused(); return; }
                if (!resp.IsSuccessStatusCode)
                {
                    GD.Print($"select: save refused ({(int)resp.StatusCode} {text.Trim()})");
                    chars.CreateFailed((int)resp.StatusCode, text.Trim());
                    return;
                }
                CharacterRow row = ParseRow(Newtonsoft.Json.Linq.JObject.Parse(text));
                chars.Saved(row);
                GD.Print($"select: saved {row.Name} ({row.Hair})");
            }
            catch (Exception e)
            {
                GD.Print($"select: save unreachable ({e.GetType().Name})");
                if (_selectUp && chars == _chars && chars.Editing == editing) chars.CreateFailed(0, "");
            }
            finally
            {
                _createBusy = false;
            }
        }

        /// <summary>CONFIRM after DELETE: DELETE /api/characters/&lt;token&gt;; 200 removes the row.</summary>
        private async System.Threading.Tasks.Task DeleteCharacter()
        {
            if (_createBusy || _chars.Editing == null || !_chars.Deleting) return;
            _createBusy = true;
            var chars = _chars;
            CharacterRow editing = chars.Editing;
            try
            {
                string site = Launcher.SiteUrl(_serverUrl) ?? throw new FormatException(_serverUrl);
                using var budget = new System.Threading.CancellationTokenSource(AccountBudgetMs);
                using HttpRequestMessage req = AccountRequest(HttpMethod.Delete, site + "/api/characters/" + Uri.EscapeDataString(editing.Token), null, Identity("session"));
                using HttpResponseMessage resp = await AccountHttp.SendAsync(req, budget.Token);
                string text = await resp.Content.ReadAsStringAsync(budget.Token);
                if (!_selectUp || chars != _chars || chars.Editing != editing) return;
                if ((int)resp.StatusCode == 401) { SessionRefused(); return; }
                if (!resp.IsSuccessStatusCode)
                {
                    GD.Print($"select: delete refused ({(int)resp.StatusCode} {text.Trim()})");
                    chars.CreateFailed((int)resp.StatusCode, text.Trim());
                    return;
                }
                chars.Deleted(editing.Token);
                GD.Print($"select: deleted {editing.Name}");
            }
            catch (Exception e)
            {
                GD.Print($"select: delete unreachable ({e.GetType().Name})");
                if (_selectUp && chars == _chars && chars.Editing == editing) chars.CreateFailed(0, "");
            }
            finally
            {
                _createBusy = false;
            }
        }

        /// <summary>PLAY in the select: the row's body on the local rig, its token saved, the stage gone, the connect.</summary>
        private void PlaySelected()
        {
            if (!_selectUp || !_chars.CanPlay) return;
            CharacterRow row = _chars.Rows[_chars.Selected];
            SelfBody = string.IsNullOrEmpty(row.Body) ? "char.player" : row.Body;
            _viewModel.SetBody(SelfBody);
            SaveToken(row.Token); // Reconnect keeps working
            CloseSelect();
            _ui.Root.Visible = true;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Captured;
            _selectBorn = true;
            _gotSnapshot = false;
            GD.Print($"select: play {row.Name} ({SelfBody})");
            Connect(row.Token);
        }

        /// <summary>
        /// GDD: a 1008 close before the first snapshot (the character went
        /// under you) returns to the select, reloaded. The pump would retry
        /// the dead token forever, so the client is replaced.
        /// </summary>
        private bool CharacterRefused()
        {
            if (!_selectBorn || _gotSnapshot || _net.LastCloseCode != 1008) return false;
            GD.Print("select: the server refused the character (1008), back to the select");
            _selectBorn = false;
            _net.Dispose();
            _net = new NetClient();
            OpenSelect(fake: false);
            return true;
        }

        // ---- select rig (-uiShot … -uiChars <state>) ----------------------------

        /// <summary>-uiChars &lt;state&gt;: the select in a named state with fake rows, no network.</summary>
        private void FakeChars(string state)
        {
            OpenSelect(fake: true);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rows = new List<CharacterRow>
            {
                new CharacterRow { Token = "fake-kade", Name = "Kade", Body = "char.ubc.f", Hair = "hair.long", Credits = 1240, LastSeenMs = now - 2 * 3_600_000L },
                new CharacterRow { Token = "fake-tam", Name = "Tam", Body = "char.player", Hair = "hair.buzzed", Credits = 300, LastSeenMs = now - 3 * 86_400_000L },
                new CharacterRow { Token = "fake-vex", Name = "Vex", Body = "char.player.f", Hair = "hair.buns", Credits = 5, LastSeenMs = now },
            };
            switch (state)
            {
                case "select":
                    _chars.Loaded(rows);
                    // -uiCharsSelect <n>: pre-select fake row n (0 Kade, 1 Tam, 2 Vex)
                    _chars.Select(int.Parse(Arg("-uiCharsSelect") ?? "0", CultureInfo.InvariantCulture));
                    break;
                case "create":
                    _chars.Loaded(rows);
                    _chars.NewCharacter();
                    _chars.SetName("Ka");
                    _chars.SetFemale(true);
                    _chars.SetVanguard(true);
                    _chars.SetHair(Arg("-uiHair") ?? "hair.buns");   // -uiHair <id>: the form's hair for a shot
                    break;
                case "edit":   // Phase 18: the form in edit mode on fake row 0
                    _chars.Loaded(rows);
                    _chars.Edit(0);
                    break;
                case "delete": // Phase 18: the inline DELETE confirm
                    _chars.Loaded(rows);
                    _chars.Edit(0);
                    _chars.AskDelete();
                    break;
                case "empty": _chars.Loaded(new List<CharacterRow>()); break;
                case "loading": break;
                case "failed": _chars.Fail(LoadFailed); break;
                default: GD.PushError($"-uiChars: unknown state {state}"); break;
            }
            GD.Print($"select: faked {_chars.Now}");
        }

        /// <summary>
        /// Mid-session: a newer release is downloaded in the background and
        /// the HUD asks for a restart. Nobody is pulled out of a fight -- it
        /// applies when the game closes (_ExitTree), and a player who
        /// never closes gets it at the next launch anyway.
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdateInSession()
        {
            _updateChecking = true;
            try
            {
                if (await _updates.CheckForUpdatesAsync() is { } update)
                {
                    await _updates.DownloadUpdatesAsync(update);
                    _updateReady = update.TargetFullRelease;
                    GD.Print($"update: {_updates.CurrentVersion} -> {_updateReady.Version} ready, applies on exit");
                }
            }
            catch (Exception e)
            {
                GD.Print($"update: check failed ({e.Message})");
            }
            _updateChecking = false;
        }

        private const string ReleasesRepo = "https://github.com/stevenholder/space_adventure";

        // ---- lifecycle ------------------------------------------------------

        public override void _Ready()
        {
            // Velopack's install/update hooks arrive as process args; Godot
            // hands them through untouched. A no-op for anything not installed.
            VelopackApp.Build().SetArgs(OS.GetCmdlineArgs()).Run();

            string q = Arg("-quitAfter");
            if (q != null) _quitAfter = double.Parse(q, CultureInfo.InvariantCulture);

            GD.Print($"boot: godot {Engine.GetVersionInfo()["string"]} " +
                     $"dotnet {RuntimeInformation.FrameworkDescription} " +
                     $"display {DisplayServer.GetName()} window {DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y} at {DisplayServer.WindowGetPosition().X},{DisplayServer.WindowGetPosition().Y}");

            if (Flag("-selftest"))
            {
                GetTree().Quit(SelfTest());
                SetProcess(false); // Quit lands after this frame; no HUD was built for RunFrame to draw
                return;
            }

            // A player's launch opens the launcher window first (GDD
            // "Launcher"): sized and unscaled before anything else draws.
            // Rig: -uiDot leaves the window exactly as the engine created it
            // (what a Velopack hook run shows), for a screen capture.
            bool launcher = LauncherMode;
            if (Flag("-uiDot")) { /* stay the dot; -quitAfter ends the run */ }
            else if (launcher) OpenLauncherWindow();
            else OpenGameWindow();

            _material = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 1f,
                Metallic = 0f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            };

            // Far enough that the whole planet reads as a planet from any
            // altitude a flight reaches (C36) — the world is ~380 m across
            // and the scripted orbits sit within ~1500 m.
            _camera = new Camera3D { Name = "Eye", Near = 0.05f, Far = 6000f, Fov = 60f };
            AddChild(_camera);
            _camera.Current = true;

            // The rig is drawn by the main camera, not a SubViewport overlay:
            // under gl_compatibility nothing in a transparent SubViewport
            // received any light (the rig rendered as an unlit black slab),
            // so the Unity-style camera stack is gone. ponytail: the rig can
            // clip into a wall closer than ~1.2 m; a depth-cleared overlay
            // pass is the upgrade if that ever reads badly on a real display.

            // The sun. Direction is what the Unity build had (Euler 35, −140
            // there), carried into the Sim frame as a vector.
            _sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 1.0f, ShadowEnabled = true };
            _sun.LightCullMask &= ~VmLayer; // the rig has its own light
            AddChild(_sun);
            _sun.LookAtFromPosition(Vector3.Zero, new Vector3(-0.527f, -0.574f, 0.627f), Vector3.Up);

            // A light that follows the eye and reaches ONLY the rig (cull
            // mask). Ambient is low now that the sky is space, so a weapon
            // lit by the sun alone is a black cutout whenever you face away
            // from it. An omni, not a directional: a second DirectionalLight3D
            // never lit anything under gl_compatibility.
            _rigLight = new OmniLight3D { Name = "RigLight", LightEnergy = 2.5f, OmniRange = 3f, LightCullMask = VmLayer, ShadowEnabled = false };
            AddChild(_rigLight);

            // Until the sky lands with the world seed: black, with the same
            // faint ambient the space sky carries, so a face turned from the
            // sun is dim rather than a cutout.
            _environment = new WorldEnvironment
            {
                Name = "Environment",
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = Colors.Black,
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.17f, 0.18f, 0.24f),
                    AmbientLightEnergy = 1f,
                },
            };
            AddChild(_environment);

            _fps = new FpsController(_camera, _input);
            _predictor = new Predictor();
            _timeline = new SnapshotTimeline();
            _assets = new AssetRegistry(_material);
            _views = new EntityViews(this, _material, _assets);
            _structures = new Structures(this, _material, _assets);
            _rocks = new Rocks(this, _material, _assets);
            _viewModel = new ViewModel(_camera, _material, VmLayer, this, _assets);
            _viewModel.ArmsVisible = false; // until the world is up
            _fx = new CombatFx(this);
            _sfx = new Sfx(this) { Ear = () => _camera.GlobalPosition };
            _views.Sfx = _sfx;
            _hud = new Hud();
            _character = new Character();
            _interact = new Interaction(_views, _character);

            _ui = new UiRoot(this);
            _hudView = new HudView(_ui.Root);
            _combatFeed = new CombatFeed(_ui.Root);
            _map = new MapView(_ui.Root);
            _icons = new Icons(_assets.Root);
            _shopView = new ShopView(_ui.Root, _character, _interact, _icons, NextCmdSeq, b => _net.Send(b));
            _benchView = new BenchView(_ui.Root, _character, _skills, _icons, NextCmdSeq, b => _net.Send(b));
            _hotbar = new Hotbar();
            _hotbar.Load();
            _hotbarView = new HotbarView(_ui.Root, _hotbar, _character, _icons);
            _chatView = new ChatView(_ui.Root, SendChat) { Closed = () => _chat.Open = false };
            _bagsView = new BackpackView(_ui.Root, _character, _icons, NextCmdSeq, b => _net.Send(b), _interact);
            _sheetView = new CharacterView(_ui.Root, _character, _skills, _icons, _assets, NextCmdSeq, b => _net.Send(b));
            _promptView = new PromptView(_ui.Root);
            _journalView = new JournalView(_ui.Root, _missionLog, _partyState, NearestBoard, NextCmdSeq, b => _net.Send(b));
            _partyView = new PartyView(_ui.Root, _partyState, NearbyPlayers, NextCmdSeq, b => _net.Send(b));
            _partyFrames = new PartyFrames(_ui.Root);
            _skillsView = new SkillsView(_ui.Root, _skills, _character);
            _skillsFeed = new SkillsFeed(_ui.Root);
            _settings = new Settings();
            _settings.Load();
            _settingsView = new SettingsView(_ui.Root, _settings, ApplySettings);
            _gameMenu = new GameMenuView(_ui.Root, () => GetTree().Quit(0), () => _settingsView.Show(true));
            // The rig fixes its own window (--resolution, headless shots), so
            // the saved display mode is for a player's session only.
            // The launcher holds them back until PLAY.
            if (launcher) _fps.Sensitivity = FpsController.BaseSensitivity * _settings.MouseSensitivity;
            else if (!Rigged) ApplySettings();
            else GetTree().Root.ContentScaleFactor = _settings.UiScale;

            // -dumpNodes <asset id>: print the imported node tree and clips,
            // then quit. Pins the node-name and animation import rules.
            // -dumpSfx <dir>: write every synthesized sound as a .wav, then quit.
            string sfxDir = Arg("-dumpSfx");
            if (sfxDir != null)
            {
                foreach (var (name, wav) in new Sfx(this).All()) wav.SaveToWav($"{sfxDir}/{name}.wav");
                GD.Print($"sfx: wrote {new Sfx(this).All().Length} sounds to {sfxDir}");
                GetTree().Quit(0);
                return;
            }
            string dump = Arg("-dumpNodes");
            if (dump != null)
            {
                GD.Print(_assets.Describe(dump));
                GetTree().Quit(0);
                return;
            }

            _net = new NetClient();
            _serverUrl = ResolveServerUrl();
            string fake = Arg("-uiLauncher");
            string fakeChars = Arg("-uiChars");
            if (fakeChars != null) FakeChars(fakeChars);
            else if (launcher)
            {
                BuildLauncher();
                string fakeLogin = Arg("-uiLogin");
                if (fake != null) FakeLauncher(fake);
                if (fakeLogin != null) FakeLogin(fakeLogin);
                if (fake == null && fakeLogin == null) _ = UpdateStep(); // a faked state is never overwritten by the real check
            }
            else
            {
                _ = UpdateThenConnect();
                Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Captured;
            }

            // -uiShot <path>: save a screenshot once the world settles, the
            // review artifact for C60 (test/out/ui/) and the only eyes a
            // headless agent has.
            string shot = Arg("-uiShot");
            if (shot != null && (fake != null || fakeChars != null || Arg("-uiPlayReal") != null)) _ = SaveLauncherShot(shot);
            else if (shot != null) _ = SaveUiShot(shot);
        }

        public override void _ExitTree()
        {
            // Quitting with an update downloaded: hand it to Velopack, which
            // waits for this process to exit and swaps the files. No restart --
            // the player chose to quit; the next launch is the new build.
            if (_updates != null && _updateReady != null)
            {
                try { _updates.WaitExitThenApplyUpdates(_updateReady, silent: true, restart: false); }
                catch (Exception e) { GD.Print($"update: apply on exit failed ({e.Message})"); }
            }
            _net?.Dispose();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }

        public override void _UnhandledInput(InputEvent e) => _input.Feed(e);

        public override void _Process(double delta)
        {
            if (_updates != null && _updateReady == null && !_updateChecking && Clock.Now > _nextUpdateCheck)
            {
                _nextUpdateCheck = Clock.Now + UpdateEvery;
                _ = CheckForUpdateInSession();
            }
            // Godot .NET reports an unhandled exception and keeps running, which
            // is the same lie Unity's batchmode told: a headless run would log
            // the error and still exit 0. Under -quitAfter the exit code is the
            // verdict, so the first exception ends the run with a non-zero one.
            try
            {
                Clock.Dt = delta;
                if (_selectUp)
                {
                    _elapsed += delta;
                    if (_quitAfter >= 0 && _elapsed >= _quitAfter) GetTree().Quit(0);
                    else SelectFrame(delta);
                    return;
                }
                if (_launcherUp)
                {
                    _elapsed += delta;
                    if (_quitAfter >= 0 && _elapsed >= _quitAfter) GetTree().Quit(0);
                    else LauncherFrame();
                    return;
                }
                RunFrame(delta);
            }
            catch (Exception ex)
            {
                GD.PushError($"unhandled: {ex}");
                if (_quitAfter >= 0) GetTree().Quit(1);
                else throw;
            }
            finally
            {
                _input.EndFrame();
            }
        }

        // ---- the frame ------------------------------------------------------

        private void RunFrame(double delta)
        {
            _elapsed += delta;
            if (_quitAfter >= 0 && _elapsed >= _quitAfter)
            {
                GD.Print($"boot: quitting after {_elapsed:F1}s");
                GetTree().Quit(0);
                return;
            }

            DrainNetwork();
            if (CharacterRefused()) return;
            if (!_worldBuilt)
            {
                // No world yet: the only thing worth drawing is why.
                _hudView.SetBanner(LinkBanner(), true);
                return;
            }

            // The chat line, while open, swallows every game key (one gate:
            // InputState.Muted); Enter with nothing else open raises it.
            // The LineEdit owns Enter/Escape while it has focus.
            _input.Muted = _chat.Open;
            if (!_chat.Open && !ModalOpen && !_map.Open && (_input.Pressed(Key.Enter) || _input.Pressed(Key.KpEnter)))
            {
                _chat.Open = true;
                _chatView.SetOpen(true);
            }

            // Escape: close whatever is open; with nothing open, the game
            // menu (WoW's rule). The menu itself frees the cursor, so the
            // old "Esc frees the cursor" latch is gone.
            if (_input.Pressed(Key.Escape))
            {
                if (ModalOpen || _map.Open) CloseAllPanels();
                else _gameMenu.Show(true);
            }
            if (_input.Pressed(Key.F3)) _hud.DebugOpen = !_hud.DebugOpen;
            if (_input.Pressed(Key.M)) _map.Toggle();
            // R reloads when a gun is worn; the server would refuse otherwise,
            // and a refusal for pressing R with empty hands is noise.
            // Tab swaps the hand between the gun and the melee weapon (when
            // both are worn). The server answers with `equipped`; flipping
            // here too means the swap shows on the frame the key went down.
            if (_input.Pressed(Key.Tab) && _character.Primary != "" && _character.Melee != "")
            {
                _character.WieldMelee = !_character.WieldMelee;
                _net.Send(Character.WieldCmd(NextCmdSeq(), _character.WieldMelee));
            }
            if (_input.Pressed(Key.R) && _character.Held == _character.Primary && _character.Defs.Item(_character.Primary)?.Weapon != null)
            {
                _net.Send(Character.ReloadCmd(NextCmdSeq()));
                // Cosmetic and local, like the muzzle flash: the arms time
                // themselves to the weapon's reload_time.
                double rt = _character.Defs.Item(_character.Primary).Weapon.ReloadTime;
                _viewModel.Reload(rt > 0 ? rt : 2.0);
                _sfx.Reload(rt > 0 ? rt : 2.0);
            }
            // Phase 13: 1–5 Q E T Z X fire the hotbar; Shift picks the second
            // row (Shift is also sprint — a hotkey while sprinting fires the
            // shift row, which is what a modifier means).
            _hotbarView.Shift = (Hotbar.ShiftRow && _input.Held(Key.Shift)) || _rigShift;
            foreach (Key k in Hotbar.Keys)
                if (_input.Pressed(k)) FireHotbar(Hotbar.SlotFor(k, _hotbarView.Shift));
            if (_input.Pressed(Key.B)) OpenPanel(_bagsView, _sheetView);
            if (_input.Pressed(Key.C)) OpenPanel(_sheetView, _bagsView);
            if (_input.Pressed(Key.J))
            {
                bool open = !_journalView.Open;
                _journalView.Show(open);
                if (open)
                {
                    _partyView.Show(false);
                    uint board = NearestBoard();
                    if (board != 0)
                        _net.Send(Encode.Cmd(NextCmdSeq(), Op.MissionList, $"{{\"npc\":{board}}}"));
                }
            }
            if (_input.Pressed(Key.P))
            {
                bool open = !_partyView.Open;
                _partyView.Show(open);
                if (open) _journalView.Show(false);
            }
            if (_input.Pressed(Key.K)) ToggleSkills();

            // The map and the panels want the pointer. Movement keeps working
            // under either.
            bool wantsCursor = _map.Open || ModalOpen;
            if (wantsCursor && Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured)
                Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
            else if (!wantsCursor && Godot.Input.MouseMode != Godot.Input.MouseModeEnum.Captured)
                Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Captured;
            // The pilot's mouse steers the ship, never the view (GDD:
            // hull-fixed camera). Accumulate the raw delta for SendTick.
            bool captured = Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured;
            bool piloting = Piloting;
            _fps.MouseLookEnabled = captured && !piloting;
            if (piloting && captured) _mouseAccum += _input.MouseDelta;

            State state = _predictor.State;
            // Seated, the local up comes from the vehicle — the body predictor
            // is reset and its position stale.
            Vec3 upPos = state.Pos;
            if (_seat != 0)
            {
                if (SeatKind == EntityType.Ship && _ship.Ready) upPos = _ship.State.Pos;
                else if (_rover.Ready) upPos = _rover.State.Pos;
            }
            LocalInput li = _fps.Sample(upPos.Normalized(), state.Facing);
            RigInput(ref li);

            // Fixed 20 Hz input, matching the server's tick. Sending at frame
            // rate would put several inputs in one tick, and the server keeps
            // only the last: the rest of the movement silently never happens.
            _tickAccumulator += delta;
            while (_tickAccumulator >= Rules.DT)
            {
                _tickAccumulator -= Rules.DT;
                SendTick(li);
            }
            // Draw poses between the last two predicted ticks (Smoothing.cs).
            double alpha = _tickAccumulator / Rules.DT;
            _predictor.Smooth.Advance(alpha, delta);
            _rover.Smooth.Advance(alpha, delta);
            _ship.Smooth.Advance(alpha, delta);

            // Look-at targeting, then E. The shop swallows E so closing it
            // does not immediately reopen it on the same key press.
            if (_seat == 0)
            {
                Vector3 eye = Frame.ToGodot(state.Pos);
                eye += eye.Normalized() * FpsController.EyeHeight;
                _interact._selfId = _net.EntityId;
                _interact.Update(eye, Frame.ToGodot(li.Look));
            }
            // An NPC's panel closes when you walk away from the NPC (GDD
            // `ui_close_dist`): the shop and the bench are somebody's counter.
            if (_interact.ShopOpen && OutOfCounterRange(_interact.ShopNpc)) { _interact.CloseShop(); _shopView.Show(false); }
            if (_benchView.Open && _benchView.Bench != 0 && OutOfCounterRange(_benchView.Bench)) _benchView.Show(false);
            else if (Clock.Now > _noticeUntil) _interact.Notice = "";
            // After the clear above, or the hint is wiped the frame it is set.
            if (_seat != 0 && Clock.Now > _noticeUntil) _interact.Notice = SeatKind == EntityType.Ship ? "F  ·  exit ship" : "F  ·  exit rover";
            if (li.InteractPressed && !_map.Open && !(_bagsView.Open || _sheetView.Open)) OnInteract();

            _timeline.OneWaySeconds = _net.RttMs > 0 ? _net.RttMs / 2000.0 : 0.0;
            _views.Render(_timeline, _net.EntityId);
            if (_seat != 0) PlaceSeatCamera();
            else _fps.PlaceCamera(_predictor.Smooth.Pos);
            _views.PlaceSeated();   // after the vehicle you drive has moved
            _rigLight.GlobalPosition = _camera.GlobalTransform * new Vector3(0.35f, 0.25f, 0.1f); // above and right of the eye: lights the top and rear of the rifle

            // The rig follows the character sheet, which is the one place
            // that knows what is equipped. The body stands where the
            // simulation puts it, and the rig sways against the real speed
            // rather than the input.
            // -rigArmed: show the rig without a purchase (screenshot rig).
            // The server still drops the shots of an unarmed player.
            if (_rigArmed && string.IsNullOrEmpty(_character.Primary)) _character.Primary = "weapon.pulse";
            if (_rigWorn.Count > 0 && _viewModel.BodyReady)
            {
                foreach (var (slot, asset) in _rigWorn)
                {
                    _viewModel.Wear(slot, asset);
                    GD.Print($"rig: wearing {slot}={asset}");
                }
                _rigWorn.Clear();
            }
            _viewModel.ArmsVisible = CanShoot;
            string held = _character.Held;
            bool holding = CanShoot && !string.IsNullOrEmpty(held);
            _viewModel.Hold(holding ? _views.Defs.ItemAsset(held) : "", holding ? _views.Defs.HoldSuffix(held) : "");
            _viewModel.BodyVisible = _seat == 0;
            State body = _predictor.State;
            _viewModel.Place(_predictor.Smooth.Pos, body.Facing);
            // Right mouse aims, only while the world has the pointer (a free
            // cursor's right-click belongs to the bags and the hotbar).
            bool aiming = _rigAim || (CanShoot && Swung == null && _input.RightButtonHeld && Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured);
            float clear = _rigLowered ? 0f
                : _seat == 0 ? ViewModel.Blocked(_camera.GlobalPosition, -_camera.GlobalBasis.Z, ViewModel.Reach, _colliders, _terrain)
                : float.PositiveInfinity;
            _viewModel.Tick(_fps.LookDelta, (float)body.Vel.Length, (float)delta, aiming, clear);
            // Footsteps: one per stride while on foot and on the ground.
            if (_seat == 0 && body.Grounded && body.Vel.Length > 0.5)
            {
                _stepDist += (float)(body.Vel.Length * delta);
                if (_stepDist > (body.Vel.Length > CharacterAnim.SprintAt ? 1.0f : 0.75f)) { _stepDist = 0; _sfx.OwnStep(_stepN++); }
            }
            _fx.Tick();

            UpdateHudView();
            ChatFrame();
            _combatFeed.Tick(_camera);
            _skillsFeed.Tick(_skills, _character.Defs, (float)Clock.Now);

            _statFrames++;
            if (delta > _statWorstDt) _statWorstDt = delta;
            double now = Clock.Now;
            if (now - _statWindowStart >= 5.0)
            {
                // The first window is skipped: it contains scene build and
                // model loads, which are startup cost, not frame budget.
                if (_statWindowStart > 0)
                    GD.Print($"framestats: {_statFrames / (now - _statWindowStart):F1} fps avg, worst frame {_statWorstDt * 1000:F1} ms, {_views.Count} entities");
                _statWindowStart = now;
                _statFrames = 0;
                _statWorstDt = 0;
            }
        }

        /// <summary>E, on whatever the cone picked.</summary>
        private void OnInteract()
        {
            if (_seat != 0)
            {
                // Always available, at any speed (GDD; C32).
                _net.Send(Encode.Disembark());
                return;
            }
            if (_interact.ShopOpen) { _interact.CloseShop(); _shopView.Show(false); return; }
            if (_benchView.Open) { _benchView.Show(false); return; }
            if (_interact.Target == 0) return;
            switch (_interact.TargetType)
            {
                case EntityType.Node:
                    // Phase 12: the channel is the server's; the reply's
                    // duration starts the bar, gather_end ends it.
                    _net.Send(Encode.Cmd(NextCmdSeq(), Op.Gather, $"{{\"node\":{_interact.Target}}}"));
                    break;
                case EntityType.Npc when _views.TryGet(_interact.Target, out var bv) && bv.Label == "npc.workbench":
                    _benchView.Bench = _interact.Target;
                    _benchView.Status = "";
                    _benchView.Show(true);
                    _net.Send(Interaction.InventoryCmd(NextCmdSeq()));
                    _net.Send(Encode.Cmd(NextCmdSeq(), Op.Skills, "{}"));
                    break;
                case EntityType.Player:
                    // Look + E is the fast invite path (GDD "Parties").
                    _net.Send(Encode.Cmd(NextCmdSeq(), Op.PartyInvite, $"{{\"target\":{_interact.Target}}}"));
                    _interact.Notice = "invite sent";
                    _noticeUntil = Clock.Now + 2;
                    break;
                case EntityType.Npc when _views.TryGet(_interact.Target, out var tv) && tv.Label == "npc.dispatcher":
                    // The dispatcher is a BOARD, not a shop: E opens the
                    // journal and asks for the offers.
                    _journalView.Show(true);
                    _net.Send(Encode.Cmd(NextCmdSeq(), Op.MissionList, $"{{\"npc\":{_interact.Target}}}"));
                    break;
                case EntityType.Vehicle:
                case EntityType.Ship:
                    // Ask for the control seat; on "occupied" the seat_result
                    // handler walks down the passenger seats.
                    _net.Send(Encode.Board(_interact.Target, 1));
                    break;
                default:
                {
                    byte[] cmd = _interact.OpenShop(NextCmdSeq());
                    if (cmd != null)
                    {
                        _net.Send(cmd);
                        _net.Send(Interaction.InventoryCmd(NextCmdSeq()));
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Camera at the seat eye point (GDD seat_eye, sim frame — which is
        /// the vehicle's local frame here, no conversion). The control seat
        /// sees the PREDICTED vehicle; steering one that lags your own input
        /// by a round trip is the bug prediction exists to kill. A passenger
        /// rides the interpolated view like any remote entity. Rotation stays
        /// the FpsController's — free look in every seat — except the
        /// pilot's, which is hull-fixed.
        /// </summary>
        private void PlaceSeatCamera()
        {
            bool ship = SeatKind == EntityType.Ship;
            Transform3D hull;
            bool haveView = _views.TryGet(_seatVehicle, out var vv) && vv.Root != null;
            if (_seat == 1 && (ship ? _ship.Ready : _rover.Ready))
            {
                RenderSmoother sm = ship ? _ship.Smooth : _rover.Smooth;
                Vec3 p = sm.Pos;
                Quat q = sm.Rot;
                hull = new Transform3D(Frame.BasisOf(q), Frame.ToGodot(p));
                if (haveView) vv.Root.GlobalTransform = hull;
            }
            else if (haveView)
            {
                hull = vv.Root.GlobalTransform;
            }
            else return; // no vehicle row seen yet; keep last camera pose

            var table = ship ? ShipSeatEye : RoverSeatEye;
            int seat = _seat < table.Length ? _seat : 1;
            _camera.GlobalPosition = hull * Frame.ToGodot(table[seat]);

            // The pilot's camera is hull-fixed: orientation IS the ship's
            // attitude, the mouse steers the ship, not the view (GDD "Camera
            // and rig"). The ship's forward is +Z and a Camera3D looks down
            // −Z, so the hull basis is turned about its own up first.
            if (ship && _seat == 1) _camera.GlobalBasis = hull.Basis * Frame.ModelFlip;
        }

        private void SendTick(LocalInput li)
        {
            _seq++;

            if (Piloting)
            {
                // Pilot: mode 1, v = [thrust, roll, yaw_rate, pitch_rate, 0]
                // (GDD flight input map). Mouse deltas were accumulated per
                // frame; convert to rad/s over the tick. Rightward drag →
                // negative yaw (left turn positive, RH rule about +Y); upward
                // drag (pointer −Y) → negative pitch, which is nose up.
                double yaw = -_mouseAccum.X / Rules.DT * KRate;
                double pitch = _mouseAccum.Y / Rules.DT * KRate;
                _mouseAccum = Vector2.Zero;
                var finp = new FlightInput
                {
                    Thrust = li.MoveY,
                    Roll = -li.MoveX, // A (strafe −1) = roll left = +1
                    YawRate = yaw,
                    PitchRate = pitch,
                    Boost = (li.ActionMask & Net.Action.Sprint) != 0,
                };
                _ship.Colliders = WithMovingBodies(_seatVehicle);
                _ship.Apply(_seq, finp);
                _net.Send(Encode.Input(
                    (float)finp.Thrust, (float)finp.Roll,
                    (float)finp.YawRate, (float)finp.PitchRate, 0,
                    finp.Boost ? Net.Action.Boost : (ushort)0, _seq, 1));
                return;
            }
            if (_seat == 1)
            {
                // Driving: mode 2, v = [throttle, steer, 0, 0, 0]. Forward
                // key is throttle, strafe keys steer (PROTOCOL "input").
                double throttle = li.MoveY, steer = li.MoveX;
                _rover.Colliders = WithMovingBodies(_seatVehicle);
                _rover.Apply(_seq, throttle, steer);
                _net.Send(Encode.Input((float)throttle, (float)steer, 0, 0, 0, 0, _seq, 2));
                return;
            }
            if (_seat != 0)
            {
                // Passenger: movement is ignored server-side, but look still
                // flows for pitch_q. Send it zeroed so nothing depends on the mercy.
                _net.Send(Encode.Input(0, 0,
                    (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z, 0, _seq));
                return;
            }

            // Dead: the server ignores everything but look, so predict the
            // same -- moving a corpse locally only earned a teleport back
            // when the snapshot arrived.
            if (_hud.Dead)
            {
                li.MoveX = 0; li.MoveY = 0; li.ActionMask = 0; li.FirePressed = false;
            }
            var input = new Sim.Input
            {
                MoveX = li.MoveX,
                MoveY = li.MoveY,
                LookDir = li.Look,
                ActionMask = li.ActionMask,
            };
            _predictor.SetColliders(WithMovingBodies(0));
            _predictor.Apply(_seq, input);
            _net.Send(Encode.Input(
                (float)li.MoveX, (float)li.MoveY,
                (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z,
                (ushort)li.ActionMask, _seq));

            // Nothing happens without a weapon: the server drops a `fire`
            // from an unarmed player outright, and a muzzle flash anyway is a
            // lie. Cadence is enforced server-side and an early shot is
            // DROPPED, so hold to the weapon's own interval. The shot names
            // the input that was in effect when the trigger went down; the
            // server rewinds by how far back that input executed.
            // An empty magazine does not fire: the server drops the shot anyway
            // (server.go fireLocked), and drawing a flash and recoil for it
            // made an empty gun look like it was shooting. -1 = not known yet.
            // A hand weapon swings instead: same `fire`, no magazine, paced by
            // its interval; the server resolves who the arc touches.
            if (Swung is MeleeDef melee)
            {
                if (li.FirePressed && _viewModel.Armed && _seat == 0 && Clock.Now >= _nextFireAt)
                {
                    _nextFireAt = Clock.Now + (melee.Interval > 0 ? melee.Interval : 0.6);
                    _net.Send(Encode.Fire(_seq, (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z));
                    _viewModel.Swing(_character.Defs.SwingClip(_character.Held));
                    _sfx.Swing(melee.Hands >= 2);
                }
            }
            else if (li.FirePressed && _viewModel.Armed && CanShoot && _character.Magazine != 0 && Clock.Now >= _nextFireAt)
            {
                double fi = _character.Defs.Item(_character.Primary)?.Weapon?.FireInterval ?? 0;
                _nextFireAt = Clock.Now + (fi > 0 ? fi : FireIntervalSeconds);
                _net.Send(Encode.Fire(_seq, (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z));
                _fx.OnLocalFire(_viewModel.Muzzle, _viewModel.Layers);
                _viewModel.Fire(); // the arms kick; the camera never does
                _sfx.OwnShot(_character.Primary);
            }
            else if (li.FirePressed && _viewModel.Armed && CanShoot && _character.Magazine == 0 && Clock.Now >= _nextFireAt)
            {
                _nextFireAt = Clock.Now + 0.3;   // an empty gun clicks, it does not rattle
                _sfx.DryFire();
            }
        }

        // ---- the wire -------------------------------------------------------

        private void DrainNetwork()
        {
            while (_net != null && _net.Poll(out Net.Frame frame))
            {
                try
                {
                    HandleFrame(frame);
                }
                catch (WireException e)
                {
                    GD.PushError($"malformed {frame.Type:x4}: {e.Message}");
                }
            }
        }

        private void HandleFrame(Net.Frame frame)
        {
            switch (frame.Type)
            {
                case Msg.Terrain:
                {
                    TerrainMsg t = Decode.Terrain(frame.Reader);
                    _terrain = TerrainField.FromWire(t.Radii, t.RadiusMin, t.RadiusMax);
                    _sfx.Terrain = _terrain;
                    _hudView.Terrain = _terrain;
                    BuildWorld();
                    break;
                }
                case Msg.Colliders:
                {
                    Net.Collider[] rows = Decode.Colliders(frame.Reader);
                    _colliders = new Sim.Collider[rows.Length];
                    for (int i = 0; i < rows.Length; i++)
                    {
                        _colliders[i] = new Sim.Collider
                        {
                            Kind = (Sim.ColliderKind)rows[i].Kind,
                            Center = new Vec3(rows[i].CenterX, rows[i].CenterY, rows[i].CenterZ),
                            Half = new Vec3(rows[i].HalfX, rows[i].HalfY, rows[i].HalfZ),
                            Rot = new Quat(rows[i].QuatX, rows[i].QuatY, rows[i].QuatZ, rows[i].QuatW),
                        };
                    }
                    _predictor.SetColliders(_colliders);
                    // Same array to the predictor and to the renderer, so what
                    // you walk into is what you can see.
                    _structures.Build(_colliders);
                    _sfx.Colliders = _colliders;   // and what you hear through
                    _hudView.Colliders = _colliders;   // and whose health bar you see
                    GD.Print($"colliders: {_colliders.Length}");
                    break;
                }
                case Msg.Props:
                    // Drawn by Structures; the solid ones collide.
                {
                    var props = Decode.Props(frame.Reader);
                    _structures.BuildProps(props);
                    // ...and, since full collision, the solid ones are walls
                    // (sim/props.go): prediction pushes out of them as the
                    // server does.
                    var solid = new List<Sim.Collider>();
                    foreach (var p in props)
                        if (Sim.Collide.PropCollider(p.Asset, new Vec3(p.PosX, p.PosY, p.PosZ),
                                new Quat(p.QuatX, p.QuatY, p.QuatZ, p.QuatW), p.Scale, out Sim.Collider c))
                            solid.Add(c);
                    _propColliders = solid.ToArray();
                }
                    break;
                case Msg.Defs:
                {
                    // Item names, kinds and equipment slots, and the `asset`
                    // id behind every entity, which is what tells EntityViews
                    // which model in art/manifest.json to load.
                    Defs defs = Decode.Defs(frame.Reader);
                    _character.Defs = defs;
                    _views.Defs = defs;
                    // The wallet and the worn set are only ever pushed in a
                    // reply, so ask once now: the credits and ammo boxes read
                    // "—" until the first shop or reload otherwise.
                    _net.Send(Interaction.InventoryCmd(NextCmdSeq()));
                    break;
                }
                case Msg.Spawn:
                    _views.OnSpawn(Decode.Spawn(frame.Reader));
                    break;
                case Msg.Despawn:
                    _views.OnDespawn(Decode.Despawn(frame.Reader));
                    break;
                case Msg.Snapshot:
                {
                    Snapshot snap = Decode.Snapshot(frame.Reader);
                    _gotSnapshot = true;
                    _timeline.Add(snap, Clock.Now);
                    foreach (var row in snap.Entities)
                    {
                        if (row.Id == _net.EntityId)
                        {
                            // Occupancy is whatever our row says. On a seat
                            // change, drop the stale predictor.
                            if (row.ParentId != _seatVehicle || row.Seat != _seat)
                            {
                                _seatVehicle = row.ParentId;
                                _seat = row.Seat;
                                _rover.Reset();
                                _ship.Reset();
                                _mouseAccum = Vector2.Zero;
                                if (_seat != 0) _predictor.Reset();
                            }
                            if (_seat == 0)
                            {
                                _predictor.Reconcile(
                                    new Vec3(row.PosX, row.PosY, row.PosZ),
                                    new Vec3(row.VelX, row.VelY, row.VelZ),
                                    SnapshotTimeline.FacingOf(row),
                                    row.Grounded,
                                    snap.AckSeq);
                            }
                            _hud.Health = row.Health;
                            _character.Health = row.Health;
                            // Death is the server's call (GDD: dead flag set,
                            // input ignored except look). Latch the moment for
                            // the countdown; the flag clearing is the respawn.
                            if (!_rigDeathDemo)
                            {
                                if (row.Dead && !_hud.Dead) _hud.DeadSince = Clock.Now;
                                _hud.Dead = row.Dead;
                            }
                        }
                        else if (_seat != 0 && row.Id == _seatVehicle)
                        {
                            // Our row precedes world entities in the snapshot
                            // (players first), so _seat is already current.
                            var pos = new Vec3(row.PosX, row.PosY, row.PosZ);
                            var vel = new Vec3(row.VelX, row.VelY, row.VelZ);
                            var q = new Quat(row.QuatX, row.QuatY, row.QuatZ, row.QuatW);
                            if (SeatKind == EntityType.Ship)
                                _ship.Reconcile(pos, vel, q, row.Grounded, row.Space, snap.AckSeq);
                            else
                                _rover.Reconcile(pos, vel, q, row.Grounded, snap.AckSeq);
                        }
                    }
                    break;
                }
                case Msg.Event:
                {
                    EventMsg ev = Decode.Event(frame.Reader);
                    switch (ev.EventId)
                    {
                        case EventId.ShotFired when ev.EntityId == _net.EntityId:
                            _character.OnShotFired();
                            goto case EventId.ShotFired;
                        case EventId.ShotFired:
                            // Our own shots are drawn from the barrel; everyone
                            // else's from the origin the server reported.
                            _fx.OnShotFired(ev, ev.EntityId == _net.EntityId ? _viewModel.MuzzlePosition : (Vector3?)null);
                            if (ev.EntityId != _net.EntityId && ev.Data.Length >= 12)
                            {
                                var sr = new WireReader(ev.Data);
                                Vector3 from = Frame.ToGodot(new Vec3(sr.ReadF32(), sr.ReadF32(), sr.ReadF32()));
                                _sfx.ShotAt(from, _views.TryGet(ev.EntityId, out var shooter) ? shooter.EquippedItem : "");
                            }
                            break;
                        case EventId.Hit:
                            _fx.OnHit(ev, _net.EntityId);
                            if (ev.Data.Length >= 16)
                            {
                                var hr = new WireReader(ev.Data);
                                hr.ReadU32();
                                _sfx.ImpactAt(Frame.ToGodot(new Vec3(hr.ReadF32(), hr.ReadF32(), hr.ReadF32())));
                            }
                            _views.OnHit(ev.EntityId);
                            OnHitFeedback(ev);
                            break;
                        case EventId.MissionProgress:
                            _missionLog.OnProgress(WireReader.Utf8.GetString(ev.Data));
                            if (_journalView.Open) _journalView.Rebuild();
                            break;
                        case EventId.MissionComplete:
                            _missionLog.OnComplete(WireReader.Utf8.GetString(ev.Data));
                            if (_journalView.Open) _journalView.Rebuild();
                            break;
                        case EventId.PriorityOffer:
                            _missionLog.OnPriorityOffer(WireReader.Utf8.GetString(ev.Data));
                            if (_journalView.Open) _journalView.Rebuild();
                            break;
                        case EventId.MissionShared:
                            _missionLog.OnShared(WireReader.Utf8.GetString(ev.Data));
                            if (_journalView.Open) _journalView.Rebuild();
                            break;
                        case EventId.PartyUpdate:
                            _partyState.OnUpdate(WireReader.Utf8.GetString(ev.Data));
                            if (_partyView.Open) _partyView.Rebuild();
                            break;
                        case EventId.PartyInvited:
                            _partyState.OnInvited(WireReader.Utf8.GetString(ev.Data));
                            if (_rigAutoParty)
                                _net.Send(Encode.Cmd(NextCmdSeq(), Op.PartyRespond, "{\"accept\":true}"));
                            if (_partyView.Open) _partyView.Rebuild();
                            break;
                        case EventId.SkillXP:
                            _skills.OnXP(WireReader.Utf8.GetString(ev.Data), (float)Clock.Now);
                            if (_skillsView.Open) _skillsView.Rebuild();
                            if (_benchView.Open) _benchView.Rebuild();
                            break;
                        case EventId.Attack:
                            // Our own swing was played when the button went down.
                            if (ev.EntityId != _net.EntityId)
                            {
                                _views.OnAttack(ev.EntityId);
                                if (_views.TryGet(ev.EntityId, out var swinger) && _views.Defs.Item(swinger.EquippedItem)?.Melee is MeleeDef m)
                                    _sfx.SwingAt(swinger.Model?.GlobalPosition ?? Vector3.Zero, m.Hands >= 2);
                            }
                            break;
                        case EventId.Explosion when ev.Data.Length >= 16:
                        {
                            // A thrown charge burst: pos, radius, item id.
                            var xr = new WireReader(ev.Data);
                            Vector3 at = Frame.ToGodot(new Vec3(xr.ReadF32(), xr.ReadF32(), xr.ReadF32()));
                            float radius = xr.ReadF32();
                            string what = WireReader.Utf8.GetString(ev.Data, 16, ev.Data.Length - 16);
                            _fx.OnExplosion(at, radius, what);
                            _sfx.ExplosionAt(at, radius);
                            break;
                        }
                        case EventId.GatherEnd:
                            OnGatherEnd(WireReader.Utf8.GetString(ev.Data));
                            break;
                        case EventId.Worn:
                        {
                            // "slot=item" (PROTOCOL event_id 0x000F); armor on
                            // any body, our own included.
                            string data = WireReader.Utf8.GetString(ev.Data);
                            int eq = data.IndexOf('=');
                            if (eq > 0)
                            {
                                string slot = data.Substring(0, eq), item = data.Substring(eq + 1);
                                _views.OnWorn(ev.EntityId, slot, item);
                                if (ev.EntityId == _net.EntityId) _viewModel.Wear(slot, string.IsNullOrEmpty(item) ? "" : EntityViews.WornAsset(_views.Defs, item));
                            }
                            break;
                        }
                        case EventId.Chat:
                        {
                            // name + "\0" + text (PROTOCOL event_id 0x0011);
                            // our own line comes back the same way.
                            string data = WireReader.Utf8.GetString(ev.Data);
                            int nul = data.IndexOf('\0');
                            if (nul >= 0) _chat.Push(data.Substring(0, nul), data.Substring(nul + 1), Clock.Now);
                            break;
                        }
                        case EventId.Equipped:
                        {
                            string item = WireReader.Utf8.GetString(ev.Data);
                            _views.OnEquipped(ev.EntityId, item);
                            // Our own weapon comes down the same channel, and
                            // is replayed at join, so a reconnect holding a
                            // rifle shows one (PROTOCOL event_id 0x0006).
                            if (ev.EntityId == _net.EntityId) _character.OnHeld(item);
                            break;
                        }
                    }
                    _hud.OnEvent(ev, _net.EntityId);
                    break;
                }
                case Msg.SeatResult:
                {
                    SeatResult sr = Decode.SeatResult(frame.Reader);
                    ushort crew = _views.TryGet(sr.EntityId, out var sv) && sv.Type == EntityType.Ship
                        ? (ushort)3 : (ushort)2;
                    if (sr.Result == SeatResult.Occupied && sr.Seat < crew)
                    {
                        // That seat is taken — walk down the bench.
                        _net.Send(Encode.Board(sr.EntityId, (ushort)(sr.Seat + 1)));
                    }
                    else if (!sr.Ok)
                    {
                        _interact.Notice = sr.Result switch
                        {
                            SeatResult.Occupied => "seat taken",
                            SeatResult.OutOfRange => "too far away",
                            _ => "can't do that",
                        };
                        _noticeUntil = Clock.Now + 2;
                    }
                    // A grant needs no handling here: occupancy is whatever
                    // our next snapshot row says (PROTOCOL "board/disembark").
                    break;
                }
                case Msg.CmdResult:
                {
                    CmdResult r = Decode.CmdResult(frame.Reader);
                    _hud.OnCmdResult(r);
                    if (r.Ok && (r.Opcode == Op.Inventory || r.Opcode == Op.ShopBuy || r.Opcode == Op.ShopSell || r.Opcode == Op.ShopBuyback || r.Opcode == Op.Craft)) _character.OnWallet(r.Body);
                    if (r.Opcode == Op.Gather) OnGatherResult(r);
                    if (r.Opcode == Op.Use) OnUseResult(r);
                    if (r.Opcode == Op.Craft)
                    {
                        _benchView.Status = r.Ok ? "crafted" : Reason(r.Body);
                        if (_benchView.Open) _benchView.Rebuild();
                    }
                    if (r.Opcode == Op.Chat && !r.Ok)
                    {
                        _interact.Notice = r.StatusCode switch
                        {
                            Status.RateLimited => "chat: too fast",
                            Status.Malformed => "chat: too long",
                            Status.Refused => "chat: nothing to send",
                            _ => "chat: not sent",
                        };
                        _noticeUntil = Clock.Now + 2;
                    }
                    if (r.Opcode == Op.ShopSell && !r.Ok) { _interact.Notice = Reason(r.Body); _noticeUntil = Clock.Now + 2; }
                    if (r.Ok && r.Opcode == Op.Equip) _character.OnEquipResult(r.Body);
                    if (r.Ok && (r.Opcode == Op.Equip || r.Opcode == Op.Inventory || r.Opcode == Op.ShopBuy))
                    {
                        if (_bagsView.Open) _bagsView.Rebuild();
                        if (_sheetView.Open) _sheetView.Rebuild();
                    }
                    if (r.Ok && r.Opcode == Op.Reload) _character.OnReload(r.Body);
                    byte[] followUp = _interact.OnCmdResult(r, NextCmdSeq);
                    if (followUp != null) _net.Send(followUp);
                    if (r.Opcode == Op.MissionList && r.Ok) _missionLog.OnListResult(r.Body);
                    if (r.Opcode == Op.MissionAccept)
                    {
                        _missionLog.OnAcceptResult(r.Ok, r.Body);
                        if (_journalView.Open) _journalView.Rebuild();
                    }
                    if (r.Opcode == Op.Skills && r.Ok) _skills.OnSheet(r.Body);
                    if (_skillsView.Open) _skillsView.Rebuild();
                    if (_shopView.Open) _shopView.Rebuild();
                    if (_bagsView.Open) _bagsView.Rebuild();
                    if (_sheetView.Open) _sheetView.Rebuild();
                    if (_journalView.Open) _journalView.Rebuild();
                    if (_partyView.Open) _partyView.Rebuild();
                    break;
                }
            }
        }

        private void BuildWorld()
        {
            // The sky is scenery. If generating it fails, say so and carry on
            // with the flat environment rather than losing the planet.
            try
            {
                _environment = Sky.Install(this, _net.WorldSeed, _environment);
            }
            catch (Exception e)
            {
                GD.PushWarning($"sky unavailable, keeping the flat environment: {e.Message}");
            }
            _planet?.QueueFree();
            _planet = TerrainMesh.Build(_terrain, _material, this, _net.WorldSeed);
            _predictor.Seed(_terrain, _colliders);
            _rover.Seed(_terrain);
            _ship.Seed(_terrain);
            // Scatter is pure in (terrain, world_seed), so it can only run
            // once the terrain has landed -- which is here, and not earlier.
            _rocks.Build(_terrain, _net.WorldSeed);
            _worldBuilt = true;
            _rigArmed = Flag("-rigArmed");
            // -rigWorn slot=asset,...: dress the local body for a shot (asset
            // ids, not items). Applied by the frame loop once the body is on.
            if (Arg("-rigWorn") is string worn)
                foreach (string pair in worn.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (pair.Split('=') is { Length: 2 } kv) _rigWorn.Add((kv[0].Trim(), kv[1].Trim()));
            _rigAim = Flag("-uiAim");
            _rigLowered = Flag("-uiLowered");
            // The sheet up front, so the first drip and the K panel already
            // know the levels a reconnecting player arrives with.
            _net.Send(Encode.Cmd(NextCmdSeq(), Op.Skills, "{}"));

            // A packaged player has no HUD anyone is watching when it is run
            // headless in CI (C45 is "joins the deployed server from a cold
            // start"), so say so in the log. Once per world build.
            // No collider count here: `colliders` arrives AFTER `terrain`
            // (PROTOCOL's join order); they log themselves when they land.
            GD.Print($"world ready: entity={_net.EntityId} seed={_net.WorldSeed} " +
                     $"tickHz={_net.TickHz} spawn={_predictor.State.Pos.Length:F1} m from centre");
        }

        // ---- the interface ----------------------------------------------------


        /// <summary>The chat line's Enter: one cmd 0x0016, the text as a JSON string.</summary>
        private void SendChat(string text) =>
            _net.Send(Encode.Cmd(NextCmdSeq(), Op.Chat, "{\"text\":" + Newtonsoft.Json.JsonConvert.ToString(text) + "}"));

        /// <summary>Per frame: lines past 10 s go, the rest fade and draw.</summary>
        private void ChatFrame()
        {
            _chat.Expire(Clock.Now);
            _chatView.Set(_chat, Clock.Now);
        }

        /// <summary>-uiPanel bags|sheet|map|journal|party|skills|account|debug: opened for a screenshot.</summary>
        private void OpenUiPanel(string name)
        {
            switch (name)
            {
                case "bags": case "backpack": OpenPanel(_bagsView, _sheetView); break;
                case "sheet": case "character": OpenPanel(_sheetView, _bagsView); break;
                case "map": _map.Toggle(); break;
                case "journal": _journalView.Show(true); break;
                case "party": _partyView.Show(true); break;
                case "skills": ToggleSkills(); break;
                case "debug": _hud.DebugOpen = true; break;
                case "menu": _gameMenu.Show(true); break;
                case "bench": _benchView.Bench = 0; _benchView.Show(true); break;
                case "settings": _settingsView.Show(true); break;
            }
        }

        /// <summary>
        /// Feeds the HUD from the same sources everything else reads, plus
        /// compass markers by egocentric bearing (Bearing.To).
        /// </summary>
        private void UpdateHudView()
        {
            _hudView.SetVitals(_hud.Health, 100);
            _hudView.SetDeath(_hud.Dead ? _hud.RespawnIn : -1);
            _hudView.SetAmmo(_character.Magazine, _character.Reserve,
                _character.Magazine >= 0 && !string.IsNullOrEmpty(_character.Primary) && _character.Held == _character.Primary);
            _hudView.SetCredits(_character.Credits);
            _hotbarView.Refresh(Clock.Now);
            if (_channelEnd > 0)
            {
                double span = _channelEnd - _channelStart;
                float frac = span > 0 ? (float)((Clock.Now - _channelStart) / span) : 1f;
                _hudView.SetChannel(Mathf.Clamp(frac, 0f, 1f), _channelLabel);
            }
            else _hudView.SetChannel(-1f, "");

            var me = Viewer;
            var markers = new List<(string, double)>();
            foreach (var v in _views.All)
            {
                if (v.Root == null || !v.Root.Visible || (_seat != 0 && v.Id == _seatVehicle)) continue;
                string name = v.Type switch
                {
                    EntityType.Npc when v.Label == "npc.quartermaster" => "SHOP",
                    EntityType.Npc when v.Label == "npc.warlord" => "WARLORD",
                    EntityType.Vehicle => "ROVER",
                    EntityType.Ship => "SHIP",
                    _ => null,
                };
                if (name == null) continue;
                markers.Add((name, Bearing.To(me.Pos, me.Facing, Frame.ToSim(v.Root.GlobalPosition))));
            }

            // POI markers, gated by mast visibility (C68): a site joins the
            // compass when its mast tip would clear the horizon —
            // visible ≈ 22.6 + √(300·h) m for eye height 1.7 on r=150
            // (GDD "Silhouette and the 23 m horizon"); 12.6 m masts → 84 m.
            const float mastDiscovery = 84f;
            Vector3 myPos = Frame.ToGodot(me.Pos);
            foreach (var (pos, scrap) in _structures.Masts)
            {
                float chord = (pos - myPos).Length();
                float surface = 2f * 150f * Mathf.Asin(Mathf.Clamp(chord / (2f * 150f), 0f, 1f));
                if (surface > mastDiscovery) continue;
                markers.Add((PoiName(pos, scrap).ToUpperInvariant(), Bearing.To(me.Pos, me.Facing, Frame.ToSim(pos))));
            }
            // Phase 13: scanner pings for scan_show seconds.
            _scanPings.RemoveAll(p => Clock.Now > p.until);
            foreach (var (name, pos, _) in _scanPings)
                markers.Add((name, Bearing.To(me.Pos, me.Facing, Frame.ToSim(pos))));
            _hudView.SetMarkers(markers);

            // The shop view mirrors Interaction's state: stock arriving opens
            // it, CloseShop (or a despawned NPC) closes it.
            if (_interact.ShopOpen && !_shopView.Open) _shopView.Show(true);
            if (!_interact.ShopOpen && _shopView.Open) _shopView.Show(false);

            // The world prompt (E · talk) belongs to the world: with a panel
            // open it sat on top of the shop it had just opened.
            _promptView.Set(ModalOpen || _map.Open ? "" : !string.IsNullOrEmpty(_interact.Notice) ? _interact.Notice : _interact.Prompt);

            double now = Clock.Now;
            if (_missionLog.PriorityMission != null && now < _missionLog.PriorityUntil)
                _hudView.SetBanner($"PRIORITY: warlord sighted at {_missionLog.PriorityPoi?.ToUpperInvariant()} — open the journal (J)", true);
            else if (now - _missionLog.LastCompletedAt < 5)
                _hudView.SetBanner($"MISSION COMPLETE  +{_missionLog.LastCompletedCredits} CR", false);
            else if (now - _missionLog.LastSharedAt < 6)
                _hudView.SetBanner($"{_missionLog.LastSharedBy} shared \"{_missionLog.LastSharedName}\" — J for the journal", false);
            else if (_partyState.PendingFrom != 0 && now - _partyState.PendingAt < 10)
                _hudView.SetBanner($"{_partyState.PendingName} invites you to a party — P to answer", false);
            else if (!_worldBuilt || _net.State != LinkState.Joined)
                _hudView.SetBanner(LinkBanner(), true);
            else if (_updateReady != null)
                _hudView.SetBanner($"UPDATE {_updateReady.Version} READY — restart the game to play it", false);
            else
                _hudView.SetBanner(null, false);
            _hudView.SetVersion(_updateReady != null ? $"{BuildLabel} → v{_updateReady.Version}" : BuildLabel);

            UpdateFlightReadout();

            // Party frames, left edge: name + live health per member.
            _framesScratch.Clear();
            if (_partyState.InParty)
            {
                int playerMax = 100;
                if (_character.Defs.Entities != null &&
                    _character.Defs.Entities.TryGetValue("player", out var pd) && pd.MaxHealth > 0)
                    playerMax = pd.MaxHealth;
                foreach (var (mid, mname) in _partyState.Members)
                {
                    if (mid == _net.EntityId)
                        _framesScratch.Add((mname, _character.Health, playerMax, true, true));
                    else if (_views.TryGet(mid, out var mv))
                        _framesScratch.Add((mname, mv.Health, playerMax, true, false));
                    else
                        _framesScratch.Add((mname, 0, playerMax, false, false));
                }
            }
            _partyFrames.Update(_framesScratch);

            // The raw event log (cmd results as JSON) is a developer's view:
            // it rides with the F3 debug panel, not the player's screen.
            _hudView.SetLog(_hud.DebugOpen ? _hud.Lines : System.Array.Empty<string>());
            _hudView.SetDebug(_hud.DebugText(_net, _predictor, _character));
            _hudView.UpdateHealthBars(_camera, _views, !_map.Open);

            _map.Draw(_terrain, Frame.ToGodot(me.Pos), Frame.ToGodot(me.Facing), MapMarkers());
        }

        private readonly List<Sim.Collider> _moving = new List<Sim.Collider>();
        private Sim.Collider[] _propColliders = Array.Empty<Sim.Collider>();

        /// <summary>
        /// The static colliders plus everyone else as the server sees them
        /// (server.go tick, "moving obstacles"): on-foot players and live
        /// NPCs as body spheres, rovers and ships as hull boxes -- from the
        /// poses we draw, so contact predicts instead of snapping back.
        /// `except` leaves out the vehicle we drive (it is not its own wall).
        /// ponytail: remotes are drawn ~100 ms behind the server, so a body
        /// you walk into is where it was; the reconcile absorbs the gap.
        /// </summary>
        private Sim.Collider[] WithMovingBodies(uint except)
        {
            _moving.Clear();
            _moving.AddRange(_colliders);
            if (_rocks != null) _moving.AddRange(_rocks.Colliders);
            _moving.AddRange(_propColliders);
            foreach (EntityView v in _views.All)
            {
                if (v.Root == null || !v.Root.Visible || v.Id == _net.EntityId || v.Id == except) continue;
                Vec3 pos = Frame.ToSim(v.Root.GlobalPosition);
                if (v.Type == EntityType.Vehicle || v.Type == EntityType.Ship)
                {
                    Quaternion q = v.Root.GlobalBasis.GetRotationQuaternion();
                    var quat = new Quat(q.X, q.Y, q.Z, q.W);
                    _moving.Add(v.Type == EntityType.Vehicle ? Sim.Collide.RoverCollider(pos, quat) : Sim.Collide.ShipCollider(pos, quat));
                }
                else if ((v.Type == EntityType.Player || v.Type == EntityType.Npc) && !v.Dead && v.ParentId == 0)
                    _moving.Add(Sim.Collide.BodyCollider(pos, v.Type == EntityType.Npc ? _views.Defs.Npc(v.Label).Radius : Sim.Collide.BodyRadius));
            }
            return _moving.ToArray();
        }

        /// <summary>
        /// Where you are and where you face, for the compass and hit
        /// bearings: the CAMERA, not the on-foot predictor -- seated, the
        /// predictor is frozen at the boarding point and the compass froze
        /// with it (playtest 2026-10-02).
        /// </summary>
        private (Vec3 Pos, Vec3 Facing) Viewer =>
            (Frame.ToSim(_camera.GlobalPosition), Frame.ToSim(-_camera.GlobalBasis.Z));

        /// <summary>The seat readout: rover speed and role, or the ship's speed, altitude, regime, role.</summary>
        private void UpdateFlightReadout()
        {
            if (SeatKind == EntityType.Vehicle)
            {
                // The rover's line: speed from the predicted rover we steer.
                bool driver = _seat == 1 && _rover.Ready;
                _hudView.SetFlight(driver ? $"{_rover.State.Vel.Length * 3.6,5:F0} km/h   DRIVER" : "PASSENGER");
                return;
            }
            if (SeatKind != EntityType.Ship) { _hudView.SetFlight(null); return; }
            bool pilot = Piloting;
            Vec3 p;
            Vec3 v;
            bool space;
            if (pilot && _ship.Ready)
            {
                ShipSimState s = _ship.State;
                p = s.Pos; v = s.Vel; space = s.Space;
            }
            else if (_views.TryGet(_seatVehicle, out var vv) && vv.Root != null)
            {
                p = Frame.ToSim(vv.Root.GlobalPosition);
                v = Vec3.Zero;
                space = p.Length >= FlightRules.SpaceRadius;
            }
            else { _hudView.SetFlight(null); return; }

            double alt = p.Length - _terrain.SampleRadius(p.Normalized());
            _hudView.SetFlight(pilot
                ? $"{v.Length,6:F1} m/s   alt {alt,5:F0} m   {(space ? "SPACE" : "ATMO ")}   PILOT"
                : $"alt {alt,5:F0} m   {(space ? "SPACE" : "ATMO ")}   PASSENGER");
        }

        /// <summary>
        /// A mast's name from what stands under it: the client knows factions
        /// (kit style) but not zone ids, and both factions built two sites.
        /// The dispatcher marks the relay, the wrecks mark the outpost; the
        /// other colony site is the range and the other scrap site the camp.
        /// </summary>
        private string PoiName(Vector3 mast, bool scrap)
        {
            const float near = 60f;
            foreach (EntityView v in _views.All)
            {
                if (v.Root == null) continue;
                if (v.Root.GlobalPosition.DistanceTo(mast) > near) continue;
                if (!scrap && v.Type == EntityType.Npc && v.Label == "npc.dispatcher") return "Relay";
                if (scrap && v.Type == EntityType.Node && v.Label == "node.wreck") return "Outpost";
            }
            return scrap ? "Camp" : "Range";
        }

        /// <summary>
        /// Everything on the map: the live entities, plus the spawn point. The
        /// map is heading-up with no compass rose, so one landmark that never
        /// moves is what turns "things near me" into "where am I".
        /// </summary>
        private IEnumerable<MapMarker> MapMarkers()
        {
            foreach (MapMarker m in _views.Markers()) yield return m;
            Vec3 dir = Step.SpawnDir.Normalized();
            yield return new MapMarker(Frame.ToGodot(dir * _terrain.SampleRadius(dir)), EntityType.Ship, "Spawn", "spawn");
            // The POIs by their masts, named: a map is for finding places.
            foreach (var (pos, scrap) in _structures.Masts)
                yield return new MapMarker(pos, EntityType.Target, PoiName(pos, scrap), "poi");
        }

        /// <summary>
        /// Feeds the combat feed from a hit event (wire layout: shooter u32 |
        /// pos f32[3] | damage u16 | health_after u16). Numbers for every hit;
        /// the marker only for YOURS; the incoming arc only when the victim is you.
        /// </summary>
        private void OnHitFeedback(EventMsg ev)
        {
            if (ev.Data.Length < 20) return;
            var r = new WireReader(ev.Data);
            uint shooter = r.ReadU32();
            var simPoint = new Vec3(r.ReadF32(), r.ReadF32(), r.ReadF32());
            int damage = r.ReadU16();
            int healthAfter = r.ReadU16();

            _combatFeed.Damage(Frame.ToGodot(simPoint), damage, healthAfter == 0);
            if (shooter == _net.EntityId) _combatFeed.HitMarker(healthAfter == 0);
            if (ev.EntityId == _net.EntityId && _views.TryGet(shooter, out var sv) && sv.Root != null)
            {
                var me = Viewer;
                _combatFeed.Incoming(Bearing.To(me.Pos, me.Facing, Frame.ToSim(sv.Root.GlobalPosition)));
            }
        }

        /// <summary>The board NPC within interact reach, or 0.</summary>
        private uint NearestBoard()
        {
            Vector3 eye = _camera.GlobalPosition;
            foreach (var v in _views.All)
            {
                if (v.Root == null) continue;
                if (v.Label != "npc.dispatcher" && v.Label != "npc.quartermaster") continue;
                if ((v.Root.GlobalPosition - eye).Length() <= 3.5f) return v.Id;
            }
            return 0;
        }

        /// <summary>Players within 30 m, for the party panel's roster.</summary>
        private List<(uint id, string name)> NearbyPlayers()
        {
            var outp = new List<(uint, string)>();
            Vector3 eye = _camera.GlobalPosition;
            foreach (var v in _views.All)
            {
                if (v.Root == null || v.Type != EntityType.Player || v.Id == _net.EntityId) continue;
                if ((v.Root.GlobalPosition - eye).Length() <= 30f)
                    outp.Add((v.Id, string.IsNullOrEmpty(v.Label) ? $"player {v.Id}" : v.Label));
            }
            return outp;
        }

        /// <summary>K: the skills panel, refreshed from the server each time it opens.</summary>
        private void ToggleSkills()
        {
            bool open = !_skillsView.Open;
            _skillsView.Show(open);
            if (open)
            {
                _journalView.Show(false);
                _partyView.Show(false);
                _net.Send(Encode.Cmd(NextCmdSeq(), Op.Skills, "{}"));
            }
        }

        /// <summary>
        /// Toggles a panel and, if it just opened, asks the server for fresh
        /// credits and inventory: panels show server truth rather than
        /// whatever was last seen.
        /// </summary>
        /// <summary>Escape's first job: every panel, the map and the shop.</summary>
        /// <summary>The rig or a self-test owns the window; a player's settings do not apply.</summary>
        private bool Rigged => Arg("-uiShot") != null || Arg("-quitAfter") != null || Flag("-selftest") || Flag("-dumpNodes") || Arg("-dumpSfx") != null;

        /// <summary>Window mode, canvas scale and look speed from the settings.</summary>
        private void ApplySettings()
        {
            _settings.Apply(GetTree().Root);
            _fps.Sensitivity = FpsController.BaseSensitivity * _settings.MouseSensitivity;
        }

        /// <summary>GDD "Interaction" `ui_close_dist`: how far from its NPC an open counter survives.</summary>
        private const float CounterCloseDist = 5.0f;

        private bool OutOfCounterRange(uint npc)
        {
            if (!_views.TryGet(npc, out var v) || v.Root == null) return true;
            return Frame.ToGodot(_predictor.State.Pos).DistanceTo(v.Root.GlobalPosition) > CounterCloseDist;
        }

        /// <summary>Phase 13: a hotbar slot fired — an item or a worn ability sends `use`.</summary>
        private void FireHotbar(int slot)
        {
            if (slot < 0) return;
            var r = _hotbar.Refs[slot];
            switch (r.Kind)
            {
                case "item":
                case "ability":
                    if (_hotbarView.Cooling(r.Id, Clock.Now)) return; // the server would refuse; save the round trip
                    if (r.Kind == "item" && _character.Count(r.Id) <= 0) { _interact.Notice = "none left"; _noticeUntil = Clock.Now + 1.5; return; }
                    if (r.Kind == "ability" && _character.SlotHolding(r.Id) == "") { _interact.Notice = "not worn"; _noticeUntil = Clock.Now + 1.5; return; }
                    _net.Send(Character.UseCmd(NextCmdSeq(), r.Id));
                    break;
            }
        }

        /// <summary>Phase 13: the `use` reply — cooldown to the bar, pings to the compass, the bag refreshed.</summary>
        private void OnUseResult(CmdResult r)
        {
            if (!r.Ok) { _interact.Notice = Reason(r.Body); _noticeUntil = Clock.Now + 2; return; }
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(r.Body);
                string item = (string)o["item"] ?? "";
                _hotbarView.SetCooldown(item, (double?)o["cooldown"] ?? 0, Clock.Now);
                var effect = o["effect"] as Newtonsoft.Json.Linq.JObject;
                if (effect?["health"] != null) { _interact.Notice = $"+{_character.Defs.ItemName(item)}"; _noticeUntil = Clock.Now + 1.5; }
                if (effect?["pings"] is Newtonsoft.Json.Linq.JArray pings)
                {
                    int nodes = 0, drops = 0;
                    foreach (var ping in pings)
                    {
                        string def = (string)ping["def"] ?? "";
                        var pos = ping["pos"];
                        if (pos == null) continue;
                        Vector3 p = Frame.ToGodot(new Vec3((double)pos[0], (double)pos[1], (double)pos[2]));
                        string name = def.StartsWith("node.ore.iron") ? "IRON" : def.StartsWith("node.ore.copper") ? "COPPER" : def.StartsWith("node.wreck") ? "WRECK" : "DROP";
                        if (name == "DROP") drops++; else nodes++;
                        _scanPings.Add((name, p, Clock.Now + 20));
                    }
                    _interact.Notice = $"scan: {nodes} node{(nodes == 1 ? "" : "s")}, {drops} drop{(drops == 1 ? "" : "s")}";
                    _noticeUntil = Clock.Now + 3;
                }
                if (_character.Defs.Item(item)?.Consumable != null) _net.Send(Interaction.InventoryCmd(NextCmdSeq()));
            }
            catch (Newtonsoft.Json.JsonException) { }
        }

        /// <summary>Phase 12: the gather reply — a duration starts the bar, a refusal explains itself.</summary>
        private void OnGatherResult(CmdResult r)
        {
            if (!r.Ok) { _interact.Notice = Reason(r.Body); _noticeUntil = Clock.Now + 2; return; }
            double dur = 0;
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(r.Body);
                dur = (double?)o["duration"] ?? 0;
            }
            catch (Newtonsoft.Json.JsonException) { }
            if (dur <= 0) return;
            _channelStart = Clock.Now;
            _channelEnd = Clock.Now + dur;
            var nd = _views.TryGet(_interact.Target, out var tv) ? _character.Defs.Node(tv.Label) : null;
            _channelLabel = nd?.Skill == "salvaging" ? "cutting" : "drilling";
        }

        /// <summary>Phase 12: gather_end — clear the bar, say why, refresh the bag on a yield.</summary>
        private void OnGatherEnd(string json)
        {
            _channelStart = _channelEnd = -1;
            string reason = "", item = "";
            int qty = 0;
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                reason = (string)o["reason"] ?? "";
                item = (string)o["item"] ?? "";
                qty = (int?)o["qty"] ?? 0;
            }
            catch (Newtonsoft.Json.JsonException) { }
            if (reason == "done")
            {
                _interact.Notice = qty > 0 ? $"+{qty} {_character.Defs.ItemName(item)}" : "nothing came out";
                _net.Send(Interaction.InventoryCmd(NextCmdSeq()));
            }
            else _interact.Notice = reason switch
            {
                "moved" => "you moved",
                "hit" => "interrupted",
                "died" => "",
                "depleted" => "depleted",
                "cancel" => "cancelled",
                _ => reason,
            };
            _noticeUntil = Clock.Now + 2;
        }

        /// <summary>A refusal's {"reason"} as the words the HUD shows.</summary>
        private static string Reason(string body)
        {
            string code = "";
            try { code = (string)Newtonsoft.Json.Linq.JObject.Parse(body ?? "{}")["reason"] ?? ""; }
            catch (Newtonsoft.Json.JsonException) { }
            return code switch
            {
                "no_tool" => "needs the right tool in TOOL",
                "locked" => "skill too low",
                "depleted" => "depleted",
                "busy" => "already working",
                "no_space" => "no room in the bag",
                "out_of_range" => "too far away",
                "missing_materials" => "missing materials",
                "unsellable" => "no one buys that",
                "equipped" => "take it off first",
                "not_owned" => "you do not have that",
                "insufficient_credits" => "cannot afford that",
                "cooldown" => "not ready",
                "unusable" => "cannot use that",
                "no_effect" => "no need",
                "no_buyback" => "nothing to buy back",
                "dead" => "you are dead",
                "" => "refused",
                _ => code.Replace('_', ' '),
            };
        }

        private void CloseAllPanels()
        {
            foreach (ModalView m in new ModalView[] { _sheetView, _bagsView, _shopView, _journalView, _partyView, _skillsView, _gameMenu, _benchView, _settingsView })
                if (m != null && m.Open) m.Show(false);
            if (_map.Open) _map.Toggle();
            if (_interact.ShopOpen) _interact.CloseShop();
        }

        private void OpenPanel(ModalView view, ModalView other)
        {
            // Panels stack: the backpack and the character panel are meant
            // to be open together (drag between them), and every panel
            // drags to wherever the player keeps it. Nothing closes
            // anything else; each key toggles its own.
            bool open = !view.Open;
            view.Show(open);
            if (open) _net.Send(Character.RefreshCmd(NextCmdSeq()));
        }

        /// <summary>
        /// Tears down the connection and rejoins with a new token. The world
        /// stands; everything derived from OUR identity resets.
        /// </summary>
        private void Reconnect(string token)
        {
            _net.Dispose();
            _predictor.Reset();
            _rover.Reset();
            _ship.Reset();
            _timeline.Clear();
            _seat = 0;
            _seatVehicle = 0;
            _net = new NetClient();
            _net.Connect(ResolveServerUrl(), System.Environment.MachineName ?? "player", token);
        }

        // ---- launcher rig (-uiShot … -uiLauncher <state>) ----------------------

        /// <summary>Puts the model in a named state with fake text: no update check, no network.</summary>
        private void FakeLauncher(string state)
        {
            switch (state)
            {
                case "updating": _launch.Found("1.0.42"); _launch.Progress(37); break;
                case "restarting": _launch.Found("1.0.42"); _launch.Downloaded(); break;
                case "uptodate": _launch.Current("1.0.42"); break;
                case "devbuild": _launch.NotInstalled(); break;
                case "offline": _launch.Failed("fake"); break;
                case "checking": break;
                default: GD.PushError($"-uiLauncher: unknown state {state}"); break;
            }
            _launcherView.SetServer(Launcher.ServerLine(3));
            GD.Print($"launcher: faked {_launch.Now}");
        }

        /// <summary>-uiLogin &lt;state&gt;: the account column in a named state, a fake email, no network.</summary>
        private void FakeLogin(string state)
        {
            const string email = "pilot@example.com";
            _login.SignOut(); // the model only: a remembered sign-in on this machine does not leak into the shot
            switch (state)
            {
                case "signedout": break;
                case "busy": _login.Submit(false); break;
                case "signedin": _login.Remembered(email); break;
                case "failed": _login.Submit(false); _login.Fail(401); break;
                default: GD.PushError($"-uiLogin: unknown state {state}"); break;
            }
            GD.Print($"launcher: login faked {_login.Now}");
        }

        private async System.Threading.Tasks.Task SaveLauncherShot(string path)
        {
            double wait = double.Parse(Arg("-uiShotAfter") ?? "2", CultureInfo.InvariantCulture);
            await ToSignal(GetTree().CreateTimer(wait), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            Image img = GetViewport().GetTexture().GetImage();
            Error err = img.SavePng(path);
            GD.Print($"uiShot: {path} {img.GetWidth()}x{img.GetHeight()} {err}");
        }

        // ---- self-test --------------------------------------------------------

        /// <summary>
        /// `-selftest`: the engine-bound checks that need no server and no
        /// display — the sign rules in Fps and the winding measure. Console
        /// runner, exits non-zero, same shape as SimDump --selftest.
        /// </summary>
        private static int SelfTest()
        {
            int failed = 0;
            void Check(string what, bool ok)
            {
                GD.Print($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) failed++;
            }

            FpsController.SelfCheck(Check);

            // Frame: identity mapping, and the orientation basis puts +Z on
            // the facing and +Y on up, as Step.OrientationQuat promises.
            var p = new Vec3(0, 150, 0);
            var f = new Vec3(0, 0, 1);
            Basis b = Frame.OrientationBasis(p, f);
            Check("orientation basis: +Z is the facing", (b * Vector3.Back).DistanceTo(Frame.ToGodot(f)) < 1e-5f);
            Check("orientation basis: +Y is up", (b * Vector3.Up).DistanceTo(Vector3.Up) < 1e-5f);
            Check("model flip turns −Z into the facing", (Frame.ModelFlip * Vector3.Forward).DistanceTo(Vector3.Back) < 1e-5f);

            // First-person arms: the holder puts the body's eye on the camera
            // and the model's forward (+Z after Attach's flip) down −Z.
            Transform3D fp = ViewModel.FpHolder(Basis.Identity, Vector3.Zero);
            Check("fp arms: the body's eye sits on the camera", (fp * (Frame.ModelFlip * new Vector3(0, FpsController.EyeHeight, 0))).Length() < 1e-4f);
            Check("fp arms: the model faces down the camera's −Z", (fp.Basis * Vector3.Back).DistanceTo(Vector3.Forward) < 1e-5f);
            Check("fp clips: unarmed beats lowered beats aiming beats the gait",
                FirstPersonAnim.Pick(9f, false, true, true) == "fp_unarmed" && FirstPersonAnim.Pick(9f, true, true, true) == "fp_lower"
                && FirstPersonAnim.Pick(9f, true, true, false) == "fp_ads" && FirstPersonAnim.Pick(9f, true, false, false) == "fp_sprint"
                && FirstPersonAnim.Pick(1f, true, false, false) == "fp_walk" && FirstPersonAnim.Pick(0f, true, false, false) == "fp_idle");
            Check("near cut: all eight variants compile with NearCut/NearCutBand", ViewModel.NearCutShadersParse());
            var wall = new[] { new Sim.Collider { Kind = Sim.ColliderKind.Box, Center = new Vec3(0, 151, -0.8), Half = new Vec3(1, 1, 0.1), Rot = new Quat(0, 0, 0, 1) } };
            var ball = new[] { new Sim.Collider { Kind = Sim.ColliderKind.Sphere, Center = new Vec3(2, 151, -0.8), Half = new Vec3(0.5, 0, 0), Rot = new Quat(0, 0, 0, 1) } };
            var eyeAt = new Vector3(0, 151, 0);
            Check("fp lower: a wall 0.8 m ahead is in the way", ViewModel.Blocked(eyeAt, Vector3.Forward, ViewModel.Reach, wall, null) < ViewModel.LowerAt);
            Check("fp lower: the same wall behind is not", float.IsPositiveInfinity(ViewModel.Blocked(eyeAt, Vector3.Back, ViewModel.Reach, wall, null)));
            Check("fp lower: a ball off to the side is not", float.IsPositiveInfinity(ViewModel.Blocked(eyeAt, Vector3.Forward, ViewModel.Reach, ball, null)));
            Check("sfx: a shot behind the wall is muffled, one beside it is not",
                Sfx.Occluded(eyeAt, new Vector3(0, 150, -6), wall, null) && !Sfx.Occluded(eyeAt, new Vector3(10, 150, -6), wall, null));

            // Winding: a counter-clockwise triangle seen from outside the
            // planet is a front face and must NOT be flipped.
            var verts = new[] { new Vector3(0, 150, 0), new Vector3(1, 150, 0), new Vector3(0, 150, -1) };
            // Phase 13: the hotbar's defaults and its save format.
            var hb = new UI.Hotbar();
            Check("hotbar: a fresh profile's bar is empty", Array.TrueForAll(hb.Refs, r => r.Empty));
            Check("hotbar: a reference round-trips through its string", UI.HotbarRef.Parse("item:consumable.medkit").Id == "consumable.medkit" && UI.HotbarRef.Parse("").Empty);
            Check("hotbar: an old action entry reads as empty", UI.HotbarRef.Parse("action:interact").Empty);
            Check("hotbar: keys are 1–5 Q E T Z X; Shift+Q is slot 16; R and F are not on the bar",
                UI.Hotbar.SlotFor(Key.Q, true) == 15 && UI.Hotbar.SlotFor(Key.Z, false) == 8 && UI.Hotbar.SlotFor(Key.X, false) == 9
                && UI.Hotbar.SlotFor(Key.R, false) == -1 && UI.Hotbar.SlotFor(Key.F, false) == -1);
            Check("sfx: weapon families", Sfx.Family("weapon.dmr") == "dmr" && Sfx.Family("weapon.sidearm") == "pistol"
                && Sfx.Family("weapon.smg") == "smg" && Sfx.Family("weapon.pulse") == "rifle" && Sfx.Family("") == "rifle"
                && Sfx.Family("weapon.smg.frost") == "smg" && Sfx.Family("weapon.pulse.dune") == "rifle");
            var sfx = new Sfx(null);
            Check("sfx: every sound synthesizes, 16-bit, normalized", Array.TrueForAll(sfx.All(), s =>
            {
                byte[] d = s.wav.Data;
                int peak = 0;
                for (int i = 0; i + 1 < d.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(d[i] | d[i + 1] << 8)));
                return d.Length > 400 && peak > 25000 && peak < 32000;
            }));
            // Phase 20: the chat log's model (GDD "Chat (Phase 20)", C176).
            var chat = new UI.ChatLog();
            Check("chat: an empty log is not visible", !chat.Visible && chat.Lines.Count == 0);
            for (int i = 0; i < 10; i++) chat.Push($"p{i}", $"line {i}", i * 0.1);
            Check("chat: Push caps at 8, oldest dropped", chat.Visible && chat.Lines.Count == UI.ChatLog.MaxLines
                && chat.Lines[0].Text == "line 2" && chat.Lines[7].Text == "line 9");
            var aged = new UI.ChatLog();
            aged.Push("Kade", "old", 0.0);
            aged.Push("Tam", "young", 5.0);
            aged.Expire(10.5);
            Check("chat: Expire drops lines older than 10 s, keeps younger", aged.Lines.Count == 1 && aged.Lines[0].Name == "Tam");
            Check("chat: a line fades over its last 2 s", UI.ChatLog.Alpha(aged.Lines[0], 12.0) == 1f
                && Math.Abs(UI.ChatLog.Alpha(aged.Lines[0], 14.0) - 0.5f) < 1e-5f);
            aged.Expire(16.0);
            Check("chat: all expired is not visible", !aged.Visible);
            var open = new UI.ChatLog();
            bool closedAtStart = !open.Open;
            open.Open = true;
            bool opened = open.Open;
            open.Open = false;
            Check("chat: Open toggles", closedAtStart && opened && !open.Open);
            // Phase 15: the launcher's state machine (GDD table).
            var l1 = new UI.Launcher();
            Check("launcher: starts Checking with PLAY disabled", l1.Now == UI.Launcher.State.Checking && !l1.PlayEnabled && l1.Line == "CHECKING FOR UPDATES…");
            l1.Found("1.0.42");
            l1.Progress(37);
            Check("launcher: Updating line is exact", l1.Line == "UPDATING TO v1.0.42 … 37 %" && Math.Abs(l1.Fraction - 0.37f) < 1e-6f && !l1.PlayEnabled);
            l1.Downloaded();
            Check("launcher: Found→Progress→Downloaded is Restarting, PLAY disabled", l1.Now == UI.Launcher.State.Restarting && !l1.PlayEnabled && l1.Line == "RESTARTING…");
            var l2 = new UI.Launcher();
            l2.Current("1.0.42");
            Check("launcher: Current is UpToDate, PLAY enabled", l2.Now == UI.Launcher.State.UpToDate && l2.PlayEnabled && l2.Line == "UP TO DATE · v1.0.42" && l2.Fraction < 0);
            var l3 = new UI.Launcher();
            l3.Failed("timeout");
            Check("launcher: Failed is Offline, PLAY enabled", l3.Now == UI.Launcher.State.Offline && l3.PlayEnabled && l3.Line == "UPDATE CHECK FAILED · playing the installed build" && l3.Reason == "timeout");
            var l4 = new UI.Launcher();
            l4.Found("1.0.42");
            l4.Failed("download died");
            Check("launcher: a download failing mid-way is Offline, PLAY enabled", l4.Now == UI.Launcher.State.Offline && l4.PlayEnabled);
            var l5 = new UI.Launcher();
            l5.NotInstalled();
            Check("launcher: NotInstalled is DevBuild, PLAY enabled", l5.Now == UI.Launcher.State.DevBuild && l5.PlayEnabled && l5.Line == "DEV BUILD · not installed, no update check");
            Check("launcher: site URL from the game URL",
                UI.Launcher.SiteUrl("wss://game.stevenholder.info/ws") == "https://game.stevenholder.info"
                && UI.Launcher.SiteUrl("ws://127.0.0.1:18080/ws") == "http://127.0.0.1:18080");
            Check("launcher: server line", UI.Launcher.ServerLine(3) == "SERVER · ONLINE · 3 PLAYING" && UI.Launcher.ServerLine(null) == "SERVER · UNREACHABLE");
            // Phase 16: the account column's model (GDD "Account column in the launcher").
            var g = new UI.Login();
            Check("login: starts SignedOut, PLAY not allowed", g.Now == UI.Login.State.SignedOut && !g.PlayAllowed && g.Line == "Sign in to play");
            g.Remembered("pilot@example.com");
            Check("login: Remembered is SignedIn, PLAY allowed", g.Now == UI.Login.State.SignedIn && g.PlayAllowed && g.Line == "SIGNED IN · pilot@example.com");
            var gb = new UI.Login();
            gb.Submit(false);
            Check("login: Submit(sign in) is Busy", gb.Now == UI.Login.State.Busy && !gb.PlayAllowed && gb.Line == "SIGNING IN…");
            var gc = new UI.Login();
            gc.Submit(true);
            Check("login: Submit(create) is Busy", gc.Now == UI.Login.State.Busy && gc.Creating && gc.Line == "CREATING ACCOUNT…");
            gc.Ok("pilot@example.com");
            Check("login: Ok is SignedIn", gc.Now == UI.Login.State.SignedIn && gc.PlayAllowed && gc.Email == "pilot@example.com");
            foreach (var (status, reason) in new[] { (401, "WRONG EMAIL OR PASSWORD"), (409, "EMAIL ALREADY REGISTERED"), (400, "PASSWORD TOO SHORT"), (429, "TOO MANY TRIES · wait a moment"), (0, "SITE UNREACHABLE") })
            {
                var gf = new UI.Login();
                gf.Submit(false);
                gf.Fail(status);
                Check($"login: Fail({status}) is Failed · {reason}", gf.Now == UI.Login.State.Failed && gf.Reason == reason && !gf.PlayAllowed);
            }
            var gr = new UI.Login();
            gr.Remembered("pilot@example.com");
            gr.Refused();
            Check("login: Refused is SIGNED OUT · sign in again", gr.Now == UI.Login.State.Failed && gr.Reason == "SIGNED OUT · sign in again" && !gr.PlayAllowed);
            g.SignOut();
            Check("login: SignOut is SignedOut, email gone", g.Now == UI.Login.State.SignedOut && g.Email == "" && !g.PlayAllowed);
            string longName = UI.Login.Shorten("averyveryverylongname@example.com");
            Check("login: a long email is shortened with …", longName.Length <= 26 && longName.EndsWith("…", StringComparison.Ordinal));
            Check("login: a short email is unchanged", UI.Login.Shorten("a@b.c") == "a@b.c");
            // Phase 16: the character select's model (GDD "Character select").
            CharacterRow Row(string name, string body) => new CharacterRow { Token = "t-" + name, Name = name, Body = body, Credits = 10, LastSeenMs = 0 };
            var three = new List<CharacterRow> { Row("Kade", "char.ubc.f"), Row("Tam", "char.player"), Row("Vex", "char.player.f") };
            var ch = new Characters();
            Check("chars: starts Loading, PLAY dark", ch.Now == Characters.State.Loading && !ch.CanPlay);
            ch.Loaded(three);
            Check("chars: Loaded(3) is List, row 0 selected, PLAY lit, stage on row 0's body",
                ch.Now == Characters.State.List && ch.Selected == 0 && ch.CanPlay && ch.StageBody == "char.ubc.f");
            ch.Select(2);
            Check("chars: Select(2) stages row 2's body", ch.Selected == 2 && ch.StageBody == "char.player.f");
            var ce = new Characters();
            ce.Loaded(new List<CharacterRow>());
            Check("chars: Loaded(empty) opens Create, PLAY dark", ce.Now == Characters.State.Create && !ce.CanPlay);
            ch.NewCharacter();
            Check("chars: NewCharacter is Create, stage char.player", ch.Now == Characters.State.Create && ch.StageBody == "char.player");
            ch.SetFemale(true);
            ch.SetVanguard(true);
            Check("chars: F + VANGUARD stages char.ubc.f", ch.StageBody == "char.ubc.f");
            foreach (var (name, ok) in new[] { ("Ka", false), ("Kade", true), ("Kade!", false), (" Kade", false), ("Ka  de", false), ("Abcdefghijklmnop", true), ("Abcdefghijklmnopq", false) })
                Check($"chars: NameOk(\"{name}\") is {ok}", Characters.NameOk(name) == ok);
            ch.SetName("Kade");
            Check("chars: a good name lights CREATE", ch.CanCreate);
            ch.CreateFailed(409, "name taken");
            Check("chars: CreateFailed(409, name taken) is NAME TAKEN, still Create", ch.Reason == "NAME TAKEN" && ch.Now == Characters.State.Create);
            ch.Created(Row("Juno", "char.ubc.f"));
            Check("chars: Created lists and selects the new row, PLAY lit",
                ch.Now == Characters.State.List && ch.Rows.Count == 4 && ch.Rows[ch.Selected].Name == "Juno" && ch.CanPlay);
            ch.NewCharacter();
            ch.Cancel();
            Check("chars: Cancel is List", ch.Now == Characters.State.List);
            Check("chars: NEW lit under five", ch.CanNew);
            ch.NewCharacter();
            ch.Created(Row("Zed", "char.player"));
            Check("chars: NEW dark at five", ch.Rows.Count == 5 && !ch.CanNew);
            var cf = new Characters();
            cf.Fail("COULD NOT LOAD CHARACTERS · retry");
            Check("chars: Fail is Failed, PLAY dark", cf.Now == Characters.State.Failed && !cf.CanPlay && cf.Reason == "COULD NOT LOAD CHARACTERS · retry");
            foreach (var (female, vanguard, id) in new[] { (false, false, "char.player"), (true, false, "char.player.f"), (false, true, "char.ubc"), (true, true, "char.ubc.f") })
                Check($"chars: BodyId/ParseBody round-trip {id}", Characters.BodyId(female, vanguard) == id && Characters.ParseBody(id) == (female, vanguard));
            // Phase 17: the HAIR row (GDD "Faces and hair").
            var hr = new Characters();
            hr.Loaded(new List<CharacterRow>
            {
                new CharacterRow { Token = "t-k", Name = "Kade", Body = "char.ubc.f", Hair = "hair.long" },
                new CharacterRow { Token = "t-t", Name = "Tam", Body = "char.player", Hair = "hair.none" },
            });
            Check("chars: StageHair in List is the selected row's", hr.StageHair == "hair.long");
            hr.Select(1);
            Check("chars: StageHair follows Select", hr.StageHair == "hair.none");
            hr.NewCharacter();
            Check("chars: NewCharacter's hair is the first real style, staged",
                hr.Hair == Characters.HairStyles[1].id && hr.Hair == "hair.buzzed" && hr.StageHair == "hair.buzzed");
            hr.NextHair();
            Check("chars: NextHair steps BUZZED → BUZZED F", hr.Hair == "hair.buzzed_female" && Characters.HairLabel(hr.Hair) == "BUZZED F");
            hr.PrevHair(); hr.PrevHair();
            Check("chars: PrevHair steps back to NONE", hr.Hair == "hair.none" && Characters.HairLabel(hr.Hair) == "NONE");
            hr.PrevHair();
            Check("chars: PrevHair wraps NONE → BEARD", hr.Hair == "hair.beard");
            hr.NextHair();
            Check("chars: NextHair wraps BEARD → NONE", hr.Hair == "hair.none");
            for (int i = 0; i < Characters.HairStyles.Length; i++) hr.NextHair();
            Check("chars: a full lap of NextHair comes home", hr.Hair == "hair.none");
            hr.SetHair("hair.long");
            hr.Cancel();
            Check("chars: Cancel stages the row's hair again", hr.StageHair == "hair.none");
            hr.NewCharacter();
            Check("chars: NewCharacter resets the hair", hr.Hair == "hair.buzzed");
            hr.CreateFailed(400, "bad hair");
            Check("chars: CreateFailed(400, bad hair) is BAD HAIR, still Create", hr.Reason == "BAD HAIR" && hr.Now == Characters.State.Create);
            // Phase 18: edit and delete (GDD "Edit a character").
            var ed = new Characters();
            ed.Loaded(new List<CharacterRow>
            {
                new CharacterRow { Token = "t-k", Name = "Kade", Body = "char.ubc.f", Hair = "hair.long" },
                new CharacterRow { Token = "t-t", Name = "Tam", Body = "char.player", Hair = "hair.none" },
                new CharacterRow { Token = "t-v", Name = "Vex", Body = "char.player.f", Hair = "hair.buns" },
            });
            ed.Edit(0);
            Check("chars: Edit(0) is Create prefilled from row 0, Editing set",
                ed.Now == Characters.State.Create && ed.Editing == ed.Rows[0] && ed.Name == "Kade" && ed.Hair == "hair.long"
                && ed.Female && ed.Vanguard && ed.Selected == 0);
            Check("chars: edit mode stages the row's body and the form's hair", ed.StageBody == "char.ubc.f" && ed.StageHair == "hair.long");
            Check("chars: unchanged edit is not Dirty, SAVE dark, CREATE dark", !ed.Dirty && !ed.CanSave && !ed.CanCreate);
            ed.NextHair();
            Check("chars: a hair change is Dirty, SAVE lit, staged live", ed.Dirty && ed.CanSave && ed.StageHair == "hair.buns");
            ed.PrevHair();
            Check("chars: hair back to the row's is clean again", !ed.Dirty && !ed.CanSave);
            ed.SetName("Ka");
            Check("chars: a bad name is Dirty but SAVE dark", ed.Dirty && !ed.CanSave);
            ed.SetName("Kadence");
            Check("chars: a good new name lights SAVE", ed.CanSave);
            ed.CreateFailed(404, "no such character");
            Check("chars: CreateFailed(404) is NO SUCH CHARACTER, still editing", ed.Reason == "NO SUCH CHARACTER" && ed.Editing != null && ed.Now == Characters.State.Create);
            ed.Saved(new CharacterRow { Token = "t-k", Name = "Kadence", Body = "char.ubc.f", Hair = "hair.buns" });
            Check("chars: Saved replaces the row in place, selected, List, Editing cleared",
                ed.Now == Characters.State.List && ed.Rows.Count == 3 && ed.Selected == 0 && ed.Rows[0].Name == "Kadence"
                && ed.StageHair == "hair.buns" && ed.Editing == null);
            ed.Edit(1);
            ed.Cancel();
            Check("chars: Cancel clears Editing, List", ed.Editing == null && !ed.Deleting && ed.Now == Characters.State.List);
            ed.Edit(1);
            ed.AskDelete();
            Check("chars: AskDelete asks", ed.Deleting && ed.Now == Characters.State.Create);
            ed.KeepIt();
            Check("chars: KeepIt keeps, still editing", !ed.Deleting && ed.Editing != null && ed.Now == Characters.State.Create);
            ed.AskDelete();
            ed.Deleted("t-t");
            Check("chars: Deleted removes the row and selects the next",
                ed.Now == Characters.State.List && ed.Rows.Count == 2 && ed.Rows[ed.Selected].Name == "Vex" && ed.Editing == null && !ed.Deleting);
            ed.Edit(1);
            ed.Deleted("t-v");
            Check("chars: Deleted on the last row selects the previous", ed.Rows.Count == 1 && ed.Selected == 0 && ed.Rows[0].Name == "Kadence");
            ed.Edit(0);
            ed.Deleted("t-k");
            Check("chars: Deleted on the only row opens Create", ed.Rows.Count == 0 && ed.Now == Characters.State.Create && ed.Editing == null && ed.Selected == -1);
            ed.NewCharacter();
            Check("chars: NewCharacter is not edit mode", ed.Editing == null && !ed.CanSave);
            Check("chars: a hair slot's item is its own asset, armor goes through items.json",
                EntityViews.WornAsset(Defs.Empty, "hair.buns") == "hair.buns" && EntityViews.WornAsset(Defs.Empty, "armor.helmet.scout") == "");
            Check("chars: a player spawn's data splits on the first NUL",
                EntityViews.SplitPlayerData("Kade\0char.ubc.f") == ("Kade", "char.ubc.f") && EntityViews.SplitPlayerData("Tam") == ("Tam", null));
            Check("outward CCW triangle is not inward", !TerrainMesh.FacesInward(verts, new[] { 0, 1, 2 }));
            Check("the same triangle reversed is inward", TerrainMesh.FacesInward(verts, new[] { 0, 2, 1 }));

            GD.Print(failed == 0 ? "OVERALL: PASS" : $"OVERALL: FAIL ({failed})");
            return failed == 0 ? 0 : 1;
        }
    }
}
