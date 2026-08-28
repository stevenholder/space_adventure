// The first-person rig: what you see of yourself.
//
// Two separate things, and they are separate on purpose.
//
// THE VIEWMODEL — arms and weapon — is parented to the camera and drawn by a
// SECOND camera that renders only the ViewModel layer, clearing depth first.
// Without that overlay pass the weapon intersects any wall you stand against
// and half the gun disappears into it. Every first-person game does this; it
// is not an optimisation, it is the only way a 0.5 m prop coexists with world
// geometry at arm's length.
//
// THE BODY is the opposite: a real object at the player's feet, on the normal
// layer, casting a normal shadow — so looking down shows your chest and legs
// where they actually are, and other players see the same body in the same
// place. It stops at the collarbone. Modelling a head you are standing inside
// buys nothing but a view of the inside of a skull.
//
// Everything is primitives. There is no asset pipeline in this client yet
// (`art/` holds glTF the Unity side cannot load), so this is a stand-in that
// is honest about being one — correctly SIZED, so the aim and the feel are
// right when real meshes replace it.

using UnityEngine;

namespace SpaceAdventure.Game
{
    public sealed class ViewModel
    {
        // Held roughly where a rifle sits at the low ready: right of centre,
        // below the sightline, canted slightly inward.
        //
        // The height is not free. At the overlay camera's 48 degree FOV the
        // visible half-height at this distance is about 0.20 m, so a weapon
        // centred much below -0.12 has its underside cut off by the bottom of
        // the frame. Retune this together with ViewModelCamera.fieldOfView,
        // never one alone.
        private static readonly Vector3 RestPosition = new Vector3(0.15f, -0.11f, 0.40f);
        private static readonly Vector3 RestEuler = new Vector3(0f, -4f, 2f);

        private const float SwayDegrees = 0.9f;   // how far the rig lags a fast turn
        private const float SwaySmoothing = 12f;
        private const float BobSpeed = 9f;
        private const float BobAmount = 0.014f;

        private readonly Transform _rig;      // arms + weapon, parented to the camera
        private readonly GameObject _weapon;
        private readonly Transform _muzzle;   // barrel tip: where shots LOOK like they leave
        private readonly Transform _body;     // the real body, in the world

        private Vector2 _sway;
        private float _bobPhase;

        public ViewModel(Camera eye, Material material, int layer, Transform worldParent)
        {
            // ---- viewmodel: arms and weapon, on the overlay layer ----
            var rig = new GameObject("ViewModel");
            rig.transform.SetParent(eye.transform, false);
            rig.transform.localPosition = RestPosition;
            rig.transform.localEulerAngles = RestEuler;
            _rig = rig.transform;

            _weapon = Part(_rig, "weapon-body", new Vector3(0.07f, 0.08f, 0.42f), new Vector3(0, 0, 0.05f),
                           new Color(0.20f, 0.22f, 0.26f), material, layer);
            Part(_weapon.transform, "barrel", new Vector3(0.032f, 0.032f, 0.34f), new Vector3(0, 0.012f, 0.36f),
                 new Color(0.14f, 0.15f, 0.18f), material, layer);
            Part(_weapon.transform, "magazine", new Vector3(0.05f, 0.16f, 0.09f), new Vector3(0, -0.11f, -0.02f),
                 new Color(0.16f, 0.17f, 0.20f), material, layer);
            Part(_weapon.transform, "stock", new Vector3(0.05f, 0.09f, 0.20f), new Vector3(0, -0.02f, -0.28f),
                 new Color(0.18f, 0.16f, 0.14f), material, layer);
            Part(_weapon.transform, "sight", new Vector3(0.02f, 0.045f, 0.05f), new Vector3(0, 0.06f, 0.10f),
                 new Color(0.10f, 0.11f, 0.13f), material, layer);

            // An empty at the barrel tip. The server resolves a shot from the
            // player's EYE, which is the correct thing for hit registration
            // and the wrong place to draw from: a tracer and a flash starting
            // at the eye appear in the dead centre of the screen, in front of
            // the gun. This is where they visually start instead.
            // Parented to the RIG, not to the weapon. The weapon body carries a
            // non-uniform scale (0.07, 0.08, 0.42) that every child inherits,
            // which would both misplace this and stretch anything attached to
            // it into an ellipsoid. The rig is unscaled, so these are metres.
            //
            // Barrel tip, worked out in rig space: the weapon body sits at
            // z 0.05 and the barrel at 0.36 x 0.42 = 0.151 beyond that, with
            // half its 0.34 x 0.42 = 0.143 length again on top — 0.27. This
            // sits just past it.
            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(_rig, false);
            muzzle.transform.localPosition = new Vector3(0f, 0.002f, 0.29f);
            muzzle.gameObject.layer = layer;
            _muzzle = muzzle.transform;

            var glove = new Color(0.30f, 0.31f, 0.34f);
            // Forward hand on the barrel, rear hand on the grip.
            Part(_rig, "hand-forward", new Vector3(0.075f, 0.075f, 0.10f), new Vector3(-0.005f, -0.055f, 0.26f),
                 glove, material, layer);
            Part(_rig, "forearm-forward", new Vector3(0.06f, 0.06f, 0.26f), new Vector3(-0.07f, -0.15f, 0.10f),
                 glove, material, layer, new Vector3(-28f, 16f, 0f));
            Part(_rig, "hand-rear", new Vector3(0.07f, 0.075f, 0.09f), new Vector3(0.01f, -0.075f, -0.05f),
                 glove, material, layer);
            Part(_rig, "forearm-rear", new Vector3(0.06f, 0.06f, 0.24f), new Vector3(0.08f, -0.17f, -0.16f),
                 glove, material, layer, new Vector3(-34f, -14f, 0f));

            // ---- body: a real object in the world, seen when you look down ----
            var body = new GameObject("LocalBody");
            body.transform.SetParent(worldParent, false);
            _body = body.transform;

            var suit = new Color(0.35f, 0.65f, 0.95f); // the same blue remote players wear
            Part(_body, "torso", new Vector3(0.42f, 0.62f, 0.26f), new Vector3(0, 1.12f, 0), suit, material, 0);
            Part(_body, "hips", new Vector3(0.36f, 0.22f, 0.24f), new Vector3(0, 0.78f, 0), suit * 0.85f, material, 0);
            Part(_body, "leg-left", new Vector3(0.16f, 0.72f, 0.18f), new Vector3(-0.11f, 0.36f, 0), suit * 0.7f, material, 0);
            Part(_body, "leg-right", new Vector3(0.16f, 0.72f, 0.18f), new Vector3(0.11f, 0.36f, 0), suit * 0.7f, material, 0);
            Part(_body, "shoulder-left", new Vector3(0.15f, 0.15f, 0.15f), new Vector3(-0.26f, 1.36f, 0), suit * 0.9f, material, 0);
            Part(_body, "shoulder-right", new Vector3(0.15f, 0.15f, 0.15f), new Vector3(0.26f, 1.36f, 0), suit * 0.9f, material, 0);
        }

