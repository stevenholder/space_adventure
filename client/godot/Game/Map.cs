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
        private const int Resolution = 384;

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
                "M closes · rings every 100 m · bands: basin, lowland, upland, highland, peak · pins: spawn, relay, outpost · diamonds: ore · ring: wreck · triangle: hostile",
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

                Glyph(at, m);
                if (!string.IsNullOrEmpty(m.Label)) PlaceLabel(at + new Vector2(4, 0), m.Label);
            }

            // You, at the centre: an arrow pointing up the screen by construction.
            Poly(centre, new[] { V(0, -10), V(7, 8), V(0, 4), V(-7, 8) }, new Color(0.98f, 0.98f, 1f));
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
            // Stylised, not photographed: the height under every texel is
            // posterised into a handful of flat bands (basin, lowland,
            // upland, highland, peak), and a texel whose band differs from
            // its left or upper neighbour is drawn as a contour line. Craters
            // become rings, hills become nested shapes, and nothing shimmers.
            var band = new byte[Resolution * Resolution];
            for (int py = 0; py < Resolution; py++)
            {
                float ny = 1f - (py + 0.5f) / Resolution * 2f;
                for (int px = 0; px < Resolution; px++)
                {
                    float nx = (px + 0.5f) / Resolution * 2f - 1f;
                    float rho = Mathf.Sqrt(nx * nx + ny * ny);
                    float theta = rho * MapExtent / radius;
                    Vector3 tangent = rho < 1e-6f ? fwd : (right * (nx / rho) + fwd * (ny / rho));
                    Vector3 dir = up * Mathf.Cos(theta) + tangent * Mathf.Sin(theta);
                    double height = terrain.SampleRadius(new Vec3(dir.X, dir.Y, dir.Z)) - TerrainField.PlanetRadius;
                    band[py * Resolution + px] = BandOf(height);
                }
            }

            var pixels = new byte[Resolution * Resolution * 4];
            for (int py = 0; py < Resolution; py++)
                for (int px = 0; px < Resolution; px++)
                {
                    int k = py * Resolution + px;
                    byte b = band[k];
                    bool edge = (px > 0 && band[k - 1] != b) || (py > 0 && band[k - Resolution] != b);
                    Color c = edge ? Contour : Bands[b];
                    int i = k * 4;
                    pixels[i] = (byte)(c.R * 255); pixels[i + 1] = (byte)(c.G * 255);
                    pixels[i + 2] = (byte)(c.B * 255); pixels[i + 3] = 255;
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

        /// <summary>Height above the planet's base radius → band index into Bands.</summary>
        private static byte BandOf(double h) => h < -8 ? (byte)0 : h < 4 ? (byte)1 : h < 14 ? (byte)2 : h < 26 ? (byte)3 : (byte)4;

        // Flat map colours, basin to peak: a cartographer's ramp, not the
        // ground's own palette, so the bands read as bands.
        private static readonly Color[] Bands =
        {
            new Color(0.16f, 0.24f, 0.28f), // basin
            new Color(0.32f, 0.46f, 0.30f), // lowland
            new Color(0.48f, 0.52f, 0.30f), // upland
            new Color(0.58f, 0.46f, 0.32f), // highland
            new Color(0.74f, 0.70f, 0.64f), // peak
        };
        private static readonly Color Contour = new Color(0.08f, 0.10f, 0.12f, 1f);

        /// <summary>
        /// One glyph per kind of thing, sized to read at a glance: a house for
        /// a shop, a flag for a board, a cross for the bench, a triangle for a
        /// hostile, diamonds for ore in the ore's colour, a ring for a wreck,
        /// a small square for loot, a box for the rover, a chevron for the
        /// ship, a round dot for another player, a pin for the spawn and the
        /// POIs. A dimmed one is depleted or dead.
        /// </summary>
        private void Glyph(Vector2 at, MapMarker m)
        {
            float a = m.Dim ? 0.45f : 1f;
            Color With(Color c) => new Color(c.R, c.G, c.B, a);
            switch (m.Icon)
            {
                case "shop": Poly(at, new[] { V(0, -7), V(7, 0), V(-7, 0) }, With(Amber)); Rect(at.X - 5, at.Y, 10, 6, With(Amber)); break;
                case "board": Rect(at.X - 1, at.Y - 7, 2, 14, With(Amber)); Poly(at, new[] { V(1, -7), V(8, -4), V(1, -1) }, With(Amber)); break;
                case "bench": Rect(at.X - 7, at.Y - 2, 14, 4, With(Amber)); Rect(at.X - 2, at.Y - 7, 4, 14, With(Amber)); break;
                case "hostile": Poly(at, new[] { V(0, -7), V(7, 6), V(-7, 6) }, With(Danger)); break;
                case "iron": Poly(at, new[] { V(0, -7), V(7, 0), V(0, 7), V(-7, 0) }, With(new Color(0.85f, 0.45f, 0.20f))); break;
                case "copper": Poly(at, new[] { V(0, -7), V(7, 0), V(0, 7), V(-7, 0) }, With(new Color(0.30f, 0.85f, 0.70f))); break;
                case "wreck": Ring(at, 7, With(new Color(0.75f, 0.75f, 0.72f))); break;
                case "loot": Rect(at.X - 3, at.Y - 3, 6, 6, With(new Color(0.50f, 0.95f, 0.45f))); break;
                case "rover": Rect(at.X - 6, at.Y - 4, 12, 8, With(Cream)); Rect(at.X - 4, at.Y - 6, 8, 3, With(Cream)); break;
                case "ship": Poly(at, new[] { V(0, -8), V(7, 6), V(0, 2), V(-7, 6) }, With(Cream)); break;
                case "player": Ring(at, 5, With(new Color(0.45f, 0.72f, 1f))); Rect(at.X - 2, at.Y - 2, 4, 4, With(new Color(0.45f, 0.72f, 1f))); break;
                case "spawn": Poly(at, new[] { V(0, 8), V(6, -2), V(-6, -2) }, With(Cream)); Ring(at + new Vector2(0, -5), 4, With(Cream)); break;
                case "poi": Poly(at, new[] { V(0, 8), V(6, -2), V(-6, -2) }, With(Amber)); Ring(at + new Vector2(0, -5), 4, With(Amber)); break;
                case "target": Ring(at, 5, With(new Color(0.95f, 0.82f, 0.30f))); break;
                default: Rect(at.X - 3, at.Y - 3, 6, 6, With(ColourFor(m.Type))); break;
            }
        }

        private static readonly Color Amber = Styles.Amber, Cream = Styles.Cream, Danger = Styles.Danger;
        private static Vector2 V(float x, float y) => new Vector2(x, y);

        private void Rect(float x, float y, float w, float h, Color c) => Dot(x, y, w, h, c);

        private void Poly(Vector2 at, Vector2[] pts, Color c)
        {
            var p = new Polygon2D { Polygon = pts, Color = c, Position = at };
            _markerLayer.AddChild(p);
        }

        private void Ring(Vector2 at, float r, Color c)
        {
            Panel ring = Styles.Box(Colors.Transparent, c, 2, r);
            ring.Position = new Vector2(at.X - r, at.Y - r);
            ring.Size = new Vector2(r * 2, r * 2);
            _markerLayer.AddChild(ring);
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
