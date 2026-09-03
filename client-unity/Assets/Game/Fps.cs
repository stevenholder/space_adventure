// U14 — first-person input and camera, on a planet.
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

using SpaceAdventure.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

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
    /// Reads the Input System and maintains the eye direction. Owns the
    /// camera; owns no simulation state.
    /// </summary>
    public sealed class FpsController
    {
        /// <summary>Eye height above the foot position (GDD on-foot table).</summary>
        public const float EyeHeight = 1.7f;

        private const float PitchLimitDegrees = 89f;

        private readonly Camera _camera;

        /// <summary>
        /// The body's heading: a world-space unit vector, tangent to the
        /// surface, owned entirely by this class.
        ///
        /// It is NOT re-derived from the simulation each frame, and that is
        /// the whole point. The look vector this class produces is what drives
        /// the sim's facing, so seeding the heading from that facing and then
        /// applying an accumulated yaw to it feeds the yaw back into itself —
        /// the camera then spins at a constant rate forever after the first
        /// mouse movement, with no input at all. It did.
        /// </summary>
        private Vector3 _heading;
        private bool _headingSet;

        private float _pitchDegrees;

        /// <summary>Degrees per unit of pointer delta.</summary>
        public float Sensitivity { get; set; } = 0.12f;

        public bool MouseLookEnabled { get; set; } = true;

        /// <summary>Pointer movement this frame, for the viewmodel's sway.</summary>
        public Vector2 LookDelta { get; private set; }

        public FpsController(Camera camera)
        {
            _camera = camera;
        }

        /// <summary>Pitch in radians, for the HUD and for `pitch_q` parity checks.</summary>
        public float PitchRadians => _pitchDegrees * Mathf.Deg2Rad;

        /// <summary>
        /// Points the view at a world position (the -uiFace screenshot rig;
        /// headless has no mouse). Sets the owned heading and pitch the same
        /// way mouse look would have.
        /// </summary>
        public void FaceToward(Vector3 eye, Vector3 worldPoint)
        {
            Vector3 up = eye.normalized;
            Vector3 to = worldPoint - eye;
            float rise = Vector3.Dot(to, up);
            // The RAW horizontal component, not Tangent() — that helper
            // normalises, and atan2(rise, 1) pinned the pitch at the clamp
            // (a camera staring at its own feet, and a look direction whose
            // tangent projection walked the player anywhere but forward).
            Vector3 horizontal = to - up * rise;
            if (horizontal.sqrMagnitude < 1e-8f) return;
            _heading = horizontal.normalized;
            _headingSet = true;
            _pitchDegrees = Mathf.Clamp(
                -Mathf.Atan2(rise, horizontal.magnitude) * Mathf.Rad2Deg,
                -PitchLimitDegrees, PitchLimitDegrees);
        }

        /// <summary>
        /// Samples input and rebuilds the look direction against `up`.
        /// </summary>
        public LocalInput Sample(Vec3 simUp, Vec3 simFacing)
        {
            var result = new LocalInput { MoveX = 0, MoveY = 0, ActionMask = 0 };

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            LookDelta = Vector2.zero;

            Vector3 up = TerrainMesh.ToUnity(simUp).normalized;

            // First frame only: adopt the body's facing so the view starts
            // where the server says the body is pointing.
            if (!_headingSet)
            {
                _heading = Tangent(TerrainMesh.ToUnity(simFacing), up);
                _headingSet = true;
            }

            // Walking moves `up`, so the heading has to be re-seated into the
            // new tangent plane each frame. Projecting the PREVIOUS heading is
            // parallel transport: it preserves the direction the player chose
            // instead of recomputing one, which is what keeps the horizon
            // still while you walk over a hill.
            _heading = Tangent(_heading, up);

            if (MouseLookEnabled && mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                LookDelta = delta;
                // Yaw is applied as a DELTA to the heading we own. Applying an
                // accumulated angle to a facing read back from the sim is the
                // feedback loop described on _heading.
                _heading = Tangent(Quaternion.AngleAxis(delta.x * Sensitivity, up) * _heading, up);
                _pitchDegrees = Mathf.Clamp(_pitchDegrees - delta.y * Sensitivity,
                                            -PitchLimitDegrees, PitchLimitDegrees);
            }

            // Pitch about the body's right. Pitch is absolute and clamped, so
            // it is rebuilt from the angle rather than accumulated onto look.
            Vector3 right = Vector3.Cross(up, _heading).normalized;
            Vector3 look = Quaternion.AngleAxis(_pitchDegrees, right) * _heading;

            if (kb != null)
            {
                float x = 0, y = 0;
                if (kb.aKey.isPressed) x -= 1;
                if (kb.dKey.isPressed) x += 1;
                if (kb.sKey.isPressed) y -= 1;
                if (kb.wKey.isPressed) y += 1;
                result.MoveX = x;
                result.MoveY = y;

                if (kb.leftShiftKey.isPressed) result.ActionMask |= Net.Action.Sprint;
                if (kb.spaceKey.isPressed) result.ActionMask |= Net.Action.Jump;
                result.InteractPressed = kb.eKey.wasPressedThisFrame;
            }
            if (mouse != null) result.FirePressed = mouse.leftButton.isPressed;

            // The wire carries the LOOK direction, not the yaw: the server
            // rebuilds the movement frame from it, and a client that sent an
            // angle would have to agree about which axis it was measured from.
            result.Look = TerrainMesh.ToSim(look.normalized);

            if (_camera != null)
            {
                _camera.transform.rotation = Quaternion.LookRotation(look, up);
            }
            return result;
        }

        /// <summary>
        /// Projects v into the tangent plane at `up` and normalises it, with a
        /// fallback for the degenerate case where v is parallel to up.
        /// </summary>
        private static Vector3 Tangent(Vector3 v, Vector3 up)
        {
            Vector3 t = Vector3.ProjectOnPlane(v, up);
            if (t.sqrMagnitude < 1e-8f)
            {
                t = Vector3.ProjectOnPlane(Vector3.forward, up);
                if (t.sqrMagnitude < 1e-8f) t = Vector3.ProjectOnPlane(Vector3.right, up);
            }
            return t.normalized;
        }

        /// <summary>Places the camera at the eye of a body standing at `simPos`.</summary>
        public void PlaceCamera(Vec3 simPos)
        {
            if (_camera == null) return;
            Vector3 pos = TerrainMesh.ToUnity(simPos);
            Vector3 up = pos.normalized;
            _camera.transform.position = pos + up * EyeHeight;
        }
    }
}
