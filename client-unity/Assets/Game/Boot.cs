// U17 — the frame loop. Everything else is a subsystem; this is the file that
// makes them a game.
//
// It exists as its own task for a reason: Phase 2 and Phase 3 each shipped
// fully-built, fully-tested subsystems that nothing ever called, and both
// times it read as "the visuals are broken" rather than "nothing is wired up".
//
// Code-first, per client-unity/CONVENTIONS.md: no prefabs, no scene authoring.
// The whole hierarchy is built here at runtime, so the repo carries reviewable
// C# instead of GUID-keyed YAML (C47).

using System;
using System.Collections.Generic;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;
using UnityEngine;

namespace SpaceAdventure.Game
{
    public sealed class Boot : MonoBehaviour
    {
        /// <summary>
        /// Builds the world with no scene asset at all.
        ///
        /// The alternative is a committed .unity file, which is GUID-keyed
        /// YAML: unreviewable diffs and "verify" means opening the Editor.
        /// Pressing Play on any scene runs this. A packaged build still needs
        /// one scene listed in the build settings, and that is the only thing
        /// that scene has to contain.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Launch()
        {
            if (FindObjectOfType<Boot>() != null) return;
            var go = new GameObject("Boot");
            DontDestroyOnLoad(go);
            go.AddComponent<Boot>();
        }

        // ---- config ---------------------------------------------------------

