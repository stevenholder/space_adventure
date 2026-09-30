// First-person input and camera, on a planet.
//
// Three things make this different from a flat-world FPS controller:
//
// 1. "Up" is a different direction at every point on the sphere, so the
//    heading is re-seated into the local tangent plane every frame rather than
//    accumulated into a world rotation — accumulate and the horizon rolls as
//    you walk, which reads as the planet tilting.
// 2. The heading is OURS. It is never read back from the simulation, because
//    the look vector produced here is what sets the sim's facing: seeding from
//    that facing and re-applying an accumulated yaw feeds the yaw into itself
//    and the view spins forever off a single mouse movement. See _heading.
// 3. Movement is not applied here. This only produces the wish direction and
//    the look vector; Sim.Step owns what happens next, because the server runs
//    that exact code and any second opinion here is a desync.
//
// Signs, derived once (client/CONVENTIONS.md §3): the movement frame's right
// is facing × up. Rotating a vector about `up` by +θ (right-hand rule) moves
// it toward up × facing, which is LEFT — so mouse-right applies a negative
// yaw. Pitch is positive = looking up, rotating about that same right vector.
// SelfCheck below pins both, headless.

using System;
using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>What the player asked for this frame, ready for the wire.</summary>
    public struct LocalInput
    {
        public double MoveX, MoveY;
        public Vec3 Look;
        public int ActionMask;
        public bool FirePressed;
        public bool InteractPressed;
    }

    /// <summary>
    /// Reads input and maintains the eye direction. Owns the camera's
    /// orientation; owns no simulation state.
    /// </summary>
    public sealed class FpsController
    {
        /// <summary>Eye height above the foot position (GDD on-foot table).</summary>
        public const float EyeHeight = 1.7f;

        private const float PitchLimit = 89f * Mathf.Pi / 180f;

        private readonly Camera3D _camera;
        private readonly InputState _input;

        /// <summary>
        /// The body's heading: a world-space unit vector, tangent to the
        /// surface, owned entirely by this class and never re-derived from
        /// the simulation (see the header).
        /// </summary>
        private Vector3 _heading;
        private bool _headingSet;

        /// <summary>Radians, positive = looking up.</summary>
        private float _pitch;

        /// <summary>Radians per pixel of pointer delta (0.12°/px, as before).</summary>
        public const float BaseSensitivity = 0.12f * Mathf.Pi / 180f;
        public float Sensitivity { get; set; } = BaseSensitivity;

        public bool MouseLookEnabled { get; set; } = true;

        /// <summary>Pointer movement this frame, for the viewmodel's sway.</summary>
        public Vector2 LookDelta { get; private set; }

        public FpsController(Camera3D camera, InputState input)
        {
            _camera = camera;
            _input = input;
        }

        /// <summary>Pitch in radians, for the HUD and for `pitch_q` parity checks.</summary>
        public float PitchRadians => _pitch;

        /// <summary>
        /// Points the view at a world position (the -uiFace screenshot rig;
        /// headless has no mouse). Sets the owned heading and pitch the same
        /// way mouse look would have.
        /// </summary>
        public void FaceToward(Vector3 eye, Vector3 worldPoint)
        {
            Vector3 up = eye.Normalized();
            Vector3 to = worldPoint - eye;
            float rise = to.Dot(up);
            // The RAW horizontal component, not Tangent() — that helper
            // normalises, and atan2(rise, 1) pinned the pitch at the clamp.
            Vector3 horizontal = to - up * rise;
            if (horizontal.LengthSquared() < 1e-8f) return;
            _heading = horizontal.Normalized();
            _headingSet = true;
            _pitch = Mathf.Clamp(Mathf.Atan2(rise, horizontal.Length()), -PitchLimit, PitchLimit);
        }

        /// <summary>
        /// Samples input and rebuilds the look direction against `up`.
        /// </summary>
        public LocalInput Sample(Vec3 simUp, Vec3 simFacing)
        {
            Vector3 up = Frame.ToGodot(simUp).Normalized();
            if (!_headingSet)
            {
                // First frame only: adopt the body's facing so the view starts
                // where the server says the body is pointing.
                _heading = Tangent(Frame.ToGodot(simFacing), up);
                _headingSet = true;
            }

            Vector2 delta = MouseLookEnabled ? _input.MouseDelta : Vector2.Zero;
            LookDelta = delta;

            var result = new LocalInput();
            result.Look = Frame.ToSim(Look(up, delta));

            if (_input.Held(Key.A)) result.MoveX -= 1;
            if (_input.Held(Key.D)) result.MoveX += 1;
            if (_input.Held(Key.S)) result.MoveY -= 1;
            if (_input.Held(Key.W)) result.MoveY += 1;
            if (_input.Held(Key.Shift)) result.ActionMask |= Net.Action.Sprint;
            if (_input.Held(Key.Space)) result.ActionMask |= Net.Action.Jump;
            // F interacts (Phase 13 keybinds); E is a hotbar key.
            result.InteractPressed = _input.Pressed(Key.F);
            // A click with the cursor free is a UI click (a journal button, a
            // shop row), never a shot -- claiming a mission used to fire the
            // rifle. Captured pointer = the world has the mouse.
            result.FirePressed = _input.LeftButtonHeld && Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Captured;
            return result;
        }

        /// <summary>
        /// Applies a pointer delta to the owned heading and pitch, rebuilds
        /// the look vector and orients the camera. Split from Sample so the
        /// sign rules can be checked without a Godot Input singleton.
        /// </summary>
        private Vector3 Look(Vector3 up, Vector2 delta)
        {
            // Walking moves `up`, so the heading has to be re-seated into the
            // new tangent plane each frame. Projecting the PREVIOUS heading is
            // parallel transport: it preserves the direction the player chose
            // instead of recomputing one, which is what keeps the horizon
            // still while you walk over a hill.
            _heading = Tangent(_heading, up);

            // Yaw as a DELTA to the heading we own. Negative: +θ about `up`
            // turns toward up × facing, the movement frame's LEFT.
            if (delta.X != 0f) _heading = Tangent(_heading.Rotated(up, -delta.X * Sensitivity), up);
            // Pointer +Y is down; looking up is positive pitch.
            _pitch = Mathf.Clamp(_pitch - delta.Y * Sensitivity, -PitchLimit, PitchLimit);

            // Pitch about the body's right (facing × up). Absolute and
            // clamped, so it is rebuilt from the angle rather than accumulated.
            Vector3 right = _heading.Cross(up).Normalized();
            Vector3 look = _heading.Rotated(right, _pitch).Normalized();

            // Camera3D looks down its own −Z; LookingAt is for the camera only.
            if (_camera != null)
                _camera.GlobalBasis = Basis.LookingAt(look, up);
            return look;
        }

        /// <summary>
        /// Projects v into the tangent plane at `up` and normalises it, with a
        /// fallback for the degenerate case where v is parallel to up.
        /// </summary>
        private static Vector3 Tangent(Vector3 v, Vector3 up)
        {
            Vector3 t = v.Slide(up);
            if (t.LengthSquared() < 1e-8f)
            {
                t = Vector3.Forward.Slide(up);
                if (t.LengthSquared() < 1e-8f) t = Vector3.Right.Slide(up);
            }
            return t.Normalized();
        }

        /// <summary>Places the camera at the eye of a body standing at `simPos`.</summary>
        public void PlaceCamera(Vec3 simPos)
        {
            if (_camera == null) return;
            Vector3 pos = Frame.ToGodot(simPos);
            _camera.GlobalPosition = pos + pos.Normalized() * EyeHeight;
        }

        /// <summary>
        /// Pins the sign rules: mouse-right turns toward the movement frame's
        /// right (facing × up), mouse-up raises the look vector. Runs under
        /// `-selftest`, no camera and no Input singleton involved.
        /// </summary>
        public static int SelfCheck(Action<string, bool> check)
        {
            var f = new FpsController(null, null);
            Vector3 up = new Vector3(0, 1, 0);
            Vec3 facing = new Vec3(0, 0, -1);
            f._heading = Tangent(Frame.ToGodot(facing), up);
            f._headingSet = true;
            Vector3 right = f._heading.Cross(up); // the Sim's movement right

            Vector3 before = f._heading;
            f.Look(up, new Vector2(100f, 0f));              // mouse right
            check("mouse right turns toward facing × up", f._heading.Dot(right) > 0.1f && f._heading.Dot(before) > 0.9f);

            f._heading = before; f._pitch = 0f;
            Vector3 look = f.Look(up, new Vector2(0f, -100f)); // mouse up (pointer +Y is down)
            check("mouse up looks up", look.Dot(up) > 0.1f);

            f._heading = before; f._pitch = 0f;
            f.Look(up, new Vector2(0f, -100000f));
            check("pitch clamps below the pole", f.Look(up, Vector2.Zero).Dot(up) < 0.9999f);

            // Parallel transport: a heading re-seated onto a tilted up keeps
            // its direction rather than snapping to a world axis.
            f._heading = before; f._pitch = 0f;
            Vector3 tilted = new Vector3(0.3f, 1f, 0f).Normalized();
            f.Look(tilted, Vector2.Zero);
            check("heading survives a change of up", f._heading.Dot(before) > 0.95f && Mathf.Abs(f._heading.Dot(tilted)) < 1e-5f);
            return 0;
        }
    }
}
