// The UI root, built ENTIRELY at runtime: a CanvasLayer over the 3D view
// carrying one full-rect Control that every screen parents to. No theme
// resource, no scene — C91's no-assets rule applied to the interface.
//
// The root ignores the mouse so it never eats a click meant for a panel;
// panels that want the pointer get it, everything HUD-shaped ignores it.

using Godot;

namespace SpaceAdventure.Game.UI
{
    public sealed class UiRoot
    {
        public Control Root { get; }

        public UiRoot(Node parent)
        {
            var layer = new CanvasLayer { Name = "UI", Layer = 100 };
            parent.AddChild(layer);
            Root = new Control { Name = "root", MouseFilter = Control.MouseFilterEnum.Ignore };
            Root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(Root);
            GD.Print($"ui: attached ({(Styles.Display != null ? "display font" : "fallback font")})");
        }

        /// <summary>The viewport size in pixels, for screen-space placement.</summary>
        public Vector2 Size => Root.Size;
    }
}
