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
            cam.farClipPlane = 2000f;
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
            _map = new MapView();
            _character = new Character();
            _interact = new Interaction(_views, _character);
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
            if (keys?.mKey.wasPressedThisFrame == true) _map.Toggle();
            if (keys?.rKey.wasPressedThisFrame == true) _net.Send(Character.ReloadCmd(NextCmdSeq()));
            if (keys?.bKey.wasPressedThisFrame == true) OpenPanel(_character.ToggleBags);
            if (keys?.cKey.wasPressedThisFrame == true) OpenPanel(_character.ToggleSheet);

            // The map takes the mouse. Movement keeps working underneath, so
            // you can read a bearing off it and walk without closing it.
            // The map and the shop both want the pointer. Movement keeps
            // working under either.
            bool wantsCursor = _map.Open || _interact.ShopOpen || _character.AnyOpen;
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
            _fps.MouseLookEnabled = Cursor.lockState == CursorLockMode.Locked;

            var state = _predictor.State;
            LocalInput li = _fps.Sample(state.Pos.Normalized(), state.Facing);

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
            Vector3 eye = TerrainMesh.ToUnity(state.Pos);
            eye += eye.normalized * FpsController.EyeHeight;
            _interact.Update(eye, TerrainMesh.ToUnity(li.Look));
            if (li.InteractPressed && !_map.Open && !_character.AnyOpen)
            {
                if (_interact.ShopOpen) _interact.CloseShop();
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
            _viewModel.WeaponVisible = !string.IsNullOrEmpty(_character.Primary);

            _timeline.OneWaySeconds = _net.RttMs > 0 ? _net.RttMs / 2000.0 : 0.0;
            _views.Render(_timeline, _net.EntityId);
            // Instanced, so this is a submit rather than a scene walk: three
            // draw calls for four hundred rocks and no GameObjects to cull.
            _rocks.Render();
            _fps.PlaceCamera(_predictor.State.Pos);

            // The body stands where the simulation puts it, and the rig sways
            // against the real speed rather than the input.
            State now = _predictor.State;
            _viewModel.Place(TerrainMesh.ToUnity(now.Pos),
                             TerrainMesh.ToUnity(now.Pos.Normalized()),
                             TerrainMesh.ToUnity(now.Facing));
            _viewModel.Tick(_fps.LookDelta, (float)now.Vel.Length, Time.deltaTime);
            _fx.Tick();
        }

        private ushort NextCmdSeq() => ++_cmdSeq;

        /// <summary>
        /// Toggles a panel and, if it just opened, asks the server for fresh
        /// credits and inventory. Panels show server truth rather than
        /// whatever was last seen — a bag that still lists a rifle you sold on
        /// another client is worse than a bag that takes a round trip.
        /// </summary>
        private void OpenPanel(System.Action toggle)
        {
            toggle();
            if (_character.AnyOpen) _net.Send(Character.RefreshCmd(NextCmdSeq()));
        }

        private void SendTick(LocalInput li)
        {
            _seq++;
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
                        if (row.Id != _net.EntityId) continue;
                        _predictor.Reconcile(
                            new Vec3(row.PosX, row.PosY, row.PosZ),
                            new Vec3(row.VelX, row.VelY, row.VelZ),
                            FacingFrom(row),
                            row.Grounded,
                            snap.AckSeq);
                        _hud.Health = row.Health;
                        _character.Health = row.Health;
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
                        case EventId.Hit: _fx.OnHit(ev, _net.EntityId); break;
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
                case Msg.CmdResult:
                {
                    CmdResult r = Decode.CmdResult(frame.Reader);
                    _hud.OnCmdResult(r);
                    if (r.Ok && (r.Opcode == Op.Inventory || r.Opcode == Op.ShopBuy)) _character.OnWallet(r.Body);
                    if (r.Ok && r.Opcode == Op.Equip) _character.OnEquipAccepted();
                    if (r.Ok && r.Opcode == Op.Reload) _character.OnReload(r.Body);
                    byte[] followUp = _interact.OnCmdResult(r, NextCmdSeq);
                    if (followUp != null) _net.Send(followUp);
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

        private void OnGUI()
        {
            if (_worldBuilt && !_map.Open) _hud.DrawHealthBars(_camera, _views);
            _hud?.Draw(_net, _predictor, _character);
            if (_map == null || !_worldBuilt) return;

            if (!_map.Open)
            {
                byte[] cmd = _character.AnyOpen
                    ? _character.Draw(NextCmdSeq)
                    : _interact.Draw(NextCmdSeq);
                if (cmd != null) _net.Send(cmd);
            }

            State s = _predictor.State;
            _map.Draw(_terrain,
                      TerrainMesh.ToUnity(s.Pos),
                      TerrainMesh.ToUnity(s.Facing),
                      MapMarkers());
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
