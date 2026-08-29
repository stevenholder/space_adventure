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
// model, so the two cannot drift apart. The head is still there as a
// shadows-only renderer, so the silhouette on the ground is whole.
//
// The head has to go. The near plane does not remove it: the eye is at 1.70
// and the visor sits 0.095 m in front of that, past the 0.05 m near plane,
// where a 0.20 m box covers the whole screen width and everything from 9
// degrees below centre to 30 above. It rendered as a black band across the
// top half of the view. First-person games hide the local head for exactly
// this reason.
//
// The models START as the box meshes in Models.cs and are replaced by the real
// ones from art/ as they load. The boxes are not dead code: they are what is on
// screen for the first frames, and they are what the real models are FITTED TO
// -- the rest pose, the hand placement and the muzzle marker are all measured
// against the box rifle's frame (+Z forward, origin at the grip), while the
// imported rifle points -Z about a centred origin because that is the contract
// art/README.md sets for every weapon. AssetRegistry.AttachFitted reconciles
// the two by bounding box rather than by a hardcoded offset, so this rig keeps
// working when the model changes again.

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

        public ViewModel(Camera eye, Material material, int layer, Transform worldParent,
                        AssetRegistry assets)
        {
            // ---- viewmodel: arms and weapon, on the overlay layer ----
            var rig = new GameObject("ViewModel");
            rig.transform.SetParent(eye.transform, false);
            rig.transform.localPosition = RestPosition;
            rig.transform.localEulerAngles = RestEuler;
            _rig = rig.transform;

            _weapon = BoxMesh.Attach(_rig, "rifle", Models.Rifle(), material, layer);

            // yaw 180: the box rifle points +Z and every offset in this file is
            // measured in that frame; weapon.pulse points -Z. A bounding box
            // cannot tell which end is the barrel, so that half is stated here
            // and the rest is derived.
            assets.AttachFitted("weapon.pulse", _weapon.transform, Models.Rifle(),
                                180f, layer,
                                _ => _weapon.GetComponent<MeshRenderer>().enabled = false);

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

            // The head casts but does not draw. Without it the shadow on the
            // ground in front of you is headless, which is more distracting
            // than the visor ever was.
            GameObject head = BoxMesh.Attach(_body, "head-shadow", Models.PlayerHead(), material, 0);
            head.GetComponent<MeshRenderer>().shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

            // The real body, over the boxes: your own legs, animated, when you
            // look down.
            //
            // The model is segmented rather than skinned -- separate nodes per
            // limb -- which is what makes this possible at all. Everything from
            // the waist up is switched to shadows-only instead of hidden, so
            // the silhouette on the ground stays whole while nothing is in the
            // lens. A skinned body could not be split this way: one
            // SkinnedMeshRenderer is all or nothing, and the whole body would
            // have had to go shadows-only.
            //
            // Which parts: `head` because the camera is inside it, `torso`
            // because at 0.3 m it fills the lower half of the screen, and the
            // arms because they hang off the shoulders into view. The legs are
            // what you actually want to see.
            assets.Attach("char.player", _body, model =>
            {
                foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                {
                    if (!UpperBody.Contains(t.gameObject.name)) continue;
                    var r = t.GetComponent<Renderer>();
                    if (r != null)
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }
                _bodyAnim = CharacterAnim.For(model);
                foreach (Renderer r in _body.GetComponentsInChildren<Renderer>())
                    if (r.gameObject.name is "model" or "head-shadow") r.enabled = false;
            });
        }

        /// <summary>
        /// The nodes hidden on the LOCAL body only. Names are the contract from
        /// art/README.md, which every character model is imported to satisfy.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> UpperBody =
            new System.Collections.Generic.HashSet<string> { "head", "torso", "arm.l", "arm.r" };

        /// <summary>Drives the local body's legs. Null until the model lands.</summary>
        private CharacterAnim _bodyAnim;

        /// <summary>
        /// Your own gait, from your own predicted speed -- not from drawn
        /// positions like a remote, because the local body is predicted rather
        /// than interpolated and its transform is authoritative here.
        /// </summary>
        private void DriveBody(float speed) => _bodyAnim?.Drive(speed, false);

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

            // Same speed the weapon bob already runs on, so your legs and your
            // gun cannot disagree about whether you are moving.
            DriveBody(speed);
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