        /// <summary>
        /// The barrel tip, in world space. Shots are DRAWN from here; they are
        /// still RESOLVED from the eye position the server rewound to.
        /// </summary>
        public Transform Muzzle => _muzzle;

        /// <summary>Shows or hides the weapon; the hands stay either way.</summary>
        public bool WeaponVisible
        {
            get => _weapon.activeSelf;
            set => _weapon.SetActive(value);
        }

        /// <summary>
        /// Sways the rig against the turn and bobs it with the stride.
        ///
        /// `speed` is the body's actual speed from the simulation, not the
        /// input: bobbing on input alone keeps bobbing while you walk into a
        /// wall, which is a small thing that reads as very wrong.
        /// </summary>
        public void Tick(Vector2 lookDelta, float speed, float dt)
        {
            _sway = Vector2.Lerp(_sway, -lookDelta * SwayDegrees, 1f - Mathf.Exp(-SwaySmoothing * dt));

            float moving = Mathf.Clamp01(speed / 4.5f);
            _bobPhase += dt * BobSpeed * moving;
            float bobX = Mathf.Cos(_bobPhase) * BobAmount * moving;
            float bobY = Mathf.Abs(Mathf.Sin(_bobPhase)) * -BobAmount * moving;

            _rig.localPosition = RestPosition + new Vector3(bobX, bobY, 0f);
            _rig.localEulerAngles = RestEuler + new Vector3(_sway.y, _sway.x, 0f);
        }

        /// <summary>
        /// Stands the body at the player's feet, facing where the body faces —
        /// which is NOT where the camera looks. Pitch belongs to the head; a
        /// body that pitches with the view lies on its back when you look up.
        /// </summary>
        public void Place(Vector3 feet, Vector3 up, Vector3 facing)
        {
            _body.position = feet;
            Vector3 flat = Vector3.ProjectOnPlane(facing, up);
            if (flat.sqrMagnitude > 1e-8f) _body.rotation = Quaternion.LookRotation(flat.normalized, up);
        }

        private static GameObject Part(Transform parent, string name, Vector3 size, Vector3 localPos,
                                       Color colour, Material material, int layer, Vector3? euler = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(go.GetComponent<UnityEngine.Collider>()); // collision is the sim's job
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            if (euler.HasValue) go.transform.localEulerAngles = euler.Value;
            go.layer = layer;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(material) { color = colour };
            // The viewmodel is drawn by an overlay camera the sun does not
            // reach the same way; shadows off keeps it from self-shadowing
            // into a black slab.
            if (layer != 0)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            return go;
        }
    }
}
