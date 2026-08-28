// The models, as box lists. This is the art, and it is source code.
//
// Every humanoid is 1.8 m to the crown and 0.7 m across the shoulders, because
// that is the capsule the SERVER resolves shots against (items.json hitbox:
// radius 0.35, height 1.8). A model that is not that size teaches the wrong
// aim: you learn to shoot at the shape and the server tests the capsule.
//
// Meshes are built once per archetype and shared. Every grunt is the same
// twenty boxes, so there is no reason for each to own a copy.
//
// Origin is between the feet, +Y up, +Z forward — the same frame the entity
// row's position and facing use, so a model needs no offset to stand up.

using System.Collections.Generic;
using UnityEngine;

namespace SpaceAdventure.Game
{
    public static class Models
    {
        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        private static Mesh Get(string key, System.Func<List<Box>> build)
        {
            if (!Cache.TryGetValue(key, out Mesh mesh) || mesh == null)
            {
                mesh = BoxMesh.Build(build(), key);
                Cache[key] = mesh;
            }
            return mesh;
        }

        // ---- humanoids ---------------------------------------------------

        /// <summary>The player body and every NPC, tinted per role.</summary>
        public static Mesh Humanoid(string key, Color suit, Color trim, Color skin, bool helmet)
            => Get(key, () => HumanoidBoxes(suit, trim, skin, helmet));

        private static List<Box> HumanoidBoxes(Color suit, Color trim, Color skin, bool helmet)
        {
            var dark = suit * 0.75f;
            var boots = trim * 0.6f;
            var b = new List<Box>
            {
                // Legs, feet flat on the ground.
                new Box(new Vector3(-0.115f, 0.40f, 0f), new Vector3(0.17f, 0.80f, 0.19f), dark),
                new Box(new Vector3( 0.115f, 0.40f, 0f), new Vector3(0.17f, 0.80f, 0.19f), dark),
                new Box(new Vector3(-0.115f, 0.045f, 0.03f), new Vector3(0.19f, 0.09f, 0.27f), boots),
                new Box(new Vector3( 0.115f, 0.045f, 0.03f), new Vector3(0.19f, 0.09f, 0.27f), boots),

                // Hips and torso. The chest is wider than the waist, which is
                // most of what makes a box stack read as a person.
                new Box(new Vector3(0f, 0.90f, 0f), new Vector3(0.40f, 0.20f, 0.25f), trim),
                new Box(new Vector3(0f, 1.16f, 0f), new Vector3(0.44f, 0.34f, 0.26f), suit),
                new Box(new Vector3(0f, 1.40f, 0f), new Vector3(0.52f, 0.20f, 0.28f), suit),

                // Shoulder caps set the 0.70 m width the hitbox implies.
                new Box(new Vector3(-0.30f, 1.44f, 0f), new Vector3(0.16f, 0.16f, 0.24f), trim),
                new Box(new Vector3( 0.30f, 1.44f, 0f), new Vector3(0.16f, 0.16f, 0.24f), trim),

                // Arms, hanging slightly forward so the silhouette is not a slab.
                new Box(new Vector3(-0.30f, 1.16f, 0.02f), new Vector3(0.13f, 0.40f, 0.15f), dark),
                new Box(new Vector3( 0.30f, 1.16f, 0.02f), new Vector3(0.13f, 0.40f, 0.15f), dark),
                new Box(new Vector3(-0.30f, 0.90f, 0.06f), new Vector3(0.12f, 0.16f, 0.13f), skin),
                new Box(new Vector3( 0.30f, 0.90f, 0.06f), new Vector3(0.12f, 0.16f, 0.13f), skin),

                // Neck and head, crown at 1.80.
                new Box(new Vector3(0f, 1.56f, 0f), new Vector3(0.13f, 0.10f, 0.13f), skin),
                new Box(new Vector3(0f, 1.71f, 0f), new Vector3(0.22f, 0.24f, 0.23f), skin),
            };

            if (helmet)
            {
                b.Add(new Box(new Vector3(0f, 1.76f, 0f), new Vector3(0.25f, 0.16f, 0.26f), trim));
                // A dark visor across the eyes: it gives the head a FRONT, so
                // you can tell at a glance which way an NPC is facing.
                b.Add(new Box(new Vector3(0f, 1.72f, 0.115f), new Vector3(0.20f, 0.07f, 0.04f),
                              new Color(0.05f, 0.06f, 0.09f)));
            }
            else
            {
                b.Add(new Box(new Vector3(0f, 1.78f, -0.01f), new Vector3(0.23f, 0.09f, 0.24f), trim));
                b.Add(new Box(new Vector3(0f, 1.72f, 0.12f), new Vector3(0.13f, 0.05f, 0.03f),
                              new Color(0.10f, 0.10f, 0.12f)));
            }
            return b;
        }

        public static Mesh Player() => Humanoid("player",
            new Color(0.30f, 0.52f, 0.80f), new Color(0.20f, 0.34f, 0.55f),
            new Color(0.78f, 0.62f, 0.50f), helmet: true);

        public static Mesh Shopkeeper() => Humanoid("shopkeeper",
            new Color(0.55f, 0.48f, 0.35f), new Color(0.38f, 0.32f, 0.22f),
            new Color(0.80f, 0.65f, 0.52f), helmet: false);

        public static Mesh Grunt() => Humanoid("grunt",
            new Color(0.62f, 0.24f, 0.20f), new Color(0.40f, 0.15f, 0.13f),
            new Color(0.55f, 0.42f, 0.36f), helmet: true);

        public static Mesh Gunner() => Humanoid("gunner",
            new Color(0.52f, 0.30f, 0.42f), new Color(0.33f, 0.18f, 0.28f),
            new Color(0.55f, 0.42f, 0.36f), helmet: true);

