// The sky, which is space.
//
// Godot's default is a procedural ATMOSPHERE — a blue gradient with a horizon
// haze. On an airless rock lit by a single sun that is simply the wrong
// picture, and it also hides the thing the game is about: you are standing on
// a small body with everything else overhead.
//
// The starfield is generated from the WORLD SEED, so every client raises the
// same sky over the same planet. A sky that differs per client is one more
// thing two players cannot point at and agree about, and the seed is already
// on the wire in `hello_ack`.
//
// One generated equirectangular panorama rather than six imported textures:
// six PNGs are six binary assets to review, and this is a hundred lines that
// say exactly what the sky contains. (Godot has no cubemap sky material; the
// panorama is the native one, and simpler than the six-face rasteriser the
// Unity build needed.)

using System;
using Godot;

namespace SpaceAdventure.Game
{
    public static class Sky
    {
        /// <summary>
        /// Panorama size. 2048x1024 is ~6 MB and gives a one-texel star at
        /// the equator about the same arc a 512-texel cube face did; coarser
        /// and bilinear filtering smears a star into an empty band.
        /// </summary>
        private const int Width = 2048;
        private const int Height = 1024;

        /// <summary>Stars over the whole sphere.</summary>
        private const int StarCount = 14000;

        /// <summary>
        /// Faint points concentrated near one great circle — a galactic band.
        /// A uniform scatter of points reads as noise; the band is what makes
        /// a sky look like a place with a structure you can orient by.
        /// </summary>
        private const int DustCount = 90000;

        /// <summary>
        /// Builds the starfield and installs it as the world's environment,
        /// replacing whatever WorldEnvironment `parent` carried before.
        /// </summary>
        public static WorldEnvironment Install(Node parent, uint worldSeed, WorldEnvironment previous)
        {
            var pixels = new byte[Width * Height * 3];
            // Not pure black: pure black reads as a hole.
            for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 4; pixels[i + 1] = 5; pixels[i + 2] = 9; }

            // The same generator the sim uses, so "seed 1337" means one sky.
            Func<double> rand = Sim.Rng.Mulberry32(worldSeed);

            // The galactic plane: a random axis from the seed, with the band
            // lying perpendicular to it.
            double az = rand() * 2.0 - 1.0;
            double at = rand() * 2.0 * Math.PI;
            double ar = Math.Sqrt(Math.Max(0.0, 1.0 - az * az));
            var axis = new Vector3((float)(ar * Math.Cos(at)), (float)(ar * Math.Sin(at)), (float)az);

            for (int i = 0; i < DustCount; i++)
            {
                Vector3 dust = RandomDirection(rand);
                // Keep only what falls near the plane, with a soft edge, so
                // the band fades out instead of ending at a line.
                float offPlane = Mathf.Abs(dust.Dot(axis));
                if (offPlane > 0.28f) continue;
                float falloff = 1f - offPlane / 0.28f;
                if (rand() > falloff * falloff) continue;
                Texel(dust, out int dx, out int dy);
                float g = 0.035f * falloff;
                Plot(pixels, dx, dy, g * 0.85f, g * 0.88f, g);
            }

            for (int i = 0; i < StarCount; i++)
            {
                Vector3 dir = RandomDirection(rand);
                // Stars near the galactic plane are denser, as they are in a
                // real sky: half of them are rejected unless they fall in the band.
                if (Mathf.Abs(dir.Dot(axis)) > 0.35f && rand() > 0.55) continue;
                Texel(dir, out int px, out int py);

                // Most stars are modest. A handful are bright enough to name.
                // The floor is well above black: anything dimmer survives
                // neither the bilinear filter nor the sky's exposure.
                double roll = rand();
                float brightness = roll > 0.993 ? 1.0f : roll > 0.95 ? 0.78f : 0.34f + (float)rand() * 0.30f;

                // Cool white through to faint amber, which is roughly what a
                // sky of mixed stellar classes looks like.
                float warm = (float)rand();
                float r = brightness * (0.85f + warm * 0.15f);
                float g = brightness * (0.88f + warm * 0.07f);
                float b = brightness * (1.00f - warm * 0.12f);
                Plot(pixels, px, py, r, g, b);

                // The brightest get a one-texel bloom so they survive resampling.
                if (brightness >= 0.78f)
                {
                    Plot(pixels, px + 1, py, r * 0.35f, g * 0.35f, b * 0.35f);
                    Plot(pixels, px - 1, py, r * 0.35f, g * 0.35f, b * 0.35f);
                    Plot(pixels, px, py + 1, r * 0.35f, g * 0.35f, b * 0.35f);
                    Plot(pixels, px, py - 1, r * 0.35f, g * 0.35f, b * 0.35f);
                }
            }

            Image img = Image.CreateFromData(Width, Height, false, Image.Format.Rgb8, pixels);
            var pano = new PanoramaSkyMaterial { Panorama = ImageTexture.CreateFromImage(img), Filter = true };
            var env = new WorldEnvironment
            {
                Name = "Environment",
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Sky,
                    Sky = new Godot.Sky { SkyMaterial = pano },
                    // Bilinear resampling and the panorama's exposure both
                    // dim a one-texel star; the multiplier brings the field
                    // back to the brightness it was authored at.
                    BackgroundEnergyMultiplier = 1.35f,
                    // Ambient stays flat and slightly blue, so the unlit side
                    // of the planet is legible without looking daylit.
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.17f, 0.18f, 0.24f),
                    AmbientLightEnergy = 1f,
                },
            };
            previous?.QueueFree();
            parent.AddChild(env);
            return env;
        }

        /// <summary>
        /// A uniform direction on the sphere. NOT a uniform pixel: that clumps
        /// toward the poles of the panorama, where a texel subtends the least
        /// solid angle.
        /// </summary>
        private static Vector3 RandomDirection(Func<double> rand)
        {
            double z = rand() * 2.0 - 1.0;
            double theta = rand() * 2.0 * Math.PI;
            double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
            return new Vector3((float)(r * Math.Cos(theta)), (float)(r * Math.Sin(theta)), (float)z);
        }

        /// <summary>The panorama texel a direction lands on (Godot's equirect layout).</summary>
        private static void Texel(Vector3 d, out int px, out int py)
        {
            float u = Mathf.Atan2(d.X, -d.Z) / Mathf.Tau + 0.5f;
            float v = 0.5f - Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f)) / Mathf.Pi;
            px = Mathf.Clamp((int)(u * (Width - 1)), 0, Width - 1);
            py = Mathf.Clamp((int)(v * (Height - 1)), 0, Height - 1);
        }

        private static void Plot(byte[] pixels, int x, int y, float r, float g, float b)
        {
            if (y < 0 || y >= Height) return;
            x = ((x % Width) + Width) % Width; // the seam wraps
            int i = (y * Width + x) * 3;
            // Additive: overlapping stars brighten rather than replace.
            pixels[i] = (byte)Math.Min(255, pixels[i] + (int)(r * 255f));
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + (int)(g * 255f));
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + (int)(b * 255f));
        }
    }
}
