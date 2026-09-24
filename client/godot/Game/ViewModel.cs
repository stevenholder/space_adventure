// The first-person rig: what you see of yourself.
//
// Two separate things, and they are separate on purpose.
//
// THE VIEWMODEL — arms and weapon — is parented to the camera on its own
// render layer and drawn by a SECOND camera inside a transparent SubViewport
// that sees only that layer, over the main view. Without that overlay pass
// the weapon intersects any wall you stand against and half the gun
// disappears into it. Godot has no camera stacking; the SubViewport gives
// the depth clear the Unity overlay camera had.
//
// THE BODY is the opposite: a real object at the player's feet, on the normal
// layer, casting a normal shadow — so looking down shows your chest and legs
// where they actually are, and other players see exactly the same model.
//
// The models START as the box meshes in Models.cs and are replaced by the real
// ones from art/ as they load. The boxes are what the real models are FITTED
// TO: the rest pose, the hand placement and the muzzle marker are all measured
// against the box rifle's frame (+Z forward, origin at the grip).
// AssetRegistry.AttachFitted reconciles the two by bounding box rather than by
// a hardcoded offset. The Attach path already flips the −Z glTF model to +Z,
// so no yaw is needed here.
//
// Camera space: a Camera3D looks down −Z, and the box rifle points +Z, so the
// rig carries Frame.ModelFlip; its rest offsets are the Unity build's with Z
// negated (in front of the eye is −Z here).

