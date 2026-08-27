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
        private SnapshotTimeline _timeline;
        private Hud _hud;

        private TerrainField _terrain;
        private Sim.Collider[] _colliders = Array.Empty<Sim.Collider>();
        private GameObject _planet;
        private Material _material;

        private ushort _seq;
        private float _tickAccumulator;
        private bool _worldBuilt;
        private readonly List<EventMsg> _events = new List<EventMsg>();

        private void Start()
        {
            Application.runInBackground = true; // a windowed client that stops pumping gets dropped at 10 s

            _material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));

            var camGo = new GameObject("Eye");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 2000f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(35f, -140f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.32f, 0.38f);

            _fps = new FpsController(cam);
            _predictor = new Predictor();
            _timeline = new SnapshotTimeline();
            _views = new EntityViews(transform, _material);
            _hud = new Hud();

            _net = new NetClient();
            _net.Connect(ResolveServerUrl(), SystemInfo.deviceName ?? "player", ResolveToken());

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            DrainNetwork();

            if (!_worldBuilt) return;

            // Escape releases the mouse so the Editor stays usable.
            if (UnityEngine.InputSystem.Keyboard.current?.escapeKey.wasPressedThisFrame == true)
            {
                Cursor.lockState = Cursor.lockState == CursorLockMode.Locked
                    ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = Cursor.lockState != CursorLockMode.Locked;
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

            _timeline.OneWaySeconds = _net.RttMs > 0 ? _net.RttMs / 2000.0 : 0.0;
            _views.Render(_timeline, _net.EntityId);
            _fps.PlaceCamera(_predictor.State.Pos);
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

            if (li.FirePressed)
            {
                // The shot names the input that was in effect when the trigger
                // went down. The server rewinds by how far back that input
                // executed, so sending anything else moves where the shot
                // lands (PROTOCOL "fire"; GDD "Lag compensation").
                _net.Send(Encode.Fire(_seq, (float)li.Look.X, (float)li.Look.Y, (float)li.Look.Z));
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
                    }
                    break;
                }
                case Msg.Event:
                {
                    EventMsg ev = Decode.Event(frame.Reader);
                    _events.Add(ev);
                    if (ev.EventId == EventId.Equipped)
                    {
                        _views.OnEquipped(ev.EntityId, WireReader.Utf8.GetString(ev.Data));
                    }
                    _hud.OnEvent(ev, _net.EntityId);
                    break;
                }
                case Msg.CmdResult:
                    _hud.OnCmdResult(Decode.CmdResult(frame.Reader));
                    break;
            }
        }

        private static Vec3 FacingFrom(EntityRow row)
        {
            var q = new Quaternion(row.QuatX, row.QuatY, row.QuatZ, row.QuatW);
            if (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0) return new Vec3(0, 0, 1);
            Vector3 f = q * Vector3.forward;
            return TerrainMesh.ToSim(f);
        }

        private void BuildWorld()
        {
            if (_planet != null) Destroy(_planet);
            _planet = TerrainMesh.Build(_terrain, _material, transform);
            _predictor.Seed(_terrain, _colliders);
            _worldBuilt = true;
        }

        private void OnGUI() => _hud?.Draw(_net, _predictor, _fps);

        private void OnDestroy()
        {
            _net?.Dispose();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
