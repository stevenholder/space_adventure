// The map (M), on a planet.
//
// PROJECTION: azimuthal equidistant, centred on the player, rotated so the
// way you are facing is up.
//
// That is not a stylistic pick. A flat lat/long map of a sphere puts a seam
// somewhere and tears the poles apart, and the player SPAWNS on the +Y pole,
// which is the worst place for a projection to be undefined. Azimuthal
// equidistant centred on the viewer has no seam anywhere near them, and it
// makes both of the things a "where is it" map is for exact rather than
// approximate:
//
//   - the straight-line direction from the centre to a marker is its true
//     bearing from where you stand, so you can walk it off the screen;
//   - the distance from the centre is the true distance ALONG THE SURFACE,
//     so a ring at 100 m means a hundred metres of walking.
//
// The world is 942 m around, so the whole planet fits: the outer edge of the
// disc is the antipode, the single point furthest from you.
//
// Terrain shading is sampled from the same TerrainField the sim uses, so the
// map cannot disagree with the ground about where a hill is.

using System.Collections.Generic;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;
using UnityEngine;

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

        private Texture2D _terrainImage;
        private Texture2D _dot;
        private GUIStyle _label, _title;

        private Vector3 _builtAt = Vector3.positiveInfinity;
        private Vector3 _builtFacing;

        public bool Open { get; private set; }

        public void Toggle() => Open = !Open;
        public void Close() => Open = false;

        /// <summary>
        /// Draws the map. Called from OnGUI; does nothing while closed.
        /// </summary>
        public void Draw(TerrainField terrain, Vector3 playerPos, Vector3 playerFacing,
                         IEnumerable<MapMarker> markers)
        {
            if (!Open || terrain == null) return;
            EnsureStyles();

            float size = Mathf.Min(Screen.width, Screen.height) * 0.82f;
            var rect = new Rect((Screen.width - size) * 0.5f, (Screen.height - size) * 0.5f, size, size);

            Vector3 up = playerPos.normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(playerFacing, up);
            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
            fwd.Normalize();
            Vector3 right = Vector3.Cross(up, fwd); // Unity's right for this facing

            // The planet's radius here is the player's own distance from the
            // centre. Using the constant would put the scale slightly wrong on
            // a hill, and every distance on the map with it.
            float radius = playerPos.magnitude;
            float worldEdge = Mathf.PI * radius;        // antipode, in metres of walking
            float pixelsPerMetre = size * 0.5f / worldEdge;

            if ((playerPos - _builtAt).sqrMagnitude > RebuildAfterMetres * RebuildAfterMetres ||
                Vector3.Dot(fwd, _builtFacing) < 0.995f || _terrainImage == null)
            {
                BuildTerrainImage(terrain, up, fwd, right, radius);
                _builtAt = playerPos;
                _builtFacing = fwd;
            }

            GUI.DrawTexture(rect, _terrainImage, ScaleMode.StretchToFill);

            Vector2 centre = rect.center;
            DrawRangeRings(centre, pixelsPerMetre, size);

            foreach (MapMarker m in markers)
            {
                if (!Project(m.Pos, up, fwd, right, radius, out Vector2 offset)) continue;
                Vector2 at = centre + new Vector2(offset.x, -offset.y) * pixelsPerMetre;
                if (!rect.Contains(at)) continue;

                Color colour = ColourFor(m.Type);
                float r = m.Type == EntityType.Player ? 5f : 4f;
                GUI.color = colour;
                GUI.DrawTexture(new Rect(at.x - r, at.y - r, r * 2, r * 2), _dot);
                GUI.color = Color.white;

                if (!string.IsNullOrEmpty(m.Label))
                {
                    GUI.Label(new Rect(at.x + 7, at.y - 9, 190, 18), m.Label, _label);
                }
            }

            // You, at the centre, pointing up the screen by construction.
            GUI.color = new Color(0.95f, 0.95f, 1f);
            GUI.DrawTexture(new Rect(centre.x - 2, centre.y - 9, 4, 14), _dot);
            GUI.DrawTexture(new Rect(centre.x - 5, centre.y - 2, 10, 4), _dot);
            GUI.color = Color.white;

            GUI.Label(new Rect(rect.x, rect.y - 26, size, 24),
                $"MAP — facing up, edge is the far side of the world ({worldEdge:F0} m away)", _title);
            GUI.Label(new Rect(rect.x, rect.yMax + 4, size, 22),
                "M closes · rings every 100 m · white spawn · red hostile · yellow target · green loot · blue player",
                _label);
        }

        private void DrawRangeRings(Vector2 centre, float pixelsPerMetre, float size)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.13f);
            for (int metres = 100; metres <= 400; metres += 100)
            {
                float r = metres * pixelsPerMetre;
                if (r > size * 0.5f) break;
                // Four ticks per ring rather than a full circle: IMGUI has no
                // circle primitive, and a ring of dots costs more than it says.
                GUI.DrawTexture(new Rect(centre.x - r, centre.y - 1, 6, 2), _dot);
                GUI.DrawTexture(new Rect(centre.x + r - 6, centre.y - 1, 6, 2), _dot);
                GUI.DrawTexture(new Rect(centre.x - 1, centre.y - r, 2, 6), _dot);
                GUI.DrawTexture(new Rect(centre.x - 1, centre.y + r - 6, 2, 6), _dot);
            }
            GUI.color = Color.white;
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
            float worldEdge = Mathf.PI * radius;
            var offMap = new Color32(6, 7, 10, 255);

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
                    if (rho > 1f)
                    {
                        pixels[i] = offMap; // outside the disc: past the antipode
                        continue;
                    }

                    float arc = rho * worldEdge;
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

        private void EnsureStyles()
        {
            if (_dot != null) return;
            _dot = Texture2D.whiteTexture;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _label.normal.textColor = new Color(0.92f, 0.94f, 1f);
            _title = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
            _title.normal.textColor = new Color(0.92f, 0.94f, 1f);
        }
    }
}
