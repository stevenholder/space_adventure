// U14 — first-person input and camera, on a planet.
//
// Two things make this different from a flat-world FPS controller:
//
// 1. "Up" is a different direction at every point on the sphere. Pitch and yaw
//    are therefore kept as ANGLES and rebuilt against the local up each frame,
//    not accumulated into a world rotation — accumulate and the horizon rolls
//    as you walk, which reads as the planet tilting.
// 2. Movement is not applied here. This only produces the wish direction and
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

        private float _yawDegrees;
        private float _pitchDegrees;

        /// <summary>Degrees per unit of pointer delta.</summary>
        public float Sensitivity { get; set; } = 0.12f;

        public bool MouseLookEnabled { get; set; } = true;

        public FpsController(Camera camera)
        {
            _camera = camera;
        }

        /// <summary>Pitch in radians, for the HUD and for `pitch_q` parity checks.</summary>
        public float PitchRadians => _pitchDegrees * Mathf.Deg2Rad;

        /// <summary>
        /// Samples input and rebuilds the look direction against `up`.
        /// </summary>
        public LocalInput Sample(Vec3 simUp, Vec3 simFacing)
        {
            var result = new LocalInput { MoveX = 0, MoveY = 0, ActionMask = 0 };

            var kb = Keyboard.current;
            var mouse = Mouse.current;

            if (MouseLookEnabled && mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _yawDegrees += delta.x * Sensitivity;
                _pitchDegrees = Mathf.Clamp(_pitchDegrees - delta.y * Sensitivity,
                                            -PitchLimitDegrees, PitchLimitDegrees);
            }

            Vector3 up = TerrainMesh.ToUnity(simUp).normalized;

            // Rebuild the basis from the local up every frame. `simFacing` is
            // the body's own tangent facing from the sim, which keeps the
            // camera consistent with the body the server is simulating even
            // after a reconcile snaps it.
            Vector3 seed = TerrainMesh.ToUnity(simFacing);
            Vector3 forward = Vector3.ProjectOnPlane(seed, up);
            if (forward.sqrMagnitude < 1e-8f)
            {
                forward = Vector3.ProjectOnPlane(Vector3.forward, up);
                if (forward.sqrMagnitude < 1e-8f) forward = Vector3.ProjectOnPlane(Vector3.right, up);
            }
            forward.Normalize();

            // Yaw about the local up, then pitch about the body's right.
            Vector3 yawed = Quaternion.AngleAxis(_yawDegrees, up) * forward;
            Vector3 right = Vector3.Cross(up, yawed).normalized;
            Vector3 look = Quaternion.AngleAxis(_pitchDegrees, right) * yawed;

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
