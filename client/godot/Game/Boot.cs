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
        /// The identity token. Persistence is keyed on it (C11), so it has to
        /// survive a restart — a ConfigFile under user:// is the smallest
        /// thing that does. -token overrides (the screenshot rig uses it).
        /// </summary>
        private static string ResolveToken()
        {
            string arg = Arg("-token");
            if (arg != null) return arg;
            var cf = new ConfigFile();
            cf.Load(ConfigPath); // a missing file is an empty config
            string token = (string)cf.GetValue("identity", "token", "");
            if (string.IsNullOrEmpty(token))
            {
                token = Guid.NewGuid().ToString("N");
                SaveToken(token);
            }
            return token;
        }

        private static void SaveToken(string token)
        {
            var cf = new ConfigFile();
            cf.Load(ConfigPath);
            cf.SetValue("identity", "token", token);
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
        private Hud _hud;
        private Character _character;
        private Interaction _interact;

        // The interface layer.
        private UiRoot _ui;
        private HudView _hudView;
        private CombatFeed _combatFeed;
        private ShopView _shopView;
        private BackpackView _bagsView;
        private CharacterView _sheetView;
        private Icons _icons;
        private PromptView _promptView;
        private AccountView _accountView;
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
            (_sheetView?.Open ?? false) || (_accountView?.Open ?? false) ||
            (_journalView?.Open ?? false) || (_partyView?.Open ?? false) ||
            (_skillsView?.Open ?? false) || (_gameMenu?.Open ?? false);

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

        // ---- lifecycle ------------------------------------------------------

        public override void _Ready()
        {
            string q = Arg("-quitAfter");
            if (q != null) _quitAfter = double.Parse(q, CultureInfo.InvariantCulture);

            GD.Print($"boot: godot {Engine.GetVersionInfo()["string"]} " +
                     $"dotnet {RuntimeInformation.FrameworkDescription} " +
                     $"display {DisplayServer.GetName()}");

            if (Flag("-selftest"))
            {
                GetTree().Quit(SelfTest());
                return;
            }

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
            _viewModel.WeaponVisible = false; // until the server says we are holding one
            _fx = new CombatFx(this);
            _hud = new Hud();
            _character = new Character();
            _interact = new Interaction(_views, _character);

            _ui = new UiRoot(this);
            _hudView = new HudView(_ui.Root);
            _combatFeed = new CombatFeed(_ui.Root);
            _map = new MapView(_ui.Root);
            _icons = new Icons(_assets.Root);
            _shopView = new ShopView(_ui.Root, _character, _interact, _icons, NextCmdSeq, b => _net.Send(b));
            _bagsView = new BackpackView(_ui.Root, _character, _icons, NextCmdSeq, b => _net.Send(b));
            _sheetView = new CharacterView(_ui.Root, _character, _skills, _icons, _assets, NextCmdSeq, b => _net.Send(b));
            _promptView = new PromptView(_ui.Root);
            _journalView = new JournalView(_ui.Root, _missionLog, _partyState, NearestBoard, NextCmdSeq, b => _net.Send(b));
            _partyView = new PartyView(_ui.Root, _partyState, NearbyPlayers, NextCmdSeq, b => _net.Send(b));
            _partyFrames = new PartyFrames(_ui.Root);
            _skillsView = new SkillsView(_ui.Root, _skills, _character);
            _skillsFeed = new SkillsFeed(_ui.Root);
            _gameMenu = new GameMenuView(_ui.Root, () => _accountView.Show(true), () => GetTree().Quit(0));
            _accountView = new AccountView(_ui.Root,
                code => { _accountView.SetStatus("redeeming…"); _ = RedeemLinkCode(code); },
                () => _accountView.Show(false));

            // -dumpNodes <asset id>: print the imported node tree and clips,
            // then quit. Pins the node-name and animation import rules.
            string dump = Arg("-dumpNodes");
            if (dump != null)
            {
                GD.Print(_assets.Describe(dump));
                GetTree().Quit(0);
                return;
            }

            _net = new NetClient();
            _serverUrl = ResolveServerUrl();
            _net.Connect(_serverUrl, System.Environment.MachineName ?? "player", ResolveToken());

            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Captured;

            // -uiShot <path>: save a screenshot once the world settles, the
            // review artifact for C60 (test/out/ui/) and the only eyes a
            // headless agent has.
            string shot = Arg("-uiShot");
            if (shot != null) _ = SaveUiShot(shot);
        }

        public override void _ExitTree()
        {
            _net?.Dispose();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }

        public override void _UnhandledInput(InputEvent e) => _input.Feed(e);

        public override void _Process(double delta)
        {
            // Godot .NET reports an unhandled exception and keeps running, which
            // is the same lie Unity's batchmode told: a headless run would log
            // the error and still exit 0. Under -quitAfter the exit code is the
            // verdict, so the first exception ends the run with a non-zero one.
            try
            {
                Clock.Dt = delta;
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
            if (!_worldBuilt)
            {
                // No world yet: the only thing worth drawing is why.
                _hudView.SetBanner(LinkBanner(), true);
                return;
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
            if (_input.Pressed(Key.F1))
            {
                _accountView.Show(!_accountView.Open);
                _accountView.SetStatus("");
            }
            if (_input.Pressed(Key.M)) _map.Toggle();
            if (_input.Pressed(Key.R)) _net.Send(Character.ReloadCmd(NextCmdSeq()));
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

            // Look-at targeting, then E. The shop swallows E so closing it
            // does not immediately reopen it on the same key press.
            if (_seat == 0)
            {
                Vector3 eye = Frame.ToGodot(state.Pos);
                eye += eye.Normalized() * FpsController.EyeHeight;
                _interact._selfId = _net.EntityId;
                _interact.Update(eye, Frame.ToGodot(li.Look));
            }
            if (_seat != 0) _interact.Notice = SeatKind == EntityType.Ship ? "E  ·  exit ship" : "E  ·  exit rover";
            else if (Clock.Now > _noticeUntil) _interact.Notice = "";
            if (li.InteractPressed && !_map.Open && !(_bagsView.Open || _sheetView.Open)) OnInteract();

            _timeline.OneWaySeconds = _net.RttMs > 0 ? _net.RttMs / 2000.0 : 0.0;
            _views.Render(_timeline, _net.EntityId);
            if (_seat != 0) PlaceSeatCamera();
            else _fps.PlaceCamera(_predictor.State.Pos);
            _rigLight.GlobalPosition = _camera.GlobalTransform * new Vector3(0.35f, 0.25f, 0.1f); // above and right of the eye: lights the top and rear of the rifle

            // The rig follows the character sheet, which is the one place
            // that knows what is equipped. The body stands where the
            // simulation puts it, and the rig sways against the real speed
            // rather than the input.
            // -rigArmed: show the rig without a purchase (screenshot rig).
            // The server still drops the shots of an unarmed player.
            if (_rigArmed && string.IsNullOrEmpty(_character.Primary)) _character.Primary = "weapon.pulse";
            _viewModel.WeaponVisible = _seat == 0 && !string.IsNullOrEmpty(_character.Primary);
            State body = _predictor.State;
            _viewModel.Place(body.Pos, body.Facing);
            _viewModel.Tick(_fps.LookDelta, (float)body.Vel.Length, (float)delta);
            _fx.Tick();

            UpdateHudView();
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
            if (_interact.Target == 0) return;
            switch (_interact.TargetType)
            {
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
                Vec3 p = ship ? _ship.State.Pos : _rover.State.Pos;
                Quat q = ship ? _ship.State.Quat : _rover.State.Quat;
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
            if (li.FirePressed && _viewModel.WeaponVisible && Clock.Now >= _nextFireAt)
            {
                _nextFireAt = Clock.Now + FireIntervalSeconds;
                _net.Send(Encode.Fire(_seq, (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z));
                _fx.OnLocalFire(_viewModel.Muzzle, _viewModel.Layers);
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
                    GD.Print($"colliders: {_colliders.Length}");
                    break;
                }
                case Msg.Props:
                    // Visual only, and deliberately not routed anywhere near
                    // the predictor: props have no collision.
                    _structures.BuildProps(Decode.Props(frame.Reader));
                    break;
                case Msg.Defs:
                {
                    // Item names, kinds and equipment slots, and the `asset`
                    // id behind every entity, which is what tells EntityViews
                    // which model in art/manifest.json to load.
                    Defs defs = Decode.Defs(frame.Reader);
                    _character.Defs = defs;
                    _views.Defs = defs;
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
                    _timeline.Add(snap, (float)Clock.Now);
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
                            break;
                        case EventId.Hit:
                            _fx.OnHit(ev, _net.EntityId);
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
                            break;
                        case EventId.Equipped:
                        {
                            string item = WireReader.Utf8.GetString(ev.Data);
                            _views.OnEquipped(ev.EntityId, item);
                            // Our own weapon comes down the same channel, and
                            // is replayed at join, so a reconnect holding a
                            // rifle shows one (PROTOCOL event_id 0x0006).
                            if (ev.EntityId == _net.EntityId) _character.Primary = item;
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
                    if (r.Ok && (r.Opcode == Op.Inventory || r.Opcode == Op.ShopBuy)) _character.OnWallet(r.Body);
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
                case "account": _accountView.Show(true); break;
                case "debug": _hud.DebugOpen = true; break;
                case "menu": _gameMenu.Show(true); break;
            }
        }

        /// <summary>
        /// Feeds the HUD from the same sources everything else reads, plus
        /// compass markers by egocentric bearing (Bearing.To).
        /// </summary>
        private void UpdateHudView()
        {
            _hudView.SetVitals(_hud.Health, 100);
            _sheetView.Tick(Clock.Dt);
            _hudView.SetDeath(_hud.Dead ? _hud.RespawnIn : -1);
            _hudView.SetAmmo(_character.Magazine, _character.Reserve,
                _character.Magazine >= 0 && !string.IsNullOrEmpty(_character.Primary));
            _hudView.SetCredits(_character.Credits);

            var me = _predictor.State;
            var markers = new List<(string, double)>();
            foreach (var v in _views.All)
            {
                if (v.Root == null || !v.Root.Visible) continue;
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
                markers.Add((scrap ? "OUTPOST" : "RELAY", Bearing.To(me.Pos, me.Facing, Frame.ToSim(pos))));
            }
            _hudView.SetMarkers(markers);

            // The shop view mirrors Interaction's state: stock arriving opens
            // it, CloseShop (or a despawned NPC) closes it.
            if (_interact.ShopOpen && !_shopView.Open) _shopView.Show(true);
            if (!_interact.ShopOpen && _shopView.Open) _shopView.Show(false);

            _promptView.Set(!string.IsNullOrEmpty(_interact.Notice) ? _interact.Notice : _interact.Prompt);

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
            else
                _hudView.SetBanner(null, false);

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

            _hudView.SetLog(_hud.Lines);
            _hudView.SetDebug(_hud.DebugText(_net, _predictor, _character));
            _hudView.UpdateHealthBars(_camera, _views, !_map.Open);

            _map.Draw(_terrain, Frame.ToGodot(me.Pos), Frame.ToGodot(me.Facing), MapMarkers());
        }

        /// <summary>The flight readout: speed, altitude above the terrain under the ship, regime, role.</summary>
        private void UpdateFlightReadout()
        {
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
        /// Everything on the map: the live entities, plus the spawn point. The
        /// map is heading-up with no compass rose, so one landmark that never
        /// moves is what turns "things near me" into "where am I".
        /// </summary>
        private IEnumerable<MapMarker> MapMarkers()
        {
            foreach (MapMarker m in _views.Markers()) yield return m;
            Vec3 dir = Step.SpawnDir.Normalized();
            yield return new MapMarker(Frame.ToGodot(dir * _terrain.SampleRadius(dir)), EntityType.Ship, "Spawn");
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
                var me = _predictor.State;
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
        private void CloseAllPanels()
        {
            foreach (ModalView m in new ModalView[] { _sheetView, _bagsView, _shopView, _journalView, _partyView, _skillsView, _accountView, _gameMenu })
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
        /// POST /api/redeem on the server the game is already talking to
        /// (ws→http on the same origin), store the token, reconnect.
        /// </summary>
        private async System.Threading.Tasks.Task RedeemLinkCode(string code)
        {
            string wsUrl = ResolveServerUrl();
            string apiUrl = wsUrl.Replace("wss://", "https://").Replace("ws://", "http://");
            int slash = apiUrl.LastIndexOf("/ws", StringComparison.Ordinal);
            if (slash >= 0) apiUrl = apiUrl.Substring(0, slash);
            apiUrl += "/api/redeem";

            try
            {
                using var http = new System.Net.Http.HttpClient();
                using var req = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = new StringContent("{\"code\":\"" + code + "\"}", Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-Requested-With", "sa-client");
                using HttpResponseMessage resp = await http.SendAsync(req);
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _accountView.SetStatus((int)resp.StatusCode == 401 ? "unknown or expired code" : $"failed: {(int)resp.StatusCode}");
                    return;
                }
                int i = text.IndexOf("\"token\":\"", StringComparison.Ordinal);
                if (i < 0) { _accountView.SetStatus("bad response"); return; }
                i += 9;
                string token = text.Substring(i, text.IndexOf('"', i) - i);
                SaveToken(token);
                _accountView.SetStatus("linked — reconnecting…");
                Reconnect(token);
            }
            catch (Exception e)
            {
                _accountView.SetStatus($"failed: {e.Message}");
            }
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

            // Winding: a counter-clockwise triangle seen from outside the
            // planet is a front face and must NOT be flipped.
            var verts = new[] { new Vector3(0, 150, 0), new Vector3(1, 150, 0), new Vector3(0, 150, -1) };
            Check("outward CCW triangle is not inward", !TerrainMesh.FacesInward(verts, new[] { 0, 1, 2 }));
            Check("the same triangle reversed is inward", TerrainMesh.FacesInward(verts, new[] { 0, 2, 1 }));

            GD.Print(failed == 0 ? "OVERALL: PASS" : $"OVERALL: FAIL ({failed})");
            return failed == 0 ? 0 : 1;
        }
    }
}