        /// <summary>
        /// Server URL. C45 requires this to come from config rather than being
        /// compiled in, so an env var or -serverUrl on the command line wins
        /// over the default. The same-origin assumption died with browser
        /// delivery (ARCHITECTURE, "Client delivery").
        /// </summary>
        private static string ResolveServerUrl()
        {
            string[] argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == "-serverUrl") return argv[i + 1];
            }
            string env = Environment.GetEnvironmentVariable("SA_SERVER_URL");
            return string.IsNullOrEmpty(env) ? "ws://127.0.0.1:18080/ws" : env;
        }

        /// <summary>
        /// The identity token. Persistence is keyed on it (C11), so it has to
        /// survive a restart — PlayerPrefs is the smallest thing that does.
        /// </summary>
        private static string ResolveToken()
        {
            // -token <t> overrides (C45 config-over-compiled; the screenshot
            // rig uses it to join as a player whose saved position is where
            // the picture needs taking).
            string[] argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == "-token") return argv[i + 1];
            }
            const string key = "sa.token";
            string token = PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(token))
            {
                token = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(key, token);
                PlayerPrefs.Save();
            }
            return token;
        }

        // ---- state ----------------------------------------------------------

        private NetClient _net;
        private Predictor _predictor;
        private FpsController _fps;
        private EntityViews _views;
        private AssetRegistry _assets;
        private Structures _structures;
        private Rocks _rocks;
        private SnapshotTimeline _timeline;

        // Phase 4 — seat occupancy, from our own snapshot row (the mode byte
        // is a declaration; occupancy is what the server says). Seat 1 drives.
        private uint _seatVehicle;
        private ushort _seat;
        private float _noticeUntil;
        private readonly RoverPredictor _rover = new RoverPredictor();
        private readonly ShipPredictor _ship = new ShipPredictor();


        // Phase 8 — the UI Toolkit layer.
        private SpaceAdventure.Game.UI.UiRoot _ui;
        private SpaceAdventure.Game.UI.HudView _hudView;
        private SpaceAdventure.Game.UI.CombatFeed _combatFeed;
        private SpaceAdventure.Game.UI.ShopView _shopView;
        private SpaceAdventure.Game.UI.BagsView _bagsView;
        private SpaceAdventure.Game.UI.SheetView _sheetView;
        private SpaceAdventure.Game.UI.PromptView _promptView;

        // Phase 7 — the account link panel (F1): type a code minted on the
        // account site, redeem it for this account's game token, reconnect
        // as that player.
        private SpaceAdventure.Game.UI.AccountView _accountView;

        /// <summary>Screenshot rig (-uiApproach): inject forward+sprint.</summary>
        private bool _rigWalk;
        private bool _rigJump;
        private bool _rigFire;
        private float _detourSign = 1f;

        private bool ModalOpen =>
            (_shopView?.Open ?? false) || (_bagsView?.Open ?? false) ||
            (_sheetView?.Open ?? false) || (_accountView?.Open ?? false);

        // Mouse delta accumulated ACROSS the frames within one tick while
        // piloting — per-frame deltas consumed per-tick would drop most of
        // the motion (the drive-phase gotcha, avoided this time).
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

        // C46 frame-budget evidence: every 5 s, log the window's average fps
        // and worst frame to the player log, where `unity run` and the C46
        // measurement pass can read them. One compare and one add per frame.
        private float _statWindowStart;
        private int _statFrames;
        private float _statWorstDt;
        private Hud _hud;
        private CombatFx _fx;
        private ViewModel _viewModel;
        private MapView _map;
        private Character _character;
        private Interaction _interact;
        private Camera _camera;
        private bool _cursorFreed;

        private TerrainField _terrain;
        private Sim.Collider[] _colliders = Array.Empty<Sim.Collider>();
        private GameObject _planet;
        // One material for everything with geometry. Terrain, bodies, props
        // and the viewmodel all carry their colour in the vertex stream, so
        // they all want the same shader — and Standard, which ignores vertex
        // colour, would render every one of them white.
        private Material _material;

        /// <summary>
        /// weapon.pulse fire_interval, from the GDD weapon table. Hard-coded
        /// until `defs` is parsed for the equipped weapon's own rules — the
        /// server is the authority either way and simply drops anything early.
        /// </summary>
        private const float FireIntervalSeconds = 0.15f;

        private ushort _seq;

        /// <summary>
        /// cmd sequence, counted apart from the input seq. `cmd` correlates a
        /// request with its result; `input.seq` is what the shot rewind is
        /// computed from. Sharing one counter would make a shop purchase move
        /// where the next bullet lands.
        /// </summary>
        private ushort _cmdSeq;

        private float _nextFireAt;
        private float _tickAccumulator;
        private bool _worldBuilt;

        private void Start()
        {
            Application.runInBackground = true; // a windowed client that stops pumping gets dropped at 10 s

            // Phase 8: the UI Toolkit root. The spike's self-check stays until
            // every screen is ported (it logs the toolkit/text verdicts).
            _ui = new SpaceAdventure.Game.UI.UiRoot();
            _hudView = new SpaceAdventure.Game.UI.HudView(_ui.Root);
            _combatFeed = new SpaceAdventure.Game.UI.CombatFeed(_ui.Root);

            // -uiShot <path>: save a screenshot after the world settles, the
            // review artifact for C60 (test/out/ui/).
            string[] argvUi = Environment.GetCommandLineArgs();
            int shotAt = Array.IndexOf(argvUi, "-uiShot");
            if (shotAt >= 0 && shotAt + 1 < argvUi.Length)
                StartCoroutine(SaveUiShot(argvUi[shotAt + 1]));

            // No vsync, capped at 120. Vsync waits on whatever refresh the OS
            // reports, and a virtual or remote display can report ~4 Hz — the
            // C46 measurement found the player pinned at 6 fps by exactly
            // that, with the actual frame cost at 2 ms. The cap keeps an
            // uncapped loop (3000+ fps measured) from burning the GPU.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;

            _material = new Material(RequireShader("SpaceAdventure/TerrainVertexColor", "Standard"));

            // Still ONE material for everything, now also usable by
            // Graphics.RenderMeshInstanced -- which throws on any material
            // that has not opted in, however capable its shader is. Enabling
            // it costs nothing for the meshes drawn the ordinary way.
            _material.enableInstancing = true;

            int vmLayer = LayerMask.NameToLayer("ViewModel");
            if (vmLayer < 0) vmLayer = 8; // unnamed until the Editor runs EnsureLayers

            var camGo = new GameObject("Eye");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            // Far enough that the whole planet reads as a planet from any
            // altitude a flight reaches (C36) — the world is ~380 m across
            // and the scripted orbits sit within ~1500 m.
            cam.farClipPlane = 6000f;
            cam.fieldOfView = 60f; // ~90 degrees horizontal at 16:9, the FPS norm
            cam.cullingMask = ~(1 << vmLayer); // the world, minus the rig

            // The overlay pass. It clears depth and draws only the rig, so a
            // weapon held 0.4 m from the eye cannot intersect a wall the body
            // is pressed against. Its own narrower FOV is what keeps the
            // weapon from looking warped at the edge of a wide view.
            var vmCamGo = new GameObject("ViewModelCamera");
            vmCamGo.transform.SetParent(camGo.transform, false);
            var vmCam = vmCamGo.AddComponent<Camera>();
            vmCam.clearFlags = CameraClearFlags.Depth;
            vmCam.cullingMask = 1 << vmLayer;
            vmCam.nearClipPlane = 0.01f;
            vmCam.farClipPlane = 10f;
            vmCam.fieldOfView = 48f;
            vmCam.depth = cam.depth + 1;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.9f;
            sun.transform.rotation = Quaternion.Euler(35f, -140f, 0f);
            sun.cullingMask = ~(1 << vmLayer); // the rig has its own light

            // A light that follows the eye and reaches ONLY the rig. Ambient
            // is 0.10 now that the sky is space, so a weapon lit by the sun
            // alone is a black cutout whenever you face away from it — and
            // which way you happen to be facing is not a good reason to lose
            // sight of your own hands.
            var rigLight = new GameObject("RigLight").AddComponent<Light>();
            rigLight.transform.SetParent(camGo.transform, false);
            rigLight.type = LightType.Directional;
            rigLight.intensity = 1.05f;
            rigLight.cullingMask = 1 << vmLayer;
            rigLight.shadows = LightShadows.None;
            rigLight.transform.localRotation = Quaternion.Euler(28f, -32f, 0f);

            // The sky is installed once the world seed arrives with hello_ack,
            // so every client raises the same stars over the same planet.

            _camera = cam;
            _fps = new FpsController(cam);
            _predictor = new Predictor();
            _timeline = new SnapshotTimeline();
            _assets = new AssetRegistry(_material);
            _views = new EntityViews(transform, _material, _assets);
            _structures = new Structures(transform, _material, _assets);
            _rocks = new Rocks(_material, _assets);
            _viewModel = new ViewModel(cam, _material, vmLayer, transform, _assets);
            _viewModel.WeaponVisible = false; // until the server says we are holding one
            _hud = new Hud();
            _map = new MapView(_ui.Root);
            _character = new Character();
            _interact = new Interaction(_views, _character);
            _shopView = new SpaceAdventure.Game.UI.ShopView(_ui.Root, _character, _interact, NextCmdSeq, b => _net.Send(b));
            _bagsView = new SpaceAdventure.Game.UI.BagsView(_ui.Root, _character, NextCmdSeq, b => _net.Send(b));
            _sheetView = new SpaceAdventure.Game.UI.SheetView(_ui.Root, _character);
            _promptView = new SpaceAdventure.Game.UI.PromptView(_ui.Root);
            _accountView = new SpaceAdventure.Game.UI.AccountView(_ui.Root,
                code => { _accountView.SetStatus("redeeming…"); StartCoroutine(RedeemLinkCode(code)); },
                () => _accountView.Show(false));
            _fx = new CombatFx(transform);

            _net = new NetClient();
            _net.Connect(ResolveServerUrl(), SystemInfo.deviceName ?? "player", ResolveToken());

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>
        /// Finds the first shader that exists, and says something useful when
        /// none do. Shader.Find returns null in a PLAYER for any shader no
        /// asset references — every material here is built in C#, so the build
        /// cannot see the dependency and strips them. Editor/BuildTools.cs
        /// registers them as always-included; this turns a regression there
        /// into a legible message instead of an ArgumentNullException raised
        /// from inside Material's constructor.
        /// </summary>
        private static Shader RequireShader(params string[] names)
        {
            foreach (string name in names)
            {
                Shader s = Shader.Find(name);
                if (s != null) return s;
            }
            throw new InvalidOperationException(
                $"none of these shaders are in the build: {string.Join(", ", names)}. " +
                "Run 'Space Adventure/Ensure Shaders' (Editor/BuildTools.cs) and rebuild.");
        }

        private void Update()
        {
            DrainNetwork();

            if (!_worldBuilt) return;

            var keys = UnityEngine.InputSystem.Keyboard.current;

            // Escape releases the mouse so the Editor stays usable.
            if (keys?.escapeKey.wasPressedThisFrame == true)
            {
                // Esc is a deliberate release, so it latches: without the
                // latch the next frame's UI check would grab the pointer
                // straight back and the key would look broken.
                _cursorFreed = !_cursorFreed;
                Cursor.lockState = _cursorFreed ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = _cursorFreed;
            }
            if (keys?.f3Key.wasPressedThisFrame == true) _hud.DebugOpen = !_hud.DebugOpen;
            if (keys?.f1Key.wasPressedThisFrame == true)
            {
                _accountView.Show(!_accountView.Open);
                _accountView.SetStatus("");
            }
            if (keys?.mKey.wasPressedThisFrame == true) _map.Toggle();
            if (keys?.rKey.wasPressedThisFrame == true) _net.Send(Character.ReloadCmd(NextCmdSeq()));
            if (keys?.bKey.wasPressedThisFrame == true) OpenPanel(_bagsView, _sheetView);
            if (keys?.cKey.wasPressedThisFrame == true) OpenPanel(_sheetView, _bagsView);

            // The map takes the mouse. Movement keeps working underneath, so
            // you can read a bearing off it and walk without closing it.
            // The map and the shop both want the pointer. Movement keeps
            // working under either.
            bool wantsCursor = _map.Open || ModalOpen;
            if (wantsCursor && Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (!wantsCursor && !_cursorFreed && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            // The pilot's mouse steers the ship, never the view (GDD:
            // hull-fixed camera). Accumulate the raw delta for SendTick.
            bool piloting = Piloting;
            _fps.MouseLookEnabled = Cursor.lockState == CursorLockMode.Locked && !piloting;
            if (piloting && Cursor.lockState == CursorLockMode.Locked)
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m != null) _mouseAccum += m.delta.ReadValue();
            }

            var state = _predictor.State;
            // Seated, the local up comes from the rover — the body predictor
            // is reset and its position stale.
            Vec3 upPos = _seat != 0 && _rover.Ready ? _rover.State.Pos : state.Pos;
            LocalInput li = _fps.Sample(upPos.Normalized(), state.Facing);
            if (_rigWalk)
            {
                li.MoveY = 1;
                li.ActionMask |= Net.Action.Sprint;
                if (_rigJump) li.ActionMask |= Net.Action.Jump;
            }
            if (_rigFire) li.FirePressed = true;

            // Fixed 20 Hz input, matching the server's tick. Sending at frame
            // rate would put several inputs in one tick, and the server keeps
            // only the last: the rest of the movement silently never happens.
            _tickAccumulator += Time.deltaTime;
            while (_tickAccumulator >= (float)Rules.DT)
            {
                _tickAccumulator -= (float)Rules.DT;
                SendTick(li);
            }

            // Look-at targeting, then E. The shop swallows E so closing it
            // does not immediately reopen it on the same key press.
            if (_seat == 0)
            {
                Vector3 eye = TerrainMesh.ToUnity(state.Pos);
                eye += eye.normalized * FpsController.EyeHeight;
                _interact.Update(eye, TerrainMesh.ToUnity(li.Look));
            }
            if (_seat != 0) _interact.Notice = SeatKind == EntityType.Ship ? "E  ·  exit ship" : "E  ·  exit rover";
            else if (Time.time > _noticeUntil) _interact.Notice = "";
            if (li.InteractPressed && !_map.Open && !(_bagsView.Open || _sheetView.Open))
            {
                if (_seat != 0)
                {
                    // Always available, at any speed (GDD; C32).
                    _net.Send(Encode.Disembark());
                }
                else if (_interact.ShopOpen) { _interact.CloseShop(); _shopView.Show(false); }
                else if ((_interact.TargetType == EntityType.Vehicle ||
                          _interact.TargetType == EntityType.Ship) && _interact.Target != 0)
                {
                    // Ask for the control seat; on "occupied" the seat_result
                    // handler walks down the passenger seats.
                    _net.Send(Encode.Board(_interact.Target, 1));
                }
                else
                {
                    byte[] cmd = _interact.OpenShop(NextCmdSeq());
                    if (cmd != null)
                    {
                        _net.Send(cmd);
                        _net.Send(Interaction.InventoryCmd(NextCmdSeq()));
                    }
                }
            }

            // The rig follows the character sheet, which is the one place
            // that knows what is equipped — whether it learned from the
            // broadcast event or from an accepted equip.
            _viewModel.WeaponVisible = _seat == 0 && !string.IsNullOrEmpty(_character.Primary);

            _timeline.OneWaySeconds = _net.RttMs > 0 ? _net.RttMs / 2000.0 : 0.0;
            _views.Render(_timeline, _net.EntityId);
            // Instanced, so this is a submit rather than a scene walk: three
            // draw calls for four hundred rocks and no GameObjects to cull.
            _rocks.Render();
            if (_seat != 0) PlaceSeatCamera();
            else _fps.PlaceCamera(_predictor.State.Pos);

            // The body stands where the simulation puts it, and the rig sways
            // against the real speed rather than the input.
            State now = _predictor.State;
            _viewModel.Place(TerrainMesh.ToUnity(now.Pos),
                             TerrainMesh.ToUnity(now.Pos.Normalized()),
                             TerrainMesh.ToUnity(now.Facing));
            _viewModel.Tick(_fps.LookDelta, (float)now.Vel.Length, Time.deltaTime);
            _fx.Tick();

            UpdateHudView();
            _combatFeed?.Tick(_camera);

            _statFrames++;
            if (Time.unscaledDeltaTime > _statWorstDt) _statWorstDt = Time.unscaledDeltaTime;
            if (Time.unscaledTime - _statWindowStart >= 5f)
            {
                // The first window is skipped: it contains scene build and
                // model loads, which are startup cost, not frame budget.
                if (_statWindowStart > 0f)
                    Debug.Log($"framestats: {_statFrames / (Time.unscaledTime - _statWindowStart):F1} fps avg, worst frame {_statWorstDt * 1000f:F1} ms, {_views.Count} entities");
                _statWindowStart = Time.unscaledTime;
                _statFrames = 0;
                _statWorstDt = 0f;
            }
        }

        private ushort NextCmdSeq() => ++_cmdSeq;

        /// <summary>
        /// Feeds the Phase 8 HUD from the same sources the IMGUI one reads,
        /// plus compass markers by egocentric bearing (Bearing.To).
        /// </summary>
        private void UpdateHudView()
        {
            if (_hudView == null) return;
            _hudView.SetVitals(_hud.Health, 100);
            _hudView.SetAmmo(_character.Magazine, _character.Reserve,
                _character.Magazine >= 0 && !string.IsNullOrEmpty(_character.Primary));
            _hudView.SetCredits(_character.Credits);

            var me = _predictor.State;
            var markers = new List<(string, double)>();
            foreach (var v in _views.All)
            {
                if (v.Root == null || !v.Root.activeSelf) continue;
                string name = v.Type switch
                {
                    EntityType.Npc when v.Label == "npc.quartermaster" => "SHOP",
                    EntityType.Vehicle => "ROVER",
                    EntityType.Ship => "SHIP",
                    _ => null,
                };
                if (name == null) continue;
                Vector3 p = v.Root.transform.position;
                var target = new Vec3(p.x, p.y, -p.z); // Unity → sim
                markers.Add((name, Bearing.To(me.Pos, me.Facing, target)));
            }
            _hudView.SetMarkers(markers);

            // The shop view mirrors Interaction's state: stock arriving opens
            // it, CloseShop (or a despawned NPC) closes it.
            if (_interact.ShopOpen && !_shopView.Open) _shopView.Show(true);
            if (!_interact.ShopOpen && _shopView.Open) _shopView.Show(false);

            _promptView.Set(!string.IsNullOrEmpty(_interact.Notice) ? _interact.Notice : _interact.Prompt);

            UpdateFlightReadout();

            _hudView.SetLog(_hud.Lines);
            _hudView.SetDebug(_hud.DebugText(_net, _predictor, _character));
            _hudView.UpdateHealthBars(_camera, _views, !_map.Open);

            State ms = _predictor.State;
            _map.Draw(_terrain,
                      TerrainMesh.ToUnity(ms.Pos),
                      TerrainMesh.ToUnity(ms.Facing),
                      MapMarkers());
        }

        /// <summary>
        /// Camera at the seat eye point (GDD "Rover seats" seat_eye, sim
        /// frame → Unity local is (x, y, −z)). The driver's rover draws at
        /// the PREDICTED state — steering a vehicle that lags your own wheel
        /// by a round trip is the exact bug prediction exists to kill; a
        /// passenger rides the interpolated view like any remote entity.
        /// Rotation stays the FpsController's: the camera never changes mode
        /// (GDD), free look in every seat.
        /// </summary>
        private void PlaceSeatCamera()
        {
            bool ship = SeatKind == EntityType.Ship;
            Vector3 pos;
            Quaternion rot;
            bool haveView = _views.TryGet(_seatVehicle, out var vv) && vv.Root != null;
            if (_seat == 1 && (ship ? _ship.Ready : _rover.Ready))
            {
                // The control seat sees the PREDICTED vehicle; steering one
                // that lags your own input by a round trip is the bug
                // prediction exists to kill.
                Vec3 p = ship ? _ship.State.Pos : _rover.State.Pos;
                Quat q = ship ? _ship.State.Quat : _rover.State.Quat;
                pos = TerrainMesh.ToUnity(p);
                rot = RotFrom(q);
                if (haveView) vv.Root.transform.SetPositionAndRotation(pos, rot);
            }
            else if (haveView)
            {
                pos = vv.Root.transform.position;
                rot = vv.Root.transform.rotation;
            }
            else return; // no vehicle row seen yet; keep last camera pose

            var table = ship ? ShipSeatEye : RoverSeatEye;
            int seat = _seat < table.Length ? _seat : 1;
            Vec3 eye = table[seat];
            _camera.transform.position = pos + rot * new Vector3((float)eye.X, (float)eye.Y, (float)-eye.Z);

            // The pilot's camera is hull-fixed: orientation IS the ship's
            // attitude, the mouse steers the ship, not the view (GDD
            // "Camera and rig"). Passengers keep the free look the
            // FpsController already applied this frame.
            if (ship && _seat == 1) _camera.transform.rotation = rot;
        }

        /// <summary>
        /// The flight readout (ROADMAP task 12): speed, altitude above the
        /// terrain under the ship, regime, role. IMGUI like everything else
        /// — no assets, C47 holds.
        /// </summary>
        private void UpdateFlightReadout()
        {
            if (SeatKind != EntityType.Ship) { _hudView.SetFlight(null); return; }
            bool pilot = Piloting;
            ShipSimState s = _ship.Ready ? _ship.State : default;
            Vec3 p;
            Vec3 v;
            bool space;
            if (pilot && _ship.Ready)
            {
                p = s.Pos; v = s.Vel; space = s.Space;
            }
            else if (_views.TryGet(_seatVehicle, out var vv) && vv.Root != null)
            {
                Vector3 up0 = vv.Root.transform.position;
                p = new Vec3(up0.x, up0.y, -up0.z);
                v = Vec3.Zero;
                space = p.Length >= FlightRules.SpaceRadius;
            }
            else { _hudView.SetFlight(null); return; }

            Vec3 dir = p.Normalized();
            double alt = p.Length - _terrain.SampleRadius(dir);
            _hudView.SetFlight(pilot
                ? $"{v.Length,6:F1} m/s   alt {alt,5:F0} m   {(space ? "SPACE" : "ATMO ")}   PILOT"
                : $"alt {alt,5:F0} m   {(space ? "SPACE" : "ATMO ")}   PASSENGER");
        }

        /// <summary>
        /// POST /api/redeem on the server the game is already talking to
        /// (ws→http on the same origin), store the token, reconnect.
        /// </summary>
        private System.Collections.IEnumerator RedeemLinkCode(string code)
        {
            string wsUrl = ResolveServerUrl();
            string apiUrl = wsUrl.Replace("wss://", "https://").Replace("ws://", "http://");
            int slash = apiUrl.LastIndexOf("/ws", StringComparison.Ordinal);
            if (slash >= 0) apiUrl = apiUrl.Substring(0, slash);
            apiUrl += "/api/redeem";

            byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"code\":\"" + code + "\"}");
            using var req = new UnityEngine.Networking.UnityWebRequest(apiUrl, "POST");
            req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body);
            req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("X-Requested-With", "sa-client");
            yield return req.SendWebRequest();

            if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                _accountView.SetStatus(req.responseCode == 401
                    ? "unknown or expired code"
                    : $"failed: {req.error}");
                yield break;
            }
            string text = req.downloadHandler.text;
            int i = text.IndexOf("\"token\":\"", StringComparison.Ordinal);
            if (i < 0) { _accountView.SetStatus("bad response"); yield break; }
            i += 9;
            string token = text.Substring(i, text.IndexOf('"', i) - i);

            PlayerPrefs.SetString("sa.token", token);
            PlayerPrefs.Save();
            _accountView.SetStatus("linked — reconnecting…");
            Reconnect(token);
        }

        /// <summary>
        /// Tears down the connection and rejoins with a new token. The world
        /// itself stands (same terrain, same entities — their views are keyed
        /// by id and simply update); everything derived from OUR identity
        /// resets: predictors, the timeline, seat state.
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
            _net.Connect(ResolveServerUrl(), SystemInfo.deviceName ?? "player", token);
        }

        /// <summary>
        /// Feeds the combat feed from a hit event (same wire layout
        /// Combat.OnHit reads: shooter u32 | pos f32[3] | damage u16 |
        /// health_after u16). Numbers for every hit; the marker only for
        /// YOURS; the incoming arc only when the victim is you.
        /// </summary>
        private void OnHitFeedback(EventMsg ev)
        {
            if (_combatFeed == null || ev.Data.Length < 20) return;
            var r = new WireReader(ev.Data);
            uint shooter = r.ReadU32();
            var simPoint = new Vec3(r.ReadF32(), r.ReadF32(), r.ReadF32());
            int damage = r.ReadU16();
            int healthAfter = r.ReadU16();
            Vector3 point = TerrainMesh.ToUnity(simPoint);

            _combatFeed.Damage(point, damage, healthAfter == 0);
            if (shooter == _net.EntityId) _combatFeed.HitMarker(healthAfter == 0);
            if (ev.EntityId == _net.EntityId && _views.TryGet(shooter, out var sv) && sv.Root != null)
            {
                Vector3 sp = sv.Root.transform.position;
                var target = new Vec3(sp.x, sp.y, -sp.z);
                var me = _predictor.State;
                _combatFeed.Incoming(Bearing.To(me.Pos, me.Facing, target));
            }
        }

        /// <summary>Saves the C60 review screenshot once the scene settles.</summary>
        private System.Collections.IEnumerator SaveUiShot(string path)
        {
            yield return new WaitForSeconds(8f);
            // -uiPanel bags|sheet: open that panel first, so the C60 gallery
            // can capture the modals without simulated key presses. Shop is
            // excluded — it only opens off a live NPC interaction.
            string[] argv = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(argv, "-uiPanel");
            if (at >= 0 && at + 1 < argv.Length)
            {
                if (argv[at + 1] == "bags") OpenPanel(_bagsView, _sheetView);
                if (argv[at + 1] == "sheet") OpenPanel(_sheetView, _bagsView);
                if (argv[at + 1] == "map") _map.Toggle();
                if (argv[at + 1] == "account") _accountView.Show(true);
                yield return new WaitForSeconds(1f); // refresh round trip
            }
            // -uiDemo: stage the combat-feedback showcase right here — two
            // local grunt bodies on the terrain ahead (same Create path as a
            // live spawn, so the same health-bar rules apply) and the same
            // CombatFeed calls the live hit event drives. The live path is
            // separately proven by the event log; this exists so the LOOK is
            // photographable on demand, near spawn, in daylight.
            if (Array.IndexOf(argv, "-uiDemo") >= 0)
            {
                Vector3 eyeD = _camera.transform.position;
                Vector3 upD = eyeD.normalized;
                Vector3 fwdD = Vector3.ProjectOnPlane(_camera.transform.forward, upD).normalized;

                EntityView PlaceGrunt(uint id, float dist, float sideDeg, ushort health)
                {
                    Vector3 dir = Quaternion.AngleAxis(sideDeg, upD) * fwdD;
                    Vector3 spot = eyeD + dir * dist;
                    Vector3 sd = spot.normalized;
                    var simDir = new Vec3(sd.x, sd.y, -sd.z);
                    float r = (float)_terrain.SampleRadius(simDir);
                    Vector3 pos = sd * r;
                    var v = _views.SpawnLocalDemo(id, EntityType.Npc, "npc.grunt", pos, -dir);
                    v.MaxHealth = 60;
                    v.Health = health;
                    return v;
                }

                var g1 = PlaceGrunt(0x000F0001, 9f, -8f, 38);
                var g2 = PlaceGrunt(0x000F0002, 13f, 14f, 12);
                yield return new WaitForSeconds(0.4f); // let the models land
                _fps.FaceToward(_camera.transform.position,
                    (g1.Root.transform.position + g2.Root.transform.position) * 0.5f + upD * 1.2f);
                yield return new WaitForSeconds(0.15f);

                Vector3 up1 = g1.Root.transform.position.normalized;
                Vector3 up2 = g2.Root.transform.position.normalized;
                _combatFeed.Damage(g2.Root.transform.position + up2 * 1.5f, 47, true);
                _combatFeed.Incoming(2.6);
                yield return new WaitForSeconds(0.25f);
                _combatFeed.Damage(g1.Root.transform.position + up1 * 1.55f, 20, false);
                _combatFeed.HitMarker(false);
                yield return new WaitForSeconds(0.12f);
            }

            // -uiFace target|npc|wounded: aim the camera at the nearest such
            // entity, so a combat-feedback shot has something in frame.
            int faceAt = Array.IndexOf(argv, "-uiFace");
            if (faceAt >= 0 && faceAt + 1 < argv.Length)
            {
                string wantArg = argv[faceAt + 1];
                ushort want = wantArg == "npc" || wantArg == "hostile" ? EntityType.Npc
                    : wantArg == "player" ? EntityType.Player
                    : EntityType.Target;
                bool Match (EntityView v, string arg, ushort type) => arg switch
                {
                    "wounded" => v.ShowHealthBar,
                    "hostile" => v.Type == EntityType.Npc && v.Label != "npc.quartermaster",
                    _ => v.Type == type,
                };
                Vector3 eye = _camera.transform.position;
                EntityView best = null;
                float bestD = float.MaxValue;
                // "wounded" can flicker out (a kill respawns the target at
                // full health), so poll for one instead of sampling once.
                for (float waited = 0; best == null && waited < 12f; waited += 0.25f)
                {
                    foreach (EntityView v in _views.All)
                    {
                        if (v.Root == null || v.Id == _net.EntityId) continue;
                        if (!Match(v, wantArg, want)) continue;
                        float d = (v.Root.transform.position - eye).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = v; }
                    }
                    if (best == null) yield return new WaitForSeconds(0.25f);
                }
                if (best != null)
                {
                    // The goal is a FIXED copy: a kill-and-respawn cycle can
                    // move or recycle the live Root mid-walk, and a walk
                    // toward a ghost climbs the wrong hill.
                    Vector3 goal = best.Root.transform.position;
                    _fps.FaceToward(eye, goal);
                    Debug.Log($"ui: facing {wantArg} {best.Id} at {Mathf.Sqrt(bestD):F0} m");

                    // -uiApproach <m>: walk toward the faced spot until within
                    // that many metres (the planet's horizon from eye height
                    // is ~23 m, so anything worth photographing has to be
                    // closed to arm's reach first).
                    int appAt = Array.IndexOf(argv, "-uiApproach");
                    if (appAt >= 0 && appAt + 1 < argv.Length &&
                        float.TryParse(argv[appAt + 1], out float closeTo))
                    {
                        float deadline = Time.time + 150f;
                        _rigWalk = true;
                        Vector3 lastEye = _camera.transform.position;
                        float lastMoveAt = Time.time;
                        float detourUntil = 0f;
                        while (Time.time < deadline)
                        {
                            Vector3 eyeNow = _camera.transform.position;
                            Vector3 to = goal - eyeNow;
                            if (to.magnitude <= closeTo) break;
                            // Wedged (the quartermaster stands on this exact
                            // bearing)? Sidestep: aim 40 degrees off a moment.
                            if ((eyeNow - lastEye).magnitude > 0.3f) { lastEye = eyeNow; lastMoveAt = Time.time; }
                            else if (Time.time - lastMoveAt > 0.8f && Time.time > detourUntil)
                            {
                                detourUntil = Time.time + 3f;
                                lastMoveAt = Time.time;
                                _detourSign = -_detourSign; // alternate sides around obstacles
                            }
                            // Jump at whatever we are wedged on — a scarp
                            // under max_slope yields to it (the t29 walker's
                            // trick, ported).
                            _rigJump = Time.time - lastMoveAt > 0.6f || Time.time < detourUntil;
                            Vector3 aimPoint = goal;
                            if (Time.time < detourUntil)
                            {
                                Vector3 up = eyeNow.normalized;
                                aimPoint = eyeNow + Quaternion.AngleAxis(_detourSign * 40f, up) * to;
                            }
                            _fps.FaceToward(eyeNow, aimPoint);
                            yield return new WaitForSeconds(0.2f);
                        }
                        _rigWalk = false;
                        _rigJump = false;
                        Debug.Log($"ui: approached to {(goal - _camera.transform.position).magnitude:F0} m");

                        // Re-pick at arrival: the thing wounded NOW may be a
                        // different entity than the one that led us here —
                        // and -uiReface <kind> can retarget entirely (walk to
                        // the gunner, then photograph what it is shooting).
                        int refAt = Array.IndexOf(argv, "-uiReface");
                        string refaceArg = refAt >= 0 && refAt + 1 < argv.Length ? argv[refAt + 1] : wantArg;
                        ushort refaceWant = refaceArg == "npc" ? EntityType.Npc
                            : refaceArg == "player" ? EntityType.Player
                            : EntityType.Target;
                        EntityView again = null;
                        float againScore = float.MaxValue;
                        for (float waited = 0; again == null && waited < 12f; waited += 0.25f)
                        {
                            foreach (EntityView v in _views.All)
                            {
                                if (v.Root == null || v.Id == _net.EntityId) continue;
                                if (!Match(v, refaceArg, refaceWant)) continue;
                                // Hostiles rank by REMAINING HEALTH: a burst
                                // aimed at a 12 hp leftover ends before the
                                // camera fires; a full grunt keeps popping.
                                float d = refaceArg == "hostile"
                                    ? -v.Health
                                    : (v.Root.transform.position - _camera.transform.position).sqrMagnitude;
                                if (d < againScore) { againScore = d; again = v; }
                            }
                            if (again == null) yield return new WaitForSeconds(0.25f);
                        }
                        if (again != null && again.Root != null)
                        {
                            _fps.FaceToward(_camera.transform.position, again.Root.transform.position);
                            Debug.Log($"ui: refaced {again.Id} health {again.Health}");

                            // -uiFire <secs>: reload, then hold the trigger on
                            // the refaced entity. Our own shots put damage
                            // numbers, hit markers and a draining health bar
                            // on screen — the C61 picture, from our own gun.
                            int fireAt2 = Array.IndexOf(argv, "-uiFire");
                            if (fireAt2 >= 0 && fireAt2 + 1 < argv.Length &&
                                float.TryParse(argv[fireAt2 + 1], out float fireSecs))
                            {
                                _net.Send(Character.ReloadCmd(NextCmdSeq()));
                                yield return new WaitForSeconds(0.6f);
                                _rigFire = true;
                                // Burn the burst RETARGETING the healthiest
                                // living hostile — one grunt dies in three
                                // hits and a burst aimed at a corpse takes a
                                // photo of nothing. Falls through to capture
                                // still firing.
                                float stopAt = Time.time + fireSecs;
                                while (Time.time < stopAt)
                                {
                                    EntityView tgt = null;
                                    foreach (EntityView v in _views.All)
                                    {
                                        if (v.Root == null || v.Id == _net.EntityId) continue;
                                        if (!Match(v, refaceArg, refaceWant)) continue;
                                        if (tgt == null || v.Health > tgt.Health) tgt = v;
                                    }
                                    if (tgt != null)
                                    {
                                        Vector3 up2 = _camera.transform.position.normalized;
                                        _fps.FaceToward(_camera.transform.position,
                                            tgt.Root.transform.position + up2 * 0.9f);
                                    }
                                    yield return new WaitForSeconds(0.1f);
                                }
                            }
                        }
                    }
                }
                if (!_rigFire) yield return new WaitForSeconds(1.5f); // let a popup land
            }
            yield return new WaitForEndOfFrame();
            var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Destroy(tex);
            Debug.Log($"ui: screenshot saved to {path}");
        }

        private static Quaternion RotFrom(Quat q)
        {
            Vector3 fwd = TerrainMesh.ToUnity(Quat.Rotate(q, new Vec3(0, 0, 1)));
            Vector3 up = TerrainMesh.ToUnity(Quat.Rotate(q, new Vec3(0, 1, 0)));
            if (fwd.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f) return Quaternion.identity;
            return Quaternion.LookRotation(fwd, up);
        }

        /// <summary>
        /// Toggles a panel and, if it just opened, asks the server for fresh
        /// credits and inventory. Panels show server truth rather than
        /// whatever was last seen — a bag that still lists a rifle you sold on
        /// another client is worse than a bag that takes a round trip.
        /// </summary>
        private void OpenPanel(SpaceAdventure.Game.UI.ModalView view, SpaceAdventure.Game.UI.ModalView other)
        {
            bool open = !view.Open;
            view.Show(open);
            if (open)
            {
                other.Show(false);
                _net.Send(Character.RefreshCmd(NextCmdSeq()));
            }
        }

        private void SendTick(LocalInput li)
        {
            _seq++;

            if (Piloting)
            {
                // Pilot: mode 1, v = [thrust, roll, yaw_rate, pitch_rate, 0]
                // (GDD flight input map). Mouse deltas were accumulated per
                // frame; convert to rad/s over the tick. Signs: rightward
                // drag → negative yaw (left turn positive, RH rule about
                // +Y); upward drag → negative pitch (nose up — the
                // corrected convention).
                double yaw = -_mouseAccum.x / Rules.DT * KRate;
                double pitch = -_mouseAccum.y / Rules.DT * KRate; // Unity +y = up = nose up = negative
                _mouseAccum = Vector2.zero;
                var inp = new FlightInput
                {
                    Thrust = li.MoveY,
                    Roll = -li.MoveX, // A (strafe −1) = roll left = +1
                    YawRate = yaw,
                    PitchRate = pitch,
                    Boost = (li.ActionMask & SpaceAdventure.Net.Action.Sprint) != 0,
                };
                _ship.Apply(_seq, inp);
                _net.Send(Encode.Input(
                    (float)inp.Thrust, (float)inp.Roll,
                    (float)inp.YawRate, (float)inp.PitchRate, 0,
                    inp.Boost ? SpaceAdventure.Net.Action.Boost : (ushort)0, _seq, 1));
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
                // Passenger: movement is ignored server-side (the body is
                // composed from the vehicle), but look still flows for
                // pitch_q. Send it zeroed so nothing depends on the mercy.
                _net.Send(Encode.Input(0, 0,
                    (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z, 0, _seq));
                return;
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

            // Nothing happens without a weapon. The server drops a `fire`
            // from an unarmed player outright, so sending one is a wasted
            // round trip — and showing the muzzle flash anyway is a lie: the
            // effect claims a shot the server never resolved. The armed state
            // is the server's own, from the `equipped` event, so this cannot
            // drift into refusing to let an armed player shoot.
            //
            // An empty magazine is the same bug one level down and is not
            // covered: the client does not track ammunition yet, so the flash
            // still fires on a dry weapon. `reload` is a routed stub
            // server-side, so there is nothing to track against until that
            // lands.
            //
            // Cadence is enforced server-side and an early shot is DROPPED,
            // not queued, so a client that fires every tick just loses most of
            // them. Hold to the weapon's own interval; the server allows one
            // tick of tolerance, which covers the rounding.
            if (li.FirePressed && _viewModel.WeaponVisible && Time.time >= _nextFireAt)
            {
                _nextFireAt = Time.time + FireIntervalSeconds;
                // The shot names the input that was in effect when the trigger
                // went down. The server rewinds by how far back that input
                // executed, so sending anything else moves where the shot
                // lands (PROTOCOL "fire"; GDD "Lag compensation").
                _net.Send(Encode.Fire(_seq, (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z));
                _fx.OnLocalFire(_viewModel.Muzzle);
            }
        }

        private void DrainNetwork()
        {
            while (_net != null && _net.Poll(out Frame frame))
            {
                try
                {
                    HandleFrame(frame);
                }
                catch (WireException e)
                {
                    Debug.LogError($"malformed {frame.Type:x4}: {e.Message}");
                }
            }
        }

        private void HandleFrame(Frame frame)
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
                    // you walk into is what you can see. Until now only the
                    // predictor got it, and the camp perimeter and range walls
                    // were solid and invisible.
                    _structures.Build(_colliders);
                    Debug.Log($"colliders: {_colliders.Length}");
                    break;
                }
                case Msg.Props:
                    // Visual only, and deliberately not routed anywhere near
                    // the predictor: props have no collision.
                    _structures.BuildProps(Decode.Props(frame.Reader));
                    break;
                case Msg.Defs:
                {
                    // Item names, kinds and equipment slots -- the shop needs
                    // the slot to know what can be equipped at all -- and the
                    // `asset` id behind every entity, which is what tells
                    // EntityViews which model in art/manifest.json to load.
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
                    _timeline.Add(snap, Time.time);
                    foreach (var row in snap.Entities)
                    {
                        if (row.Id == _net.EntityId)
                        {
                            // Occupancy is whatever our row says. On a seat
                            // change, drop the stale predictor: the on-foot
                            // one on boarding, the rover one on leaving.
                            if (row.ParentId != _seatVehicle || row.Seat != _seat)
                            {
                                _seatVehicle = row.ParentId;
                                _seat = row.Seat;
                                _rover.Reset();
                                _ship.Reset();
                                _mouseAccum = Vector2.zero;
                                if (_seat != 0) _predictor.Reset();
                            }
                            if (_seat == 0)
                            {
                                _predictor.Reconcile(
                                    new Vec3(row.PosX, row.PosY, row.PosZ),
                                    new Vec3(row.VelX, row.VelY, row.VelZ),
                                    FacingFrom(row),
                                    row.Grounded,
                                    snap.AckSeq);
                            }
                            _hud.Health = row.Health;
                            _character.Health = row.Health;
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
                            // else's from the origin the server reported, which
                            // is where their body actually is.
                            _fx.OnShotFired(ev, ev.EntityId == _net.EntityId
                                ? _viewModel.Muzzle.position
                                : (Vector3?)null);
                            break;
                        case EventId.Hit:
                            _fx.OnHit(ev, _net.EntityId);
                            OnHitFeedback(ev);
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
                        _noticeUntil = Time.time + 2f;
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
                    if (r.Ok && r.Opcode == Op.Equip) _character.OnEquipAccepted();
                    if (r.Ok && r.Opcode == Op.Reload) _character.OnReload(r.Body);
                    byte[] followUp = _interact.OnCmdResult(r, NextCmdSeq);
                    if (followUp != null) _net.Send(followUp);
                    if (_shopView.Open) _shopView.Rebuild();
                    if (_bagsView.Open) _bagsView.Rebuild();
                    if (_sheetView.Open) _sheetView.Rebuild();
                    break;
                }
            }
        }

        /// <summary>
        /// The local body's facing, in sim space, for reconciliation. Shares
        /// EntityViews' decoder so the two cannot drift apart.
        /// </summary>
        private static Vec3 FacingFrom(EntityRow row) => SnapshotTimeline.FacingOf(row);

        private void BuildWorld()
        {
            // The sky is scenery. If generating it fails on some device, say
            // so and carry on with the default one rather than losing the
            // planet, the body and the connection along with it.
            try
            {
                Sky.Install(_net.WorldSeed, RequireShader("Skybox/Cubemap"));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"skybox unavailable, using the default: {e.Message}");
            }
            if (_planet != null) Destroy(_planet);
            _planet = TerrainMesh.Build(_terrain, _material, transform);
            _predictor.Seed(_terrain, _colliders);
            _rover.Seed(_terrain);
            _ship.Seed(_terrain);

            // Scatter is pure in (terrain, world_seed), so it can only run
            // once the terrain has landed -- which is here, and not earlier.
            _rocks.Build(_terrain, _net.WorldSeed);
            _worldBuilt = true;

            // A packaged player has no HUD anyone is watching when it is run
            // headless in CI (C45 is "joins the deployed server from a cold
            // start"), so say so in the log. Once per world build, not per
            // frame.
            // No collider count here on purpose: `colliders` arrives AFTER
            // `terrain` (PROTOCOL's join order), so any number printed at this
            // point is zero and reads as a bug that is not one. The colliders
            // log themselves when they land.
            Debug.Log($"world ready: entity={_net.EntityId} seed={_net.WorldSeed} " +
                      $"tickHz={_net.TickHz} spawn={_predictor.State.Pos.Length:F1} m from centre");
        }



        /// <summary>
        /// Everything on the map: the live entities, plus the spawn point.
        ///
        /// Spawn earns a fixed marker because the map is heading-UP, so it has
        /// no compass rose to orient by. One landmark that never moves is what
        /// turns "things near me" into "where am I".
        /// </summary>
        private IEnumerable<MapMarker> MapMarkers()
        {
            foreach (MapMarker m in _views.Markers()) yield return m;

            Vector3 spawn = TerrainMesh.ToUnity(
                Step.SpawnDir.Normalized() * _terrain.SampleRadius(Step.SpawnDir.Normalized()));
            yield return new MapMarker(spawn, EntityType.Ship, "Spawn");
        }

        private void OnDestroy()
        {
            _net?.Dispose();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
