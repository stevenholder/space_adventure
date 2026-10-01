// Per-frame input, fed from Boot._UnhandledInput and read during _Process.
//
// Godot dispatches input events before the frame's _Process callbacks, so by
// the time the frame loop runs, every key edge and every mouse motion of this
// frame has been fed here; EndFrame clears the edges once the loop has read
// them. Held state comes from Input directly. Physical keys throughout, so
// WASD is WASD on any layout, as the Unity Input System's keys were.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game
{
    public sealed class InputState
    {
        private readonly HashSet<Key> _pressedThisFrame = new HashSet<Key>();

        /// <summary>Pointer motion accumulated this frame (pixels; +Y is down).</summary>
        public Vector2 MouseDelta { get; private set; }

        public void Feed(InputEvent e)
        {
            switch (e)
            {
                case InputEventKey k when k.Pressed && !k.Echo:
                    _pressedThisFrame.Add(k.PhysicalKeycode);
                    break;
                case InputEventMouseMotion m:
                    MouseDelta += m.Relative;
                    break;
            }
        }

        /// <summary>Held right now.</summary>
        public bool Held(Key k) => Input.IsPhysicalKeyPressed(k);

        /// <summary>Went down this frame (the Input System's wasPressedThisFrame).</summary>
        public bool Pressed(Key k) => _pressedThisFrame.Contains(k);

        public bool LeftButtonHeld => Input.IsMouseButtonPressed(MouseButton.Left);
        public bool RightButtonHeld => Input.IsMouseButtonPressed(MouseButton.Right);

        public void EndFrame()
        {
            _pressedThisFrame.Clear();
            MouseDelta = Vector2.Zero;
        }
    }
}