using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class ViewModel
    {
        // Held roughly where a rifle sits at the low ready: right of centre,
        // below the sightline, canted slightly inward. The height is not
        // free: at the overlay camera's 48° FOV the visible half-height at
        // this distance is about 0.28 m. Retune together with OverlayFov.
        // ponytail: tuned by screenshot for the volume-fitted Kenney rifle
        // (0.55 × 0.30 m), further out than the box rifle sat; eyes-on
        // tuning on a real display is the upgrade path.
        private static readonly Vector3 RestPosition = new Vector3(0.18f, -0.17f, -0.62f);
        private const float RestYawDeg = 4f;   // canted inward, toward the centre
        private const float RestRollDeg = 2f;

        public const float OverlayFov = 48f;

        private const float SwayDegrees = 0.9f;   // how far the rig lags a fast turn
        private const float SwaySmoothing = 12f;
        private const float BobSpeed = 9f;
        private const float BobAmount = 0.014f;

        private readonly Node3D _rig;          // arms + weapon, parented to the camera
        private readonly MeshInstance3D _weapon;
        private readonly Node3D _muzzle;       // barrel tip: where shots LOOK like they leave
        private readonly Node3D _body;         // the real body, in the world
        private readonly uint _layers;

        private Vector2 _sway;
        private float _bobPhase;

        public ViewModel(Camera3D eye, Material material, uint layers, Node worldParent, AssetRegistry assets)
        {
            _layers = layers;

            // ---- viewmodel: arms and weapon, on the overlay layer ----
            _rig = new Node3D { Name = "ViewModel" };
            eye.AddChild(_rig);
            PoseRig(Vector3.Zero, Vector2.Zero);

            _weapon = BoxMesh.Attach(_rig, "rifle", Models.Rifle(), material, layers);
            assets.AttachFitted("weapon.pulse", _weapon, Models.Rifle(), 0f, layers,
                _ => _weapon.Mesh = null); // the box goes; its children (hands, model) stay

            // Hands are placed ON the rifle, in the rifle's own space, so they
            // stay put if its proportions change: forward hand on the
            // handguard, rear hand at the grip.
            var forward = BoxMesh.Attach(_weapon, "hand-forward", Models.Hand(), material, layers);
            forward.Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(10f), 0, 0)), new Vector3(-0.005f, -0.055f, 0.30f));
            var rear = BoxMesh.Attach(_weapon, "hand-rear", Models.Hand(), material, layers);
            rear.Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(24f), 0, 0)), new Vector3(0.005f, -0.075f, -0.075f));

            // Parented to the RIG, not to the weapon, so its offsets stay in
            // metres. The fitted rifle's barrel ends near z 0.38 in rig space.
            _muzzle = new Node3D { Name = "muzzle", Position = new Vector3(0f, 0.012f, 0.40f) };
            _rig.AddChild(_muzzle);

            // ---- body: a real object in the world, seen when you look down ----
            _body = new Node3D { Name = "LocalBody" };
            worldParent.AddChild(_body);
            BoxMesh.Attach(_body, "model", Models.PlayerLocal(), material, 0);

            // The head casts but does not draw. Without it the shadow on the
            // ground in front of you is headless.
            MeshInstance3D head = BoxMesh.Attach(_body, "head-shadow", Models.PlayerHead(), material, 0);
            head.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;

            // The real body, over the boxes: your own legs, animated, when you
            // look down. The model is segmented -- separate nodes per limb --
            // so `head`, the one part the camera is inside, goes shadows-only
            // while the torso (which carries the hips) stays drawn.
            assets.Attach("char.player", _body, model =>
            {
                Node3D headNode = AssetRegistry.FindNode(model, "head");
                if (headNode != null)
                    foreach (GeometryInstance3D g in AssetRegistry.Descendants<GeometryInstance3D>(headNode))
                        g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                _bodyAnim = CharacterAnim.For(model);
                foreach (Node3D n in AssetRegistry.Descendants<Node3D>(_body))
                    if (n.Name == "model" || n.Name == "head-shadow") n.Visible = false;
            });
        }

        /// <summary>Drives the local body's legs. Null until the model lands.</summary>
        private CharacterAnim _bodyAnim;

        /// <summary>The barrel tip, in world space. Shots are DRAWN from here.</summary>
        public Vector3 MuzzlePosition => _muzzle.GlobalPosition;
        public Node3D Muzzle => _muzzle;
        public uint Layers => _layers;

        /// <summary>Shows or hides the weapon (and the hands on it).</summary>
        public bool WeaponVisible
        {
            get => _weapon.Visible;
            set => _weapon.Visible = value;
        }

        private void PoseRig(Vector3 bob, Vector2 swayDeg)
        {
            // Yaw positive turns the flipped rifle's −Z toward −X: inward.
            // The sway lags the turn: mouse-right (sway.X < 0) yaws left.
            var euler = new Vector3(
                Mathf.DegToRad(-swayDeg.Y),
                Mathf.DegToRad(RestYawDeg - swayDeg.X),
                Mathf.DegToRad(RestRollDeg));
            _rig.Transform = new Transform3D(Basis.FromEuler(euler) * Frame.ModelFlip, RestPosition + bob);
        }

        /// <summary>
        /// Sways the rig against the turn and bobs it with the stride.
        /// `speed` is the body's actual speed from the simulation, not the
        /// input: bobbing on input alone keeps bobbing while you walk into a wall.
        /// </summary>
        public void Tick(Vector2 lookDelta, float speed, float dt)
        {
            _sway = _sway.Lerp(-lookDelta * SwayDegrees, 1f - Mathf.Exp(-SwaySmoothing * dt));

            float moving = Mathf.Clamp(speed / 4.5f, 0f, 1f);
            _bobPhase += dt * BobSpeed * moving;
            float bobX = Mathf.Cos(_bobPhase) * BobAmount * moving;
            float bobY = Mathf.Abs(Mathf.Sin(_bobPhase)) * -BobAmount * moving;
            PoseRig(new Vector3(bobX, bobY, 0f), _sway);

            // Same speed the weapon bob already runs on, so your legs and your
            // gun cannot disagree about whether you are moving.
            _bodyAnim?.Drive(speed, false);
        }

        /// <summary>
        /// Stands the body at the player's feet, facing where the body faces —
        /// which is NOT where the camera looks. Pitch belongs to the head; a
        /// body that pitches with the view lies on its back when you look up.
        /// </summary>
        public void Place(Vec3 feet, Vec3 facing) => EntityViews.Place(_body, feet, facing);
    }
}
