// The only place Sim math meets Godot math.
//
// The Sim is right-handed, Y-up, local +Z forward (Step.OrientationQuat).
// So is Godot. The conversion is the identity, and every rotation, position
// and axis crosses this boundary unchanged — the Unity client negated Z here
// and then had to rebuild every rotation from two converted axes; none of
// that survives.
//
// The one flip that remains is not a handedness flip: glTF models face −Z
// (art/README.md, and glTF's own convention), the Sim faces +Z. So a loaded
// model is parented under one node carrying ModelFlip, in AssetRegistry and
// nowhere else. Box models in Models.cs are authored +Z-forward and need none.

using Godot;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public static class Frame
    {
        public static Vector3 ToGodot(Vec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        public static Vec3 ToSim(Vector3 v) => new Vec3(v.X, v.Y, v.Z);

        /// <summary>A Sim quaternion (x, y, z, w) as a Godot basis, verbatim.</summary>
        public static Basis BasisOf(Quat q) =>
            new Basis(new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W));

        /// <summary>
        /// The render frame of a body standing at `pos` facing `facing`:
        /// right = up × facing, up = normalize(pos), forward = facing. The
        /// same frame Step.OrientationQuat builds, so a player and an NPC are
        /// oriented by one rule. Never Basis.LookingAt for an entity — that
        /// yields −Z-forward and fights the model flip.
        /// </summary>
        public static Basis OrientationBasis(Vec3 pos, Vec3 facing)
        {
            Vec3 up = pos.Normalized();
            return BasisOf(Quat.FromBasis(Vec3.Cross(up, facing), up, facing));
        }

        /// <summary>180° about Y: a −Z-forward glTF model into the +Z-forward Sim frame.</summary>
        public static readonly Basis ModelFlip = new Basis(Vector3.Up, Mathf.Pi);
    }
}