        // ---- props --------------------------------------------------------

        /// <summary>A range target: a plate on a stand, not a floating cube.</summary>
        public static Mesh Target() => Get("target", () =>
        {
            var frame = new Color(0.35f, 0.33f, 0.30f);
            return new List<Box>
            {
                new Box(new Vector3(0f, 0.08f, 0f), new Vector3(0.60f, 0.16f, 0.36f), frame * 0.8f),
                new Box(new Vector3(-0.16f, 0.55f, 0f), new Vector3(0.07f, 0.90f, 0.07f), frame),
                new Box(new Vector3( 0.16f, 0.55f, 0f), new Vector3(0.07f, 0.90f, 0.07f), frame),
                new Box(new Vector3(0f, 1.15f, 0f), new Vector3(0.70f, 0.70f, 0.08f), new Color(0.86f, 0.76f, 0.28f)),
                new Box(new Vector3(0f, 1.15f, -0.05f), new Vector3(0.34f, 0.34f, 0.03f), new Color(0.80f, 0.26f, 0.18f)),
                new Box(new Vector3(0f, 1.15f, -0.07f), new Vector3(0.12f, 0.12f, 0.03f), new Color(0.92f, 0.92f, 0.90f)),
            };
        });

        /// <summary>A dropped item: a small crate with a lid you can pick out of grass.</summary>
        public static Mesh Loot() => Get("loot", () =>
        {
            var crate = new Color(0.32f, 0.62f, 0.34f);
            return new List<Box>
            {
                new Box(new Vector3(0f, 0.14f, 0f), new Vector3(0.34f, 0.28f, 0.34f), crate),
                new Box(new Vector3(0f, 0.30f, 0f), new Vector3(0.38f, 0.06f, 0.38f), crate * 1.25f),
                new Box(new Vector3(0f, 0.14f, 0.18f), new Vector3(0.10f, 0.10f, 0.03f), new Color(0.9f, 0.9f, 0.5f)),
            };
        });

        public static Mesh Projectile() => Get("projectile", () => new List<Box>
        {
            new Box(Vector3.zero, new Vector3(0.10f, 0.10f, 0.34f), new Color(1f, 0.62f, 0.15f)),
        });

        // ---- first person -------------------------------------------------

        /// <summary>
        /// The pulse rifle, origin at the grip, muzzle down +Z. Held in the
        /// viewmodel and, later, in a remote player's hands.
        /// </summary>
        public static Mesh Rifle() => Get("rifle", () =>
        {
            var body = new Color(0.26f, 0.28f, 0.32f);
            var metal = new Color(0.17f, 0.18f, 0.21f);
            var accent = new Color(0.32f, 0.56f, 0.72f);
            var grip = new Color(0.14f, 0.13f, 0.13f);
            return new List<Box>
            {
                new Box(new Vector3(0f, 0f, 0.02f), new Vector3(0.070f, 0.095f, 0.34f), body),      // receiver
                new Box(new Vector3(0f, 0.012f, 0.30f), new Vector3(0.055f, 0.060f, 0.20f), metal), // handguard
                new Box(new Vector3(0f, 0.012f, 0.44f), new Vector3(0.030f, 0.030f, 0.16f), metal), // barrel
                new Box(new Vector3(0f, 0.012f, 0.53f), new Vector3(0.044f, 0.044f, 0.05f), metal), // muzzle brake
                new Box(new Vector3(0f, 0.030f, 0.32f), new Vector3(0.016f, 0.016f, 0.13f), accent),// charge rail
                new Box(new Vector3(0f, 0.066f, 0.06f), new Vector3(0.030f, 0.040f, 0.09f), metal), // rear sight
                new Box(new Vector3(0f, 0.070f, 0.28f), new Vector3(0.020f, 0.030f, 0.04f), metal), // front post
                new Box(new Vector3(0f, -0.115f, 0.02f), new Vector3(0.048f, 0.17f, 0.085f), metal, new Vector3(-8f, 0f, 0f)), // magazine
                new Box(new Vector3(0f, -0.095f, -0.10f), new Vector3(0.042f, 0.14f, 0.06f), grip, new Vector3(14f, 0f, 0f)),  // pistol grip
                new Box(new Vector3(0f, -0.005f, -0.20f), new Vector3(0.055f, 0.085f, 0.14f), body), // stock throat
                new Box(new Vector3(0f, -0.020f, -0.31f), new Vector3(0.065f, 0.130f, 0.07f), grip), // butt plate
            };
        });

        /// <summary>
        /// A gloved hand with a thumb, plus the forearm behind it. Two of
        /// these carry the rifle. Fingers are one box, not four: at this
        /// distance the thumb is the only one whose absence you notice.
        /// </summary>
        public static Mesh Hand() => Get("hand", () =>
        {
            var glove = new Color(0.24f, 0.25f, 0.28f);
            var cuff = new Color(0.30f, 0.52f, 0.80f);
            return new List<Box>
            {
                new Box(new Vector3(0f, 0f, 0f), new Vector3(0.085f, 0.055f, 0.105f), glove),        // palm
                new Box(new Vector3(0f, -0.012f, 0.062f), new Vector3(0.080f, 0.042f, 0.045f), glove * 0.9f), // fingers
                new Box(new Vector3(0.048f, 0.010f, 0.020f), new Vector3(0.028f, 0.032f, 0.070f), glove * 1.1f, new Vector3(0f, -18f, 0f)), // thumb
                new Box(new Vector3(0f, 0.005f, -0.115f), new Vector3(0.075f, 0.075f, 0.16f), glove * 0.85f), // forearm
                new Box(new Vector3(0f, 0.005f, -0.055f), new Vector3(0.090f, 0.090f, 0.04f), cuff), // cuff
            };
        });
    }
}
