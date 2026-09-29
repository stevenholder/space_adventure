// What a shot looks like.
//
// Every visual here is driven by a SERVER event, never by the local trigger
// press. That is the whole design rule of this file.
//
// The server owns spread, so the direction a shot actually took is not the
// direction the client aimed. `shot_fired` carries the ray the server
// resolved — its rewound origin and its post-spread direction — precisely so
// every client can draw the same tracer as the one the hit markers agree with
// (PROTOCOL.md, "fire"). Drawing the local aim instead puts your tracer along
// a line the shot did not take, and that reads as broken hit registration.
//
// The one exception is the muzzle flash, which is a local trigger effect with
// no authority attached: it says "you pulled the trigger", not "a shot went
// there".
//
// Godot draws 3D lines one pixel wide, so a tracer is a thin box stretched
// between its endpoints: the honest equivalent of a 3 cm LineRenderer.

using System.Collections.Generic;
using Godot;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>Tracers, impact marks and muzzle flash, all on a timer.</summary>
    public sealed class CombatFx
    {
        private const double TracerSeconds = 0.06;
        private const double ImpactSeconds = 0.35;
        private const double FlashSeconds = 0.05;

        /// <summary>A live effect and when it dies.</summary>
        private struct Fx
        {
            public Node3D Node;
            public double DiesAt;
            public double BornAt;
            public bool ScaleOut;
        }

        private readonly List<Fx> _live = new List<Fx>();
        private readonly Node _parent;
        private readonly ArrayMesh _tracerMesh;
        private readonly SphereMesh _sphere = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4 };
        private readonly Dictionary<Color, StandardMaterial3D> _unlit = new Dictionary<Color, StandardMaterial3D>();

        private MeshInstance3D _flash;
        private double _flashDiesAt;

        public CombatFx(Node parent)
        {
            _parent = parent;
            // A unit box from the origin to −Z, so Basis.LookingAt points it
            // at the target and the Z scale is the length.
            _tracerMesh = BoxMesh.Build(new[] { new Box(new Vector3(0, 0, -0.5f), Vector3.One, new Color(1f, 0.75f, 0.25f)) }, "tracer");
        }

        /// <summary>
        /// Unlit and vertex-coloured, tinted: a tracer that responds to the
        /// sun is invisible on the night side of a planet, which is exactly
        /// where you need to see where you shot.
        /// </summary>
        private StandardMaterial3D Unlit(Color tint)
        {
            if (_unlit.TryGetValue(tint, out var m)) return m;
            m = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = tint,
                Transparency = tint.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            };
            _unlit[tint] = m;
            return m;
        }

        /// <summary>
        /// Draws the ray the SERVER resolved. Payload per PROTOCOL.md:
        /// f32 origin[3] | f32 dir[3] | f32 dist.
        ///
        /// `drawFrom` moves only where the line STARTS. The server resolves a
        /// shot from the shooter's eye, which is right for hit registration
        /// and wrong to draw: a line from your own eye is a dot in the middle
        /// of the screen. For your own shots the line starts at the barrel
        /// and ends exactly where the server said it ended.
        /// </summary>
        public void OnShotFired(EventMsg ev, Vector3? drawFrom = null)
        {
            if (ev.Data.Length < 28) return;
            var r = new WireReader(ev.Data);
            Vector3 origin = Frame.ToGodot(new Vec3(r.ReadF32(), r.ReadF32(), r.ReadF32()));
            Vector3 dir = Frame.ToGodot(new Vec3(r.ReadF32(), r.ReadF32(), r.ReadF32()));
            float dist = r.ReadF32();
            if (dir.LengthSquared() < 1e-8f) return;

            Vector3 end = origin + dir.Normalized() * dist;
            Vector3 start = drawFrom ?? origin;
            Vector3 along = end - start;
            float len = along.Length();
            if (len < 1e-4f) return;

            Vector3 up = Mathf.Abs(along.Normalized().Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
            // Scale in the tracer's OWN frame: Basis.Scaled() scales along the
            // world axes, which stretched every tracer along world Z whatever
            // the aim -- "the lasers fire in a fixed direction" (C100).
            Basis basis = Basis.LookingAt(along, up) * Basis.FromScale(new Vector3(0.03f, 0.03f, len));
            var tracer = new MeshInstance3D
            {
                Name = $"tracer-{ev.EntityId}",
                Mesh = _tracerMesh,
                MaterialOverride = Unlit(new Color(1f, 1f, 1f, 0.8f)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(basis, start),
            };
            _parent.AddChild(tracer);
            Add(tracer, TracerSeconds, scaleOut: false);
        }

        /// <summary>
        /// Marks where a shot landed. Payload: u32 shooter | f32 point[3] |
        /// u16 damage | u16 health_after.
        /// </summary>
        public void OnHit(EventMsg ev, uint selfId)
        {
            if (ev.Data.Length < 20) return;
            var r = new WireReader(ev.Data);
            uint shooter = r.ReadU32();
            Vector3 point = Frame.ToGodot(new Vec3(r.ReadF32(), r.ReadF32(), r.ReadF32()));

            // Your own hits read white; someone else's read orange, so a
            // crossfire is legible without a damage-number system.
            var impact = new MeshInstance3D
            {
                Name = "impact",
                Mesh = _sphere,
                MaterialOverride = Unlit(shooter == selfId ? new Color(1f, 1f, 1f, 0.9f) : new Color(1f, 0.5f, 0.2f, 0.9f)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = point,
                Scale = Vector3.One * 0.22f,
            };
            _parent.AddChild(impact);
            Add(impact, ImpactSeconds, scaleOut: true);
        }

        /// <summary>
        /// A brief flash at the barrel. Local, cosmetic, no authority. It is
        /// PARENTED to the muzzle rather than positioned each shot, so it
        /// inherits the rig's sway and bob and stays welded to the barrel.
        /// That also puts it on the viewmodel layer, where the overlay camera
        /// draws it over the gun.
        /// </summary>
        public void OnLocalFire(Node3D muzzle, uint layers)
        {
            if (muzzle == null) return;
            if (_flash == null)
            {
                _flash = new MeshInstance3D
                {
                    Name = "muzzle-flash",
                    Mesh = _sphere,
                    Scale = Vector3.One * 0.055f, // metres: the muzzle node is unscaled
                    MaterialOverride = Unlit(new Color(1f, 0.92f, 0.55f, 0.95f)),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Layers = layers,
                };
                muzzle.AddChild(_flash);
            }
            _flash.Position = Vector3.Zero;
            _flash.Visible = true;
            _flashDiesAt = Clock.Now + FlashSeconds;
        }

        private void Add(Node3D node, double seconds, bool scaleOut)
            => _live.Add(new Fx { Node = node, BornAt = Clock.Now, DiesAt = Clock.Now + seconds, ScaleOut = scaleOut });

        /// <summary>Ages every effect. Call once per frame.</summary>
        public void Tick()
        {
            double now = Clock.Now;
            if (_flash != null && _flash.Visible && now >= _flashDiesAt) _flash.Visible = false;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Fx fx = _live[i];
                if (now >= fx.DiesAt)
                {
                    fx.Node.QueueFree();
                    _live.RemoveAt(i);
                    continue;
                }
                if (fx.ScaleOut)
                {
                    float k = 1f - (float)((now - fx.BornAt) / (fx.DiesAt - fx.BornAt));
                    fx.Node.Scale = Vector3.One * (0.22f * Mathf.Max(k, 0.05f));
                }
            }
        }
    }
}
