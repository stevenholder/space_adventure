// The sky, which is space.
//
// Unity's default is a procedural ATMOSPHERE — a blue gradient with a horizon
// haze. On an airless rock lit by a single sun that is simply the wrong
// picture, and it also hides the thing the game is about: you are standing on
// a small body with everything else overhead.
//
// The starfield is generated from the WORLD SEED, so every client raises the
// same sky over the same planet. That is worth the few lines it costs: a sky
// that differs per client is one more thing two players cannot point at and
// agree about, and the seed is already on the wire in `hello_ack`.
//
// A generated cubemap rather than six imported textures, because six PNGs are
// six binary assets to review, and this is thirty lines that say exactly what
// the sky contains.

using UnityEngine;

namespace SpaceAdventure.Game
{
    public static class Sky
    {
        /// <summary>Texels per cube face. 256 is ~1.2 MB total and plenty for points of light.</summary>
        private const int FaceSize = 256;

        /// <summary>Stars over the whole sphere, not per face.</summary>
        private const int StarCount = 5000;

        /// <summary>Builds the skybox and installs it in the render settings.</summary>
        public static void Install(uint worldSeed, Shader cubemapShader)
        {
            var cube = new Cubemap(FaceSize, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "starfield",
            };

            var faces = new Color[6][];
            var deepSpace = new Color(0.015f, 0.018f, 0.035f, 1f); // not pure black: pure black reads as a hole
            for (int f = 0; f < 6; f++)
            {
                faces[f] = new Color[FaceSize * FaceSize];
                for (int i = 0; i < faces[f].Length; i++) faces[f][i] = deepSpace;
            }

            // The same generator the sim uses, so "seed 1337" means one sky.
            var rand = Sim.Rng.Mulberry32(worldSeed);

            for (int i = 0; i < StarCount; i++)
            {
                // A uniform direction on the sphere, NOT a uniform pixel: the
                // latter clumps toward the cube's corners, where a face's
                // texels subtend the least solid angle.
                double z = rand() * 2.0 - 1.0;
                double theta = rand() * 2.0 * System.Math.PI;
                double r = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - z * z));
                var dir = new Vector3(
                    (float)(r * System.Math.Cos(theta)),
                    (float)(r * System.Math.Sin(theta)),
                    (float)z);

                if (!TryFaceTexel(dir, out int face, out int px, out int py)) continue;

                // Most stars are dim. A handful are bright enough to name.
                double roll = rand();
                float brightness = roll > 0.995 ? 1.0f : roll > 0.96 ? 0.65f : 0.20f + (float)rand() * 0.25f;

                // Cool white through to faint amber, which is roughly what a
                // sky of mixed stellar classes looks like.
                float warm = (float)rand();
                var colour = new Color(
                    brightness * (0.85f + warm * 0.15f),
                    brightness * (0.88f + warm * 0.07f),
                    brightness * (1.00f - warm * 0.12f),
                    1f);

                Plot(faces[face], px, py, colour);

                // Give the brightest ones a one-texel bloom so they survive
                // being resampled onto the skybox.
                if (brightness >= 0.65f)
                {
                    var halo = colour * 0.35f;
                    Plot(faces[face], px + 1, py, halo);
                    Plot(faces[face], px - 1, py, halo);
                    Plot(faces[face], px, py + 1, halo);
                    Plot(faces[face], px, py - 1, halo);
                }
            }

            for (int f = 0; f < 6; f++) cube.SetPixels(faces[f], (CubemapFace)f);
            cube.Apply(updateMipmaps: false);

            var material = new Material(cubemapShader);
            material.SetTexture("_Tex", cube);
            RenderSettings.skybox = material;

            // Ambient comes from the sky now. Low, and slightly blue, so the
            // unlit side of the planet stays legible without looking daylit.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.10f, 0.11f, 0.15f);
            RenderSettings.fog = false;
        }

        /// <summary>
        /// Picks the cube face a direction lands on and the texel within it.
        /// Face order matches Unity's CubemapFace: +X, -X, +Y, -Y, +Z, -Z.
        /// </summary>
        private static bool TryFaceTexel(Vector3 d, out int face, out int px, out int py)
        {
            face = 0; px = 0; py = 0;
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            float dominant;
            float u, v;

            if (ax >= ay && ax >= az) { face = d.x > 0 ? 0 : 1; dominant = ax; u = d.z; v = d.y; }
            else if (ay >= az) { face = d.y > 0 ? 2 : 3; dominant = ay; u = d.x; v = d.z; }
            else { face = d.z > 0 ? 4 : 5; dominant = az; u = d.x; v = d.y; }

            if (dominant < 1e-6f) return false;

            // Exact per-face UV orientation is deliberately not chased here.
            // The stars are randomly placed, so a face whose axes are mirrored
            // relative to Unity's convention produces a sky that is equally
            // random and equally uniform — the property that matters is the
            // DENSITY, which the direction sampling above already gets right.
            px = Mathf.Clamp((int)((u / dominant * 0.5f + 0.5f) * (FaceSize - 1)), 0, FaceSize - 1);
            py = Mathf.Clamp((int)((v / dominant * 0.5f + 0.5f) * (FaceSize - 1)), 0, FaceSize - 1);
            return true;
        }

        private static void Plot(Color[] pixels, int x, int y, Color colour)
        {
            if (x < 0 || y < 0 || x >= FaceSize || y >= FaceSize) return;
            int i = y * FaceSize + x;
            Color existing = pixels[i];
            // Additive: overlapping stars brighten rather than replace, which
            // keeps a dense patch from flattening to one colour.
            pixels[i] = new Color(
                Mathf.Min(1f, existing.r + colour.r),
                Mathf.Min(1f, existing.g + colour.g),
                Mathf.Min(1f, existing.b + colour.b),
                1f);
        }
    }
}
