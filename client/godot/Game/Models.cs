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
// row's position and facing use, so a model needs no offset to stand up and,
// unlike a glTF model, no flip. The local X axis is the Sim's, which mirrors
// how these boxes looked in the Unity build; every model here is symmetric
// about X except the hand's thumb, and the viewmodel places hands itself.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game
{
    public static class Models
    {
        private static readonly Dictionary<string, ArrayMesh> Cache = new Dictionary<string, ArrayMesh>();

        private static ArrayMesh Get(string key, System.Func<List<Box>> build)
        {
            if (!Cache.TryGetValue(key, out ArrayMesh mesh) || mesh == null)
            {
                mesh = BoxMesh.Build(build(), key);
                Cache[key] = mesh;
            }
            return mesh;
        }

        // ---- humanoids ---------------------------------------------------

        /// <summary>The player body and every NPC, tinted per role.</summary>
        public static ArrayMesh Humanoid(string key, Color suit, Color trim, Color skin,
                                    bool helmet, bool head = true, bool upper = true)
            => Get(key, () => HumanoidBoxes(suit, trim, skin, helmet, head, upper));

        private static List<Box> HumanoidBoxes(Color suit, Color trim, Color skin, bool helmet, bool head, bool upper)
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

                // Hips. The chest above is wider than the waist, which is
                // most of what makes a box stack read as a person.
                new Box(new Vector3(0f, 0.90f, 0f), new Vector3(0.40f, 0.20f, 0.25f), trim),

            };

            // The local body stops at the waist. Everything above it is
            // rendered shadows-only instead.
            //
            // The head had to go because the camera is inside it; the CHEST
            // had to go for the same reason one step down. The eye sits at
            // 1.70 directly above the body's own axis, so looking down puts a
            // 0.52 m torso about 0.25 m from the lens, where at 60 degrees it
            // fills the lower half of the screen as a flat slab. A real eye is
            // forward of the spine; this one is not, and moving it forward
            // would put what you see out of step with where the server
            // resolves your shots from.
            //
            // Legs and boots are what you actually want to see looking down,
            // and they are far enough away to read as legs.
            if (!upper) return b;

            b.Add(new Box(new Vector3(0f, 1.16f, 0f), new Vector3(0.44f, 0.34f, 0.26f), suit));
            b.Add(new Box(new Vector3(0f, 1.40f, 0f), new Vector3(0.52f, 0.20f, 0.28f), suit));
            b.Add(new Box(new Vector3(-0.30f, 1.44f, 0f), new Vector3(0.16f, 0.16f, 0.24f), trim));
            b.Add(new Box(new Vector3( 0.30f, 1.44f, 0f), new Vector3(0.16f, 0.16f, 0.24f), trim));
            b.Add(new Box(new Vector3(-0.30f, 1.16f, 0.02f), new Vector3(0.13f, 0.40f, 0.15f), dark));
            b.Add(new Box(new Vector3( 0.30f, 1.16f, 0.02f), new Vector3(0.13f, 0.40f, 0.15f), dark));
            b.Add(new Box(new Vector3(-0.30f, 0.90f, 0.06f), new Vector3(0.12f, 0.16f, 0.13f), skin));
            b.Add(new Box(new Vector3( 0.30f, 0.90f, 0.06f), new Vector3(0.12f, 0.16f, 0.13f), skin));

            // The local player's own body is built without a head, because the
            // camera is inside it.
            //
            // The near plane does NOT save you here, which is what the first
            // version of this got wrong. The eye sits at 1.70; the visor box
            // sits 0.095 m in front of it, past the 0.05 m near plane, and at
            // that distance a 0.20 m wide box subtends the entire screen
            // width and everything from 9 degrees below the centre to 30
            // above. It rendered as a black band over the top half of the
            // view — which is exactly what it is: the back of your own visor.
            //
            // Hiding the head for the local body only is what first-person
            // games do. It is one flag on one model rather than a second
            // model, so the body other players see cannot drift from the one
            // you stand in.
            if (!head) return b;

            // Neck and head, crown at 1.80.
            b.Add(new Box(new Vector3(0f, 1.56f, 0f), new Vector3(0.13f, 0.10f, 0.13f), skin));
            b.Add(new Box(new Vector3(0f, 1.71f, 0f), new Vector3(0.22f, 0.24f, 0.23f), skin));

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

        public static ArrayMesh Player() => Humanoid("player",
            new Color(0.30f, 0.52f, 0.80f), new Color(0.20f, 0.34f, 0.55f),
            new Color(0.78f, 0.62f, 0.50f), helmet: true);

        /// <summary>Legs and hips only: what is left once the camera is inside the rest.</summary>
        public static ArrayMesh PlayerLocal() => Humanoid("player-local",
            new Color(0.30f, 0.52f, 0.80f), new Color(0.20f, 0.34f, 0.55f),
            new Color(0.78f, 0.62f, 0.50f), helmet: true, head: false, upper: false);

        /// <summary>
        /// Everything above the waist, for the local body to cast a shadow
        /// with.
        ///
        /// Hiding the head and torso keeps them out of the lens, and it also
        /// takes them out of your SHADOW — a pair of legs walking around on
        /// their own. This mesh is rendered shadows-only, so the silhouette is
        /// whole and the geometry is still not in your face.
        /// </summary>
        public static ArrayMesh PlayerHead() => Get("player-upper", () =>
        {
            var suit = new Color(0.30f, 0.52f, 0.80f);
            var trim = new Color(0.20f, 0.34f, 0.55f);
            var skin = new Color(0.78f, 0.62f, 0.50f);
            var dark = suit * 0.75f;
            return new List<Box>
            {
                new Box(new Vector3(0f, 1.16f, 0f), new Vector3(0.44f, 0.34f, 0.26f), suit),
                new Box(new Vector3(0f, 1.40f, 0f), new Vector3(0.52f, 0.20f, 0.28f), suit),
                new Box(new Vector3(-0.30f, 1.44f, 0f), new Vector3(0.16f, 0.16f, 0.24f), trim),
                new Box(new Vector3( 0.30f, 1.44f, 0f), new Vector3(0.16f, 0.16f, 0.24f), trim),
                new Box(new Vector3(-0.30f, 1.16f, 0.02f), new Vector3(0.13f, 0.40f, 0.15f), dark),
                new Box(new Vector3( 0.30f, 1.16f, 0.02f), new Vector3(0.13f, 0.40f, 0.15f), dark),
                new Box(new Vector3(0f, 1.56f, 0f), new Vector3(0.13f, 0.10f, 0.13f), skin),
                new Box(new Vector3(0f, 1.71f, 0f), new Vector3(0.22f, 0.24f, 0.23f), skin),
                new Box(new Vector3(0f, 1.76f, 0f), new Vector3(0.25f, 0.16f, 0.26f), trim),
            };
        });

        public static ArrayMesh Shopkeeper() => Humanoid("shopkeeper",
            new Color(0.55f, 0.48f, 0.35f), new Color(0.38f, 0.32f, 0.22f),
            new Color(0.80f, 0.65f, 0.52f), helmet: false);

        public static ArrayMesh Grunt() => Humanoid("grunt",
            new Color(0.62f, 0.24f, 0.20f), new Color(0.40f, 0.15f, 0.13f),
            new Color(0.55f, 0.42f, 0.36f), helmet: true);

        public static ArrayMesh Gunner() => Humanoid("gunner",
            new Color(0.52f, 0.30f, 0.42f), new Color(0.33f, 0.18f, 0.28f),
            new Color(0.55f, 0.42f, 0.36f), helmet: true);

        // ---- props --------------------------------------------------------

        // ---- static structures -------------------------------------------
        //
        // Fallbacks for the `colliders` message, and each one matches the
        // FRAME of the .glb it stands in for -- Structures.cs positions and
        // scales the holder once, and both the box and the model that
        // replaces it have to read that transform the same way.

        /// <summary>
        /// Unit box, base at origin, 1x1 footprint: struct.wall's frame.
        /// </summary>
        public static ArrayMesh Wall() => Get("wall", () => new List<Box>
        {
            new Box(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f),
                    new Color(0.42f, 0.44f, 0.47f)),
        });

        /// <summary>
        /// Unit-RADIUS cube centred on the origin: struct.post's frame, which
        /// is a unit sphere spanning -1..1 rather than a box standing on 0.
        /// </summary>
        public static ArrayMesh Post() => Get("post", () => new List<Box>
        {
            new Box(Vector3.Zero, new Vector3(2f, 2f, 2f),
                    new Color(0.38f, 0.40f, 0.43f)),
        });

        /// <summary>A range target: a plate on a stand, not a floating cube.</summary>
        public static ArrayMesh Target() => Get("target", () =>
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
        public static ArrayMesh Loot() => Get("loot", () =>
        {
            var crate = new Color(0.32f, 0.62f, 0.34f);
            return new List<Box>
            {
                new Box(new Vector3(0f, 0.14f, 0f), new Vector3(0.34f, 0.28f, 0.34f), crate),
                new Box(new Vector3(0f, 0.30f, 0f), new Vector3(0.38f, 0.06f, 0.38f), crate * 1.25f),
                new Box(new Vector3(0f, 0.14f, 0.18f), new Vector3(0.10f, 0.10f, 0.03f), new Color(0.9f, 0.9f, 0.5f)),
            };
        });

        public static ArrayMesh Projectile() => Get("projectile", () => new List<Box>
        {
            new Box(Vector3.Zero, new Vector3(0.10f, 0.10f, 0.34f), new Color(1f, 0.62f, 0.15f)),
        });

        // ---- first person -------------------------------------------------

    }
}
