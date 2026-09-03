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
// The CROP is the part worth explaining. Showing the whole planet is possible
// — it is only 942 m around, so the antipode is 471 m away and the entire
// world fits in one disc — and it is useless. Everything a player cares about
// sits within a couple of hundred metres, so the whole-world view squeezed the
// quartermaster, the spawn point and the range into a few pixels at the centre
// with their labels on top of each other, and gave most of the screen to
// terrain nobody was going to walk to. A 300 m window puts the same landmarks
// far enough apart to read.
//
// Terrain shading is sampled from the same TerrainField the sim uses, so the
// map cannot disagree with the ground about where a hill is.

using System.Collections.Generic;
using SpaceAdventure.Game.UI;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpaceAdventure.Game
{
    /// <summary>One thing worth drawing on the map.</summary>
    public readonly struct MapMarker
    {
        public readonly Vector3 Pos;
        public readonly ushort Type;
        public readonly string Label;

        public MapMarker(Vector3 pos, ushort type, string label)
        {
            Pos = pos;
            Type = type;
            Label = label;
        }
    }

    public sealed class MapView
    {
        /// <summary>Texels across the terrain image. 256 is ~65k samples to rebuild.</summary>
        private const int Resolution = 256;

        /// <summary>Rebuild the terrain image after the player moves this far.</summary>
        private const float RebuildAfterMetres = 4f;

        /// <summary>
        /// Half-width of the window, in metres of surface distance. The map
        /// spans this far in every direction from the player.
        ///
        /// On a planet this small 300 m is not a small angle — it is 2 radians
        /// of arc, most of a hemisphere — so the far corners are genuinely
        /// stretched. That is inherent to flattening a sphere and is the right
        /// trade here: everything worth walking to is inside it, and inside
        /// 100 m the distortion is not visible.
        /// </summary>
        private const float MapExtent = 300f;

        private Texture2D _terrainImage;

        private Vector3 _builtAt = Vector3.positiveInfinity;
        private Vector3 _builtFacing;

        /// <summary>Label rectangles already used this frame, to keep them apart.</summary>
        private readonly List<Rect> _labelled = new List<Rect>();

        // Phase 8: the map draws through UI Toolkit. The panel and the image
        // are built once; the marker layer is cleared and refilled per frame
        // while the map is open. ponytail: per-frame rebuild allocates; pool
        // the dots if the profiler ever names this.
        private readonly VisualElement _panel;
        private readonly Image _image;
        private readonly VisualElement _markerLayer;

        public MapView(VisualElement root)
        {
            _panel = Styles.Panel(Styles.SkewNone);
            _panel.style.position = Position.Absolute;
            _panel.style.top = Length.Percent(6);
            _panel.style.left = Length.Percent(50);
            _panel.style.translate = new Translate(Length.Percent(-50), 0);
            _panel.style.display = DisplayStyle.None;

            _panel.Add(Styles.Header($"Map — facing up, {MapExtent:F0} m in every direction"));

            var frame = new VisualElement();
            Styles.SetBorder(frame, Styles.Ink, 2);
            _image = new Image { scaleMode = ScaleMode.StretchToFill };
            _markerLayer = new VisualElement();
            _markerLayer.style.position = Position.Absolute;
            _markerLayer.style.left = 0;
            _markerLayer.style.top = 0;
            _markerLayer.style.right = 0;
            _markerLayer.style.bottom = 0;
            frame.Add(_image);
            frame.Add(_markerLayer);
            _panel.Add(frame);

            var legend = Styles.Display_(
                "M closes · rings every 100 m · white spawn · red hostile · yellow target · green loot · blue player",
                11, Styles.Dust);
            legend.style.marginTop = 6;
            _panel.Add(legend);
            root.Add(_panel);
        }

        public bool Open => _panel.style.display == DisplayStyle.Flex;

        public void Toggle() => _panel.style.display = Open ? DisplayStyle.None : DisplayStyle.Flex;
        public void Close() => _panel.style.display = DisplayStyle.None;

        /// <summary>
        /// Updates the map. Called from Update; does nothing while closed.
        /// </summary>
        public void Draw(TerrainField terrain, Vector3 playerPos, Vector3 playerFacing,
                         IEnumerable<MapMarker> markers)
        {
            if (!Open || terrain == null) return;

            float size = Mathf.Min(Screen.width, Screen.height) * 0.62f;
            _image.style.width = size;
            _image.style.height = size;
            var rect = new Rect(0, 0, size, size);

            Vector3 up = playerPos.normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(playerFacing, up);
            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
            fwd.Normalize();
            Vector3 right = Vector3.Cross(up, fwd); // Unity's right for this facing

            // The planet's radius here is the player's own distance from the
            // centre. Using the constant would put the scale slightly wrong on
            // a hill, and every distance on the map with it.
            float radius = playerPos.magnitude;
            float pixelsPerMetre = size * 0.5f / MapExtent;

            if ((playerPos - _builtAt).sqrMagnitude > RebuildAfterMetres * RebuildAfterMetres ||
                Vector3.Dot(fwd, _builtFacing) < 0.995f || _terrainImage == null)
            {
                BuildTerrainImage(terrain, up, fwd, right, radius);
                _builtAt = playerPos;
                _builtFacing = fwd;
            }

            _image.image = _terrainImage;

            Vector2 centre = rect.center;
            _markerLayer.Clear();
            DrawRangeRings(centre, pixelsPerMetre, size);

            _labelled.Clear();
            foreach (MapMarker m in markers)
            {
                if (!Project(m.Pos, up, fwd, right, radius, out Vector2 offset)) continue;
                Vector2 at = centre + new Vector2(offset.x, -offset.y) * pixelsPerMetre;
                if (!rect.Contains(at)) continue;

                float r = m.Type == EntityType.Player ? 5f : 4f;
                Dot(at.x - r, at.y - r, r * 2, r * 2, ColourFor(m.Type));

                if (!string.IsNullOrEmpty(m.Label)) PlaceLabel(at, m.Label);
            }

            // You, at the centre, pointing up the screen by construction.
            var you = new Color(0.95f, 0.95f, 1f);
            Dot(centre.x - 2, centre.y - 9, 4, 14, you);
            Dot(centre.x - 5, centre.y - 2, 10, 4, you);
        }

        private void Dot(float x, float y, float w, float h, Color c)
        {
            var e = new VisualElement();
            e.style.position = Position.Absolute;
            e.style.left = x;
            e.style.top = y;
            e.style.width = w;
            e.style.height = h;
            e.style.backgroundColor = c;
            e.pickingMode = PickingMode.Ignore;
            _markerLayer.Add(e);
        }

        /// <summary>
        /// Draws a marker's label, nudging it down past anything already
        /// placed and dropping it if there is nowhere clear.
        ///
        /// Landmarks cluster: spawn and the quartermaster stand about four
        /// metres apart, which at this scale is the same pixel, and their
        /// labels printed straight over each other into an unreadable smear.
        /// Losing one label is better than losing both.
        /// </summary>
        private void PlaceLabel(Vector2 at, string text)
        {
            var candidate = new Rect(at.x + 7, at.y - 9, 190, 18);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                bool clear = true;
                foreach (Rect taken in _labelled)
                {
                    if (taken.Overlaps(candidate)) { clear = false; break; }
                }
                if (clear)
                {
                    _labelled.Add(candidate);
                    var l = Styles.Display_(text, 12, Styles.Cream);
                    l.style.position = Position.Absolute;
                    l.style.left = candidate.x;
                    l.style.top = candidate.y;
                    _markerLayer.Add(l);
                    return;
                }
                candidate.y += 15;
            }
        }

        private void DrawRangeRings(Vector2 centre, float pixelsPerMetre, float size)
        {
            // UI Toolkit has a circle primitive after all: a square with 50%
            // corner radius and a border is a true ring, which IMGUI's tick
            // marks only gestured at.
            for (int metres = 100; metres <= (int)MapExtent; metres += 100)
            {
                float r = metres * pixelsPerMetre;
                if (r > size * 0.5f) break;
                var ring = new VisualElement();
                ring.style.position = Position.Absolute;
                ring.style.left = centre.x - r;
                ring.style.top = centre.y - r;
                ring.style.width = r * 2;
                ring.style.height = r * 2;
                ring.style.borderTopLeftRadius = r;
                ring.style.borderTopRightRadius = r;
                ring.style.borderBottomLeftRadius = r;
                ring.style.borderBottomRightRadius = r;
                Styles.SetBorder(ring, new Color(1f, 1f, 1f, 0.13f), 1);
                ring.pickingMode = PickingMode.Ignore;
                _markerLayer.Add(ring);
            }
        }

        /// <summary>
        /// World position to map offset in METRES along the surface, or false
        /// when the point is degenerate.
        /// </summary>
        private static bool Project(Vector3 world, Vector3 up, Vector3 fwd, Vector3 right,
                                    float radius, out Vector2 offset)
        {
            offset = Vector2.zero;
            Vector3 dir = world.normalized;
            if (dir.sqrMagnitude < 1e-8f) return false;

            float cos = Mathf.Clamp(Vector3.Dot(up, dir), -1f, 1f);
            float arc = Mathf.Acos(cos) * radius; // surface distance, not chord

            Vector3 tangent = dir - up * cos;
            if (tangent.sqrMagnitude < 1e-10f)
            {
                offset = Vector2.zero; // directly underfoot, or the exact antipode
                return true;
            }
            tangent.Normalize();
            offset = new Vector2(Vector3.Dot(tangent, right), Vector3.Dot(tangent, fwd)) * arc;
            return true;
        }

        /// <summary>
        /// Paints the terrain by inverting the projection for every texel: map
        /// offset to a direction, then ask the SAME field the simulation walks
        /// on how high it is there.
        /// </summary>
        private void BuildTerrainImage(TerrainField terrain, Vector3 up, Vector3 fwd, Vector3 right, float radius)
        {
            if (_terrainImage == null)
            {
                _terrainImage = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            var pixels = new Color32[Resolution * Resolution];

            for (int py = 0; py < Resolution; py++)
            {
                // Texture rows run bottom-up; the projection's +y is north on
                // screen, so this reads straight across.
                float ny = (py + 0.5f) / Resolution * 2f - 1f;
                for (int px = 0; px < Resolution; px++)
                {
                    float nx = (px + 0.5f) / Resolution * 2f - 1f;
                    float rho = Mathf.Sqrt(nx * nx + ny * ny);
                    int i = py * Resolution + px;

                    // Square, not a disc: the window is cropped, not clipped
                    // to a circle, so the corners reach MapExtent * sqrt(2).
                    // On this planet that is still under half a circumference,
                    // so no direction wraps past the antipode and the mapping
                    // stays single-valued.
                    float arc = rho * MapExtent;
                    float theta = arc / radius;
                    Vector3 tangent = rho < 1e-6f ? fwd : (right * (nx / rho) + fwd * (ny / rho));
                    Vector3 dir = up * Mathf.Cos(theta) + tangent * Mathf.Sin(theta);

                    var simDir = new Vec3(dir.x, dir.y, dir.z);
                    double sampled = terrain.SampleRadius(simDir);
                    float h = Mathf.Clamp01((float)((sampled - TerrainField.RadiusMin) /
                                                    (TerrainField.RadiusMax - TerrainField.RadiusMin)));
                    float slope = Mathf.Clamp01((float)(terrain.Slope(simDir) / TerrainField.MaxSlope));

                    Color c = Color.Lerp(new Color(0.16f, 0.23f, 0.17f), new Color(0.62f, 0.58f, 0.48f), h);
                    // Steep ground is drawn darker, which is what turns a
                    // height ramp into something you can read a route off.
                    c = Color.Lerp(c, c * 0.45f, slope);
                    pixels[i] = c;
                }
            }

            _terrainImage.SetPixels32(pixels);
            _terrainImage.Apply(false);
        }

        private static Color ColourFor(ushort type) => type switch
        {
            EntityType.Player => new Color(0.45f, 0.72f, 1.00f),
            EntityType.Npc => new Color(0.95f, 0.45f, 0.35f),
            EntityType.Target => new Color(0.95f, 0.82f, 0.30f),
            EntityType.Loot => new Color(0.50f, 0.95f, 0.45f),
            EntityType.Projectile => new Color(1.00f, 0.65f, 0.20f),
            // Ship is borrowed as the id for fixed landmarks until Phase 5
            // needs it for an actual ship.
            EntityType.Ship => new Color(0.95f, 0.95f, 0.98f),
            _ => Color.gray,
        };

    }
}
