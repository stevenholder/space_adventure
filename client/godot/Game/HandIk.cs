// Phase 27: the hands on the weapon, by IK (ROADMAP "Phase 27").
//
// A first-person gun is posed by code under the camera (ViewModel.Tick); the
// fp_* clips keep playing underneath for the spine, shoulders, fingers and the
// elbow's swing, and this modifier owns the arm chains: every frame, after
// the AnimationPlayer and before the skin and the bone attachments see the
// pose, each hand bone is put where its hold empty sits on the weapon's
// socket (`grip` / `fore`): `hold.<rifle|pistol>.<r|l>`, made per body by
// art/tools/bpy/human.py's contact grip, position AND turn. A body without
// them falls back to its palm mount (`hand.r` / `hand.l`) on the socket,
// turned as the hold clip turns the hand relative to the gun. Two bones,
// solved analytically; the clip's own elbow is the pole, so the elbow swings
// the way the clip swings it.

using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game
{
    public sealed partial class HandIk : SkeletonModifier3D
    {
        /// <summary>One arm: its three bones, the hand's turn relative to the weapon and its mount in hand-bone space.</summary>
        private struct Arm
        {
            public int Up, Low, Hand;
            public Basis Rel;            // hand bone basis = weapon basis * Rel (skeleton space)
            public Vector3 Mount;        // the mount's origin in hand-bone space
            public Node3D MountNode;     // that mount (a hold empty, or the palm mount)
            public bool Held;            // MountNode is a hold empty: its frame goes ON the socket
            public Node3D Socket;        // on the weapon
            public int[] Fingers;        // every bone under the hand (fingers, thumb)...
            public Quaternion[] Grip;    // ...and its local rotation in the hold clip at t = 0
            public bool Ok;
        }

        private readonly Arm[] _arms = new Arm[2];   // 0 right, 1 left

        /// <summary>
        /// The last solve, skeleton space, by bone: Skeleton3D hands the clip's
        /// pose back after its modifiers, so this is the only place the arms
        /// as drawn can be read (ViewModel.FpAudit).
        /// </summary>
        public readonly Dictionary<int, Transform3D> Solved = new Dictionary<int, Transform3D>();

        /// <summary>
        /// Last solve: how far (m) the worse hand on the gun was beyond its
        /// arm's reach (negative: slack). ViewModel pulls the gun in by it,
        /// so a body with short arms or shoulders set back still holds it.
        /// </summary>
        public float Short { get; private set; }

        /// <summary>The weapon whose frame (bore +Z, top +Y) the hands hold; posed before the skeleton updates.</summary>
        public Node3D Weapon;

        /// <summary>Per hand, 0 = the clip's arm, 1 = on the socket. ViewModel eases them.</summary>
        public float WeightR = 1f, WeightL = 1f;

        /// <summary>The node each hand puts on its socket (0 right, 1 left): a hold empty, else the palm mount; null when unset.</summary>
        public Node3D MountOf(int side) => _arms[side].Ok ? _arms[side].MountNode : null;

        /// <summary>Whether the hands go by the body's hold empties (else the palm mounts and the clip's turn).</summary>
        public bool Held => _arms[0].Held || _arms[1].Held;

        /// <summary>
        /// The last solve's wrist bend (degrees) on side 0 right / 1 left:
        /// forearm (elbow -> wrist) against the knuckles (wrist -> middle
        /// finger's base), as human.py audit_fp measures it. -1 unknown.
        /// </summary>
        public float WristBend(int side)
        {
            Skeleton3D sk = GetSkeleton();
            Arm a = _arms[side];
            if (sk == null || !a.Ok || !Solved.TryGetValue(a.Hand, out Transform3D h) || !Solved.TryGetValue(a.Low, out Transform3D l)) return -1f;
            // By parent and prefix: an imported skeleton may carry suffixed names (`_2`).
            int mid = -1;
            for (int b = 0; b < sk.GetBoneCount() && mid < 0; b++)
                if (sk.GetBoneParent(b) == a.Hand && sk.GetBoneName(b).StartsWith("middle_01")) mid = b;
            if (mid < 0) return -1f;
            Vector3 knuckle = h * sk.GetBoneRest(mid).Origin - h.Origin;
            return Mathf.RadToDeg((h.Origin - l.Origin).AngleTo(knuckle));
        }

        /// <summary>A worn piece's skeleton: repeat its wearer's solve (same bones, same clip pose by BoneMirror).</summary>
        public HandIk Source;

        /// <summary>
        /// Records, once per (body, weapon class), each hand's turn relative
        /// to the weapon. With the body's `hold.<hold>.<side>` empties that
        /// turn is exact: the empty's frame on the socket's. Without them
        /// it is the hand's turn in the hold clip `clip` at t = 0, where the weapon's
        /// bore runs down the body's forward and its top up the body's up --
        /// i.e. the weapon frame is the body model's own (`body`, the holder
        /// Attach returns: +Z forward after its flip, as a weapon holder's
        /// +Z is its bore). Mounts are found by name under their bone
        /// attachments; false when the body lacks either.
        /// </summary>
        public bool Reference(Node3D body, Animation clip, Node3D weapon, string hold = null)
        {
            Skeleton3D sk = GetParent() as Skeleton3D;
            Weapon = weapon;
            _arms[0].Ok = _arms[1].Ok = false;
            if (sk == null || clip == null || weapon == null) return false;
            // The weapon's reference frame (the body's) in skeleton space.
            Basis wRef = sk.GlobalBasis.Orthonormalized().Inverse() * body.GlobalBasis.Orthonormalized();
            var pose = new Dictionary<int, Transform3D>();
            for (int s = 0; s < 2; s++)
            {
                Node3D mount = hold != null ? AssetRegistry.FindNode(body, $"hold.{hold}.{(s == 0 ? "r" : "l")}") : null;
                bool held = mount != null;
                mount ??= AssetRegistry.FindNode(body, s == 0 ? "hand.r" : "hand.l");
                Node3D socket = AssetRegistry.FindNode(weapon, s == 0 ? "grip" : "fore");
                BoneAttachment3D att = null;
                for (Node n = mount; n != null && n != sk; n = n.GetParent())
                    if (n is BoneAttachment3D a) { att = a; break; }
                if (att == null || socket == null) continue;
                int hand = att.BoneIdx, low = hand >= 0 ? sk.GetBoneParent(hand) : -1, up = low >= 0 ? sk.GetBoneParent(low) : -1;
                if (up < 0) continue;
                Basis href = RefGlobal(sk, clip, hand, pose).Basis.Orthonormalized();
                // The fingers keep the hold clip's grip whatever clip plays
                // (hip, aimed, walking, firing all closed the same way).
                var fingers = new List<int>();
                for (int b = 0; b < sk.GetBoneCount(); b++)
                    for (int p = sk.GetBoneParent(b); p >= 0; p = sk.GetBoneParent(p))
                        if (p == hand) { fingers.Add(b); break; }
                var grip = new Quaternion[fingers.Count];
                for (int i = 0; i < grip.Length; i++)
                    grip[i] = RefLocal(sk, clip, fingers[i]).Basis.Orthonormalized().GetRotationQuaternion().Normalized();
                // Held: hand * (empty in hand) = weapon * (socket in weapon), so
                // hand = weapon * S * E^-1, whatever pose the bones are in now.
                Basis S = weapon.GlobalBasis.Orthonormalized().Inverse() * socket.GlobalBasis.Orthonormalized();
                Basis E = att.GlobalBasis.Orthonormalized().Inverse() * mount.GlobalBasis.Orthonormalized();
                _arms[s] = new Arm
                {
                    Up = up, Low = low, Hand = hand, Socket = socket, Ok = true, MountNode = mount, Held = held,
                    Fingers = fingers.ToArray(), Grip = grip,
                    Rel = (held ? S * E.Inverse() : wRef.Inverse() * href).Orthonormalized(),
                    // A rigid chain under the attachment: valid whatever the pose.
                    Mount = (att.GlobalTransform.AffineInverse() * mount.GlobalTransform).Origin,
                };
            }
            return _arms[0].Ok || _arms[1].Ok;
        }

        /// <summary>A bone's skeleton-space pose in `clip` at t = 0 (rest where a bone has no track).</summary>
        private static Transform3D RefGlobal(Skeleton3D sk, Animation clip, int bone, Dictionary<int, Transform3D> memo)
        {
            if (memo.TryGetValue(bone, out Transform3D got)) return got;
            Transform3D local = RefLocal(sk, clip, bone);
            int parent = sk.GetBoneParent(bone);
            Transform3D g = parent >= 0 ? RefGlobal(sk, clip, parent, memo) * local : local;
            memo[bone] = g;
            return g;
        }

        /// <summary>A bone's local pose in `clip` at t = 0 (rest where it has no track).</summary>
        private static Transform3D RefLocal(Skeleton3D sk, Animation clip, int bone)
        {
            Transform3D rest = sk.GetBoneRest(bone);
            Vector3 pos = rest.Origin, scl = rest.Basis.Scale;
            Quaternion rot = rest.Basis.Orthonormalized().GetRotationQuaternion();
            string name = sk.GetBoneName(bone);
            for (int t = 0; t < clip.GetTrackCount(); t++)
            {
                if (clip.TrackGetPath(t).GetConcatenatedSubNames() != name) continue;
                switch (clip.TrackGetType(t))
                {
                    case Animation.TrackType.Position3D: pos = clip.PositionTrackInterpolate(t, 0); break;
                    case Animation.TrackType.Rotation3D: rot = clip.RotationTrackInterpolate(t, 0); break;
                    case Animation.TrackType.Scale3D: scl = clip.ScaleTrackInterpolate(t, 0); break;
                }
            }
            return new Transform3D(new Basis(rot).Scaled(scl), pos);
        }

        public override void _ProcessModificationWithDelta(double delta)
        {
            Skeleton3D sk = GetSkeleton();
            HandIk src = Source != null && IsInstanceValid(Source) ? Source : this;
            if (sk == null || src.Weapon == null || !IsInstanceValid(src.Weapon) || !src.Weapon.IsInsideTree()) return;
            Solved.Clear();
            float worst = float.NegativeInfinity;
            Transform3D skInv = sk.GlobalTransform.AffineInverse();
            Basis w = sk.GlobalBasis.Orthonormalized().Inverse() * src.Weapon.GlobalBasis.Orthonormalized();
            for (int s = 0; s < 2; s++)
            {
                Arm a = src._arms[s];
                float weight = s == 0 ? src.WeightR : src.WeightL;
                if (!a.Ok || !IsInstanceValid(a.Socket) || a.Up >= sk.GetBoneCount()) continue;
                Basis hb = (w * a.Rel).Orthonormalized();
                Vector3 target = skInv * a.Socket.GlobalPosition - hb * a.Mount;
                // Reach counts a hand off the gun too (a reload): the gun must not drift out and back.
                worst = Mathf.Max(worst, Solve(sk, a, target, hb, Mathf.Clamp(weight, 0f, 1f), Solved));
                // The grip, by the same weight: a reload's clip owns the left fingers while it has the hand.
                float fw = Mathf.Clamp(weight, 0f, 1f);
                if (fw > 0f)
                    for (int i = 0; i < a.Fingers.Length; i++)
                        if (a.Fingers[i] < sk.GetBoneCount())
                            sk.SetBonePoseRotation(a.Fingers[i], fw >= 1f ? a.Grip[i] : sk.GetBonePoseRotation(a.Fingers[i]).Normalized().Slerp(a.Grip[i], fw));
            }
            if (src == this) Short = float.IsNegativeInfinity(worst) ? 0f : worst;
        }

        /// <summary>The shortest turn taking direction `from` onto `to`.</summary>
        private static Basis Turn(Vector3 from, Vector3 to)
        {
            Vector3 f = from.Normalized(), t = to.Normalized(), ax = f.Cross(t);
            if (ax.LengthSquared() < 1e-12f) return Basis.Identity;
            return new Basis(ax.Normalized(), f.AngleTo(t));
        }

        /// <summary>
        /// Two bones: the shoulder stays, the elbow stays in the plane of
        /// shoulder, target and the clip's elbow, the wrist lands on the
        /// target (full extension when it is out of reach), the hand takes
        /// `hand`. Blended into the clip's local rotations by `weight`.
        /// </summary>
        private static float Solve(Skeleton3D sk, Arm a, Vector3 target, Basis hand, float weight, Dictionary<int, Transform3D> solved)
        {
            Transform3D gU = sk.GetBoneGlobalPose(a.Up), gL = sk.GetBoneGlobalPose(a.Low);
            int parent = sk.GetBoneParent(a.Up);
            Transform3D gP = parent >= 0 ? sk.GetBoneGlobalPose(parent) : Transform3D.Identity;
            Vector3 s = gU.Origin, e = gL.Origin, h = sk.GetBoneGlobalPose(a.Hand).Origin;
            float la = (e - s).Length(), lb = (h - e).Length();
            Vector3 st = target - s;
            float d = st.Length();
            if (la < 1e-5f || lb < 1e-5f || d < 1e-5f) return 0f;
            float over = (d - la - lb) * sk.GlobalBasis.Scale.X;   // metres, as the world sees it
            if (weight <= 0f) return over;
            Vector3 dir = st / d;
            d = Mathf.Clamp(d, Mathf.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
            Vector3 t = s + dir * d;
            // The clip's elbow, off the shoulder-target line, is the pole.
            Vector3 pole = (e - s) - dir * (e - s).Dot(dir);
            if (pole.LengthSquared() < 1e-10f) pole = gU.Basis.X - dir * gU.Basis.X.Dot(dir);
            pole = pole.Normalized();
            float x = (la * la - lb * lb + d * d) / (2f * d);
            Vector3 e2 = s + dir * x + pole * Mathf.Sqrt(Mathf.Max(la * la - x * x, 0f));

            Basis turnU = Turn(e - s, e2 - s);
            Basis bU = turnU * gU.Basis;
            Basis bL0 = turnU * gL.Basis;
            Basis bL = Turn(turnU * (h - e), t - e2) * bL0;
            Basis bH = hand.Scaled(sk.GetBoneGlobalPose(a.Hand).Basis.Scale);

            void Set(int bone, Basis parentGlobal, Basis global)
            {
                Quaternion want = (parentGlobal.Orthonormalized().Inverse() * global.Orthonormalized()).GetRotationQuaternion().Normalized();
                Quaternion now = sk.GetBonePoseRotation(bone).Normalized();
                sk.SetBonePoseRotation(bone, weight >= 1f ? want : now.Slerp(want, weight));
            }
            Set(a.Up, gP.Basis, bU);
            Set(a.Low, bU, bL);
            Set(a.Hand, bL, bH);
            solved[a.Up] = new Transform3D(bU, s);
            solved[a.Low] = new Transform3D(bL, e2);
            solved[a.Hand] = new Transform3D(bH, t);
            return over;
        }
    }
}
