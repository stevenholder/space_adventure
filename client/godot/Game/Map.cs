// The map (M), on a planet.
//
// PROJECTION: azimuthal equidistant, centred on the player, rotated so the
// way you are facing is up, and cropped to a square MapExtent metres on each
// side of you.
//
// The projection is not a stylistic pick. A flat lat/long map of a sphere puts
// a seam somewhere and tears the poles apart, and the player SPAWNS on the +Y
// pole, which is the worst place for a projection to be undefined. Centred on
// the viewer there is no seam anywhere near them, and both things a "where is
// it" map exists for stay exact rather than approximate:
//
//   - the straight-line direction from the centre to a marker is its true
//     bearing from where you stand, so you can walk it off the screen;
//   - the distance from the centre is the true distance ALONG THE SURFACE,
//     so a ring at 100 m means a hundred metres of walking.
//
// The CROP: showing the whole planet is possible — it is only 942 m around —
// and useless. Everything a player cares about sits within a couple of hundred
// metres; a 300 m window puts the landmarks far enough apart to read.
//
// Terrain shading is sampled from the same TerrainField the sim uses, so the
// map cannot disagree with the ground about where a hill is.

using System.Collections.Generic;
using Godot;
using SpaceAdventure.Game.UI;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class MapView
    {
        /// <summary>Texels across the terrain image. 256 is ~65k samples to rebuild.</summary>
        private const int Resolution = 256;

        /// <summary>Rebuild the terrain image after the player moves this far.</summary>
        private const float RebuildAfterMetres = 4f;

        /// <summary>
        /// Half-width of the window, in metres of surface distance. On a
        /// planet this small 300 m is 2 radians of arc, so the far corners are
        /// genuinely stretched; inside 100 m the distortion is not visible.
        /// </summary>
        private const float MapExtent = 300f;

        private ImageTexture _terrainTexture;
        private Image _terrainImage;

        private Vector3 _builtAt = new Vector3(float.PositiveInfinity, 0, 0);
        private Vector3 _builtFacing;

        /// <summary>Label rectangles already used this frame, to keep them apart.</summary>
        private readonly List<Rect2> _labelled = new List<Rect2>();

        // The panel and the image are built once; the marker layer is cleared
        // and refilled per frame while the map is open. ponytail: per-frame
        // rebuild allocates; pool the dots if the profiler ever names this.
        private readonly PanelContainer _panel;
        private readonly Panel _frame;
        private readonly TextureRect _image;
        private readonly Control _markerLayer;
        private readonly Control _root;

        public MapView(Control root)
        {
            _root = root;
            _panel = Styles.Panel(Styles.SkewNone);
            Styles.PinAt(_panel, 0.5f, 0.06f);
            _panel.Visible = false;
            VBoxContainer body = Styles.Body(_panel);
            VBoxContainer header = Styles.Header($"Map — facing up, {MapExtent:F0} m in every direction");
            body.AddChild(header);
            UI.PanelMemory.Grip(_panel, root, "map", header);

            _frame = Styles.Box(Colors.Transparent, Styles.Ink, 2);
            _image = new TextureRect
            {
                StretchMode = TextureRect.StretchModeEnum.Scale,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _image.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _markerLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            _markerLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _frame.AddChild(_image);
            _frame.AddChild(_markerLayer);
            body.AddChild(_frame);

            body.AddChild(Styles.Gap(6));
            body.AddChild(Styles.Display_(
                "M closes · rings every 100 m · white spawn · red hostile · yellow target · green loot · blue player",
                11, Styles.Dust));
            root.AddChild(_panel);
        }

        public bool Open => _panel.Visible;

        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) UI.PanelMemory.Restore(_panel, _root, "map", ref _restored);
        }
        private bool _restored;
        public void Close() => _panel.Visible = false;

        /// <summary>Updates the map. Called every frame; does nothing while closed.</summary>
        public void Draw(TerrainField terrain, Vector3 playerPos, Vector3 playerFacing,
                         IEnumerable<MapMarker> markers)
        {
            if (!Open || terrain == null) return;

            Vector2 screen = _root.Size;
            float size = Mathf.Min(screen.X, screen.Y) * 0.62f;
            _frame.CustomMinimumSize = new Vector2(size, size);
            var rect = new Rect2(0, 0, size, size);

            Vector3 up = playerPos.Normalized();
            Vector3 fwd = playerFacing.Slide(up);
            if (fwd.LengthSquared() < 1e-8f) fwd = Vector3.Forward.Slide(up);
            fwd = fwd.Normalized();
            // Screen-right for a heading-up map seen from above: facing × up.
            Vector3 right = fwd.Cross(up);

            // The planet's radius here is the player's own distance from the
            // centre. Using the constant would put the scale slightly wrong on
            // a hill, and every distance on the map with it.
            float radius = playerPos.Length();
            float pixelsPerMetre = size * 0.5f / MapExtent;

            if ((playerPos - _builtAt).LengthSquared() > RebuildAfterMetres * RebuildAfterMetres ||
                fwd.Dot(_builtFacing) < 0.995f || _terrainTexture == null)
            {
                BuildTerrainImage(terrain, up, fwd, right, radius);
                _builtAt = playerPos;
                _builtFacing = fwd;
            }
            _image.Texture = _terrainTexture;

            Vector2 centre = rect.GetCenter();
            foreach (Node child in _markerLayer.GetChildren()) { _markerLayer.RemoveChild(child); child.QueueFree(); }
            DrawRangeRings(centre, pixelsPerMetre, size);

            _labelled.Clear();
            foreach (MapMarker m in markers)
            {
                if (!Project(m.Pos, up, fwd, right, radius, out Vector2 offset)) continue;
                Vector2 at = centre + new Vector2(offset.X, -offset.Y) * pixelsPerMetre;
                if (!rect.HasPoint(at)) continue;

                float r = m.Type == EntityType.Player ? 5f : 4f;
                Dot(at.X - r, at.Y - r, r * 2, r * 2, ColourFor(m.Type));

                if (!string.IsNullOrEmpty(m.Label)) PlaceLabel(at, m.Label);
            }

            // You, at the centre, pointing up the screen by construction.
            var you = new Color(0.95f, 0.95f, 1f);
            Dot(centre.X - 2, centre.Y - 9, 4, 14, you);
            Dot(centre.X - 5, centre.Y - 2, 10, 4, you);
        }

        private void Dot(float x, float y, float w, float h, Color c)
        {
            _markerLayer.AddChild(new ColorRect
            {
                Position = new Vector2(x, y), Size = new Vector2(w, h), Color = c,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }

        /// <summary>
        /// Draws a marker's label, nudging it down past anything already
        /// placed and dropping it if there is nowhere clear. Landmarks
        /// cluster; losing one label is better than losing both.
        /// </summary>
        private void PlaceLabel(Vector2 at, string text)
        {
            var candidate = new Rect2(at.X + 7, at.Y - 9, 190, 18);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                bool clear = true;
                foreach (Rect2 taken in _labelled)
                {
                    if (taken.Intersects(candidate)) { clear = false; break; }
                }
                if (clear)
                {
                    _labelled.Add(candidate);
                    var l = Styles.Display_(text, 12, Styles.Cream);
                    l.Position = candidate.Position;
                    _markerLayer.AddChild(l);
                    return;
                }
                candidate.Position += new Vector2(0, 15);
            }
        }

        private void DrawRangeRings(Vector2 centre, float pixelsPerMetre, float size)
        {
            // A square with full corner radius and a border is a true ring.
            for (int metres = 100; metres <= (int)MapExtent; metres += 100)
            {
                float r = metres * pixelsPerMetre;
                if (r > size * 0.5f) break;
                Panel ring = Styles.Box(Colors.Transparent, new Color(1f, 1f, 1f, 0.13f), 1, r);
                ring.Position = new Vector2(centre.X - r, centre.Y - r);
                ring.Size = new Vector2(r * 2, r * 2);
                _markerLayer.AddChild(ring);
            }
        }

        /// <summary>
        /// World position to map offset in METRES along the surface, or false
        /// when the point is degenerate.
        /// </summary>
        private static bool Project(Vector3 world, Vector3 up, Vector3 fwd, Vector3 right,
                                    float radius, out Vector2 offset)
        {
            offset = Vector2.Zero;
            Vector3 dir = world.Normalized();
            if (dir.LengthSquared() < 1e-8f) return false;

            float cos = Mathf.Clamp(up.Dot(dir), -1f, 1f);
            float arc = Mathf.Acos(cos) * radius; // surface distance, not chord

            Vector3 tangent = dir - up * cos;
            if (tangent.LengthSquared() < 1e-10f)
            {
                offset = Vector2.Zero; // directly underfoot, or the exact antipode
                return true;
            }
            tangent = tangent.Normalized();
            offset = new Vector2(tangent.Dot(right), tangent.Dot(fwd)) * arc;
            return true;
        }

        /// <summary>
        /// Paints the terrain by inverting the projection for every texel: map
        /// offset to a direction, then ask the SAME field the simulation walks
        /// on how high it is there.
        /// </summary>
        private void BuildTerrainImage(TerrainField terrain, Vector3 up, Vector3 fwd, Vector3 right, float radius)
        {
            var pixels = new byte[Resolution * Resolution * 4];
            for (int py = 0; py < Resolution; py++)
            {
                // Image rows run top-down; the projection's +y is north on
                // screen, so the row index flips.
                float ny = 1f - (py + 0.5f) / Resolution * 2f;
                for (int px = 0; px < Resolution; px++)
                {
                    float nx = (px + 0.5f) / Resolution * 2f - 1f;
                    float rho = Mathf.Sqrt(nx * nx + ny * ny);

                    // Square, not a disc: the window is cropped, not clipped
                    // to a circle, so the corners reach MapExtent * sqrt(2).
                    // On this planet that is still under half a circumference,
                    // so no direction wraps past the antipode.
                    float arc = rho * MapExtent;
                    float theta = arc / radius;
                    Vector3 tangent = rho < 1e-6f ? fwd : (right * (nx / rho) + fwd * (ny / rho));
                    Vector3 dir = up * Mathf.Cos(theta) + tangent * Mathf.Sin(theta);

                    var simDir = new Vec3(dir.X, dir.Y, dir.Z);
                    double sampled = terrain.SampleRadius(simDir);
                    float slope = Mathf.Clamp((float)(terrain.Slope(simDir) / TerrainField.MaxSlope), 0f, 1f);

                    // The ground's own palette, so the map matches the view.
                    Color c = TerrainMesh.Shade(terrain, simDir, Frame.ToGodot(simDir * sampled), 0); // one index: no per-pixel jitter on a map
                    // Steep ground is drawn darker still, which is what turns
                    // a height ramp into something you can read a route off.
                    c = c.Lerp(c * 0.45f, slope);
                    int i = (py * Resolution + px) * 4;
                    pixels[i] = (byte)(c.R * 255); pixels[i + 1] = (byte)(c.G * 255);
                    pixels[i + 2] = (byte)(c.B * 255); pixels[i + 3] = 255;
                }
            }

            if (_terrainImage == null)
            {
                _terrainImage = Image.CreateFromData(Resolution, Resolution, false, Image.Format.Rgba8, pixels);
                _terrainTexture = ImageTexture.CreateFromImage(_terrainImage);
            }
            else
            {
                _terrainImage.SetData(Resolution, Resolution, false, Image.Format.Rgba8, pixels);
                _terrainTexture.Update(_terrainImage);
            }
        }

        private static Color ColourFor(ushort type) => type switch
        {
            EntityType.Player => new Color(0.45f, 0.72f, 1.00f),
            EntityType.Npc => new Color(0.95f, 0.45f, 0.35f),
            EntityType.Target => new Color(0.95f, 0.82f, 0.30f),
            EntityType.Loot => new Color(0.50f, 0.95f, 0.45f),
            EntityType.Projectile => new Color(1.00f, 0.65f, 0.20f),
            // Ship is borrowed as the id for fixed landmarks (the spawn).
            EntityType.Ship => new Color(0.95f, 0.95f, 0.98f),
            _ => Colors.Gray,
        };
    }
}
