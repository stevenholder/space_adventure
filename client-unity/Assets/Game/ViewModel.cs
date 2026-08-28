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
// where they actually are, and other players see exactly the same model.
//
// It is the same model other players see, minus the head — one flag on one
// model, so the two cannot drift apart.
//
// The head has to go. The near plane does not remove it: the eye is at 1.70
// and the visor sits 0.095 m in front of that, past the 0.05 m near plane,
// where a 0.20 m box covers the whole screen width and everything from 9
// degrees below centre to 30 above. It rendered as a black band across the
// top half of the view. First-person games hide the local head for exactly
// this reason.
//
// The models are box meshes built in Models.cs. There is no asset pipeline
// here yet (`art/` holds glTF this client cannot load), so the art is source
// code — and it is sized from the server's own hitbox, so aim and feel
// survive real meshes replacing it.

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

            _weapon = BoxMesh.Attach(_rig, "rifle", Models.Rifle(), material, layer);

            // Hands are placed ON the rifle, in the rifle's own space, so they
            // stay put if its proportions change: forward hand on the
            // handguard, rear hand at the grip.
            var forward = BoxMesh.Attach(_weapon.transform, "hand-forward", Models.Hand(), material, layer);
            forward.transform.localPosition = new Vector3(-0.005f, -0.055f, 0.30f);
            forward.transform.localEulerAngles = new Vector3(10f, 0f, 0f);

            var rear = BoxMesh.Attach(_weapon.transform, "hand-rear", Models.Hand(), material, layer);
            rear.transform.localPosition = new Vector3(0.005f, -0.075f, -0.075f);
            rear.transform.localEulerAngles = new Vector3(24f, 0f, 0f);

            // Parented to the RIG, not to the weapon, so its offsets stay in
            // metres — see the note on RestPosition. The rifle model puts its
            // muzzle brake at z 0.53 in rig space.
            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(_rig, false);
            muzzle.transform.localPosition = new Vector3(0f, 0.012f, 0.57f);
            muzzle.gameObject.layer = layer;
            _muzzle = muzzle.transform;

            // ---- body: a real object in the world, seen when you look down ----
            var body = new GameObject("LocalBody");
            body.transform.SetParent(worldParent, false);
            _body = body.transform;
            BoxMesh.Attach(_body, "model", Models.PlayerLocal(), material, 0);
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
